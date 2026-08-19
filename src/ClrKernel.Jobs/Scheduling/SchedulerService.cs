using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Jobs;

/// <summary>
/// The scheduler loop behind <c>clrkernel-jobs serve</c>. Every tick it rescans the
/// job catalog and fires what is due:
/// <list type="bullet">
///   <item>Cron jobs whose next occurrence fell inside the tick window. Missed
///     occurrences while the scheduler was down are skipped (the window starts at
///     startup), and a job with an active run is not re-enqueued (overlap = skip).</item>
///   <item>Dependency-triggered jobs: a job fires when <em>every</em> job it dependsOn
///     has a success more recent than its own last trigger time. Because every run —
///     scheduled, manual (even from another process), or chained — moves that trigger
///     clock, fan-in fires exactly once, a failed upstream stops the chain, and
///     re-running the failure to success resumes it on the next tick.</item>
/// </list>
/// Runs execute through <see cref="JobExecutor"/> under a global parallelism cap,
/// with a fixed-delay retry loop per job. On shutdown, in-flight runs observe the
/// cancellation (their kernel processes are killed and the runs marked Cancelled);
/// on startup, rows left Running by a crash are marked Failed.
/// </summary>
public sealed class SchedulerService : BackgroundService {
    private readonly JobCatalog _catalog;
    private readonly IRunStore _store;
    private readonly JobsOptions _options;
    private readonly ILogger<SchedulerService> _logger;
    private readonly SemaphoreSlim _parallelism;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, Task> _inflight = new();
    private CancellationToken _stoppingToken = CancellationToken.None;

    /// <summary>How a job actually runs — the executor by default; tests script it.</summary>
    internal Func<RunRequest, CancellationToken, Task<Run>> RunJob { get; set; }

    internal TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(10);
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    private readonly Notifier _notifier;

    public SchedulerService(
        JobCatalog catalog, IRunStore store, JobExecutor executor, Notifier notifier,
        JobsOptions options, ILogger<SchedulerService> logger) {
        _catalog = catalog;
        _store = store;
        _notifier = notifier;
        _options = options;
        _logger = logger;
        _parallelism = new SemaphoreSlim(Math.Max(1, options.MaxParallelism));
        RunJob = (request, ct) => executor.ExecuteAsync(
            request.Job, request.Trigger, request.CausedByRunId, request.Attempt, request.RunId,
            request.HadOverrides, ct);
    }

    /// <summary>
    /// Fires a job now (the API's run button). Returns the pre-assigned run id, or
    /// null when the job already has an active run launched by this process.
    /// </summary>
    public Guid? TriggerManual(JobDefinition job, bool hadOverrides = false) {
        if (_activeJobs.ContainsKey(KeyOf(job))) {
            return null;
        }
        var runId = Guid.NewGuid();
        Launch(new RunRequest(job, RunTrigger.Manual, null, runId) { HadOverrides = hadOverrides },
            _stoppingToken);
        return runId;
    }

    /// <summary>Cancels a job's in-flight run (kills its kernel). False when none is ours.</summary>
    public bool TryCancel(string environment, string jobName) {
        if (_activeJobs.TryGetValue($"{environment}:{jobName}", out var cancellation)) {
            cancellation.Cancel();
            return true;
        }
        return false;
    }

    private static string KeyOf(JobDefinition job) => $"{job.Environment}:{job.Name}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        _stoppingToken = stoppingToken;
        var orphans = await _store.MarkOrphansFailedAsync();
        if (orphans > 0) {
            _logger.LogWarning("Marked {Count} run(s) orphaned by a previous shutdown as Failed.", orphans);
        }

        var lastTick = DateTime.UtcNow;
        _logger.LogInformation(
            "Scheduler started: notebooks {Root}, tick {Tick}s, max parallelism {Parallelism}.",
            _catalog.NotebooksRoot, TickInterval.TotalSeconds, _options.MaxParallelism);

        try {
            while (true) {
                await Task.Delay(TickInterval, stoppingToken);
                var now = DateTime.UtcNow;
                try {
                    await TickAsync(lastTick, now, stoppingToken);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception e) {
                    _logger.LogError(e, "Scheduler tick failed.");
                }
                lastTick = now;
            }
        } catch (OperationCanceledException) {
            // Stopping. In-flight runs see the same token: kernels are killed and
            // their runs marked Cancelled before the host exits.
            await DrainAsync();
            _logger.LogInformation("Scheduler stopped.");
        }
    }

    /// <summary>One pass over the catalog for the (from, to] window.</summary>
    internal async Task TickAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) {
        var catalog = _catalog.Load();
        foreach (var error in catalog.Errors) {
            _logger.LogWarning("Catalog: {Error}", error);
        }

        // Automatic triggers fire only where the scheduler owns execution: prod in
        // the git workflow, or the single default environment without it. Dev runs
        // are always deliberate (manual / API).
        foreach (var job in catalog.Jobs.Where(j => j.Enabled && j.Environment is "prod" or "default")) {
            if (_activeJobs.ContainsKey(KeyOf(job))) {
                continue;
            }

            if (job.Cron != null && IsDue(job.Cron, fromUtc, toUtc)) {
                if (await _store.HasActiveRunAsync(job.Environment, job.Name)) {
                    _logger.LogWarning("{Job} is due but still has an active run; skipping this occurrence.", job.Name);
                    continue;
                }
                Launch(new RunRequest(job, RunTrigger.Schedule, null, null), cancellationToken);
                continue;
            }

            if (job.DependsOn.Count > 0) {
                var causedBy = await DependencyReadyAsync(job);
                if (causedBy != null && !await _store.HasActiveRunAsync(job.Environment, job.Name)) {
                    Launch(new RunRequest(job, RunTrigger.Dependency, causedBy.Id, null), cancellationToken);
                }
            }
        }
    }

    /// <summary>True when the cron has an occurrence inside (from, to]. Times must be UTC.</summary>
    internal static bool IsDue(string cron, DateTime fromUtc, DateTime toUtc) {
        var next = CronExpression.Parse(cron).GetNextOccurrence(fromUtc, inclusive: false);
        return next != null && next <= toUtc;
    }

    /// <summary>
    /// The freshness rule: non-null when every dependency has a success newer than
    /// this job's last trigger. Returns the newest of those runs (chain lineage).
    /// </summary>
    internal async Task<Run> DependencyReadyAsync(JobDefinition job) {
        if (job.DependsOn.Count == 0) {
            return null;
        }
        var lastTrigger = await _store.GetLastTriggerAsync(job.Environment, job.Name) ?? DateTime.MinValue;
        Run newest = null;
        foreach (var dependency in job.DependsOn) {
            // Dependencies resolve within the same environment only.
            var success = await _store.GetLastSuccessfulRunAsync(job.Environment, dependency);
            if (success?.FinishedAt is not { } finished || finished <= lastTrigger) {
                return null;
            }
            if (newest?.FinishedAt is not { } newestFinished || finished > newestFinished) {
                newest = success;
            }
        }
        return newest;
    }

    private void Launch(RunRequest request, CancellationToken cancellationToken) {
        var job = request.Job;
        // One cancellation source per launch, keyed by job name (one active launch per
        // job): TryCancel and host shutdown both flow through it into the executor.
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeJobs[KeyOf(job)] = cancellation;
        var token = cancellation.Token;
        var key = Guid.NewGuid();
        _inflight[key] = Task.Run(async () => {
            try {
                await _parallelism.WaitAsync(token);
                try {
                    _logger.LogInformation("{Job} starting ({Trigger}).", job.Name, request.Trigger);
                    var run = await RunJob(request, token);
                    var attempt = 1;
                    while (run.Status == RunStatus.Failed && attempt <= job.RetryCount
                           && !token.IsCancellationRequested) {
                        attempt++;
                        _logger.LogWarning(
                            "{Job} failed (attempt {Attempt} of {Total}); retrying in {Delay}s.",
                            job.Name, attempt - 1, job.RetryCount + 1, RetryDelay.TotalSeconds);
                        await Task.Delay(RetryDelay, token);
                        run = await RunJob(
                            request with { Trigger = RunTrigger.Retry, Attempt = attempt, RunId = null }, token);
                    }
                    _logger.Log(
                        run.Status == RunStatus.Succeeded ? LogLevel.Information : LogLevel.Warning,
                        "{Job} {Status}{Error}.", job.Name, run.Status,
                        run.ErrorSummary != null ? $": {run.ErrorSummary}" : string.Empty);

                    // After the retry loop settles, so a job that recovers on retry
                    // notifies success once rather than failure-then-success.
                    await _notifier.NotifyAsync(job, run, CancellationToken.None);
                } finally {
                    _parallelism.Release();
                }
            } catch (OperationCanceledException) {
                // Shutdown or cancel while queued or between retries; the executor
                // already recorded any in-flight run as Cancelled.
            } catch (Exception e) {
                _logger.LogError(e, "{Job} run crashed outside the executor.", job.Name);
            } finally {
                _activeJobs.TryRemove(KeyOf(job), out _);
                _inflight.TryRemove(key, out _);
                cancellation.Dispose();
            }
        }, CancellationToken.None);
    }

    /// <summary>Waits for every launched run to finish (shutdown, and tests).</summary>
    internal Task DrainAsync() => Task.WhenAll(_inflight.Values.ToArray());
}

/// <summary>One requested execution of a job. RunId is pre-assigned for manual
/// triggers so the API can answer 202 with the id before the run starts.</summary>
internal sealed record RunRequest(JobDefinition Job, RunTrigger Trigger, Guid? CausedByRunId, Guid? RunId) {
    public int Attempt { get; init; } = 1;
    public bool HadOverrides { get; init; }
}

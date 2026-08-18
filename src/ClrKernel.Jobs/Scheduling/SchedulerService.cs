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
    private readonly ConcurrentDictionary<string, byte> _activeJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, Task> _inflight = new();

    /// <summary>How a job actually runs — the executor by default; tests script it.</summary>
    internal Func<JobDefinition, RunTrigger, Guid?, int, CancellationToken, Task<Run>> RunJob { get; set; }

    internal TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(10);
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    public SchedulerService(
        JobCatalog catalog, IRunStore store, JobExecutor executor,
        JobsOptions options, ILogger<SchedulerService> logger) {
        _catalog = catalog;
        _store = store;
        _options = options;
        _logger = logger;
        _parallelism = new SemaphoreSlim(Math.Max(1, options.MaxParallelism));
        RunJob = (job, trigger, causedBy, attempt, ct) =>
            executor.ExecuteAsync(job, trigger, causedBy, attempt, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
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

        foreach (var job in catalog.Jobs.Where(j => j.Enabled)) {
            if (_activeJobs.ContainsKey(job.Name)) {
                continue;
            }

            if (job.Cron != null && IsDue(job.Cron, fromUtc, toUtc)) {
                if (await _store.HasActiveRunAsync(job.Name)) {
                    _logger.LogWarning("{Job} is due but still has an active run; skipping this occurrence.", job.Name);
                    continue;
                }
                Launch(job, RunTrigger.Schedule, null, cancellationToken);
                continue;
            }

            if (job.DependsOn.Count > 0) {
                var causedBy = await DependencyReadyAsync(job);
                if (causedBy != null && !await _store.HasActiveRunAsync(job.Name)) {
                    Launch(job, RunTrigger.Dependency, causedBy.Id, cancellationToken);
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
        var lastTrigger = await _store.GetLastTriggerAsync(job.Name) ?? DateTime.MinValue;
        Run newest = null;
        foreach (var dependency in job.DependsOn) {
            var success = await _store.GetLastSuccessfulRunAsync(dependency);
            if (success?.FinishedAt is not { } finished || finished <= lastTrigger) {
                return null;
            }
            if (newest?.FinishedAt is not { } newestFinished || finished > newestFinished) {
                newest = success;
            }
        }
        return newest;
    }

    private void Launch(JobDefinition job, RunTrigger trigger, Guid? causedByRunId, CancellationToken cancellationToken) {
        _activeJobs[job.Name] = 0;
        var key = Guid.NewGuid();
        _inflight[key] = Task.Run(async () => {
            try {
                await _parallelism.WaitAsync(cancellationToken);
                try {
                    _logger.LogInformation("{Job} starting ({Trigger}).", job.Name, trigger);
                    var run = await RunJob(job, trigger, causedByRunId, 1, cancellationToken);
                    var attempt = 1;
                    while (run.Status == RunStatus.Failed && attempt <= job.RetryCount
                           && !cancellationToken.IsCancellationRequested) {
                        attempt++;
                        _logger.LogWarning(
                            "{Job} failed (attempt {Attempt} of {Total}); retrying in {Delay}s.",
                            job.Name, attempt - 1, job.RetryCount + 1, RetryDelay.TotalSeconds);
                        await Task.Delay(RetryDelay, cancellationToken);
                        run = await RunJob(job, RunTrigger.Retry, causedByRunId, attempt, cancellationToken);
                    }
                    _logger.Log(
                        run.Status == RunStatus.Succeeded ? LogLevel.Information : LogLevel.Warning,
                        "{Job} {Status}{Error}.", job.Name, run.Status,
                        run.ErrorSummary != null ? $": {run.ErrorSummary}" : string.Empty);
                } finally {
                    _parallelism.Release();
                }
            } catch (OperationCanceledException) {
                // Shutdown while queued or between retries; the executor already
                // recorded any in-flight run as Cancelled.
            } catch (Exception e) {
                _logger.LogError(e, "{Job} run crashed outside the executor.", job.Name);
            } finally {
                _activeJobs.TryRemove(job.Name, out _);
                _inflight.TryRemove(key, out _);
            }
        }, CancellationToken.None);
    }

    /// <summary>Waits for every launched run to finish (shutdown, and tests).</summary>
    internal Task DrainAsync() => Task.WhenAll(_inflight.Values.ToArray());
}

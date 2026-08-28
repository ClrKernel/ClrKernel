using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Studio;

/// <summary>
/// The scheduler loop behind <c>clrkernel-studio serve</c>. Every tick it rescans the
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
    private readonly ProjectRegistry _projects;
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
        ProjectRegistry projects, IRunStore store, JobExecutor executor, Notifier notifier,
        JobsOptions options, ILogger<SchedulerService> logger) {
        _projects = projects;
        _store = store;
        _notifier = notifier;
        _options = options;
        _logger = logger;
        _parallelism = new SemaphoreSlim(Math.Max(1, options.MaxParallelism));
        RunJob = (request, ct) => executor.ExecuteAsync(
            request.Job, request.Trigger, request.CausedByRunId, request.Attempt, request.RunId,
            request.HadOverrides, request.ActorId, request.ActorName, request.AtCommit, ct);
    }

    /// <summary>
    /// Fires a job now (the API's run button). Returns the pre-assigned run id, or
    /// null when the job already has an active run launched by this process.
    /// </summary>
    public Guid? TriggerManual(
        JobDefinition job, bool hadOverrides = false, Guid? actorId = null, string actorName = null) {
        if (_activeJobs.ContainsKey(KeyOf(job))) {
            return null;
        }
        var runId = Guid.NewGuid();
        Launch(
            new RunRequest(job, RunTrigger.Manual, null, runId) {
                HadOverrides = hadOverrides,
                ActorId = actorId,
                ActorName = actorName,
            },
            _stoppingToken);
        return runId;
    }

    /// <summary>
    /// Runs a job again on behalf of a recorded run. Returns the new run id, or null
    /// when that job already has one in flight.
    /// <para>
    /// Deliberately <see cref="RunTrigger.Manual"/> and not <c>Retry</c>: Retry is
    /// the automatic loop after a failure, counted by <c>Attempt</c>, and a person
    /// pressing a button is not that. What makes this a rerun rather than a fresh
    /// manual run is <c>CausedByRunId</c> — together with the actor and the commit,
    /// that is the whole audit record, which is why there is no table for it.
    /// </para>
    /// </summary>
    /// <param name="cleanup">Runs when the job finishes, however it finishes —
    /// removing the checkout an exact-version rerun was read out of.</param>
    public Guid? TriggerRerun(
        JobDefinition job, Guid originalRunId, Guid? actorId, string actorName,
        Action cleanup = null, string atCommit = null) {
        if (_activeJobs.ContainsKey(KeyOf(job))) {
            cleanup?.Invoke();
            return null;
        }
        var runId = Guid.NewGuid();
        Launch(
            new RunRequest(job, RunTrigger.Manual, originalRunId, runId) {
                ActorId = actorId,
                ActorName = actorName,
                Cleanup = cleanup,
                AtCommit = atCommit,
            },
            _stoppingToken);
        return runId;
    }

    /// <summary>Cancels a job's in-flight run (kills its kernel). False when none is ours.</summary>
    public bool TryCancel(string project, string environment, string jobName) {
        if (_activeJobs.TryGetValue(KeyOf(project, environment, jobName), out var cancellation)) {
            cancellation.Cancel();
            return true;
        }
        return false;
    }

    // The project is part of the key, not decoration: two projects are each allowed
    // a job called `nightly`, and a key without it would make one of them look
    // already-running and silently skip its occurrence.
    private static string KeyOf(JobDefinition job) => KeyOf(job.Project, job.Environment, job.Name);

    private static string KeyOf(string project, string environment, string jobName) =>
        $"{project}:{environment}:{jobName}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        _stoppingToken = stoppingToken;
        var orphans = await _store.MarkOrphansFailedAsync();
        if (orphans > 0) {
            _logger.LogWarning("Marked {Count} run(s) orphaned by a previous shutdown as Failed.", orphans);
        }

        var lastTick = DateTime.UtcNow;
        _logger.LogInformation(
            "Scheduler started: {Projects} project(s), tick {Tick}s, max parallelism {Parallelism}.",
            _projects.Projects.Count, TickInterval.TotalSeconds, _options.MaxParallelism);

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

    private DateTime _lastSweep = DateTime.MinValue;

    /// <summary>
    /// Daily housekeeping, from the loop that is already ticking rather than a
    /// service of its own: personal worktrees nobody has touched in a month, and
    /// only the ones holding nothing test does not already have.
    /// </summary>
    private void SweepWorktrees(DateTime now) {
        if (_options.WorktreeIdleDays <= 0 || now - _lastSweep < TimeSpan.FromDays(1)) {
            return;
        }
        _lastSweep = now;
        foreach (var project in _projects.Projects) {
            if (_projects.GitFor(project) is not { } git) {
                continue;
            }
            try {
                foreach (var user in git.PruneIdleUserWorktrees(
                             TimeSpan.FromDays(_options.WorktreeIdleDays), now)) {
                    _logger.LogInformation(
                        "Pruned idle worktree for {User} in {Project}.", user, project.Slug);
                }
            } catch (GitException e) {
                _logger.LogWarning("Could not sweep {Project}: {Error}", project.Slug, e.Message);
            }
        }
    }

    private DateTime _lastPurge = DateTime.MinValue;

    /// <summary>
    /// Retention, from the loop that is already ticking. Off unless somebody asked
    /// for it — see <see cref="JobsOptions.RunRetentionDays"/>.
    /// <para>
    /// The rows and the artifacts go together. A run history that forgot the row but
    /// kept the executed notebook would grow the disk anyway, and one that deleted
    /// the notebook but kept the row would leave every old run's Artifact tab
    /// answering 404 forever.
    /// </para>
    /// </summary>
    private async Task PurgeRunsAsync(DateTime now) {
        if (_options.RunRetentionDays <= 0 || now - _lastPurge < TimeSpan.FromDays(1)) {
            return;
        }
        _lastPurge = now;
        var before = now - TimeSpan.FromDays(_options.RunRetentionDays);
        try {
            var artifacts = await _store.PurgeRunsAsync(before);
            var removed = 0;
            foreach (var relative in artifacts) {
                // The store hands back the artifact file; what goes is the run's own
                // directory, which is where its log lives too.
                var directory = Path.GetDirectoryName(Path.Combine(_options.DataDir, relative));
                if (directory != null && Directory.Exists(directory)) {
                    Directory.Delete(directory, recursive: true);
                    removed++;
                }
            }
            if (artifacts.Count > 0) {
                _logger.LogInformation(
                    "Retention: removed {Runs} run(s) finished before {Before:u} and {Dirs} artifact folder(s).",
                    artifacts.Count, before, removed);
            }
        } catch (Exception e) {
            // Housekeeping must never stop the tick that runs the jobs.
            _logger.LogError(e, "Retention sweep failed.");
        }
    }

    /// <summary>One pass over the catalog for the (from, to] window.</summary>
    internal async Task TickAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) {
        SweepWorktrees(toUtc);
        await PurgeRunsAsync(toUtc);
        var catalog = _projects.LoadAll();
        foreach (var error in catalog.Errors) {
            _logger.LogWarning("Catalog: {Error}", error);
        }

        // Automatic triggers fire only where the scheduler owns execution: prod in
        // the git workflow, or the single default environment without it. Test runs
        // are always deliberate (manual / API).
        foreach (var job in catalog.Jobs.Where(j => j.Enabled && Schedules(j.Environment))) {
            if (_activeJobs.ContainsKey(KeyOf(job))) {
                continue;
            }

            if (job.Cron != null && IsDue(job.Cron, fromUtc, toUtc)) {
                if (await _store.HasActiveRunAsync(job.Project, job.Environment, job.Name)) {
                    _logger.LogWarning("{Job} is due but still has an active run; skipping this occurrence.", job.Name);
                    continue;
                }
                Launch(new RunRequest(job, RunTrigger.Schedule, null, null), cancellationToken);
                continue;
            }

            if (job.DependsOn.Count > 0) {
                var causedBy = await DependencyReadyAsync(job);
                if (causedBy != null
                    && !await _store.HasActiveRunAsync(job.Project, job.Environment, job.Name)) {
                    Launch(new RunRequest(job, RunTrigger.Dependency, causedBy.Id, null), cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Where the scheduler fires jobs by itself.
    /// <para>
    /// <c>prod</c> and <c>test</c>, and <c>default</c> where there is no git workflow
    /// and those two names do not exist. <b>Never a personal branch</b> — a branch
    /// nobody else can see must not be able to start anything on its own, and that
    /// is what makes one safe to work on. The catalog does not even scan them, so
    /// this is the second of two locks rather than the only one.
    /// </para>
    /// <para>
    /// test was added deliberately: a job that only ran when somebody pressed a
    /// button in test was not being tested the way it runs. Two consequences —
    /// test jobs take kernel slots from the same <c>MaxParallelism</c> as prod, and
    /// every <c>notify:</c> rule now fires from test as well, so notification
    /// volume roughly doubles.
    /// </para>
    /// </summary>
    internal static bool Schedules(string environment) =>
        environment is "prod" or GitService.TestBranch or "default";

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
        var lastTrigger = await _store.GetLastTriggerAsync(job.Project, job.Environment, job.Name)
            ?? DateTime.MinValue;
        Run newest = null;
        foreach (var dependency in job.DependsOn) {
            // Dependencies resolve within the same environment of the same project only.
            var success = await _store.GetLastSuccessfulRunAsync(job.Project, job.Environment, dependency);
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
                // After the retry loop and the notification, because both read the
                // notebook this is about to delete.
                request.Cleanup?.Invoke();
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
    /// <summary>Who pressed run. Null for everything the scheduler starts by itself.</summary>
    public Guid? ActorId { get; init; }
    public string ActorName { get; init; }
    /// <summary>Runs when the launch finishes, however it finishes. Temporary state
    /// the run needed — a checkout of an old commit — is torn down here.</summary>
    public Action Cleanup { get; init; }
    /// <summary>The commit the job's files were checked out at, when this is a rerun
    /// of a recorded version. Null means "whatever the branch is now".</summary>
    public string AtCommit { get; init; }
}

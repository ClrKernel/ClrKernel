using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>
/// Scheduler semantics: cron windows, the dependency freshness rule (fan-in fires
/// once, failure stops the chain, re-running to success resumes it), overlap skip,
/// and the retry loop — all with a scripted RunJob, no kernel processes.
/// </summary>
[TestClass]
public class SchedulerTest {
    private string _root;
    private EfRunStore _store;
    private SchedulerService _scheduler;
    private ConcurrentQueue<(string Job, RunTrigger Trigger, int Attempt)> _launched;
    private Func<JobDefinition, RunStatus> _outcome;

    [TestInitialize]
    public void Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-scheduler-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = EfRunStore.Sqlite(Path.Combine(_root, "test.db"));
        _store.Migrate();

        var options = new JobsOptions { DataDir = _root, NotebooksRoot = _root };
        var catalog = new JobCatalog(_root);
        var executor = new JobExecutor(_store, options, NullLogger.Instance);
        var notifier = new Notifier(options, NullLogger.Instance);
        _scheduler = new SchedulerService(
            catalog, _store, executor, notifier, options, NullLogger<SchedulerService>.Instance) {
            RetryDelay = TimeSpan.Zero,
        };

        _launched = new ConcurrentQueue<(string, RunTrigger, int)>();
        _outcome = _ => RunStatus.Succeeded;
        _scheduler.RunJob = async (request, ct) => {
            var job = request.Job;
            _launched.Enqueue((job.Name, request.Trigger, request.Attempt));
            var run = new Run {
                Id = request.RunId ?? Guid.NewGuid(),
                JobName = job.Name,
                NotebookPath = job.NotebookRelative ?? "nb.nb.md",
                Status = _outcome(job),
                Trigger = request.Trigger,
                CausedByRunId = request.CausedByRunId,
                Attempt = request.Attempt,
                CreatedAt = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow,
                FinishedAt = DateTime.UtcNow,
            };
            // Mirror the executor's contract: a run moves the job's trigger clock.
            await _store.SetLastTriggerAsync(job.Name, DateTime.UtcNow);
            return await _store.CreateRunAsync(run);
        };
    }

    [TestCleanup]
    public void Cleanup() {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private void WriteJobs(string yaml) {
        File.WriteAllText(Path.Combine(_root, "nb.nb.md"), "```csharp\n1+1\n```\n");
        File.WriteAllText(Path.Combine(_root, "test.jobs.yaml"), yaml);
    }

    private async Task TickAsync(DateTime from, DateTime to) {
        await _scheduler.TickAsync(from, to, CancellationToken.None);
        await _scheduler.DrainAsync();
    }

    private static readonly DateTime _t0 = new(2026, 1, 1, 1, 0, 30, DateTimeKind.Utc);

    [TestMethod]
    public void A_cron_is_due_only_when_an_occurrence_falls_inside_the_window() {
        Assert.IsTrue(SchedulerService.IsDue("* * * * *", _t0, _t0.AddSeconds(35)), "01:01:00 is inside");
        Assert.IsFalse(SchedulerService.IsDue("* * * * *", _t0, _t0.AddSeconds(10)), "next minute not reached");
        Assert.IsTrue(SchedulerService.IsDue("0 2 * * *", _t0, _t0.AddHours(1)), "02:00 daily inside a 1h window");
        Assert.IsFalse(SchedulerService.IsDue("0 3 * * *", _t0, _t0.AddHours(1)));
    }

    [TestMethod]
    public async Task A_due_cron_job_fires_and_an_active_run_skips_the_occurrence() {
        WriteJobs(
            """
            notebook: ./nb.nb.md
            jobs:
              - name: minutely
                cron: "* * * * *"
            """);

        await TickAsync(_t0, _t0.AddMinutes(1));
        Assert.AreEqual(("minutely", RunTrigger.Schedule, 1), _launched.Single());

        // Leave a Running row behind: the next occurrence must be skipped.
        await _store.CreateRunAsync(new Run {
            Id = Guid.NewGuid(),
            JobName = "minutely",
            NotebookPath = "nb.nb.md",
            Status = RunStatus.Running,
            Trigger = RunTrigger.Schedule,
            CreatedAt = DateTime.UtcNow,
        });
        _launched.Clear();
        await TickAsync(_t0.AddMinutes(1), _t0.AddMinutes(2));
        Assert.AreEqual(0, _launched.Count, "overlap policy is skip");
    }

    [TestMethod]
    public async Task Fan_in_fires_the_dependent_exactly_once_when_all_dependencies_are_fresh() {
        WriteJobs(
            """
            notebook: ./nb.nb.md
            jobs:
              - name: a1
              - name: a2
              - name: b
                dependsOn: [a1, a2]
            """);
        var window = (_t0.AddMinutes(-1), _t0);

        // Only a1 has succeeded: b stays quiet.
        await Succeed("a1");
        await TickAsync(window.Item1, window.Item2);
        Assert.AreEqual(0, _launched.Count);

        // a2 succeeds too: b fires exactly once, with lineage to the newest success.
        var a2 = await Succeed("a2");
        await TickAsync(window.Item1, window.Item2);
        var fired = _launched.Single();
        Assert.AreEqual(("b", RunTrigger.Dependency, 1), fired);
        var bRun = (await _store.QueryRunsAsync(new RunQuery { JobName = "b" })).Single();
        Assert.AreEqual(a2.Id, bRun.CausedByRunId);

        // Nothing new upstream: b does not fire again.
        _launched.Clear();
        await TickAsync(window.Item1, window.Item2);
        Assert.AreEqual(0, _launched.Count, "fan-in is single-fire");

        // Both succeed again (e.g. re-run after a fix): the chain resumes.
        await Succeed("a1");
        await Succeed("a2");
        await TickAsync(window.Item1, window.Item2);
        Assert.AreEqual(("b", RunTrigger.Dependency, 1), _launched.Single());
    }

    [TestMethod]
    public async Task A_failed_dependency_stops_the_chain() {
        WriteJobs(
            """
            notebook: ./nb.nb.md
            jobs:
              - name: up
                cron: "* * * * *"
              - name: down
                dependsOn: [up]
            """);
        _outcome = _ => RunStatus.Failed;

        await TickAsync(_t0, _t0.AddMinutes(1));
        Assert.IsTrue(_launched.All(l => l.Job == "up"), "down never fires off a failure");

        // The failure moved up's trigger clock but produced no success: still quiet.
        _launched.Clear();
        await TickAsync(_t0.AddMinutes(1), _t0.AddMinutes(1).AddSeconds(10));
        Assert.AreEqual(0, _launched.Count);
    }

    [TestMethod]
    public async Task A_failed_run_retries_up_to_the_configured_count() {
        WriteJobs(
            """
            notebook: ./nb.nb.md
            jobs:
              - name: flaky
                cron: "* * * * *"
                retryCount: 2
            """);
        _outcome = _ => RunStatus.Failed;

        await TickAsync(_t0, _t0.AddMinutes(1));
        var attempts = _launched.ToArray();
        Assert.AreEqual(3, attempts.Length, "first attempt + 2 retries");
        Assert.AreEqual(RunTrigger.Schedule, attempts[0].Trigger);
        Assert.AreEqual(RunTrigger.Retry, attempts[1].Trigger);
        Assert.AreEqual(2, attempts[1].Attempt);
        Assert.AreEqual(3, attempts[2].Attempt);
    }

    [TestMethod]
    public async Task A_disabled_job_never_fires() {
        WriteJobs(
            """
            notebook: ./nb.nb.md
            jobs:
              - name: off
                cron: "* * * * *"
                enabled: false
            """);
        await TickAsync(_t0, _t0.AddMinutes(1));
        Assert.AreEqual(0, _launched.Count);
    }

    [TestMethod]
    public async Task A_manual_trigger_returns_the_run_id_it_will_use() {
        WriteJobs("notebook: ./nb.nb.md\njobs: [{name: manual}]");
        var job = new JobCatalog(_root).Load().Find("manual");

        var runId = _scheduler.TriggerManual(job);
        Assert.IsNotNull(runId);
        await _scheduler.DrainAsync();

        Assert.AreEqual(("manual", RunTrigger.Manual, 1), _launched.Single());
        var run = await _store.GetRunAsync(runId.Value);
        Assert.IsNotNull(run, "the pre-assigned id is the id the run is stored under");
        Assert.AreEqual("manual", run.JobName);
    }

    [TestMethod]
    public async Task Cancelling_an_in_flight_run_signals_its_token() {
        WriteJobs("notebook: ./nb.nb.md\njobs: [{name: slow}]");
        var job = new JobCatalog(_root).Load().Find("slow");

        var started = new TaskCompletionSource();
        var observed = new TaskCompletionSource<bool>();
        _scheduler.RunJob = async (request, ct) => {
            started.SetResult();
            try {
                await Task.Delay(Timeout.Infinite, ct);
            } catch (OperationCanceledException) {
                observed.SetResult(true);
                throw;
            }
            return null;
        };

        _scheduler.TriggerManual(job);
        await started.Task;
        Assert.IsTrue(_scheduler.TryCancel("slow"));
        Assert.IsTrue(await observed.Task, "the run observes the cancellation");
        await _scheduler.DrainAsync();

        Assert.IsFalse(_scheduler.TryCancel("slow"), "nothing in flight once it has finished");
    }

    private async Task<Run> Succeed(string jobName) {
        return await _store.CreateRunAsync(new Run {
            Id = Guid.NewGuid(),
            JobName = jobName,
            NotebookPath = "nb.nb.md",
            Status = RunStatus.Succeeded,
            Trigger = RunTrigger.Manual,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow,
        });
    }
}

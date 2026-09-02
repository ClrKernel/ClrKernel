using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// What "run that again" resolves to, against a real git workspace and store.
/// <para>
/// The decision that matters here is <em>which version</em>. At branch HEAD is what
/// you want after a fix; at the recorded commit is for reproducing a failure. The
/// refusals are the point of the second one — a run labelled "the exact failed
/// version" that is nothing of the kind is worse than no button at all.
/// </para>
/// </summary>
[TestClass]
public class RerunTest {
    private string _dir;
    private GitService _git;
    private ProjectRegistry _projects;
    private JobCatalog _catalog;
    private EfRunStore _store;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-rerun-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _projects = new ProjectRegistry(
            new JobsOptions { DataDir = _dir, NotebooksRoot = _dir, GitEnabled = true },
            NullLoggerFactory.Instance);
        _git = _projects.GitFor(_projects.Default);
        _git.Init();
        _catalog = _projects.CatalogFor(_projects.Default);
        _store = EfRunStore.Sqlite(Path.Combine(_dir, "test.db"));
        _store.Migrate();
    }

    [TestCleanup]
    public void Cleanup() {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TempDirectory.Delete(_dir);
    }

    private void CommitTest(string relative, string content, string message = "edit") =>
        _git.WithLock(() => {
            var path = Path.Combine(_git.TestPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            _git.Commit("test", message, relative);
        });

    private void SeedJob(string name = "etl", string cell = "1+1") {
        CommitTest("etl.nb.md", $"```csharp\n{cell}\n```\n");
        CommitTest("etl.jobs.yaml", $"jobs:\n  - name: {name}\n");
    }

    private async Task<Run> RecordAsync(
        string job = "etl", string sha = null, bool dirty = false, bool overrides = false,
        RunStatus status = RunStatus.Failed, string environment = "test",
        string notebook = "etl.nb.md", DateTime? at = null) =>
        await _store.CreateRunAsync(new Run {
            Id = Guid.NewGuid(),
            Project = ProjectRegistry.DefaultSlug,
            Environment = environment,
            JobName = job,
            NotebookPath = notebook,
            Status = status,
            Trigger = RunTrigger.Schedule,
            CreatedAt = at ?? DateTime.UtcNow,
            StartedAt = at ?? DateTime.UtcNow,
            FinishedAt = at ?? DateTime.UtcNow,
            CommitSha = sha ?? _git.HeadSha("test"),
            WasDirty = dirty,
            HadOverrides = overrides,
        });

    private Task<RerunPlan> PlanAsync(IEnumerable<Run> runs, bool exact = false) =>
        Rerun.PlanAsync(runs.ToList(), exact, _catalog, _git, _store);

    // --- the default: the branch as it is now --------------------------------

    [TestMethod]
    public async Task A_rerun_runs_the_job_as_it_is_on_the_branch_now() {
        SeedJob();
        var failed = await RecordAsync();
        // The fix: the notebook changed after the run that failed.
        CommitTest("etl.nb.md", "```csharp\n2+2\n```\n", "fix it");

        var plan = await PlanAsync(new[] { failed });

        Assert.AreEqual(0, plan.Refused.Count, string.Join("; ", plan.Refused.Select(r => r.Reason)));
        var target = plan.Targets.Single();
        Assert.AreEqual(failed.Id, target.OriginalRunId);
        Assert.AreEqual(_git.HeadSha("test"), target.Sha, "HEAD, not the sha that failed");
        Assert.IsNull(target.WorktreePath, "no checkout is needed to run what is already there");
        StringAssert.Contains(File.ReadAllText(target.Job.NotebookPath), "2+2", "it runs the fix");
    }

    [TestMethod]
    public async Task A_job_that_no_longer_exists_says_which_rerun_to_use_instead() {
        SeedJob();
        var failed = await RecordAsync();
        _git.WithLock(() => {
            File.Delete(Path.Combine(_git.TestPath, "etl.jobs.yaml"));
            _git.Commit("test", "unschedule it", "etl.jobs.yaml");
        });

        var plan = await PlanAsync(new[] { failed });

        Assert.AreEqual(0, plan.Targets.Count);
        StringAssert.Contains(plan.Refused.Single().Reason, "exact recorded version");
    }

    // --- the exact version ---------------------------------------------------

    [TestMethod]
    public async Task Rerunning_the_exact_version_reads_the_notebook_out_of_that_commit() {
        SeedJob(cell: "1+1");
        var failed = await RecordAsync();
        CommitTest("etl.nb.md", "```csharp\n2+2\n```\n", "fix it");

        var plan = await PlanAsync(new[] { failed }, exact: true);

        var target = plan.Targets.Single();
        Assert.AreEqual(failed.CommitSha, target.Sha);
        Assert.IsNotNull(target.WorktreePath);
        StringAssert.Contains(File.ReadAllText(target.Job.NotebookPath), "1+1",
            "the version that failed, not the fix");
        StringAssert.StartsWith(
            Path.GetFileName(target.WorktreePath), GitService.RerunWorktreePrefix);

        // The checkout sits beside the worktrees, never inside one — the catalog
        // scans test/ and prod/, and a job found in a copy of the past would be a
        // job the scheduler could fire.
        Assert.IsFalse(
            target.WorktreePath.StartsWith(_catalog.RootFor("test"), StringComparison.Ordinal));
        Assert.IsFalse(
            _catalog.Load().Jobs.Any(j => j.SourceFile.StartsWith(target.WorktreePath, StringComparison.Ordinal)),
            "and the catalog does not see it");

        _git.RemoveRerunWorktree(target.WorktreePath);
        Assert.IsFalse(Directory.Exists(target.WorktreePath));
    }

    /// <summary>
    /// Each of these would produce a run labelled "the exact failed version" that is
    /// not one. The label is the whole value of the feature, so they are refused with
    /// the reason rather than quietly approximated.
    /// </summary>
    [TestMethod]
    public async Task An_exact_rerun_that_would_not_be_exact_is_refused_and_says_why() {
        SeedJob();

        foreach (var (run, expected) in new[] {
            (await RecordAsync(dirty: true), "uncommitted changes"),
            (await RecordAsync(overrides: true), "overrides"),
            (await RecordAsync(sha: ""), "no commit was recorded"),
        }) {
            var plan = await PlanAsync(new[] { run }, exact: true);
            Assert.AreEqual(0, plan.Targets.Count, expected);
            StringAssert.Contains(plan.Refused.Single().Reason, expected);
        }

        // And a clean one is not refused, or the assertions above prove nothing.
        Assert.AreEqual(1, (await PlanAsync(new[] { await RecordAsync() }, exact: true)).Targets.Count);
    }

    [TestMethod]
    public async Task The_exact_version_is_one_run_at_a_time() {
        SeedJob();
        CommitTest("other.nb.md", "```csharp\n1\n```\n");
        CommitTest("other.jobs.yaml", "jobs:\n  - name: other\n");

        var plan = await PlanAsync(
            new[] { await RecordAsync(), await RecordAsync("other", notebook: "other.nb.md") },
            exact: true);

        StringAssert.Contains(plan.Error, "one run at a time");
        Assert.AreEqual(0, plan.Targets.Count);
    }

    /// <summary>
    /// The run a checkout of the past produces records that commit, not the branch's.
    /// <para>
    /// This is not bookkeeping. The promotion gate reads the latest test run's sha
    /// and asks whether the files have changed since; a rerun of an old commit that
    /// stamped itself HEAD would be evidence for a tree it never executed, which is
    /// the exact hole the gate exists to close.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task A_run_of_an_old_commit_records_that_commit_and_proves_nothing_about_head() {
        SeedJob(cell: "1+1");
        var oldSha = _git.HeadSha("test");
        CommitTest("etl.nb.md", "```csharp\n2+2\n```\n", "fix it");

        var plan = await PlanAsync(new[] { await RecordAsync(sha: oldSha) }, exact: true);
        var target = plan.Targets.Single();

        var executor = new JobExecutor(_store, new JobsOptions {
            DataDir = _dir,
            NotebooksRoot = _dir,
            GitEnabled = true,
        }, NullLogger.Instance, _projects);
        // No clrkernel binary here, so it fails — but the run row is written before
        // the kernel is ever reached, and its provenance is what is under test.
        var run = await executor.ExecuteAsync(
            target.Job, RunTrigger.Manual, target.OriginalRunId, atCommit: target.Sha);
        _git.RemoveRerunWorktree(target.WorktreePath);

        Assert.AreEqual(oldSha, run.CommitSha, "the commit it ran, not the branch's HEAD");
        Assert.AreNotEqual(_git.HeadSha("test"), run.CommitSha);
        Assert.IsFalse(run.WasDirty, "a checkout of one commit has nothing uncommitted in it");
        Assert.AreEqual(target.OriginalRunId, run.CausedByRunId);

        // And the gate reads it the way it reads any other run: the notebook has
        // moved on since that commit, so this run does not prove HEAD works.
        run.Status = RunStatus.Succeeded;
        await _store.UpdateRunAsync(run);
        var eligibility = await Promotion.CheckAsync(
            _projects.Default, _projects, _store, "etl.nb.md");
        Assert.IsFalse(eligibility.Eligible,
            "a green run of the previous version is not evidence for this one");
        Assert.IsTrue(eligibility.Reasons.Any(r => r.Contains("changed", StringComparison.OrdinalIgnoreCase)),
            string.Join("; ", eligibility.Reasons));
    }

    /// <summary>
    /// A checkout the process died on is cleaned up next time it starts.
    /// <para>
    /// The directory is the thing that has to go. <c>git worktree prune</c> only
    /// forgets worktrees whose folder is already missing, so a crash leaves a full
    /// checkout on disk that prune will never touch — one per crashed rerun, until
    /// somebody notices the disk.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task A_checkout_left_behind_by_a_crash_is_swept_on_the_next_start() {
        SeedJob();
        var plan = await PlanAsync(new[] { await RecordAsync() }, exact: true);
        var abandoned = plan.Targets.Single().WorktreePath;
        Assert.IsTrue(Directory.Exists(abandoned), "the run started and then, say, the host died");

        // What startup does. Repair is where the other worktree fixes already live.
        _git.Repair();

        Assert.IsFalse(Directory.Exists(abandoned), "the tree is gone, not merely unregistered");
        Assert.IsFalse(_git.LayoutExists == false, "and the real worktrees are untouched");
        Assert.IsTrue(Directory.Exists(_git.TestPath) && Directory.Exists(_git.ProdPath));
    }

    // --- batches -------------------------------------------------------------

    [TestMethod]
    public async Task The_same_job_selected_several_times_is_started_once() {
        SeedJob();
        var older = await RecordAsync(at: DateTime.UtcNow.AddHours(-2));
        var newer = await RecordAsync(at: DateTime.UtcNow);

        var plan = await PlanAsync(new[] { older, newer });

        Assert.AreEqual(1, plan.Targets.Count, "one job, one run — not one start and one refusal");
        Assert.AreEqual(newer.Id, plan.Targets[0].OriginalRunId, "the most recent failure is the one it repeats");
        Assert.AreEqual(0, plan.Refused.Count, "and the count the confirmation shows is the truthful one");
    }

    [TestMethod]
    public async Task A_selection_spanning_two_branches_is_refused_rather_than_guessed() {
        SeedJob();
        var plan = await PlanAsync(new[] {
            await RecordAsync(environment: "test"),
            await RecordAsync(environment: "prod"),
        });

        StringAssert.Contains(plan.Error, "one project and one branch");
        Assert.AreEqual(0, plan.Targets.Count);
    }

    [TestMethod]
    public async Task A_job_already_running_is_left_alone_and_named() {
        SeedJob();
        var failed = await RecordAsync();
        await RecordAsync(status: RunStatus.Running);

        var plan = await PlanAsync(new[] { failed });

        Assert.AreEqual(0, plan.Targets.Count);
        StringAssert.Contains(plan.Refused.Single().Reason, "already has a run in flight");
    }
}

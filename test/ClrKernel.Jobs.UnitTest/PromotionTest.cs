using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>
/// The promotion eligibility matrix and apply path, against a real git workspace and
/// store — this is the trust gate between "ran in test" and "runs in prod on a
/// schedule", so every hole the design review found gets a test.
/// </summary>
[TestClass]
public class PromotionTest {
    private string _dir;
    private GitService _git;
    private JobCatalog _catalog;
    private EfRunStore _store;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-promo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _git = new GitService(_dir, NullLogger.Instance);
        _git.Init();
        _catalog = new JobCatalog(_dir, gitLayout: true, _git);
        _store = EfRunStore.Sqlite(Path.Combine(_dir, "test.db"));
        _store.Migrate();
    }

    [TestCleanup]
    public void Cleanup() {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private void CommitDev(string relative, string content, string message = "edit") {
        _git.WithLock(() => {
            var path = Path.Combine(_git.TestPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            _git.Commit("test", message, relative);
        });
    }

    private void SeedNotebookAndJob(string cron = null) {
        CommitDev("etl.nb.md", "```csharp\n1+1\n```\n");
        CommitDev("etl.jobs.yaml",
            $"notebook: ./etl.nb.md\njobs:\n  - name: etl\n{(cron != null ? $"    cron: \"{cron}\"\n" : "")}");
    }

    private async Task<Run> RecordRunAsync(
        string job = "etl", RunStatus status = RunStatus.Succeeded,
        bool dirty = false, bool overrides = false, string sha = null) {
        return await _store.CreateRunAsync(new Run {
            Id = Guid.NewGuid(),
            Environment = "test",
            JobName = job,
            NotebookPath = "etl.nb.md",
            Status = status,
            Trigger = RunTrigger.Manual,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow,
            CommitSha = sha ?? _git.HeadSha("test"),
            WasDirty = dirty,
            HadOverrides = overrides,
        });
    }

    private Task<PromotionEligibility> CheckAsync() =>
        Promotion.CheckAsync(_catalog, _git, _store, "etl.nb.md");

    [TestMethod]
    public async Task A_never_run_job_is_not_promotable() {
        SeedNotebookAndJob();
        var result = await CheckAsync();
        Assert.IsFalse(result.Eligible);
        StringAssert.Contains(result.Reasons.Single(), "never run");
    }

    [TestMethod]
    public async Task A_red_run_is_not_promotable() {
        SeedNotebookAndJob();
        await RecordRunAsync(status: RunStatus.Failed);
        var result = await CheckAsync();
        StringAssert.Contains(result.Reasons.Single(), "Failed, not Succeeded");
    }

    [TestMethod]
    public async Task A_run_with_overrides_or_a_dirty_tree_is_not_promotable() {
        SeedNotebookAndJob();
        await RecordRunAsync(overrides: true);
        StringAssert.Contains((await CheckAsync()).Reasons.Single(), "overrides");

        await RecordRunAsync(dirty: true);
        StringAssert.Contains((await CheckAsync()).Reasons.Single(), "uncommitted");
    }

    [TestMethod]
    public async Task Edits_after_the_green_run_invalidate_it() {
        SeedNotebookAndJob();
        await RecordRunAsync();
        CommitDev("etl.nb.md", "```csharp\n2+2\n```\n", "tweak after the run");

        var result = await CheckAsync();
        StringAssert.Contains(result.Reasons.Single(), "changed since its green run");
    }

    [TestMethod]
    public async Task A_green_clean_run_promotes_and_prod_sees_the_files() {
        SeedNotebookAndJob(cron: "0 2 * * *");
        var run = await RecordRunAsync();

        var eligibility = await CheckAsync();
        Assert.IsTrue(eligibility.Eligible, string.Join("; ", eligibility.Reasons));
        Assert.AreEqual(run.Id, eligibility.EvidenceRuns["etl"]);

        var sha = Promotion.Apply(_git, eligibility, "etl.nb.md");
        Assert.IsNotNull(sha);
        Assert.IsTrue(File.Exists(Path.Combine(_git.ProdPath, "etl.nb.md")));
        Assert.IsTrue(File.Exists(Path.Combine(_git.ProdPath, "etl.jobs.yaml")));

        // The prod catalog now schedules it; and re-promoting has nothing to carry.
        var catalog = _catalog.Load();
        Assert.IsNotNull(catalog.Find("prod", "etl"));
        Assert.AreEqual("0 2 * * *", catalog.Find("prod", "etl").Cron);
        var again = await CheckAsync();
        Assert.IsFalse(again.Eligible);
        StringAssert.Contains(again.Reasons.Single(), "identical");
    }

    [TestMethod]
    public async Task An_unpromoted_dependency_blocks_the_dependent() {
        CommitDev("a.nb.md", "```csharp\n1\n```\n");
        CommitDev("a.jobs.yaml", "notebook: ./a.nb.md\njobs: [{name: a}]");
        CommitDev("b.nb.md", "```csharp\n2\n```\n");
        CommitDev("b.jobs.yaml", "notebook: ./b.nb.md\njobs: [{name: b, dependsOn: [a]}]");
        await RecordRunAsync(job: "b");

        var result = await Promotion.CheckAsync(_catalog, _git, _store, "b.nb.md");
        Assert.IsFalse(result.Eligible);
        Assert.IsTrue(result.Reasons.Any(r => r.Contains("would break prod") && r.Contains("'a'")),
            string.Join("; ", result.Reasons));
    }

    [TestMethod]
    public async Task Deleting_a_notebook_in_dev_promotes_the_deletion() {
        SeedNotebookAndJob();
        await RecordRunAsync();
        Promotion.Apply(_git, await CheckAsync(), "etl.nb.md");
        Assert.IsNotNull(_catalog.Load().Find("prod", "etl"));

        // Delete in test, commit; the promotion carries the removal.
        _git.WithLock(() => {
            File.Delete(Path.Combine(_git.TestPath, "etl.nb.md"));
            File.Delete(Path.Combine(_git.TestPath, "etl.jobs.yaml"));
            _git.Commit("test", "remove etl", "etl.nb.md", "etl.jobs.yaml");
        });

        var eligibility = await CheckAsync();
        Assert.IsTrue(eligibility.IsDeletion);
        Assert.IsTrue(eligibility.Eligible, string.Join("; ", eligibility.Reasons));

        Promotion.Apply(_git, eligibility, "etl.nb.md");
        Assert.IsFalse(File.Exists(Path.Combine(_git.ProdPath, "etl.nb.md")));
        Assert.IsNull(_catalog.Load().Find("prod", "etl"), "prod no longer schedules it");
    }

    [TestMethod]
    public async Task Sibling_jobs_must_all_be_green() {
        CommitDev("multi.nb.md", "```csharp\n1\n```\n");
        CommitDev("multi.jobs.yaml",
            "notebook: ./multi.nb.md\njobs:\n  - name: us\n  - name: eu\n");
        // Only one of the two siblings has a green run.
        await _store.CreateRunAsync(new Run {
            Id = Guid.NewGuid(),
            Environment = "test",
            JobName = "us",
            NotebookPath = "multi.nb.md",
            Status = RunStatus.Succeeded,
            Trigger = RunTrigger.Manual,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow,
            CommitSha = _git.HeadSha("test"),
        });

        var result = await Promotion.CheckAsync(_catalog, _git, _store, "multi.nb.md");
        Assert.IsFalse(result.Eligible);
        StringAssert.Contains(result.Reasons.Single(), "'eu'");
    }

    [TestMethod]
    public async Task An_interactive_run_changes_nothing_about_promotability() {
        // The guarantee behind the web editor's run buttons: a session executes
        // cells against a warm kernel and writes NO run rows, so iterating in the
        // browser can never manufacture the green evidence promotion requires.
        SeedNotebookAndJob();
        var before = await CheckAsync();
        Assert.IsFalse(before.Eligible);

        var session = new NotebookSession("s", Path.Combine(_git.TestPath, "etl.nb.md"), "/nonexistent/clrkernel");
        session.TryStartRun(
            new[] { ClrKernel.Core.Runner.MarkdownCell.Code("csharp", "1+1") }, new[] { "c0" }, out var completion);
        await completion;
        session.Dispose();

        var after = await CheckAsync();
        Assert.AreEqual(before.Eligible, after.Eligible);
        CollectionAssert.AreEqual(before.Reasons.ToList(), after.Reasons.ToList(),
            "an interactive run must be invisible to the promotion gate");
        Assert.AreEqual(0, (await _store.QueryRunsAsync(new RunQuery { Environment = "test" })).Count,
            "and must leave no run history at all");
    }
}

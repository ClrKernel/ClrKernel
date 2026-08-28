using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using ClrKernel.Core.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// The promotion eligibility matrix and apply path, against a real git workspace and
/// store — this is the trust gate between "ran in test" and "runs in prod on a
/// schedule", so every hole the design review found gets a test.
/// </summary>
[TestClass]
public class PromotionTest {
    private string _dir;
    private GitService _git;
    private ProjectRegistry _projects;
    private JobCatalog _catalog;
    private EfRunStore _store;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-promo-test-" + Guid.NewGuid().ToString("N"));
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
            Project = ProjectRegistry.DefaultSlug,
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
        Promotion.CheckAsync(_projects.Default, _projects, _store, "etl.nb.md");

    /// <summary>A language whose directives are shaped the way the SQL one's are,
    /// built here so the check is proven to run off what the kernel reports rather
    /// than off anything this project knows about SQL.</summary>
    private static readonly LanguageDescriptor _sql = new() {
        Id = "sql",
        Selectors = new[] { "#!sql", "#!sql-connect" },
        LanguageTags = new[] { "sql" },
        Directives = new[] {
            new DirectiveDefinition {
                Selector = "#!sql-connect",
                Parameters = new[] {
                    new DirectiveParameter { Name = "--name", Required = true },
                    new DirectiveParameter { Name = "--server" },
                },
            },
        },
    };

    private ConnectionStore StoreWith(string name, ConnectionScope scope) {
        var connections = new ConnectionStore(
            new JobsOptions { DataDir = _dir, NotebooksRoot = _dir },
            SecretStore.ForProviders(new InMemorySecretProvider()),
            NullLogger<ConnectionStore>.Instance);
        connections.Save(
            new StoredConnection {
                Name = name,
                Scope = scope,
                OwnerId = scope == ConnectionScope.Private ? Guid.NewGuid() : null,
                Type = "SqlServer",
                Settings = new Dictionary<string, string> { ["server"] = "dw" },
            },
            password: null, readOnlyPassword: null);
        return connections;
    }

    private Task<PromotionEligibility> CheckAsync(ConnectionStore connections) =>
        Promotion.CheckAsync(
            _projects.Default, _projects, _store, "etl.nb.md", connections, new[] { _sql },
            new[] { ClrKernel.Database.Provider.SqlServer.SqlServerConnectionProvider.Descriptor });

    [TestMethod]
    public async Task A_notebook_using_a_private_connection_is_not_promotable() {
        CommitDev("etl.nb.md", "```sql\n#!sql-connect --name scratch\nSELECT 1\n```\n");
        CommitDev("etl.jobs.yaml", "notebook: ./etl.nb.md\njobs:\n  - name: etl\n");
        await RecordRunAsync();

        var result = await CheckAsync(StoreWith("scratch", ConnectionScope.Private));
        Assert.IsFalse(result.Eligible);
        StringAssert.Contains(
            result.Reasons.Single(r => r.Contains("scratch")),
            "resolve only for the person who owns them");
    }

    [TestMethod]
    public async Task A_notebook_using_a_shared_connection_is_fine() {
        CommitDev("etl.nb.md", "```sql\n#!sql-connect --name warehouse\nSELECT 1\n```\n");
        CommitDev("etl.jobs.yaml", "notebook: ./etl.nb.md\njobs:\n  - name: etl\n");
        await RecordRunAsync();

        var result = await CheckAsync(StoreWith("warehouse", ConnectionScope.Shared));
        Assert.IsFalse(result.Reasons.Any(r => r.Contains("private connection")), string.Join(" | ", result.Reasons));
    }

    [TestMethod]
    public async Task A_notebook_defining_its_own_connection_inline_is_not_blocked() {
        // It carries its own settings, so it is not asking for anybody's saved entry
        // — blocking it because the name collides would refuse work that is fine.
        CommitDev("etl.nb.md",
            "```sql\n#!sql-connect --name scratch --server dw.db.local\nSELECT 1\n```\n");
        CommitDev("etl.jobs.yaml", "notebook: ./etl.nb.md\njobs:\n  - name: etl\n");
        await RecordRunAsync();

        var result = await CheckAsync(StoreWith("scratch", ConnectionScope.Private));
        Assert.IsFalse(result.Reasons.Any(r => r.Contains("private connection")), string.Join(" | ", result.Reasons));
    }

    [TestMethod]
    public async Task Without_the_providers_no_directive_is_classified_and_nothing_is_blocked() {
        // The caller could not say which providers exist, so a connect directive
        // cannot be told from a definition — and the check fails toward letting the
        // promotion through rather than refusing one that was never in question.
        CommitDev("etl.nb.md", "```sql\n#!sql-connect --name scratch\nSELECT 1\n```\n");
        CommitDev("etl.jobs.yaml", "notebook: ./etl.nb.md\njobs:\n  - name: etl\n");
        await RecordRunAsync();

        var result = await Promotion.CheckAsync(
            _projects.Default, _projects, _store, "etl.nb.md",
            StoreWith("scratch", ConnectionScope.Private), new[] { _sql });
        Assert.IsFalse(result.Reasons.Any(r => r.Contains("private connection")),
            string.Join(" | ", result.Reasons));
    }

    [TestMethod]
    public async Task The_check_does_not_ask_when_it_has_no_connections_to_ask_about() {
        CommitDev("etl.nb.md", "```sql\n#!sql-connect --name scratch\nSELECT 1\n```\n");
        CommitDev("etl.jobs.yaml", "notebook: ./etl.nb.md\njobs:\n  - name: etl\n");
        await RecordRunAsync();

        var result = await CheckAsync();
        Assert.IsFalse(result.Reasons.Any(r => r.Contains("private connection")));
    }

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
        Assert.IsNotNull(catalog.Find("default", "prod", "etl"));
        Assert.AreEqual("0 2 * * *", catalog.Find("default", "prod", "etl").Cron);
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

        var result = await Promotion.CheckAsync(_projects.Default, _projects, _store, "b.nb.md");
        Assert.IsFalse(result.Eligible);
        Assert.IsTrue(result.Reasons.Any(r => r.Contains("would break prod") && r.Contains("'a'")),
            string.Join("; ", result.Reasons));
    }

    [TestMethod]
    public async Task Deleting_a_notebook_in_dev_promotes_the_deletion() {
        SeedNotebookAndJob();
        await RecordRunAsync();
        Promotion.Apply(_git, await CheckAsync(), "etl.nb.md");
        Assert.IsNotNull(_catalog.Load().Find("default", "prod", "etl"));

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
        Assert.IsNull(_catalog.Load().Find("default", "prod", "etl"), "prod no longer schedules it");
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

        var result = await Promotion.CheckAsync(_projects.Default, _projects, _store, "multi.nb.md");
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
        Assert.AreEqual(0, (await _store.QueryRunsAsync(new RunQuery { Projects = new[] { "default" }, Environment = "test" })).Count,
            "and must leave no run history at all");
    }

    private Task<PromotionEligibility> Check(string path = "etl.nb.md") =>
        Promotion.CheckAsync(_projects.Default, _projects, _store, path);

    /// <summary>
    /// Either half resolves the same pair, so it does not matter which one the
    /// Promote button was pressed on.
    /// </summary>
    [TestMethod]
    public async Task Promoting_from_the_jobs_file_is_promoting_the_pair() {
        SeedNotebookAndJob("0 2 * * *");
        await RecordRunAsync();

        var fromNotebook = await Check("etl.nb.md");
        var fromYaml = await Check("etl.jobs.yaml");

        CollectionAssert.AreEquivalent(fromNotebook.Paths, fromYaml.Paths);
        CollectionAssert.AreEquivalent(new[] { "etl.nb.md", "etl.jobs.yaml" }, fromYaml.Paths);
        Assert.AreEqual(fromNotebook.Eligible, fromYaml.Eligible);
        Assert.IsTrue(fromYaml.Eligible, string.Join("; ", fromYaml.Reasons));
    }

    /// <summary>
    /// A schedule change needs the file to be sound, not a fresh run. Re-running a
    /// notebook to prove a cron is valid proves nothing about the cron.
    /// </summary>
    [TestMethod]
    public async Task Changing_only_the_schedule_needs_no_new_run() {
        SeedNotebookAndJob("0 2 * * *");
        await RecordRunAsync();
        Promotion.Apply(_git, await Check(), "etl.nb.md");

        // The green run is now stale by sha, but nothing about the notebook moved.
        CommitDev("etl.jobs.yaml", "jobs:\n  - name: etl\n    cron: \"0 5 * * *\"\n");

        var result = await Check();
        Assert.IsTrue(result.Eligible, string.Join("; ", result.Reasons));
        Assert.AreEqual(0, result.EvidenceRuns.Count, "no run was needed, so none is cited");
    }

    [TestMethod]
    public async Task A_schedule_change_that_is_not_sound_is_refused() {
        SeedNotebookAndJob("0 2 * * *");
        await RecordRunAsync();
        Promotion.Apply(_git, await Check(), "etl.nb.md");

        CommitDev("etl.jobs.yaml", "jobs:\n  - name: etl\n    cron: \"every tuesday\"\n");
        var result = await Check();
        Assert.IsFalse(result.Eligible);
        Assert.IsTrue(result.Reasons.Any(r => r.Contains("not a schedule")),
            string.Join("; ", result.Reasons));
    }

    /// <summary>
    /// Parameters are inputs to the notebook, so changing them changes what runs
    /// even though only the yaml moved — the same reason the gate refuses a run
    /// that used ad-hoc overrides.
    /// </summary>
    [TestMethod]
    public async Task Changing_parameters_still_needs_a_green_run() {
        CommitDev("etl.nb.md", "```csharp\n1+1\n```\n");
        CommitDev("etl.jobs.yaml", "jobs:\n  - name: etl\n    parameters: {region: us}\n");
        await RecordRunAsync();
        Promotion.Apply(_git, await Check(), "etl.nb.md");

        CommitDev("etl.jobs.yaml", "jobs:\n  - name: etl\n    parameters: {region: eu}\n");
        var result = await Check();
        Assert.IsFalse(result.Eligible, "prod would run inputs nothing has ever tried");
        Assert.IsTrue(result.Reasons.Any(r => r.Contains("changed since its green run")),
            string.Join("; ", result.Reasons));
    }

    [TestMethod]
    public async Task Changing_the_notebook_still_needs_a_green_run() {
        SeedNotebookAndJob("0 2 * * *");
        await RecordRunAsync();
        Promotion.Apply(_git, await Check(), "etl.nb.md");

        CommitDev("etl.nb.md", "```csharp\n2+2\n```\n");
        var result = await Check();
        Assert.IsFalse(result.Eligible);
        Assert.IsTrue(result.Reasons.Any(r => r.Contains("changed since its green run")),
            string.Join("; ", result.Reasons));
    }

    /// <summary>
    /// Deleting the jobs file unschedules the notebook and leaves it runnable by
    /// hand. It was refused outright before — isDeletion keyed on the notebook
    /// being gone, so this landed on "No jobs are defined for this notebook in
    /// test" and stayed there forever.
    /// </summary>
    [TestMethod]
    public async Task Deleting_the_jobs_file_unschedules_and_names_what_it_switches_off() {
        SeedNotebookAndJob("0 2 * * *");
        await RecordRunAsync();
        Promotion.Apply(_git, await Check(), "etl.nb.md");

        _git.WithLock(() => {
            File.Delete(Path.Combine(_git.TestPath, "etl.jobs.yaml"));
            _git.Commit("test", "unschedule", "etl.jobs.yaml");
        });

        var result = await Check("etl.jobs.yaml");
        Assert.IsTrue(result.Eligible, string.Join("; ", result.Reasons));
        Assert.IsTrue(result.IsDeletion);
        Assert.AreEqual(1, result.Unscheduling.Count);
        Assert.AreEqual("etl", result.Unscheduling[0].Name);
        Assert.AreEqual("0 2 * * *", result.Unscheduling[0].Cron);
        Assert.IsNotNull(result.Unscheduling[0].NextRun, "the confirmation says when it would have fired");

        Promotion.Apply(_git, result, "etl.jobs.yaml");
        Assert.IsFalse(File.Exists(Path.Combine(_git.ProdPath, "etl.jobs.yaml")), "the schedule is gone");
        Assert.IsTrue(File.Exists(Path.Combine(_git.ProdPath, "etl.nb.md")),
            "and the notebook stays, runnable by hand");
    }

    [TestMethod]
    public async Task Deleting_the_jobs_file_is_refused_while_a_prod_run_is_in_flight() {
        // Promotion rewrites the prod worktree, and doing that underneath a running
        // job is how a run finishes against files it did not start with.
        SeedNotebookAndJob("0 2 * * *");
        await RecordRunAsync();
        Promotion.Apply(_git, await Check(), "etl.nb.md");
        _git.WithLock(() => {
            File.Delete(Path.Combine(_git.TestPath, "etl.jobs.yaml"));
            _git.Commit("test", "unschedule", "etl.jobs.yaml");
        });
        await _store.CreateRunAsync(new Run {
            Id = Guid.NewGuid(),
            Project = ProjectRegistry.DefaultSlug,
            Environment = "prod",
            JobName = "etl",
            NotebookPath = "etl.nb.md",
            Status = RunStatus.Running,
            Trigger = RunTrigger.Schedule,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
        });

        var result = await Check("etl.jobs.yaml");
        Assert.IsFalse(result.Eligible);
        Assert.IsTrue(result.Reasons.Any(r => r.Contains("in flight")), string.Join("; ", result.Reasons));
    }

    /// <summary>
    /// The state the pairing rule exists to prevent: prod holding a schedule whose
    /// notebook is not there.
    /// </summary>
    [TestMethod]
    public async Task A_jobs_file_whose_notebook_is_gone_is_refused() {
        SeedNotebookAndJob("0 2 * * *");
        await RecordRunAsync();
        Promotion.Apply(_git, await Check(), "etl.nb.md");

        _git.WithLock(() => {
            File.Delete(Path.Combine(_git.TestPath, "etl.nb.md"));
            _git.Commit("test", "remove the notebook only", "etl.nb.md");
        });

        var result = await Check("etl.jobs.yaml");
        Assert.IsFalse(result.Eligible);
        Assert.IsTrue(result.Reasons.Any(r => r.Contains("no notebook in test")),
            string.Join("; ", result.Reasons));
    }

}

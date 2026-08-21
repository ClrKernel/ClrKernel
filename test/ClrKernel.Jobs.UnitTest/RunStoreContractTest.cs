using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.UnitTest;   // LiveTestGate, shared with the database test project
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>
/// One suite, every backend: the scheduler and API depend on these behaviours
/// whichever store is configured, so a new backend must satisfy the same contract.
/// <para>
/// sqlite and files run everywhere. PostgreSQL and SQL Server run against a real
/// server when <c>CLRKERNEL_JOBS_TEST_POSTGRES</c> / <c>CLRKERNEL_JOBS_TEST_SQLSERVER</c>
/// hold a connection string, and are skipped otherwise — set
/// <c>CLRKERNEL_TEST_REQUIRE_LIVE=1</c> (as CI does) to turn a missing server into a
/// failure instead, so a verification run cannot report success without touching one.
/// <c>dev/docker-compose.dbs.yml</c> brings both up locally.
/// </para>
/// <para>
/// The relational databases are scratch: every test empties the tables first, and the
/// suite must run on a single target framework so two TFMs cannot share one database.
/// </para>
/// </summary>
[TestClass]
public class RunStoreContractTest {
    private string _dir;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup() {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static string LiveConnectionString(string kind) => kind switch {
        "postgres" => Environment.GetEnvironmentVariable("CLRKERNEL_JOBS_TEST_POSTGRES"),
        "sqlserver" => Environment.GetEnvironmentVariable("CLRKERNEL_JOBS_TEST_SQLSERVER"),
        _ => null,
    };

    private IRunStore StoreFor(string kind) {
        var options = new JobsOptions {
            DataDir = Path.Combine(_dir, kind),
            NotebooksRoot = _dir,
            Store = kind,
        };
        Directory.CreateDirectory(options.DataDir);

        if (kind is "postgres" or "sqlserver") {
            var variable = kind == "postgres"
                ? "CLRKERNEL_JOBS_TEST_POSTGRES"
                : "CLRKERNEL_JOBS_TEST_SQLSERVER";
            var connectionString = LiveConnectionString(kind);
            LiveTestGate.Require(connectionString, variable, $"the {kind} run-store contract tests");
            options.ConnectionString = connectionString;

            var store = (EfRunStore)RunStoreFactory.Create(options);
            // A shared scratch database: start from empty so counts mean what the
            // assertions think they mean.
            store.ClearForTests();
            return store;
        }

        return RunStoreFactory.Create(options);
    }

    private static Run NewRun(string job, RunStatus status, DateTime? finished = null) => new() {
        Id = Guid.NewGuid(),
        JobName = job,
        NotebookPath = "nb.nb.md",
        Status = status,
        Trigger = RunTrigger.Manual,
        CreatedAt = DateTime.UtcNow,
        StartedAt = DateTime.UtcNow,
        FinishedAt = finished,
    };

    [TestMethod]
    [DataRow("sqlite")]
    [DataRow("files")]
    [DataRow("postgres")]
    [DataRow("sqlserver")]
    public async Task Runs_round_trip_and_query(string kind) {
        var store = StoreFor(kind);

        var run = await store.CreateRunAsync(NewRun("a", RunStatus.Running));
        Assert.AreEqual(RunStatus.Running, (await store.GetRunAsync(run.Id)).Status);

        run.Status = RunStatus.Succeeded;
        run.FinishedAt = DateTime.UtcNow;
        await store.UpdateRunAsync(run);
        Assert.AreEqual(RunStatus.Succeeded, (await store.GetRunAsync(run.Id)).Status);

        await store.CreateRunAsync(NewRun("b", RunStatus.Failed, DateTime.UtcNow));
        Assert.AreEqual(1, (await store.QueryRunsAsync(new RunQuery { JobName = "a" })).Count);
        Assert.AreEqual(1, (await store.QueryRunsAsync(new RunQuery { Status = RunStatus.Failed })).Count);
        Assert.AreEqual(2, (await store.QueryRunsAsync(new RunQuery())).Count);
        Assert.IsNull(await store.GetRunAsync(Guid.NewGuid()), "an unknown id is null, not an error");
    }

    [TestMethod]
    [DataRow("sqlite")]
    [DataRow("files")]
    [DataRow("postgres")]
    [DataRow("sqlserver")]
    public async Task Cells_are_saved_and_updated_in_order(string kind) {
        var store = StoreFor(kind);
        var run = await store.CreateRunAsync(NewRun("a", RunStatus.Running));

        await store.SaveCellsAsync(run.Id, new[] {
            new RunCell { RunId = run.Id, CellIndex = 0, Status = CellStatus.Pending, SourcePreview = "one" },
            new RunCell { RunId = run.Id, CellIndex = 1, Status = CellStatus.Pending, SourcePreview = "two" },
        });

        await store.UpdateCellAsync(new RunCell {
            RunId = run.Id,
            CellIndex = 1,
            Status = CellStatus.Failed,
            SourcePreview = "two",
            ErrorSummary = "boom",
        });

        var cells = await store.GetCellsAsync(run.Id);
        Assert.AreEqual(2, cells.Count);
        Assert.AreEqual(0, cells[0].CellIndex, "cells come back in execution order");
        Assert.AreEqual(CellStatus.Failed, cells[1].Status);
        Assert.AreEqual("boom", cells[1].ErrorSummary);
    }

    [TestMethod]
    [DataRow("sqlite")]
    [DataRow("files")]
    [DataRow("postgres")]
    [DataRow("sqlserver")]
    public async Task Scheduler_state_survives_the_round_trip(string kind) {
        var store = StoreFor(kind);

        Assert.IsNull(await store.GetLastSuccessfulRunAsync("default", "a"));
        Assert.IsNull(await store.GetLastTriggerAsync("default", "a"));
        Assert.IsFalse(await store.HasActiveRunAsync("default", "a"));

        await store.CreateRunAsync(NewRun("a", RunStatus.Succeeded, DateTime.UtcNow.AddMinutes(-10)));
        var newest = await store.CreateRunAsync(NewRun("a", RunStatus.Succeeded, DateTime.UtcNow));
        Assert.AreEqual(newest.Id, (await store.GetLastSuccessfulRunAsync("default", "a")).Id);

        var at = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await store.SetLastTriggerAsync("default", "a", at);
        Assert.AreEqual(at, await store.GetLastTriggerAsync("default", "a"));
        await store.SetLastTriggerAsync("default", "a", at.AddHours(1));
        Assert.AreEqual(at.AddHours(1), await store.GetLastTriggerAsync("default", "a"), "upsert, not insert");

        await store.CreateRunAsync(NewRun("a", RunStatus.Running));
        Assert.IsTrue(await store.HasActiveRunAsync("default", "a"));
    }

    [TestMethod]
    [DataRow("sqlite")]
    [DataRow("files")]
    [DataRow("postgres")]
    [DataRow("sqlserver")]
    public async Task Stats_and_orphan_cleanup_agree(string kind) {
        var store = StoreFor(kind);
        await store.CreateRunAsync(NewRun("a", RunStatus.Succeeded, DateTime.UtcNow));
        await store.CreateRunAsync(NewRun("a", RunStatus.Failed, DateTime.UtcNow));
        await store.CreateRunAsync(NewRun("b", RunStatus.Running));

        var stats = await store.GetStatsAsync(TimeSpan.FromDays(1));
        Assert.AreEqual(3, stats.Total);
        Assert.AreEqual(1, stats.Succeeded);
        Assert.AreEqual(1, stats.Failed);

        Assert.AreEqual(1, await store.MarkOrphansFailedAsync(), "only the Running row is an orphan");
        var orphan = (await store.QueryRunsAsync(new RunQuery { JobName = "b" })).Single();
        Assert.AreEqual(RunStatus.Failed, orphan.Status);
        StringAssert.Contains(orphan.ErrorSummary, "Orphaned");
        Assert.AreEqual(0, await store.MarkOrphansFailedAsync(), "cleanup is idempotent");
    }

    [TestMethod]
    public async Task The_file_store_keeps_each_run_beside_its_artifacts() {
        var options = new JobsOptions { DataDir = Path.Combine(_dir, "files"), NotebooksRoot = _dir, Store = "files" };
        var store = RunStoreFactory.Create(options);
        var run = await store.CreateRunAsync(NewRun("nightly", RunStatus.Succeeded, DateTime.UtcNow));

        var expected = Path.Combine(options.ArtifactsDir, "default", "nightly", run.Id.ToString("N"), "run.json");
        Assert.IsTrue(File.Exists(expected), $"expected a self-describing record at {expected}");
        StringAssert.Contains(File.ReadAllText(expected), "\"Succeeded\"", "statuses are readable, not ints");
    }

    [TestMethod]
    public void An_unknown_store_kind_is_rejected_with_the_valid_ones_named() {
        var e = Assert.ThrowsExactly<ArgumentException>(() =>
            RunStoreFactory.Create(new JobsOptions { DataDir = _dir, NotebooksRoot = _dir, Store = "mongo" }));
        StringAssert.Contains(e.Message, "sqlite");
    }

    [TestMethod]
    public void A_relational_store_without_a_connection_string_says_so() {
        foreach (var kind in new[] { "sqlserver", "postgres" }) {
            var e = Assert.ThrowsExactly<ArgumentException>(() =>
                RunStoreFactory.Create(new JobsOptions { DataDir = _dir, NotebooksRoot = _dir, Store = kind }));
            StringAssert.Contains(e.Message, "--connection-string");
        }
    }
}

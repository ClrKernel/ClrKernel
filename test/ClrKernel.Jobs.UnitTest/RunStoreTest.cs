using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>EfRunStore round-trips on a real SQLite file created by the migrations.</summary>
[TestClass]
public class RunStoreTest {
    private string _dir;
    private EfRunStore _store;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-store-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new EfRunStore(EfRunStore.SqliteOptions(Path.Combine(_dir, "test.db")));
        _store.Migrate();
    }

    [TestCleanup]
    public void Cleanup() {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static Run NewRun(string job = "nightly", RunStatus status = RunStatus.Running) => new() {
        Id = Guid.NewGuid(),
        JobName = job,
        NotebookPath = "etl/nb.nb.md",
        Status = status,
        Trigger = RunTrigger.Manual,
        CreatedAt = DateTime.UtcNow,
        StartedAt = DateTime.UtcNow,
    };

    [TestMethod]
    public async Task A_run_and_its_cells_round_trip() {
        var run = await _store.CreateRunAsync(NewRun());
        await _store.SaveCellsAsync(run.Id, new[] {
            new RunCell { RunId = run.Id, CellIndex = 0, Status = CellStatus.Pending, SourcePreview = "var x = 1;" },
            new RunCell { RunId = run.Id, CellIndex = 1, Status = CellStatus.Pending, SourcePreview = "x + 1" },
        });

        run.Status = RunStatus.Succeeded;
        run.FinishedAt = DateTime.UtcNow;
        await _store.UpdateRunAsync(run);

        var loaded = await _store.GetRunAsync(run.Id);
        Assert.AreEqual(RunStatus.Succeeded, loaded.Status);
        Assert.AreEqual("nightly", loaded.JobName);

        var cells = await _store.GetCellsAsync(run.Id);
        Assert.AreEqual(2, cells.Count);
        Assert.AreEqual("var x = 1;", cells[0].SourcePreview);
    }

    [TestMethod]
    public async Task Queries_filter_by_job_and_status() {
        await _store.CreateRunAsync(NewRun("a", RunStatus.Succeeded));
        await _store.CreateRunAsync(NewRun("a", RunStatus.Failed));
        await _store.CreateRunAsync(NewRun("b", RunStatus.Succeeded));

        Assert.AreEqual(2, (await _store.QueryRunsAsync(new RunQuery { JobName = "a" })).Count);
        Assert.AreEqual(1, (await _store.QueryRunsAsync(new RunQuery { Status = RunStatus.Failed })).Count);
        Assert.AreEqual(3, (await _store.QueryRunsAsync(new RunQuery())).Count);

        var stats = await _store.GetStatsAsync(TimeSpan.FromHours(1));
        Assert.AreEqual(3, stats.Total);
        Assert.AreEqual(2, stats.Succeeded);
        Assert.AreEqual(1, stats.Failed);
    }

    [TestMethod]
    public async Task Last_success_and_last_trigger_track_per_job() {
        Assert.IsNull(await _store.GetLastSuccessAsync("a"));
        Assert.IsNull(await _store.GetLastTriggerAsync("a"));

        var run = NewRun("a", RunStatus.Succeeded);
        run.FinishedAt = DateTime.UtcNow;
        await _store.CreateRunAsync(run);
        Assert.IsNotNull(await _store.GetLastSuccessAsync("a"));

        var triggered = DateTime.UtcNow;
        await _store.SetLastTriggerAsync("a", triggered);
        Assert.AreEqual(triggered, await _store.GetLastTriggerAsync("a"));

        var later = triggered.AddMinutes(5);
        await _store.SetLastTriggerAsync("a", later);
        Assert.AreEqual(later, await _store.GetLastTriggerAsync("a"), "upsert replaces");
    }

    [TestMethod]
    public async Task Orphaned_running_rows_are_marked_failed() {
        await _store.CreateRunAsync(NewRun("stuck", RunStatus.Running));
        await _store.CreateRunAsync(NewRun("done", RunStatus.Succeeded));

        Assert.AreEqual(1, await _store.MarkOrphansFailedAsync());
        var stuck = (await _store.QueryRunsAsync(new RunQuery { JobName = "stuck" })).Single();
        Assert.AreEqual(RunStatus.Failed, stuck.Status);
        StringAssert.Contains(stuck.ErrorSummary, "Orphaned");
    }
}

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// 0.10 renamed the editable branch from <c>dev</c> to <c>test</c>, and the run
/// history has to come with it. Promotability asks "has this job run in the editable
/// environment?" — history stranded under the old name doesn't error, it just makes
/// every notebook quietly un-promotable, which is the failure nobody would trace back
/// to a rename. Both stores are covered because both hold the branch name.
/// </summary>
[TestClass]
public class EnvironmentMigrationTest {
    private string _dir;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-envmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup() {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>One runs row in the pre-projects schema, which had no project column.</summary>
    private static string InsertRun(string environment, string id) =>
        "INSERT INTO runs (id, environment, job_name, notebook_path, status, trigger_type, " +
        "attempt, created_at, was_dirty, had_overrides) VALUES " +
        $"('{id}', '{environment}', 'nightly', 'etl.nb.md', 'Succeeded', 'Schedule', 1, " +
        "'2026-08-01 00:00:00', 0, 0)";

    [TestMethod]
    public async Task Sqlite_history_written_under_dev_is_found_under_test() {
        var options = new JobsOptions { DataDir = _dir, NotebooksRoot = _dir, Store = "sqlite" };
        var connectionString = $"Data Source={options.DefaultSqlitePath}";
        var contextOptions = new DbContextOptionsBuilder<SqliteRunsDbContext>()
            .UseSqlite(connectionString).Options;

        // Stop at the 0.9 schema, then write the rows a 0.9 server would have.
        // Raw SQL, not the entity model: the model has moved on since, and inserting
        // through it would be writing today's columns into yesterday's table.
        using (var db = new SqliteRunsDbContext(contextOptions)) {
            // "AddAuth" is the last migration before the rename — the 0.9 schema.
            db.GetInfrastructure().GetRequiredService<IMigrator>().Migrate("AddAuth");
            db.Database.ExecuteSqlRaw(InsertRun("dev", "11111111-1111-1111-1111-111111111111"));
            db.Database.ExecuteSqlRaw(InsertRun("prod", "22222222-2222-2222-2222-222222222222"));
            db.Database.ExecuteSqlRaw(
                "INSERT INTO job_trigger_state (environment, job_name, last_trigger_at) " +
                "VALUES ('dev', 'nightly', '2026-08-01 00:00:00')");
        }

        var store = RunStoreFactory.Create(options);   // applies the rest, rename included

        var underTest = await store.QueryRunsAsync(new RunQuery { Environment = "test" });
        Assert.AreEqual(1, underTest.Count, "the dev run answers to test now");
        Assert.AreEqual("nightly", underTest[0].JobName);
        Assert.AreEqual(0, (await store.QueryRunsAsync(new RunQuery { Environment = "dev" })).Count,
            "and to nothing under the old name");
        Assert.AreEqual(1, (await store.QueryRunsAsync(new RunQuery { Environment = "prod" })).Count,
            "prod is untouched");
        Assert.IsNotNull(await store.GetLastTriggerAsync("default", "test", "nightly"),
            "the fan-in clock travels too");
    }

    /// <summary>
    /// The project column lands on rows that predate it. Worth its own test because
    /// adding it changes job_trigger_state's <em>primary key</em>, which SQLite can
    /// only do by rebuilding the table — and a rebuild that quietly loses its rows
    /// looks exactly like a table that was always empty.
    /// </summary>
    [TestMethod]
    public async Task Rows_written_before_projects_belong_to_the_default_project() {
        var options = new JobsOptions { DataDir = _dir, NotebooksRoot = _dir, Store = "sqlite" };
        var contextOptions = new DbContextOptionsBuilder<SqliteRunsDbContext>()
            .UseSqlite($"Data Source={options.DefaultSqlitePath}").Options;

        // "RenameDevToTest" is the last migration before projects existed.
        using (var db = new SqliteRunsDbContext(contextOptions)) {
            db.GetInfrastructure().GetRequiredService<IMigrator>().Migrate("RenameDevToTest");
            db.Database.ExecuteSqlRaw(InsertRun("test", "11111111-1111-1111-1111-111111111111"));
            db.Database.ExecuteSqlRaw(
                "INSERT INTO job_trigger_state (environment, job_name, last_trigger_at) " +
                "VALUES ('test', 'nightly', '2026-08-01 00:00:00')");
        }

        var store = RunStoreFactory.Create(options);

        var runs = await store.QueryRunsAsync(new RunQuery { Project = ProjectRegistry.DefaultSlug });
        Assert.AreEqual(1, runs.Count, "the run belongs to the default project");
        Assert.AreEqual("test", runs[0].Environment);
        Assert.IsNotNull(
            await store.GetLastTriggerAsync(ProjectRegistry.DefaultSlug, "test", "nightly"),
            "the primary-key rebuild carried the trigger row");
    }

    [TestMethod]
    public async Task File_store_history_written_under_dev_is_found_under_test() {
        var options = new JobsOptions { DataDir = _dir, NotebooksRoot = _dir, Store = "files" };
        var id = Guid.NewGuid();

        // What a 0.9 file store left on disk: the environment names the directory
        // *and* sits inside the record, so a directory rename alone would not do it.
        var runDir = Path.Combine(options.ArtifactsDir, "dev", "nightly", id.ToString("N"));
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "run.json"), $$"""
            {"Run":{"Id":"{{id}}","Environment":"dev","JobName":"nightly",
             "NotebookPath":"etl.nb.md","Status":"Succeeded","Trigger":"Schedule",
             "Attempt":1,"CreatedAt":"2026-08-01T00:00:00Z"},"Cells":[]}
            """);
        File.WriteAllText(Path.Combine(_dir, "triggers.json"), """
            {"dev/nightly":"2026-08-01T00:00:00Z"}
            """);

        var store = RunStoreFactory.Create(options);

        var underTest = await store.QueryRunsAsync(new RunQuery { Environment = "test" });
        Assert.AreEqual(1, underTest.Count);
        Assert.AreEqual(id, underTest[0].Id);
        Assert.AreEqual(0, (await store.QueryRunsAsync(new RunQuery { Environment = "dev" })).Count);
        Assert.IsTrue(Directory.Exists(Path.Combine(options.ArtifactsDir, "test", "nightly")),
            "the artifacts moved with the record");
        Assert.IsNotNull(await store.GetLastTriggerAsync("default", "test", "nightly"));
    }
}

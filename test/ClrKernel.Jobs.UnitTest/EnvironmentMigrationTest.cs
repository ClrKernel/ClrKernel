using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

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

    private static Run NewRun(string environment) => new() {
        Id = Guid.NewGuid(),
        Environment = environment,
        JobName = "nightly",
        NotebookPath = "etl.nb.md",
        Status = RunStatus.Succeeded,
        CreatedAt = DateTime.UtcNow,
        CommitSha = new string('a', 40),
    };

    [TestMethod]
    public async Task Sqlite_history_written_under_dev_is_found_under_test() {
        var options = new JobsOptions { DataDir = _dir, NotebooksRoot = _dir, Store = "sqlite" };
        var connectionString = $"Data Source={options.DefaultSqlitePath}";
        var contextOptions = new DbContextOptionsBuilder<SqliteRunsDbContext>()
            .UseSqlite(connectionString).Options;

        // Stop at the 0.9 schema, then write the rows a 0.9 server would have.
        using (var db = new SqliteRunsDbContext(contextOptions)) {
            // "AddAuth" is the last migration before the rename — the 0.9 schema.
            db.GetInfrastructure().GetRequiredService<IMigrator>().Migrate("AddAuth");
            db.Runs.Add(NewRun("dev"));
            db.Runs.Add(NewRun("prod"));
            db.JobTriggerStates.Add(new JobTriggerState {
                Environment = "dev",
                JobName = "nightly",
                LastTriggerAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var store = RunStoreFactory.Create(options);   // applies the rest, rename included

        var underTest = await store.QueryRunsAsync(new RunQuery { Environment = "test" });
        Assert.AreEqual(1, underTest.Count, "the dev run answers to test now");
        Assert.AreEqual("nightly", underTest[0].JobName);
        Assert.AreEqual(0, (await store.QueryRunsAsync(new RunQuery { Environment = "dev" })).Count,
            "and to nothing under the old name");
        Assert.AreEqual(1, (await store.QueryRunsAsync(new RunQuery { Environment = "prod" })).Count,
            "prod is untouched");
        Assert.IsNotNull(await store.GetLastTriggerAsync("test", "nightly"), "the fan-in clock travels too");
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
        Assert.IsNotNull(await store.GetLastTriggerAsync("test", "nightly"));
    }
}

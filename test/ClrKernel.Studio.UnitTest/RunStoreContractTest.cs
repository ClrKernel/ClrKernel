using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.UnitTest;   // LiveTestGate, shared with the database test project
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// One suite, every backend: the scheduler and API depend on these behaviours
/// whichever store is configured, so a new backend must satisfy the same contract.
/// <para>
/// sqlite and files run everywhere. PostgreSQL and SQL Server run against a real
/// server when <c>CLRKERNEL_STUDIO_TEST_POSTGRES</c> / <c>CLRKERNEL_STUDIO_TEST_SQLSERVER</c>
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
        "postgres" => Environment.GetEnvironmentVariable("CLRKERNEL_STUDIO_TEST_POSTGRES"),
        "sqlserver" => Environment.GetEnvironmentVariable("CLRKERNEL_STUDIO_TEST_SQLSERVER"),
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
                ? "CLRKERNEL_STUDIO_TEST_POSTGRES"
                : "CLRKERNEL_STUDIO_TEST_SQLSERVER";
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
        Assert.AreEqual(1, (await store.QueryRunsAsync(new RunQuery { Projects = new[] { "default" }, JobName = "a" })).Count);
        Assert.AreEqual(1, (await store.QueryRunsAsync(new RunQuery { Projects = new[] { "default" }, Status = RunStatus.Failed })).Count);
        Assert.AreEqual(2, (await store.QueryRunsAsync(new RunQuery { Projects = new[] { "default" } })).Count);
        Assert.IsNull(await store.GetRunAsync(Guid.NewGuid()), "an unknown id is null, not an error");
    }

    /// <summary>
    /// The monitoring grid asks the store for one page of an unbounded table, so
    /// every filter, the order and the paging have to be the store's — and have to
    /// mean the same thing on all four backends, or "sorted by job" reads differently
    /// depending on what somebody configured.
    /// <para>
    /// The project scope is in the same list on purpose. It is not one filter among
    /// several: a page filtered after the query comes back short, and a short page is
    /// how a permissions bug hides as a paging bug.
    /// </para>
    /// </summary>
    [TestMethod]
    [DataRow("sqlite")]
    [DataRow("files")]
    [DataRow("postgres")]
    [DataRow("sqlserver")]
    public async Task Runs_are_filtered_ordered_and_scoped_by_the_store(string kind) {
        var store = StoreFor(kind);
        var ada = Guid.NewGuid();
        var start = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        async Task<Run> Add(
            string project, string environment, string job, DateTime at,
            RunTrigger trigger = RunTrigger.Schedule, Guid? actor = null, string notebook = "nb.nb.md") =>
            await store.CreateRunAsync(new Run {
                Id = Guid.NewGuid(),
                Project = project,
                Environment = environment,
                JobName = job,
                NotebookPath = notebook,
                Status = RunStatus.Succeeded,
                Trigger = trigger,
                CreatedAt = at,
                StartedAt = at,
                FinishedAt = at.AddMinutes(1),
                ActorId = actor,
                ActorName = actor == null ? null : "Ada Lovelace",
            });

        var oldest = await Add("default", "test", "alpha", start);
        var middle = await Add("default", "prod", "beta", start.AddHours(1),
            RunTrigger.Manual, ada, "other.nb.md");
        var newest = await Add("default", "prod", "gamma", start.AddHours(2));
        var elsewhere = await Add("secret", "prod", "delta", start.AddHours(3));

        // A project you cannot see contributes no rows — not even the newest one,
        // which is exactly the row a post-query filter would have paged in and
        // dropped.
        var mine = await store.QueryRunsAsync(new RunQuery { Projects = new[] { "default" } });
        Assert.AreEqual(3, mine.Count);
        Assert.AreEqual(newest.Id, mine[0].Id, "newest first by default");
        Assert.AreEqual(oldest.Id, mine[2].Id);
        CollectionAssert.DoesNotContain(mine.Select(r => r.Id).ToList(), elsewhere.Id);

        Assert.AreEqual(4,
            (await store.QueryRunsAsync(new RunQuery { Projects = new[] { "default", "secret" } })).Count,
            "and every row of every project that is named");
        Assert.AreEqual(0,
            (await store.QueryRunsAsync(new RunQuery { Projects = Array.Empty<string>() })).Count,
            "somebody who can see no projects sees no runs, not all of them");

        // Each filter on its own.
        Assert.AreEqual(2, (await store.QueryRunsAsync(
            new RunQuery { Projects = new[] { "default" }, Environment = "prod" })).Count);
        Assert.AreEqual(middle.Id, (await store.QueryRunsAsync(
            new RunQuery { Projects = new[] { "default" }, Trigger = RunTrigger.Manual })).Single().Id);
        Assert.AreEqual(middle.Id, (await store.QueryRunsAsync(
            new RunQuery { Projects = new[] { "default" }, ActorId = ada })).Single().Id);
        Assert.AreEqual(middle.Id, (await store.QueryRunsAsync(
            new RunQuery { Projects = new[] { "default" }, NotebookPath = "other.nb.md" })).Single().Id);
        Assert.AreEqual(2, (await store.QueryRunsAsync(
            new RunQuery { Projects = new[] { "default" }, Since = start.AddMinutes(30) })).Count,
            "Since is inclusive of everything at or after it");
        Assert.AreEqual(oldest.Id, (await store.QueryRunsAsync(
            new RunQuery { Projects = new[] { "default" }, Until = start.AddMinutes(30) })).Single().Id);

        // Order, both ways, on a column that is not the default.
        var byJob = await store.QueryRunsAsync(new RunQuery {
            Projects = new[] { "default" },
            Sort = RunSort.JobName,
            Ascending = true,
        });
        CollectionAssert.AreEqual(
            new[] { "alpha", "beta", "gamma" }, byJob.Select(r => r.JobName).ToArray());
        var oldestFirst = await store.QueryRunsAsync(new RunQuery {
            Projects = new[] { "default" },
            Ascending = true,
        });
        Assert.AreEqual(oldest.Id, oldestFirst[0].Id);

        // Paging is the store's too, and the pages have to partition the rows —
        // a row on both pages, or on neither, is the bug this is here to catch.
        var page1 = await store.QueryRunsAsync(new RunQuery {
            Projects = new[] { "default" },
            Limit = 2,
        });
        var page2 = await store.QueryRunsAsync(new RunQuery {
            Projects = new[] { "default" },
            Limit = 2,
            Offset = 2,
        });
        Assert.AreEqual(2, page1.Count);
        Assert.AreEqual(1, page2.Count);
        CollectionAssert.AreEqual(
            new[] { newest.Id, middle.Id, oldest.Id },
            page1.Concat(page2).Select(r => r.Id).ToArray());
    }

    /// <summary>
    /// Runs that tie on the sort key keep one order across requests. Without a
    /// tiebreaker the database is free to return them in either, and a row that
    /// shifts between two Skip/Take pages is a row nobody ever sees.
    /// </summary>
    [TestMethod]
    [DataRow("sqlite")]
    [DataRow("files")]
    [DataRow("postgres")]
    [DataRow("sqlserver")]
    public async Task Ties_page_in_a_stable_order(string kind) {
        var store = StoreFor(kind);
        var at = new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 6; i++) {
            await store.CreateRunAsync(new Run {
                Id = Guid.NewGuid(),
                Project = "default",
                Environment = "prod",
                JobName = "same",
                NotebookPath = "nb.nb.md",
                Status = RunStatus.Succeeded,
                Trigger = RunTrigger.Schedule,
                CreatedAt = at,
                StartedAt = at,
                FinishedAt = at,
            });
        }

        var paged = new List<Guid>();
        for (var offset = 0; offset < 6; offset += 2) {
            paged.AddRange((await store.QueryRunsAsync(new RunQuery {
                Projects = new[] { "default" },
                Limit = 2,
                Offset = offset,
            })).Select(r => r.Id));
        }
        CollectionAssert.AllItemsAreUnique(paged, "every run appears on exactly one page");
        Assert.AreEqual(6, paged.Count);

        var again = (await store.QueryRunsAsync(new RunQuery {
            Projects = new[] { "default" },
            Limit = 6,
        })).Select(r => r.Id).ToList();
        CollectionAssert.AreEqual(paged, again, "and in the same order as one unpaged read");
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

        Assert.IsNull(await store.GetLastSuccessfulRunAsync("default", "default", "a"));
        Assert.IsNull(await store.GetLastTriggerAsync("default", "default", "a"));
        Assert.IsFalse(await store.HasActiveRunAsync("default", "default", "a"));

        await store.CreateRunAsync(NewRun("a", RunStatus.Succeeded, DateTime.UtcNow.AddMinutes(-10)));
        var newest = await store.CreateRunAsync(NewRun("a", RunStatus.Succeeded, DateTime.UtcNow));
        Assert.AreEqual(newest.Id, (await store.GetLastSuccessfulRunAsync("default", "default", "a")).Id);

        var at = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await store.SetLastTriggerAsync("default", "default", "a", at);
        Assert.AreEqual(at, await store.GetLastTriggerAsync("default", "default", "a"));
        await store.SetLastTriggerAsync("default", "default", "a", at.AddHours(1));
        Assert.AreEqual(at.AddHours(1), await store.GetLastTriggerAsync("default", "default", "a"), "upsert, not insert");

        await store.CreateRunAsync(NewRun("a", RunStatus.Running));
        Assert.IsTrue(await store.HasActiveRunAsync("default", "default", "a"));
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
        var orphan = (await store.QueryRunsAsync(new RunQuery { Projects = new[] { "default" }, JobName = "b" })).Single();
        Assert.AreEqual(RunStatus.Failed, orphan.Status);
        StringAssert.Contains(orphan.ErrorSummary, "Orphaned");
        Assert.AreEqual(0, await store.MarkOrphansFailedAsync(), "cleanup is idempotent");
    }

    /// <summary>
    /// Retention. Two of these are not policy but structure: the promotion gate
    /// reads a job's most recent run, so a sweep that could delete it would be a
    /// policy about disk quietly becoming a policy about deployment — and a run
    /// still in flight is not old, it is unfinished.
    /// </summary>
    [TestMethod]
    [DataRow("sqlite")]
    [DataRow("files")]
    [DataRow("postgres")]
    [DataRow("sqlserver")]
    public async Task Retention_forgets_old_runs_but_never_the_latest_or_the_unfinished(string kind) {
        var store = StoreFor(kind);
        var now = DateTime.UtcNow;

        async Task<Run> Add(string job, DateTime at, RunStatus status = RunStatus.Succeeded) =>
            await store.CreateRunAsync(new Run {
                Id = Guid.NewGuid(),
                Project = "default",
                Environment = "prod",
                JobName = job,
                NotebookPath = "nb.nb.md",
                Status = status,
                Trigger = RunTrigger.Schedule,
                CreatedAt = at,
                StartedAt = at,
                FinishedAt = status is RunStatus.Pending or RunStatus.Running ? null : at.AddMinutes(1),
                ArtifactPath = $"artifacts/prod/{job}/{Guid.NewGuid():N}/output.ipynb",
            });

        var ancient = await Add("nightly", now.AddDays(-90));
        var old = await Add("nightly", now.AddDays(-60));
        var latest = await Add("nightly", now.AddDays(-40));
        // A different job whose only run is old: it is still that job's latest.
        var onlyRun = await Add("quarterly", now.AddDays(-100));
        // Left over from a crash three months ago — no FinishedAt, so not old.
        var stuck = await Add("hourly", now.AddDays(-95), RunStatus.Running);
        var recent = await Add("nightly", now.AddMinutes(-5));

        var artifacts = await store.PurgeRunsAsync(now.AddDays(-30));

        Assert.AreEqual(3, artifacts.Count,
            "the two superseded old ones and the one that used to be latest");
        Assert.IsNull(await store.GetRunAsync(ancient.Id));
        Assert.IsNull(await store.GetRunAsync(old.Id));
        Assert.IsNull(await store.GetRunAsync(latest.Id), "superseded by the recent one");
        Assert.IsNotNull(await store.GetRunAsync(recent.Id));
        Assert.IsNotNull(await store.GetRunAsync(onlyRun.Id),
            "a job's most recent run is what proves it works, whatever its age");
        Assert.IsNotNull(await store.GetRunAsync(stuck.Id),
            "unfinished is not old");

        // The paths come back so the caller can delete what is on disk. A row
        // forgotten while its executed notebook stays is half a retention policy.
        Assert.IsTrue(artifacts.All(p => p.Contains("output.ipynb")), string.Join(", ", artifacts));

        Assert.AreEqual(0, (await store.PurgeRunsAsync(now.AddDays(-30))).Count,
            "and it is idempotent");
    }

    /// <summary>
    /// The delivery feed, on every backend. "Why did nobody hear about this?" must
    /// not depend on which store somebody configured — and the failures are the
    /// half that answers it.
    /// </summary>
    [TestMethod]
    [DataRow("sqlite")]
    [DataRow("files")]
    [DataRow("postgres")]
    [DataRow("sqlserver")]
    public async Task Notifications_record_what_was_sent_and_what_was_not(string kind) {
        var store = StoreFor(kind);
        var at = DateTime.UtcNow;

        async Task Add(string project, string channel, string error, int minutesAgo) =>
            await store.RecordDeliveryAsync(new NotificationDelivery {
                Id = Guid.NewGuid(),
                Project = project,
                Environment = "prod",
                Event = "JobFailed",
                Channel = channel,
                Subject = "nightly",
                SentAt = at.AddMinutes(-minutesAgo),
                Error = error,
            });

        await Add("default", "ops", null, 10);
        await Add("default", "pager", "Webhook returned 500 Internal Server Error.", 5);
        await Add("secret", "theirs", null, 1);

        var mine = await store.DeliveriesAsync(new NotificationQuery { Projects = new[] { "default" } });
        Assert.AreEqual(2, mine.Count, "another project's notifications are another project's");
        Assert.AreEqual("pager", mine[0].Channel, "newest first");

        var failures = await store.DeliveriesAsync(new NotificationQuery {
            Projects = new[] { "default" },
            FailuresOnly = true,
        });
        Assert.AreEqual(1, failures.Count);
        StringAssert.Contains(failures[0].Error, "500");
        Assert.AreEqual("JobFailed", failures[0].Event);

        Assert.AreEqual(0,
            (await store.DeliveriesAsync(new NotificationQuery { Projects = Array.Empty<string>() })).Count,
            "somebody who can see no projects sees no notifications");
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

    /// <summary>
    /// Git says the files changed; this says who sent them and what stopped
    /// running. Every backend has to agree, because the answer to "why did this
    /// job stop?" cannot depend on which store somebody configured.
    /// </summary>
    [TestMethod]
    [DataRow("sqlite")]
    [DataRow("files")]
    [DataRow("postgres")]
    [DataRow("sqlserver")]
    public async Task Promotions_are_recorded_and_read_back(string kind) {
        var store = StoreFor(kind);
        var actor = Guid.NewGuid();

        await store.RecordPromotionAsync(new PromotionAudit {
            Id = Guid.NewGuid(),
            Project = "default",
            Paths = "etl.nb.md\netl.jobs.yaml",
            ActorId = actor,
            ActorName = "Ada Lovelace",
            PromotedAt = DateTime.UtcNow.AddMinutes(-5),
            CommitSha = "abc123",
            EvidenceRuns = Guid.NewGuid().ToString(),
        });
        await store.RecordPromotionAsync(new PromotionAudit {
            Id = Guid.NewGuid(),
            Project = "default",
            Paths = "old.jobs.yaml",
            ActorId = actor,
            ActorName = "Ada Lovelace",
            PromotedAt = DateTime.UtcNow,
            IsDeletion = true,
            CommitSha = "def456",
            Unscheduled = "nightly (0 2 * * *)",
        });

        var all = await store.PromotionAuditAsync(new PromotionAuditQuery());
        Assert.AreEqual(2, all.Count);
        Assert.AreEqual("def456", all[0].CommitSha, "newest first");
        Assert.AreEqual("Ada Lovelace", all[0].ActorName);

        // The question this exists to answer: what stopped running, and when.
        var unschedules = await store.PromotionAuditAsync(
            new PromotionAuditQuery { UnschedulesOnly = true });
        Assert.AreEqual(1, unschedules.Count);
        Assert.IsTrue(unschedules[0].IsDeletion);
        StringAssert.Contains(unschedules[0].Unscheduled, "nightly");

        Assert.AreEqual(0,
            (await store.PromotionAuditAsync(new PromotionAuditQuery { Project = "other" })).Count,
            "another project's promotions are another project's");
    }

}

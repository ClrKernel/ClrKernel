using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// Drives the real endpoint pipeline over a live loopback host: job CRUD writes
/// actual *.jobs.yaml files, runs come from the store, and the API key and
/// traversal guards are exercised as a client would hit them.
/// </summary>
[TestClass]
public class ApiTest {
    private string _root;
    private WebApplication _app;
    private HttpClient _client;
    private EfRunStore _store;
    private JobsOptions _options;
    private User _admin;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [TestInitialize]
    public async Task Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-api-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "notebooks", "etl"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        File.WriteAllText(NotebookPath, "```csharp\n1+1\n```\n");

        _options = new JobsOptions {
            DataDir = Path.Combine(_root, "data"),
            NotebooksRoot = Path.Combine(_root, "notebooks"),
        };
        _store = EfRunStore.Sqlite(Path.Combine(_options.DataDir, "test.db"));
        _store.Migrate();

        _app = Program.BuildApp(
            _options, new ProjectRegistry(_options, NullLoggerFactory.Instance), _store,
            TestAuth.StoreFor(Path.Combine(_options.DataDir, "test.db")));
        // An ephemeral port: the default 5000 collides with a real `serve` on the
        // dev machine and with any other host started by the suite.
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();
        var address = _app.Urls.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
        _admin = await TestAuth.SignInAsync(_app, _client, UserRole.ServerAdmin);
    }

    [TestCleanup]
    public async Task Cleanup() {
        _client?.Dispose();
        if (_app != null) {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private string NotebookPath => Path.Combine(_root, "notebooks", "etl", "nightly.nb.md");

    private static JobWrite NewJob(string name) => new() {
        Name = name,
        Notebook = "etl/nightly.nb.md",
        Cron = "0 2 * * *",
        Parameters = new Dictionary<string, object> { ["region"] = "us" },
    };

    [TestMethod]
    public async Task Health_reports_the_catalog() {
        var health = await _client.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.AreEqual("ok", health.GetProperty("status").GetString());
        Assert.AreEqual(0, health.GetProperty("jobs").GetInt32());
    }

    [TestMethod]
    public async Task A_job_can_be_created_read_updated_and_deleted_through_the_yaml() {
        var created = await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", NewJob("nightly-us"));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

        var yamlPath = Path.Combine(_root, "notebooks", "etl", "nightly.jobs.yaml");
        Assert.IsTrue(File.Exists(yamlPath), "the job is persisted as a jobs file next to its notebook");
        StringAssert.Contains(File.ReadAllText(yamlPath), "nightly-us");

        var fetched = await _client.GetFromJsonAsync<JobView>("/api/projects/default/branches/default/jobs/nightly-us", _json);
        Assert.AreEqual("etl/nightly.nb.md", fetched.Notebook);
        Assert.AreEqual("0 2 * * *", fetched.Cron);
        Assert.AreEqual("us", fetched.Parameters["region"].ToString());

        // A second job on the same notebook joins the same file.
        Assert.AreEqual(HttpStatusCode.Created,
            (await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", NewJob("nightly-eu"))).StatusCode);
        var list = await _client.GetFromJsonAsync<JsonElement>("/api/jobs");
        Assert.AreEqual(2, list.GetProperty("jobs").GetArrayLength());
        Assert.AreEqual(0, list.GetProperty("errors").GetArrayLength());

        var update = NewJob("nightly-us");
        update.Cron = "30 3 * * *";
        update.Enabled = false;
        var updated = await _client.PutAsJsonAsync("/api/projects/default/branches/default/jobs/nightly-us", update);
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
        var reread = await _client.GetFromJsonAsync<JobView>("/api/projects/default/branches/default/jobs/nightly-us", _json);
        Assert.AreEqual("30 3 * * *", reread.Cron);
        Assert.IsFalse(reread.Enabled);

        Assert.AreEqual(HttpStatusCode.NoContent,
            (await _client.DeleteAsync("/api/projects/default/branches/default/jobs/nightly-us")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync("/api/projects/default/branches/default/jobs/nightly-us")).StatusCode);
        StringAssert.Contains(File.ReadAllText(yamlPath), "nightly-eu", "the sibling job survives");
    }

    [TestMethod]
    public async Task Invalid_job_writes_are_refused() {
        var noNotebook = NewJob("ghost");
        noNotebook.Notebook = "etl/missing.nb.md";
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", noNotebook)).StatusCode);

        var traversal = NewJob("escape");
        traversal.Notebook = "../../etc/passwd";
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", traversal)).StatusCode);

        var badCron = NewJob("bad-cron");
        badCron.Cron = "not a cron";
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", badCron)).StatusCode,
            "an unloadable job never reaches the disk");

        await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", NewJob("taken"));
        Assert.AreEqual(HttpStatusCode.Conflict,
            (await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", NewJob("taken"))).StatusCode);
    }

    [TestMethod]
    public async Task An_unregistered_project_is_404_everywhere_it_is_named() {
        var listed = await _client.GetFromJsonAsync<JsonElement>("/api/projects");
        Assert.AreEqual(1, listed.GetProperty("projects").GetArrayLength());
        Assert.AreEqual("default", listed.GetProperty("projects")[0].GetProperty("slug").GetString());

        // 404 and not 403 on purpose: a project you cannot see must look exactly
        // like a project that does not exist, or the names leak to anyone guessing.
        foreach (var url in new[] {
            "/api/projects/finance",
            "/api/projects/finance/notebooks",
            "/api/projects/finance/branches/default/notebooks/content?path=etl/nightly.nb.md",
            "/api/projects/finance/branches/default/notebooks/cells?path=etl/nightly.nb.md",
            "/api/projects/finance/branches/default/jobs/nightly",
        }) {
            Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync(url)).StatusCode, url);
        }
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await _client.PostAsJsonAsync(
                "/api/projects/finance/branches/default/jobs", NewJob("x"))).StatusCode);
    }

    [TestMethod]
    public async Task A_project_can_be_registered_configured_and_forgotten() {
        var finance = Path.Combine(_root, "finance");
        Directory.CreateDirectory(finance);

        var created = await _client.PostAsJsonAsync("/api/projects",
            new { name = "Finance Close", root = finance });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode,
            await created.Content.ReadAsStringAsync());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("finance-close", body.GetProperty("project").GetProperty("slug").GetString());
        Assert.IsFalse(body.GetProperty("createdRoot").GetBoolean(), "this folder was already there");

        // Its jobs and notebooks are reachable under its own slug straight away.
        Assert.AreEqual(HttpStatusCode.OK,
            (await _client.GetAsync("/api/projects/finance-close/notebooks")).StatusCode);

        var edited = await _client.PutAsJsonAsync("/api/projects/finance-close", new {
            name = "Finance",
            root = "/somewhere/else",
            remoteMode = "ServerAuthoritative",
            remote = "origin",
            remoteSecret = "FINANCE_GIT_TOKEN",
        });
        var after = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("Finance", after.GetProperty("name").GetString());
        Assert.AreEqual(finance, after.GetProperty("root").GetString(), "the root is not editable");
        // A reference, and the only thing about a remote credential this API ever
        // holds — there is no field here that could carry the credential itself.
        Assert.AreEqual("FINANCE_GIT_TOKEN", after.GetProperty("remoteSecret").GetString());

        Assert.AreEqual(HttpStatusCode.NoContent,
            (await _client.DeleteAsync("/api/projects/finance-close")).StatusCode);
        Assert.IsTrue(File.Exists(Path.Combine(finance)) || Directory.Exists(finance),
            "unregistering forgets a project; it does not delete one");
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await _client.GetAsync("/api/projects/finance-close")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.DeleteAsync("/api/projects/default")).StatusCode,
            "the last project cannot be forgotten");
    }

    [TestMethod]
    public async Task Registering_makes_the_folder_when_it_is_not_there_yet() {
        var fresh = Path.Combine(_root, "brand", "new");
        var created = await _client.PostAsJsonAsync("/api/projects",
            new { name = "Brand New", root = fresh });

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode,
            await created.Content.ReadAsStringAsync());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(body.GetProperty("createdRoot").GetBoolean());
        Assert.IsTrue(Directory.Exists(fresh));
        Assert.AreEqual(HttpStatusCode.OK,
            (await _client.GetAsync("/api/projects/brand-new/notebooks")).StatusCode);
    }

    [TestMethod]
    public async Task The_notebook_tree_and_content_respect_the_root() {
        var payload = await _client.GetFromJsonAsync<JsonElement>("/api/projects/default/notebooks");
        var envs = payload.GetProperty("environments");
        Assert.AreEqual(1, envs.GetArrayLength(), "no git workflow: one default environment");
        var tree = JsonSerializer.Deserialize<TreeNode>(
            envs[0].GetProperty("tree").GetRawText(), _json);
        var etl = tree.Children.Single(c => c.IsDirectory);
        Assert.AreEqual("etl/nightly.nb.md", etl.Children.Single(c => c.Kind == "notebook").Path);

        var content = await _client.GetAsync("/api/projects/default/branches/default/notebooks/content?path=etl/nightly.nb.md");
        StringAssert.Contains(await content.Content.ReadAsStringAsync(), "1+1");

        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.GetAsync("/api/projects/default/branches/default/notebooks/content?path=../../../etc/passwd")).StatusCode);
    }

    [TestMethod]
    public async Task Runs_are_listed_with_cells_and_their_artifacts_are_served() {
        var runId = Guid.NewGuid();
        var artifactRelative = Path.Combine("artifacts", "j", runId.ToString("N"), "output.ipynb");
        var artifactPath = Path.Combine(_options.DataDir, artifactRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath));
        File.WriteAllText(artifactPath, "{\"cells\":[]}");

        await _store.CreateRunAsync(new Run {
            Id = runId,
            JobName = "j",
            NotebookPath = "etl/nightly.nb.md",
            Status = RunStatus.Succeeded,
            Trigger = RunTrigger.Manual,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow,
            ArtifactPath = artifactRelative.Replace('\\', '/'),
        });
        await _store.SaveCellsAsync(runId, new[] {
            new RunCell { RunId = runId, CellIndex = 0, Status = CellStatus.Succeeded, SourcePreview = "1+1" },
        });

        var runs = await _client.GetFromJsonAsync<JsonElement>("/api/runs");
        Assert.AreEqual(1, runs.GetProperty("runs").GetArrayLength());
        Assert.IsFalse(runs.GetProperty("hasMore").GetBoolean());

        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}");
        Assert.AreEqual("Succeeded", detail.GetProperty("run").GetProperty("status").GetString());
        Assert.AreEqual(1, detail.GetProperty("cells").GetArrayLength());

        var artifact = await _client.GetAsync($"/api/runs/{runId}/artifact");
        StringAssert.Contains(await artifact.Content.ReadAsStringAsync(), "cells");
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/runs/{runId}/log")).StatusCode);

        var stats = await _client.GetFromJsonAsync<JsonElement>("/api/stats?days=7");
        Assert.AreEqual(1, stats.GetProperty("total").GetInt32());
        Assert.AreEqual(1, stats.GetProperty("succeeded").GetInt32());

        Assert.AreEqual(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/runs?status=nonsense")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/runs/{Guid.NewGuid()}")).StatusCode);
    }

    /// <summary>
    /// What is about to run. Computed from the crons rather than stored, so the
    /// only way it can disagree with the scheduler is by using a different parser —
    /// which is why it uses the same one.
    /// </summary>
    [TestMethod]
    public async Task Upcoming_runs_come_from_the_crons_and_skip_what_will_not_fire() {
        await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", NewJob("nightly"));
        var hourly = NewJob("hourly");
        hourly.Cron = "0 * * * *";
        await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", hourly);
        var off = NewJob("disabled-one");
        off.Enabled = false;
        await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", off);
        var manual = NewJob("on-demand");
        manual.Cron = null;
        await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", manual);

        var body = await _client.GetFromJsonAsync<JsonElement>("/api/schedule/upcoming");
        var names = body.GetProperty("upcoming").EnumerateArray()
            .Select(u => u.GetProperty("job").GetString()).ToList();

        CollectionAssert.AreEquivalent(new[] { "nightly", "hourly" }, names,
            "a job with no cron never fires, and neither does a disabled one");
        // Soonest first: the dashboard's answer to "what is next" is the first row.
        Assert.AreEqual("hourly", names[0], "hourly comes before a 02:00 daily");

        var first = body.GetProperty("upcoming")[0];
        Assert.IsTrue(DateTime.Parse(first.GetProperty("at").GetString()).ToUniversalTime()
            > DateTime.UtcNow, "an occurrence that has not happened yet");
        Assert.AreEqual("0 * * * *", first.GetProperty("cron").GetString());

        Assert.AreEqual(1,
            (await _client.GetFromJsonAsync<JsonElement>("/api/schedule/upcoming?limit=1"))
                .GetProperty("upcoming").GetArrayLength());
    }

    /// <summary>
    /// The rerun route's shape. What it means to rerun is
    /// <see cref="RerunTest"/>'s subject; this is what the request is allowed to say.
    /// </summary>
    [TestMethod]
    public async Task A_rerun_names_runs_that_exist_and_refuses_a_batch_it_cannot_confirm() {
        async Task<Run> Record(string job, string environment) =>
            await _store.CreateRunAsync(new Run {
                Id = Guid.NewGuid(),
                JobName = job,
                NotebookPath = "etl/nightly.nb.md",
                Environment = environment,
                Status = RunStatus.Failed,
                Trigger = RunTrigger.Schedule,
                CreatedAt = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow,
                FinishedAt = DateTime.UtcNow,
            });

        await _client.PostAsJsonAsync(
            "/api/projects/default/branches/default/jobs", NewJob("nightly"));
        var here = await Record("nightly", "default");
        var elsewhere = await Record("hourly", "prod");

        Assert.AreEqual(HttpStatusCode.NotFound,
            (await _client.PostAsJsonAsync("/api/runs/rerun", new { runIds = new[] { Guid.NewGuid() } }))
                .StatusCode,
            "a run id nobody recorded");
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/runs/rerun", new { runIds = Array.Empty<Guid>() }))
                .StatusCode,
            "nothing selected");
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync(
                "/api/runs/rerun",
                new { runIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray() }))
                .StatusCode,
            "more rows than one request may name");

        // The confirmation names *the* branch, so a selection with two of them is
        // refused rather than guessed at.
        var mixed = await _client.PostAsJsonAsync(
            "/api/runs/rerun", new { runIds = new[] { here.Id, elsewhere.Id } });
        Assert.AreEqual(HttpStatusCode.BadRequest, mixed.StatusCode);
        StringAssert.Contains(await mixed.Content.ReadAsStringAsync(), "one project and one branch");

        // A run whose job is still there starts, and says what it started and where.
        var accepted = await _client.PostAsJsonAsync(
            "/api/runs/rerun", new { runIds = new[] { here.Id } });
        Assert.AreEqual(HttpStatusCode.Accepted, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("default", body.GetProperty("environment").GetString());
        var started = body.GetProperty("started");
        Assert.AreEqual(1, started.GetArrayLength());
        Assert.AreEqual(here.Id.ToString(), started[0].GetProperty("rerunOf").GetString());

        // And the new run points back at the one it repeats — that lineage, with the
        // actor and the commit, is the whole audit record.
        var rerunId = Guid.Parse(started[0].GetProperty("runId").GetString());
        Run recorded = null;
        for (var i = 0; i < 50 && recorded == null; i++) {
            recorded = await _store.GetRunAsync(rerunId);
            if (recorded == null) {
                await Task.Delay(100);
            }
        }
        Assert.IsNotNull(recorded, "the run the route promised was never recorded");
        Assert.AreEqual(here.Id, recorded.CausedByRunId);
        Assert.AreEqual(RunTrigger.Manual, recorded.Trigger,
            "a person pressing a button is a manual run; Retry is the automatic loop");
        Assert.AreEqual(_admin.DisplayName, recorded.ActorName);
    }

    /// <summary>
    /// The monitoring grid's filters, at the route. A filter the server does not
    /// understand is a 400 naming what it would have understood — never a silently
    /// unfiltered page, which answers a question nobody asked and looks like data.
    /// </summary>
    [TestMethod]
    public async Task The_run_grid_filters_pages_and_rejects_a_filter_it_does_not_know() {
        var start = DateTime.UtcNow.AddHours(-3);
        for (var i = 0; i < 3; i++) {
            await _store.CreateRunAsync(new Run {
                Id = Guid.NewGuid(),
                JobName = i == 0 ? "nightly" : "hourly",
                NotebookPath = i == 0 ? "etl/nightly.nb.md" : "etl/hourly.nb.md",
                Status = i == 2 ? RunStatus.Failed : RunStatus.Succeeded,
                Trigger = i == 2 ? RunTrigger.Manual : RunTrigger.Schedule,
                CreatedAt = start.AddHours(i),
                StartedAt = start.AddHours(i),
                FinishedAt = start.AddHours(i).AddMinutes(1),
            });
        }

        async Task<JsonElement> Grid(string query) =>
            await _client.GetFromJsonAsync<JsonElement>($"/api/runs?{query}");

        Assert.AreEqual(1, (await Grid("job=nightly")).GetProperty("runs").GetArrayLength());
        Assert.AreEqual(2, (await Grid("path=etl/hourly.nb.md")).GetProperty("runs").GetArrayLength());
        Assert.AreEqual(1, (await Grid("status=failed")).GetProperty("runs").GetArrayLength());
        Assert.AreEqual(2, (await Grid("trigger=schedule")).GetProperty("runs").GetArrayLength());
        Assert.AreEqual(2,
            (await Grid($"since={Uri.EscapeDataString(start.AddMinutes(30).ToString("o"))}"))
                .GetProperty("runs").GetArrayLength());

        // hasMore is what the Next button reads, so it has to be true when there is
        // a next page and false on the last one — off by one here means a button
        // that leads nowhere.
        var page = await Grid("limit=2");
        Assert.AreEqual(2, page.GetProperty("runs").GetArrayLength());
        Assert.IsTrue(page.GetProperty("hasMore").GetBoolean());
        var last = await Grid("limit=2&offset=2");
        Assert.AreEqual(1, last.GetProperty("runs").GetArrayLength());
        Assert.IsFalse(last.GetProperty("hasMore").GetBoolean());

        // Ascending flips the order rather than being ignored.
        var ascending = await Grid("asc=true&sort=started");
        Assert.AreEqual("nightly",
            ascending.GetProperty("runs")[0].GetProperty("jobName").GetString());

        foreach (var bad in new[] { "status=nonsense", "trigger=nonsense", "sort=nonsense" }) {
            var response = await _client.GetAsync($"/api/runs?{bad}");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, bad);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Expected one of");
        }
    }

    [TestMethod]
    public async Task An_ad_hoc_run_can_override_parameters_without_touching_the_yaml() {
        await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", NewJob("adhoc"));

        var response = await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs/adhoc/run",
            new { parameters = new Dictionary<string, object> { ["region"] = "eu", ["extra"] = 7 } });
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);

        // The stored job keeps its own parameters — the override was for that run only.
        var job = await _client.GetFromJsonAsync<JobView>("/api/projects/default/branches/default/jobs/adhoc", _json);
        Assert.AreEqual("us", job.Parameters["region"].ToString());
        Assert.IsFalse(job.Parameters.ContainsKey("extra"));
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(_root, "notebooks", "etl", "nightly.jobs.yaml")), "region: us");
    }

    [TestMethod]
    public async Task A_run_without_a_body_still_works() {
        await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", NewJob("plain"));
        var response = await _client.PostAsync("/api/projects/default/branches/default/jobs/plain/run", null);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode,
            await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Channels_can_be_read_and_written_through_the_api() {
        var empty = await _client.GetFromJsonAsync<JsonElement>("/api/channels");
        Assert.AreEqual(0, empty.GetProperty("channels").GetArrayLength());

        var write = await _client.PutAsJsonAsync("/api/channels", new {
            channels = new object[] {
                new { name = "ops", type = "webhook", url = "https://example.com/hook", bearerSecretRef = "ops-token" },
                new {
                    name = "mail", type = "email", host = "smtp.example.com", port = 587,
                    from = "jobs@example.com", to = new[] { "oncall@example.com" },
                    user = "jobs@example.com", passwordSecretRef = "smtp-password",
                },
            },
        });
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);

        var yaml = File.ReadAllText(Path.Combine(_root, "notebooks", NotificationChannels.FileName));
        StringAssert.Contains(yaml, "bearerSecretRef: ops-token");
        StringAssert.Contains(yaml, "passwordSecretRef: smtp-password");

        var read = await _client.GetFromJsonAsync<JsonElement>("/api/channels");
        Assert.AreEqual(2, read.GetProperty("channels").GetArrayLength());
        Assert.AreEqual(0, read.GetProperty("errors").GetArrayLength());
    }

    [TestMethod]
    public async Task An_invalid_channel_set_is_refused_and_the_old_file_survives() {
        await _client.PutAsJsonAsync("/api/channels", new {
            channels = new object[] {
                new { name = "ops", type = "webhook", url = "https://example.com/hook" },
            },
        });

        var bad = await _client.PutAsJsonAsync("/api/channels", new {
            channels = new object[] { new { name = "broken", type = "webhook" } },   // no url
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);

        var yaml = File.ReadAllText(Path.Combine(_root, "notebooks", NotificationChannels.FileName));
        StringAssert.Contains(yaml, "ops", "the previous channels are still on disk");
        Assert.IsFalse(yaml.Contains("broken"));
    }

    [TestMethod]
    public async Task Settings_are_readable_and_the_writable_ones_persist() {
        var settings = await _client.GetFromJsonAsync<JsonElement>("/api/settings");
        var sections = settings.GetProperty("sections");
        Assert.IsTrue(sections.GetArrayLength() >= 3);
        Assert.IsFalse(settings.GetRawText().Contains("hunter2"),
            "the configured API key must never appear in the settings payload");

        var saved = await _client.PutAsJsonAsync("/api/settings/general",
            new Dictionary<string, object> { ["maxParallelism"] = 7 });
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
        var json = File.ReadAllText(Path.Combine(_options.DataDir, "settings.json"));
        StringAssert.Contains(json, "\"maxParallelism\": 7");

        var refused = await _client.PutAsJsonAsync("/api/settings/security",
            new Dictionary<string, object> { ["apiKey"] = "hijack" });
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [TestMethod]
    public async Task An_unknown_api_route_is_a_json_404_not_the_spa_shell() {
        var response = await _client.GetAsync("/api/nope");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        StringAssert.Contains(response.Content.Headers.ContentType?.MediaType, "json");
    }

    /// <summary>
    /// What the cron field reads back. Answered by the same Cronos that will accept
    /// or refuse the expression at save, so the two cannot disagree — and a bad
    /// expression is an answer rather than an error, because typing one is what
    /// happens on the way to a good one.
    /// </summary>
    [TestMethod]
    public async Task A_cron_preview_says_when_it_runs_or_why_it_will_not() {
        var good = await _client.GetFromJsonAsync<JsonElement>("/api/cron/preview?expression=0 2 * * *");
        Assert.IsTrue(good.GetProperty("valid").GetBoolean());
        var next = good.GetProperty("next").EnumerateArray()
            .Select(t => DateTime.Parse(t.GetString(), null, DateTimeStyles.RoundtripKind)).ToList();
        Assert.AreEqual(5, next.Count);
        CollectionAssert.AllItemsAreUnique(next);
        // UTC, because UTC is what SchedulerService.IsDue compares against. A
        // preview in local time is how somebody schedules the nightly close for
        // the wrong hour.
        Assert.IsTrue(next.All(t => t.Hour == 2), string.Join(", ", next));

        var bad = await _client.GetAsync("/api/cron/preview?expression=every tuesday pls");
        Assert.AreEqual(HttpStatusCode.OK, bad.StatusCode, "a half-typed cron is not a server error");
        var body = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsFalse(body.GetProperty("valid").GetBoolean());
        Assert.IsFalse(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        Assert.AreEqual(0, body.GetProperty("next").GetArrayLength());
    }

    [TestMethod]
    public async Task Triggering_an_unknown_job_is_a_404_and_cancel_needs_an_active_run() {
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.PostAsync("/api/projects/default/branches/default/jobs/nope/run", null)).StatusCode);
        await _client.PostAsJsonAsync("/api/projects/default/branches/default/jobs", NewJob("idle"));
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.PostAsync("/api/projects/default/branches/default/jobs/idle/cancel", null)).StatusCode);
    }
}

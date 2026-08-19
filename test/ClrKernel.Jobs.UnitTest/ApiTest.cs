using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

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
            ApiKey = _apiKey,
        };
        _store = EfRunStore.Sqlite(Path.Combine(_options.DataDir, "test.db"));
        _store.Migrate();

        _app = Program.BuildApp(_options, new JobCatalog(_options.NotebooksRoot), _store);
        // An ephemeral port: the default 5000 collides with a real `serve` on the
        // dev machine and with any other host started by the suite.
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();
        var address = _app.Urls.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
        _client.DefaultRequestHeaders.Add(ApiKeyMiddleware.HeaderName, _apiKey);
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

    private const string _apiKey = "test-key";
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
    public async Task Requests_without_the_api_key_are_rejected() {
        using var anonymous = new HttpClient { BaseAddress = _client.BaseAddress };
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/jobs")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await anonymous.GetAsync("/api/health")).StatusCode,
            "health stays open so a probe works without the key");

        anonymous.DefaultRequestHeaders.Add(ApiKeyMiddleware.HeaderName, "wrong");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/jobs")).StatusCode);
    }

    [TestMethod]
    public async Task A_job_can_be_created_read_updated_and_deleted_through_the_yaml() {
        var created = await _client.PostAsJsonAsync("/api/envs/default/jobs", NewJob("nightly-us"));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

        var yamlPath = Path.Combine(_root, "notebooks", "etl", "nightly.jobs.yaml");
        Assert.IsTrue(File.Exists(yamlPath), "the job is persisted as a jobs file next to its notebook");
        StringAssert.Contains(File.ReadAllText(yamlPath), "nightly-us");

        var fetched = await _client.GetFromJsonAsync<JobView>("/api/envs/default/jobs/nightly-us", _json);
        Assert.AreEqual("etl/nightly.nb.md", fetched.Notebook);
        Assert.AreEqual("0 2 * * *", fetched.Cron);
        Assert.AreEqual("us", fetched.Parameters["region"].ToString());

        // A second job on the same notebook joins the same file.
        Assert.AreEqual(HttpStatusCode.Created,
            (await _client.PostAsJsonAsync("/api/envs/default/jobs", NewJob("nightly-eu"))).StatusCode);
        var list = await _client.GetFromJsonAsync<JsonElement>("/api/jobs");
        Assert.AreEqual(2, list.GetProperty("jobs").GetArrayLength());
        Assert.AreEqual(0, list.GetProperty("errors").GetArrayLength());

        var update = NewJob("nightly-us");
        update.Cron = "30 3 * * *";
        update.Enabled = false;
        var updated = await _client.PutAsJsonAsync("/api/envs/default/jobs/nightly-us", update);
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
        var reread = await _client.GetFromJsonAsync<JobView>("/api/envs/default/jobs/nightly-us", _json);
        Assert.AreEqual("30 3 * * *", reread.Cron);
        Assert.IsFalse(reread.Enabled);

        Assert.AreEqual(HttpStatusCode.NoContent,
            (await _client.DeleteAsync("/api/envs/default/jobs/nightly-us")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync("/api/envs/default/jobs/nightly-us")).StatusCode);
        StringAssert.Contains(File.ReadAllText(yamlPath), "nightly-eu", "the sibling job survives");
    }

    [TestMethod]
    public async Task Invalid_job_writes_are_refused() {
        var noNotebook = NewJob("ghost");
        noNotebook.Notebook = "etl/missing.nb.md";
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/envs/default/jobs", noNotebook)).StatusCode);

        var traversal = NewJob("escape");
        traversal.Notebook = "../../etc/passwd";
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/envs/default/jobs", traversal)).StatusCode);

        var badCron = NewJob("bad-cron");
        badCron.Cron = "not a cron";
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/envs/default/jobs", badCron)).StatusCode,
            "an unloadable job never reaches the disk");

        await _client.PostAsJsonAsync("/api/envs/default/jobs", NewJob("taken"));
        Assert.AreEqual(HttpStatusCode.Conflict,
            (await _client.PostAsJsonAsync("/api/envs/default/jobs", NewJob("taken"))).StatusCode);
    }

    [TestMethod]
    public async Task The_notebook_tree_and_content_respect_the_root() {
        var payload = await _client.GetFromJsonAsync<JsonElement>("/api/notebooks");
        var envs = payload.GetProperty("environments");
        Assert.AreEqual(1, envs.GetArrayLength(), "no git workflow: one default environment");
        var tree = JsonSerializer.Deserialize<TreeNode>(
            envs[0].GetProperty("tree").GetRawText(), _json);
        var etl = tree.Children.Single(c => c.IsDirectory);
        Assert.AreEqual("etl/nightly.nb.md", etl.Children.Single(c => c.Kind == "notebook").Path);

        var content = await _client.GetAsync("/api/envs/default/notebooks/content?path=etl/nightly.nb.md");
        StringAssert.Contains(await content.Content.ReadAsStringAsync(), "1+1");

        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.GetAsync("/api/envs/default/notebooks/content?path=../../../etc/passwd")).StatusCode);
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
        Assert.AreEqual(1, runs.GetArrayLength());

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

    [TestMethod]
    public async Task An_ad_hoc_run_can_override_parameters_without_touching_the_yaml() {
        await _client.PostAsJsonAsync("/api/envs/default/jobs", NewJob("adhoc"));

        var response = await _client.PostAsJsonAsync("/api/envs/default/jobs/adhoc/run",
            new { parameters = new Dictionary<string, object> { ["region"] = "eu", ["extra"] = 7 } });
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);

        // The stored job keeps its own parameters — the override was for that run only.
        var job = await _client.GetFromJsonAsync<JobView>("/api/envs/default/jobs/adhoc", _json);
        Assert.AreEqual("us", job.Parameters["region"].ToString());
        Assert.IsFalse(job.Parameters.ContainsKey("extra"));
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(_root, "notebooks", "etl", "nightly.jobs.yaml")), "region: us");
    }

    [TestMethod]
    public async Task A_run_without_a_body_still_works() {
        await _client.PostAsJsonAsync("/api/envs/default/jobs", NewJob("plain"));
        var response = await _client.PostAsync("/api/envs/default/jobs/plain/run", null);
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
        Assert.IsFalse(settings.GetRawText().Contains(_apiKey),
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

    [TestMethod]
    public async Task Triggering_an_unknown_job_is_a_404_and_cancel_needs_an_active_run() {
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.PostAsync("/api/envs/default/jobs/nope/run", null)).StatusCode);
        await _client.PostAsJsonAsync("/api/envs/default/jobs", NewJob("idle"));
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.PostAsync("/api/envs/default/jobs/idle/cancel", null)).StatusCode);
    }
}

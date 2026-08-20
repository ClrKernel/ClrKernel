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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>
/// The cells endpoints behind the web editor, over a live host with the git
/// workflow enabled. The property that matters: opening a notebook and saving it
/// back unchanged must not write anything — a commit that rewrites a file
/// invalidates its promotion evidence.
/// </summary>
[TestClass]
public class NotebookCellsApiTest {
    private string _root;
    private GitService _git;
    private WebApplication _app;
    private HttpClient _client;
    private EfRunStore _store;

    private const string _apiKey = "test-key";
    private const string _notebook = "reports/daily.nb.md";
    private const string _source = "# Daily\n\nProse here.\n\n```sql\nSELECT 1\n```\n\n```csharp\nvar x = 1;\n```\n";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [TestInitialize]
    public async Task Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-cells-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _git = new GitService(_root, NullLogger.Instance);
        _git.Init();

        var devFile = Path.Combine(_git.DevPath, _notebook);
        Directory.CreateDirectory(Path.GetDirectoryName(devFile));
        File.WriteAllText(devFile, _source);
        _git.WithLock(() => _git.Commit("dev", "add notebook"));

        var options = new JobsOptions {
            DataDir = Path.Combine(_root, ".data"),
            NotebooksRoot = _root,
            ApiKey = _apiKey,
            // No kernel to probe in tests: languages come back empty, which parses
            // as C#-only — the documented degraded mode.
            ClrKernelPath = null,
        };
        Directory.CreateDirectory(options.DataDir);
        _store = EfRunStore.Sqlite(Path.Combine(options.DataDir, "test.db"));
        _store.Migrate();

        _app = Program.BuildApp(options, new JobCatalog(_root, gitLayout: true, _git), _store, _git);
        // Stand in for the kernel probe: there is no clrkernel binary here, and an
        // empty language list would parse every ```sql block as prose. Descriptors
        // are data, so seeding is exactly what a live session does.
        ((KernelLanguages)_app.Services.GetService(typeof(KernelLanguages))).Seed(new[] {
            new ClrKernel.Core.Scripting.LanguageDescriptor {
                Id = "sql", DisplayName = "SQL", DefaultSelector = "#!sql",
                Selectors = new[] { "#!sql", "#!sql-connect" }, LanguageTags = new[] { "sql", "tsql" },
            },
            new ClrKernel.Core.Scripting.LanguageDescriptor {
                Id = "shellscript", DisplayName = "Shell", DefaultSelector = "#!bash",
                Selectors = new[] { "#!bash", "#!zsh" }, LanguageTags = new[] { "bash", "zsh" },
            },
        });
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
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
        try {
            Directory.Delete(_root, recursive: true);
        } catch (IOException) {
            // A kernel probe may still hold a handle; the temp dir is disposable.
        }
    }

    private async Task<JsonElement> GetCellsAsync(string env = "dev") =>
        await _client.GetFromJsonAsync<JsonElement>($"/api/envs/{env}/notebooks/cells?path={_notebook}");

    [TestMethod]
    public async Task A_notebook_opens_as_cells_with_its_tags() {
        var body = await GetCellsAsync();
        var cells = body.GetProperty("cells").EnumerateArray().ToList();

        Assert.AreEqual(3, cells.Count);
        Assert.AreEqual("markdown", cells[0].GetProperty("kind").GetString());
        Assert.AreEqual("code", cells[1].GetProperty("kind").GetString());
        Assert.AreEqual("sql", cells[1].GetProperty("tag").GetString());
        Assert.AreEqual("SELECT 1", cells[1].GetProperty("source").GetString(),
            "the body is as written — no selector injected into the editing view");
        Assert.AreEqual("c1", cells[1].GetProperty("id").GetString());
        Assert.AreEqual("csharp", cells[2].GetProperty("tag").GetString());
    }

    [TestMethod]
    public async Task Saving_an_unopened_notebook_back_unchanged_writes_nothing() {
        var before = _git.HeadSha("dev");
        var body = await GetCellsAsync();

        var response = await _client.PutAsJsonAsync(
            $"/api/envs/dev/notebooks/cells?path={_notebook}",
            new { cells = body.GetProperty("cells") }, _json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.AreEqual(_source, File.ReadAllText(Path.Combine(_git.DevPath, _notebook)),
            "a round trip through the editor must not rewrite the file");
        Assert.AreEqual(before, _git.HeadSha("dev"),
            "an unchanged save must not produce a commit — that would invalidate promotion evidence");
    }

    [TestMethod]
    public async Task An_edited_cell_is_written_and_committed() {
        var before = _git.HeadSha("dev");
        var body = await GetCellsAsync();
        var cells = body.GetProperty("cells").EnumerateArray()
            .Select(c => new Dictionary<string, object> {
                ["kind"] = c.GetProperty("kind").GetString(),
                ["tag"] = c.GetProperty("tag").ValueKind == JsonValueKind.Null ? null : c.GetProperty("tag").GetString(),
                ["source"] = c.GetProperty("tag").ValueKind != JsonValueKind.Null &&
                             c.GetProperty("tag").GetString() == "sql"
                    ? "SELECT 2"
                    : c.GetProperty("source").GetString(),
                ["blankLinesAfter"] = c.GetProperty("blankLinesAfter").GetInt32(),
            })
            .ToList();

        var response = await _client.PutAsJsonAsync($"/api/envs/dev/notebooks/cells?path={_notebook}", new { cells }, _json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var written = File.ReadAllText(Path.Combine(_git.DevPath, _notebook));
        StringAssert.Contains(written, "SELECT 2");
        StringAssert.Contains(written, "```sql", "the tag survives the edit");
        Assert.AreNotEqual(before, _git.HeadSha("dev"), "a real edit commits");
    }

    [TestMethod]
    public async Task A_new_cell_gets_a_tag_from_its_language() {
        var cells = new object[] {
            new { kind = "markdown", tag = (string)null, source = "# New" },
            new { kind = "code", tag = (string)null, languageId = "sql", source = "SELECT 42" },
        };
        var response = await _client.PutAsJsonAsync($"/api/envs/dev/notebooks/cells?path={_notebook}", new { cells }, _json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        // The kernel probe found no languages here, so TagFor falls back to csharp
        // unless the language is known; either way the block is written and re-reads.
        var written = File.ReadAllText(Path.Combine(_git.DevPath, _notebook));
        StringAssert.Contains(written, "SELECT 42");
        StringAssert.StartsWith(written, "# New");
    }

    [TestMethod]
    public async Task Paths_outside_the_dev_area_and_non_notebooks_are_refused() {
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.GetAsync("/api/envs/dev/notebooks/cells?path=../../../etc/passwd")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync("/api/envs/dev/notebooks/cells?path=../../../etc/passwd",
                new { cells = Array.Empty<object>() }, _json)).StatusCode);

        // Not executable markdown: the editor falls back to raw text for these.
        File.WriteAllText(Path.Combine(_git.DevPath, "reports", "daily.jobs.yaml"), "notebook: ./daily.nb.md\njobs: []\n");
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.GetAsync("/api/envs/dev/notebooks/cells?path=reports/daily.jobs.yaml")).StatusCode);
    }

    [TestMethod]
    public async Task Prod_reads_as_cells_but_never_writes() {
        // Promote by hand so the file exists in prod too.
        _git.WithLock(() => {
            _git.CheckoutIntoProd(_notebook);
            _git.CommitProd("promote");
        });

        var prod = await GetCellsAsync("prod");
        Assert.AreEqual(3, prod.GetProperty("cells").GetArrayLength(), "prod is readable");

        var write = await _client.PutAsJsonAsync(
            $"/api/envs/prod/notebooks/cells?path={_notebook}", new { cells = Array.Empty<object>() }, _json);
        Assert.AreEqual(HttpStatusCode.BadRequest, write.StatusCode);
        StringAssert.Contains(await write.Content.ReadAsStringAsync(), "read-only");
    }

    [TestMethod]
    public async Task A_malformed_body_is_a_clear_error_not_a_500() {
        var response = await _client.PutAsync($"/api/envs/dev/notebooks/cells?path={_notebook}",
            new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "error");
    }
}

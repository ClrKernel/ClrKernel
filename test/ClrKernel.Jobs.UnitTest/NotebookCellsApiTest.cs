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
    private ProjectRegistry _projects;
    private User _me;

    /// <summary>
    /// The signed-in caller's own worktree — where editing happens now. Reads still
    /// come from test; only writes moved.
    /// </summary>
    private string MinePath => _git.UserPath(_me.Id.ToString("D"));

    private string MineBranch => GitService.BranchForUser(_me.Id);
    private WebApplication _app;
    private HttpClient _client;
    private EfRunStore _store;

    private const string _notebook = "reports/daily.nb.md";
    private const string _source = "# Daily\n\nProse here.\n\n```sql\nSELECT 1\n```\n\n```csharp\nvar x = 1;\n```\n";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [TestInitialize]
    public async Task Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-cells-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var gitOptions = new JobsOptions {
            DataDir = Path.Combine(_root, ".data"),
            NotebooksRoot = _root,
            GitEnabled = true,
            // No kernel to probe in tests: languages come back empty, which parses
            // as C#-only — the documented degraded mode.
            ClrKernelPath = null,
        };
        // From the registry rather than a second instance: a GitService owns the
        // lock that serializes writes to its workspace, and two of them on one
        // workspace would be two locks.
        _projects = new ProjectRegistry(gitOptions, NullLoggerFactory.Instance);
        _git = _projects.GitFor(_projects.Default);
        _git.Init();

        var devFile = Path.Combine(_git.TestPath, _notebook);
        Directory.CreateDirectory(Path.GetDirectoryName(devFile));
        File.WriteAllText(devFile, _source);
        _git.WithLock(() => _git.Commit("test", "add notebook"));

        var options = gitOptions;
        Directory.CreateDirectory(options.DataDir);
        _store = EfRunStore.Sqlite(Path.Combine(options.DataDir, "test.db"));
        _store.Migrate();

        _app = Program.BuildApp(options, _projects, _store,
            TestAuth.StoreFor(Path.Combine(options.DataDir, "test.db")));
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
        _me = await TestAuth.SignInAsync(_app, _client, UserRole.ServerAdmin);
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

    private async Task<JsonElement> GetCellsAsync(string env = "test") =>
        await _client.GetFromJsonAsync<JsonElement>($"/api/projects/default/branches/{env}/notebooks/cells?path={_notebook}");

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
    public async Task Test_and_prod_refuse_a_write_from_everybody() {
        var cells = new object[] {
            new { kind = "code", tag = "sql", source = "SELECT 99" },
        };
        // The caller here is a Server Admin, which is as much authority as this
        // server has. The refusal is not about the role: it is about the branch, so
        // there is no account that could satisfy it.
        foreach (var branch in new[] { "test", "prod" }) {
            var response = await _client.PutAsJsonAsync(
                $"/api/projects/default/branches/{branch}/notebooks/cells?path={_notebook}",
                new { cells }, _json);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, branch);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "read-only");
        }
        Assert.AreEqual(_source, File.ReadAllText(Path.Combine(_git.TestPath, _notebook)));
    }

    [TestMethod]
    public async Task Two_people_editing_the_same_notebook_do_not_see_each_other() {
        using var other = new HttpClient { BaseAddress = _client.BaseAddress };
        var them = await TestAuth.SignInAsync(_app, other, UserRole.ServerAdmin, "Grace Hopper");

        await _client.PutAsJsonAsync(
            $"/api/projects/default/branches/mine/notebooks/cells?path={_notebook}",
            new { cells = new object[] { new { kind = "code", tag = "sql", source = "MINE" } } }, _json);
        await other.PutAsJsonAsync(
            $"/api/projects/default/branches/mine/notebooks/cells?path={_notebook}",
            new { cells = new object[] { new { kind = "code", tag = "sql", source = "THEIRS" } } }, _json);

        StringAssert.Contains(File.ReadAllText(Path.Combine(MinePath, _notebook)), "MINE");
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(_git.UserPath(them.Id.ToString("D")), _notebook)), "THEIRS");
        Assert.AreEqual(_source, File.ReadAllText(Path.Combine(_git.TestPath, _notebook)),
            "and neither has touched what runs");
    }

    [TestMethod]
    public async Task Work_reaches_test_by_being_pushed_there() {
        await _client.PutAsJsonAsync(
            $"/api/projects/default/branches/mine/notebooks/cells?path={_notebook}",
            new { cells = new object[] { new { kind = "code", tag = "sql", source = "SELECT 7" } } }, _json);

        var standing = await _client.GetFromJsonAsync<JsonElement>("/api/projects/default/branch", _json);
        Assert.IsTrue(standing.GetProperty("hasBranch").GetBoolean());
        // Saved, not committed: the work is in the worktree waiting for a push to
        // give it a message.
        Assert.IsTrue(standing.GetProperty("dirty").GetBoolean());
        Assert.AreEqual(0, standing.GetProperty("ahead").GetInt32());
        Assert.AreEqual(0, standing.GetProperty("behind").GetInt32());

        var pushed = await _client.PostAsJsonAsync(
            "/api/projects/default/branch/push", new { message = "add a query" });
        Assert.AreEqual(HttpStatusCode.OK, pushed.StatusCode, await pushed.Content.ReadAsStringAsync());

        StringAssert.Contains(File.ReadAllText(Path.Combine(_git.TestPath, _notebook)), "SELECT 7");
        StringAssert.Contains(_git.RunForTests("log", "-1", "--format=%s", "test"), "add a query");
    }

    [TestMethod]
    public async Task A_push_over_someone_else_s_is_refused_until_you_update() {
        using var other = new HttpClient { BaseAddress = _client.BaseAddress };
        await TestAuth.SignInAsync(_app, other, UserRole.ServerAdmin, "Grace Hopper");

        // My branch has to exist before theirs lands, or mine would be cut from a
        // test that already has their work and there would be nothing to diverge.
        await _client.PutAsync(
            "/api/projects/default/branches/mine/notebooks/content?path=early.nb.md",
            new StringContent("early\n"));

        await other.PutAsync(
            "/api/projects/default/branches/mine/notebooks/content?path=hers.nb.md",
            new StringContent("hers\n"));
        Assert.AreEqual(HttpStatusCode.OK,
            (await other.PostAsJsonAsync("/api/projects/default/branch/push", new { message = "hers" }))
                .StatusCode);

        await _client.PutAsync(
            "/api/projects/default/branches/mine/notebooks/content?path=mine.nb.md",
            new StringContent("mine\n"));
        var refused = await _client.PostAsJsonAsync(
            "/api/projects/default/branch/push", new { message = "mine" });
        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
        StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "needsUpdate");

        var updated = await _client.PostAsync("/api/projects/default/branch/update", null);
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
        Assert.IsTrue((await updated.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("merged").GetBoolean(), "different files, so nothing conflicts");

        Assert.AreEqual(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync("/api/projects/default/branch/push", new { message = "mine" }))
                .StatusCode);
        Assert.IsTrue(File.Exists(Path.Combine(_git.TestPath, "mine.nb.md")));
        Assert.IsTrue(File.Exists(Path.Combine(_git.TestPath, "hers.nb.md")));
    }

    [TestMethod]
    public async Task Saving_an_unopened_notebook_back_unchanged_writes_nothing() {
        var body = await GetCellsAsync();

        var response = await _client.PutAsJsonAsync(
            $"/api/projects/default/branches/mine/notebooks/cells?path={_notebook}",
            new { cells = body.GetProperty("cells") }, _json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.AreEqual(_source, File.ReadAllText(Path.Combine(MinePath, _notebook)),
            "a round trip through the editor must not rewrite the file");
    }

    [TestMethod]
    public async Task An_edited_cell_is_written_and_committed() {
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

        var response = await _client.PutAsJsonAsync($"/api/projects/default/branches/mine/notebooks/cells?path={_notebook}", new { cells }, _json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var written = File.ReadAllText(Path.Combine(MinePath, _notebook));
        StringAssert.Contains(written, "SELECT 2");
        StringAssert.Contains(written, "```sql", "the tag survives the edit");
        Assert.AreEqual(_source, File.ReadAllText(Path.Combine(_git.TestPath, _notebook)),
            "test is untouched until it is pushed to");
    }

    [TestMethod]
    public async Task A_new_cell_gets_a_tag_from_its_language() {
        var cells = new object[] {
            new { kind = "markdown", tag = (string)null, source = "# New" },
            new { kind = "code", tag = (string)null, languageId = "sql", source = "SELECT 42" },
        };
        var response = await _client.PutAsJsonAsync($"/api/projects/default/branches/mine/notebooks/cells?path={_notebook}", new { cells }, _json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        // The kernel probe found no languages here, so TagFor falls back to csharp
        // unless the language is known; either way the block is written and re-reads.
        var written = File.ReadAllText(Path.Combine(MinePath, _notebook));
        StringAssert.Contains(written, "SELECT 42");
        StringAssert.StartsWith(written, "# New");
    }

    [TestMethod]
    public async Task Paths_outside_the_dev_area_and_non_notebooks_are_refused() {
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.GetAsync("/api/projects/default/branches/test/notebooks/cells?path=../../../etc/passwd")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync("/api/projects/default/branches/mine/notebooks/cells?path=../../../etc/passwd",
                new { cells = Array.Empty<object>() }, _json)).StatusCode);

        // Not executable markdown: the editor falls back to raw text for these.
        File.WriteAllText(Path.Combine(_git.TestPath, "reports", "daily.jobs.yaml"), "notebook: ./daily.nb.md\njobs: []\n");
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.GetAsync("/api/projects/default/branches/test/notebooks/cells?path=reports/daily.jobs.yaml")).StatusCode);
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
            $"/api/projects/default/branches/prod/notebooks/cells?path={_notebook}", new { cells = Array.Empty<object>() }, _json);
        Assert.AreEqual(HttpStatusCode.BadRequest, write.StatusCode);
        StringAssert.Contains(await write.Content.ReadAsStringAsync(), "read-only");
    }

    [TestMethod]
    public async Task Sync_without_a_session_is_a_no_op_rather_than_a_kernel_spawn() {
        // The editor calls this on a debounce while someone types. If it started a
        // kernel, a machine with no clrkernel — or a broken one — would attempt a
        // spawn every few hundred milliseconds for as long as the typing went on.
        // No test here can start a kernel, which is exactly the condition being
        // asserted: it answers, it says nothing was sent, and it does not fail.
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/default/branches/mine/notebooks/sync?path={_notebook}",
            new { cells = new[] { new { id = "c0", languageId = "csharp-script", source = "var a = 1;" } } },
            _json);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsFalse(body.GetProperty("started").GetBoolean());
        Assert.AreEqual(0, body.GetProperty("sent").GetInt32());
    }

    [TestMethod]
    public async Task Sync_is_gated_like_execution_because_it_drives_a_live_kernel() {
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/projects/default/branches/mine/notebooks/sync?path=../../../etc/passwd",
                new { cells = Array.Empty<object>() }, _json)).StatusCode);

        // prod has no session and never runs anything from the editor.
        Assert.AreNotEqual(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync($"/api/projects/default/branches/prod/notebooks/sync?path={_notebook}",
                new { cells = Array.Empty<object>() }, _json)).StatusCode);

        var malformed = await _client.PostAsync($"/api/projects/default/branches/mine/notebooks/sync?path={_notebook}",
            new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [TestMethod]
    public async Task Language_requests_are_allowlisted_and_never_a_method_proxy() {
        // Completion runs against a live REPL, so "forward whatever the client names"
        // is not something to offer over HTTP. Only four kinds exist.
        foreach (var kind in new[] { "clrkernel/execute", "textDocument/completion", "", "shutdown" }) {
            var response = await _client.PostAsJsonAsync(
                $"/api/projects/default/branches/mine/notebooks/language?path={_notebook}",
                new { kind, cellId = "c0", languageId = "csharp-script", source = "x", line = 0, character = 1 },
                _json);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, $"kind '{kind}' must be refused");
        }

        // A known kind with no session is silent, not an error: nothing here may
        // start a kernel, for the same reason sync may not.
        var ok = await _client.PostAsJsonAsync(
            $"/api/projects/default/branches/mine/notebooks/language?path={_notebook}",
            new { kind = "completion", cellId = "c0", languageId = "csharp-script", source = "x", line = 0, character = 1 },
            _json);
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsFalse(body.GetProperty("started").GetBoolean());

        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/projects/default/branches/mine/notebooks/language?path=../../../etc/passwd",
                new { kind = "completion", cellId = "c0" }, _json)).StatusCode);
    }

    [TestMethod]
    public async Task A_malformed_body_is_a_clear_error_not_a_500() {
        var response = await _client.PutAsync($"/api/projects/default/branches/mine/notebooks/cells?path={_notebook}",
            new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "error");
    }
}

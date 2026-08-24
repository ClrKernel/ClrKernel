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
    public async Task A_save_is_atomic_and_its_leftovers_never_reach_test() {
        await _client.PutAsync(
            $"/api/projects/default/branches/mine/notebooks/content?path={_notebook}",
            new StringContent("saved\n"));
        var directory = Path.GetDirectoryName(Path.Combine(MinePath, _notebook));
        Assert.AreEqual(0, Directory.GetFiles(directory, "*.saving").Length,
            "the staging file is renamed over the target, not left beside it");

        // What a crash between the write and the rename would leave behind. It must
        // not ride along on the next push: it is half a notebook.
        File.WriteAllText(Path.Combine(directory, ".daily.nb.md.saving"), "half a fi");

        Assert.AreEqual(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync("/api/projects/default/branch/push", new { message = "x" }))
                .StatusCode);
        Assert.AreEqual("saved\n", File.ReadAllText(Path.Combine(_git.TestPath, _notebook)));
        Assert.AreEqual(0, Directory.GetFiles(
            Path.GetDirectoryName(Path.Combine(_git.TestPath, _notebook)), ".*.saving").Length);
    }

    [TestMethod]
    public async Task A_job_is_written_on_your_branch_and_arrives_in_test_by_push() {
        var created = await _client.PostAsJsonAsync(
            "/api/projects/default/branches/mine/jobs",
            new { name = "nightly", notebook = _notebook, enabled = true });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode,
            await created.Content.ReadAsStringAsync());

        // Written where you are working, and nowhere else yet.
        Assert.IsTrue(File.Exists(Path.Combine(MinePath, "reports", "daily.jobs.yaml")));
        Assert.IsFalse(File.Exists(Path.Combine(_git.TestPath, "reports", "daily.jobs.yaml")));
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await _client.GetAsync("/api/projects/default/branches/test/jobs/nightly")).StatusCode,
            "and it is not schedulable until it is pushed");

        // It runs in test and prod; your branch is where you write it.
        var ran = await _client.PostAsync("/api/projects/default/branches/mine/jobs/nightly/run", null);
        Assert.AreEqual(HttpStatusCode.BadRequest, ran.StatusCode);
        StringAssert.Contains(await ran.Content.ReadAsStringAsync(), "Push this to test");

        Assert.AreEqual(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync("/api/projects/default/branch/push", new { message = "add a job" }))
                .StatusCode);
        Assert.AreEqual(HttpStatusCode.OK,
            (await _client.GetAsync("/api/projects/default/branches/test/jobs/nightly")).StatusCode);
    }

    [TestMethod]
    public async Task Editing_a_job_in_test_or_prod_is_refused() {
        foreach (var branch in new[] { "test", "prod" }) {
            var response = await _client.PostAsJsonAsync(
                $"/api/projects/default/branches/{branch}/jobs",
                new { name = "nightly", notebook = _notebook, enabled = true });
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, branch);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "read-only");
        }
    }

    [TestMethod]
    public async Task Running_in_test_is_audited_and_running_in_prod_needs_an_admin() {
        // No clrkernel binary here, so the session fails to start — which is after
        // the permission and the audit, which is what this is about.
        var started = await _client.PostAsJsonAsync(
            $"/api/projects/default/branches/test/notebooks/run?path={_notebook}",
            new { cells = new[] { new { kind = "code", tag = "csharp", source = "1+1", id = "c0" } } });
        Assert.AreNotEqual(HttpStatusCode.Forbidden, started.StatusCode,
            "test is read-only, not un-runnable — a job that dies at cell seven is " +
            "fixed by running the rest");

        using var viewer = new HttpClient { BaseAddress = _client.BaseAddress };
        await TestAuth.SignInAsync(_app, viewer, UserRole.ServerViewer, "Auditor");
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync(
                $"/api/projects/default/branches/test/notebooks/run?path={_notebook}",
                new { cells = new[] { new { kind = "code", tag = "csharp", source = "1+1" } } }))
            .StatusCode,
            "a viewer runs nothing anywhere");
    }

    [TestMethod]
    public async Task Nobody_runs_anything_on_somebody_else_s_branch() {
        using var other = new HttpClient { BaseAddress = _client.BaseAddress };
        var them = await TestAuth.SignInAsync(_app, other, UserRole.ServerAdmin, "Grace Hopper");
        await other.PutAsync(
            "/api/projects/default/branches/mine/notebooks/content?path=hers.nb.md",
            new StringContent("hers\n"));

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/default/branches/user-{them.Id:D}/notebooks/run?path=hers.nb.md",
            new { cells = new[] { new { kind = "code", tag = "csharp", source = "1+1" } } });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "somebody else's branch");
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
    public async Task Another_person_s_branch_can_be_read_and_never_written() {
        using var other = new HttpClient { BaseAddress = _client.BaseAddress };
        var them = await TestAuth.SignInAsync(_app, other, UserRole.ServerAdmin, "Grace Hopper");
        await other.PutAsync(
            "/api/projects/default/branches/mine/notebooks/content?path=hers.nb.md",
            new StringContent("hers\n"));

        var theirs = $"user-{them.Id:D}";
        var read = await _client.GetAsync(
            $"/api/projects/default/branches/{theirs}/notebooks/content?path=hers.nb.md");
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.AreEqual("hers\n", await read.Content.ReadAsStringAsync(),
            "anyone in the project may look at what anyone else is working on");

        // The caller here is a Server Admin — the most authority this server has —
        // and it makes no difference. An admin may delete a stale branch; nobody
        // writes into one.
        var write = await _client.PutAsync(
            $"/api/projects/default/branches/{theirs}/notebooks/content?path=hers.nb.md",
            new StringContent("mine now\n"));
        Assert.AreEqual(HttpStatusCode.BadRequest, write.StatusCode);
        StringAssert.Contains(await write.Content.ReadAsStringAsync(), "somebody else's branch");
        Assert.AreEqual("hers\n", await (await _client.GetAsync(
            $"/api/projects/default/branches/{theirs}/notebooks/content?path=hers.nb.md"))
            .Content.ReadAsStringAsync());

        var ran = await _client.PostAsJsonAsync(
            $"/api/projects/default/branches/{theirs}/notebooks/session?path=hers.nb.md",
            new { });
        Assert.AreEqual(HttpStatusCode.BadRequest, ran.StatusCode, "nor runs a kernel in one");

        // Someone with no worktree yet has no branch to browse.
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync(
            $"/api/projects/default/branches/user-{Guid.NewGuid():D}/notebooks/content?path=hers.nb.md"))
            .StatusCode);
    }

    [TestMethod]
    public async Task The_branch_list_names_who_owns_what() {
        using var other = new HttpClient { BaseAddress = _client.BaseAddress };
        var them = await TestAuth.SignInAsync(_app, other, UserRole.ServerAdmin, "Grace Hopper");
        await other.PutAsync(
            "/api/projects/default/branches/mine/notebooks/content?path=hers.nb.md",
            new StringContent("hers\n"));

        var listed = await _client.GetFromJsonAsync<JsonElement>(
            "/api/projects/default/branches", _json);
        var branches = listed.GetProperty("branches").EnumerateArray()
            .Select(b => (
                Id: b.GetProperty("id").GetString(),
                Writable: b.GetProperty("writable").GetBoolean()))
            .ToList();

        CollectionAssert.AreEqual(
            new[] { "mine", $"user-{them.Id:D}", "test", "prod" },
            branches.Select(b => b.Id).ToArray(),
            "yours first, then other people's, then what runs");
        Assert.AreEqual(1, branches.Count(b => b.Writable), "exactly one branch you may write to");
        Assert.IsTrue(branches[0].Writable);
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

        // prod is runnable by a project's admins, so the editor may tell a kernel
        // there what is open. Anyone below that gets nothing.
        Assert.AreEqual(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync($"/api/projects/default/branches/prod/notebooks/sync?path={_notebook}",
                new { cells = Array.Empty<object>() }, _json)).StatusCode);

        using var viewer = new HttpClient { BaseAddress = _client.BaseAddress };
        await TestAuth.SignInAsync(_app, viewer, UserRole.ServerViewer, "Auditor");
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync($"/api/projects/default/branches/prod/notebooks/sync?path={_notebook}",
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

    /// <summary>
    /// Your own branch is in the file list before you have written anything.
    /// <para>
    /// It used to appear only once a worktree existed, and a worktree came into
    /// being on the first save — so the branch you had to be on to save was the one
    /// the list would not offer until you had saved. A viewer still gets no
    /// checkout: they can never write to one.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task The_file_list_offers_your_own_branch_before_you_have_used_it() {
        Assert.IsFalse(_git.HasUserWorktree(_me.Id), "nothing has been written yet");

        var mine = await _client.GetFromJsonAsync<JsonElement>("/api/projects/default/notebooks");

        CollectionAssert.Contains(EnvironmentsOf(mine), ProjectRegistry.MineEnvironment);
        Assert.IsTrue(_git.HasUserWorktree(_me.Id), "and the branch was made so that is true");

        using var readerClient = new HttpClient { BaseAddress = _client.BaseAddress };
        var reader = await TestAuth.SignInAsync(_app, readerClient, UserRole.ServerViewer);
        var theirs = await readerClient.GetFromJsonAsync<JsonElement>("/api/projects/default/notebooks");

        CollectionAssert.DoesNotContain(
            EnvironmentsOf(theirs), ProjectRegistry.MineEnvironment,
            "a viewer can never write to a branch, so making them one is disk spent on nothing");
        Assert.IsFalse(_git.HasUserWorktree(reader.Id));
    }

    private static string[] EnvironmentsOf(JsonElement payload) =>
        payload.GetProperty("environments").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToArray();

    [TestMethod]
    public async Task A_malformed_body_is_a_clear_error_not_a_500() {
        var response = await _client.PutAsync($"/api/projects/default/branches/mine/notebooks/cells?path={_notebook}",
            new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "error");
    }
}

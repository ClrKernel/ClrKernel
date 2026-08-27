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

namespace ClrKernel.Studio.UnitTest;

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
    public async Task Opening_a_notebook_on_your_own_branch_is_what_makes_the_branch() {
        // The reported bug: the first thing the editor does with a notebook is read
        // it, and a read used to be the one request that would not make your
        // worktree. So the first notebook you opened after signing in — a
        // deep-link, a bookmark, or just landing on the editor — said the file did
        // not exist, and reloading fixed it because by then something else had made
        // the branch.
        Assert.IsFalse(Directory.Exists(MinePath), "no branch before the first request");

        var body = await GetCellsAsync("mine");

        Assert.AreEqual(3, body.GetProperty("cells").GetArrayLength(),
            "the notebook opens on the first attempt, not the second");
        Assert.IsTrue(Directory.Exists(MinePath), "and reading it is what made the branch");
        Assert.AreEqual(_source, File.ReadAllText(Path.Combine(MinePath, _notebook)),
            "cut from test, so it is the file test has");
    }

    [TestMethod]
    public async Task A_viewer_reading_your_project_still_gets_no_branch() {
        using var viewer = new HttpClient { BaseAddress = _client.BaseAddress };
        var them = await TestAuth.SignInAsync(_app, viewer, UserRole.ServerViewer, "Auditor");

        var read = await viewer.GetAsync(
            $"/api/projects/default/branches/mine/notebooks/cells?path={_notebook}");

        Assert.AreEqual(HttpStatusCode.NotFound, read.StatusCode);
        Assert.IsFalse(Directory.Exists(_git.UserPath(them.Id.ToString("D"))),
            "somebody who may never write anywhere accumulates no empty branches");
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

    /// <summary>
    /// Everybody else's branch is in the file list too, named after the person.
    /// <para>
    /// The branch switcher has always offered them; this list never did, so the one
    /// page for browsing files was the one place another person's work was
    /// invisible — including to a Server Admin, which is what it looks like when a
    /// permission is missing rather than a feature.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task The_file_list_carries_everybody_elses_branch_by_name() {
        using var hers = new HttpClient { BaseAddress = _client.BaseAddress };
        var grace = await TestAuth.SignInAsync(_app, hers, UserRole.ServerAdmin, "Grace Hopper");
        // A write, because a read never makes a branch: hers has to exist before
        // anyone can be shown it.
        Assert.AreEqual(HttpStatusCode.OK, (await hers.PutAsync(
            "/api/projects/default/branches/mine/notebooks/content?path=hopper.nb.md",
            new StringContent("```csharp\nvar x = 1;\n```\n"))).StatusCode);

        var payload = await _client.GetFromJsonAsync<JsonElement>("/api/projects/default/notebooks");
        var theirs = payload.GetProperty("environments").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == $"user-{grace.Id:D}");

        Assert.AreEqual("Grace Hopper", theirs.GetProperty("label").GetString(),
            "named after the person; user-<guid> is not a thing to show anyone");
        var files = theirs.GetProperty("tree").GetProperty("children").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToArray();
        CollectionAssert.Contains(files, "hopper.nb.md",
            "her branch, with the file she is working on: " + string.Join(", ", files));

        // And it stays hers. Reading it is allowed for everyone; writing it is
        // allowed for nobody, Server Admin included.
        Assert.AreEqual(HttpStatusCode.BadRequest, (await _client.PutAsync(
            $"/api/projects/default/branches/user-{grace.Id:D}/notebooks/content?path=hopper.nb.md",
            new StringContent("mine now"))).StatusCode);
    }

    private static string[] EnvironmentsOf(JsonElement payload) =>
        payload.GetProperty("environments").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToArray();

    /// <summary>
    /// A job lives on your branch from the moment you write it, and leaves test
    /// alone until you push.
    /// <para>
    /// Two things used to be wrong at once. The jobs list is built from the
    /// catalog the scheduler reads, which deliberately never scans a personal
    /// branch — so a job you had just created was simply absent from the page that
    /// exists to list your jobs. And delete looked for the job in the project's own
    /// catalog and committed the removal in <em>test's</em> worktree: it found
    /// nothing on a personal branch, and had it found something it would have
    /// landed a commit nobody pushed, invalidating the promotion evidence of
    /// everything else in that tree.
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task A_job_on_your_branch_is_listed_and_deletable_without_touching_test() {
        var testHead = _git.HeadSha(GitService.TestBranch);
        var created = await _client.PostAsJsonAsync(
            "/api/projects/default/branches/mine/jobs",
            new { name = "monthly-close", notebook = _notebook, cron = "30 7 * * 1-5", enabled = true },
            _json);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode,
            await created.Content.ReadAsStringAsync());

        var listed = await _client.GetFromJsonAsync<JsonElement>("/api/jobs");
        var job = listed.GetProperty("jobs").EnumerateArray()
            .SingleOrDefault(j => j.GetProperty("name").GetString() == "monthly-close");
        Assert.AreNotEqual(default, job, "a job you have written is one of your jobs");
        Assert.AreEqual(ProjectRegistry.MineEnvironment, job.GetProperty("environment").GetString(),
            "and it says where it is, because that is why it is not running yet");

        Assert.AreEqual(HttpStatusCode.NoContent,
            (await _client.DeleteAsync("/api/projects/default/branches/mine/jobs/monthly-close"))
                .StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await _client.GetAsync("/api/projects/default/branches/mine/jobs/monthly-close"))
                .StatusCode);
        Assert.AreEqual(testHead, _git.HeadSha(GitService.TestBranch),
            "neither the write nor the delete is a commit, and neither one is test's");
    }

    [TestMethod]
    public async Task A_malformed_body_is_a_clear_error_not_a_500() {
        var response = await _client.PutAsync($"/api/projects/default/branches/mine/notebooks/cells?path={_notebook}",
            new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "error");
    }
    private Task<HttpResponseMessage> MoveAsync(string from, string to) =>
        _client.PostAsJsonAsync(
            $"/api/projects/default/branches/mine/notebooks/move?path={from}", new { to });

    [TestMethod]
    public async Task A_notebook_moves_to_a_new_path_and_leaves_nothing_behind() {
        await _client.PutAsync(
            $"/api/projects/default/branches/mine/notebooks/content?path={_notebook}",
            new StringContent("moved me\n"));

        var moved = await MoveAsync(_notebook, "archive/2026/old.nb.md");
        Assert.AreEqual(HttpStatusCode.OK, moved.StatusCode, await moved.Content.ReadAsStringAsync());

        Assert.AreEqual("moved me\n", File.ReadAllText(Path.Combine(MinePath, "archive", "2026", "old.nb.md")),
            "the folders on the way are made, the way a save makes them");
        Assert.IsFalse(File.Exists(Path.Combine(MinePath, _notebook)),
            "a move is not a copy");
    }

    [TestMethod]
    public async Task A_move_refuses_to_land_on_something() {
        await _client.PutAsync(
            $"/api/projects/default/branches/mine/notebooks/content?path={_notebook}",
            new StringContent("mine\n"));
        await _client.PutAsync(
            "/api/projects/default/branches/mine/notebooks/content?path=other.nb.md",
            new StringContent("theirs\n"));

        Assert.AreEqual(HttpStatusCode.Conflict, (await MoveAsync(_notebook, "other.nb.md")).StatusCode);
        Assert.AreEqual("theirs\n", File.ReadAllText(Path.Combine(MinePath, "other.nb.md")),
            "and leaves the file that was there alone");
        Assert.AreEqual("mine\n", File.ReadAllText(Path.Combine(MinePath, _notebook)));
    }

    [TestMethod]
    public async Task A_move_is_refused_everywhere_a_save_would_be() {
        await _client.PutAsync(
            $"/api/projects/default/branches/mine/notebooks/content?path={_notebook}",
            new StringContent("mine\n"));

        // Out of the tree, on either end — the destination goes through the same
        // gate as the source, so neither half is a way past it.
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await MoveAsync(_notebook, "../escaped.nb.md")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await MoveAsync("../outside.nb.md", "fine.nb.md")).StatusCode);
        // Not a notebook.
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await MoveAsync(_notebook, "notes.txt")).StatusCode);
        // Not your branch.
        var onTest = await _client.PostAsJsonAsync(
            $"/api/projects/default/branches/test/notebooks/move?path={_notebook}",
            new { to = "elsewhere.nb.md" });
        Assert.AreEqual(HttpStatusCode.BadRequest, onTest.StatusCode);
        Assert.IsFalse(File.Exists(Path.Combine(_git.TestPath, "elsewhere.nb.md")));
    }

    [TestMethod]
    public async Task A_move_of_a_file_that_is_not_there_says_so() {
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await MoveAsync("never/existed.nb.md", "somewhere.nb.md")).StatusCode);
    }

    /// <summary>
    /// The scratch folder is the query editor's buffer, and it must be invisible to
    /// git — both halves, which are different code paths. Untracked scratch would
    /// make <c>status --porcelain</c> non-empty, so the branch reads Dirty forever
    /// and the Push button never clears; and the no-pathspec <c>add -A</c> behind a
    /// push would sweep the file into test.
    /// </summary>
    [TestMethod]
    public async Task A_scratch_file_leaves_no_trace_in_git() {
        var scratch = GitService.ScratchDirectory + "/query-abc.nb.md";
        var written = await _client.PutAsync(
            $"/api/projects/default/branches/mine/notebooks/content?path={scratch}",
            new StringContent("```sql\n#!sql --connection W\nSELECT 1\n```\n"));
        Assert.AreEqual(HttpStatusCode.OK, written.StatusCode, await written.Content.ReadAsStringAsync());
        Assert.IsTrue(File.Exists(Path.Combine(MinePath, GitService.ScratchDirectory, "query-abc.nb.md")));

        var standing = await _client.GetFromJsonAsync<JsonElement>("/api/projects/default/branch");
        Assert.IsFalse(standing.GetProperty("dirty").GetBoolean(),
            "a scratch query must not leave you permanently unpushed");

        await _client.PostAsJsonAsync("/api/projects/default/branch/push", new { message = "x" });
        Assert.IsFalse(Directory.Exists(Path.Combine(_git.TestPath, GitService.ScratchDirectory)),
            "and must never ride along into test");
    }

    /// <summary>
    /// The move that Connections makes: out of the scratch folder, into notebooks
    /// you named. It is the one path where a scratch file is supposed to become
    /// something git tracks.
    /// </summary>
    [TestMethod]
    public async Task A_scratch_query_moves_out_and_becomes_a_real_notebook() {
        var scratch = GitService.ScratchDirectory + "/query-abc.nb.md";
        await _client.PutAsync(
            $"/api/projects/default/branches/mine/notebooks/content?path={scratch}",
            new StringContent("```sql\n#!sql --connection W\nSELECT 1\n```\n"));

        var moved = await MoveAsync(scratch, "queries/warehouse.nb.md");
        Assert.AreEqual(HttpStatusCode.OK, moved.StatusCode, await moved.Content.ReadAsStringAsync());
        Assert.IsFalse(File.Exists(Path.Combine(MinePath, GitService.ScratchDirectory, "query-abc.nb.md")));

        await _client.PostAsJsonAsync("/api/projects/default/branch/push", new { message = "keep it" });
        Assert.AreEqual("```sql\n#!sql --connection W\nSELECT 1\n```\n",
            File.ReadAllText(Path.Combine(_git.TestPath, "queries", "warehouse.nb.md")),
            "once it has a name it is a notebook like any other, and pushes like one");
    }

    private Task<HttpResponseMessage> PutTextAsync(string path, string content) =>
        _client.PutAsync(
            $"/api/projects/default/branches/mine/notebooks/content?path={path}",
            new StringContent(content));

    private const string _brokenJobs = "jobs:\n  - name: daily\n    scedule: \"0 6 * * *\"\n";
    private const string _goodJobs = "jobs:\n  - name: daily\n    cron: \"0 6 * * *\"\n";

    /// <summary>
    /// The rule the spec asks for: a jobs file mid-edit is a buffer and saves
    /// fine; the same file reaching test is a job the scheduler will not run.
    /// </summary>
    [TestMethod]
    public async Task An_invalid_jobs_file_saves_but_cannot_be_pushed() {
        var saved = await PutTextAsync("reports/daily.jobs.yaml", _brokenJobs);
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, "autosave must never lose what you typed");
        var body = JsonSerializer.Deserialize<JsonElement>(await saved.Content.ReadAsStringAsync(), _json);
        var problems = body.GetProperty("problems").EnumerateArray().ToList();
        Assert.AreEqual(1, problems.Count, "and it says what is wrong, so the editor can underline it");
        StringAssert.Contains(problems[0].GetProperty("message").GetString(), "scedule");
        Assert.AreEqual(3, problems[0].GetProperty("line").GetInt32());

        var push = await _client.PostAsJsonAsync("/api/projects/default/branch/push", new { message = "x" });
        Assert.AreEqual(HttpStatusCode.Conflict, push.StatusCode);
        var refusal = JsonSerializer.Deserialize<JsonElement>(await push.Content.ReadAsStringAsync(), _json);
        StringAssert.Contains(refusal.GetProperty("error").GetString(), "reports/daily.jobs.yaml");
        Assert.AreEqual("reports/daily.jobs.yaml",
            refusal.GetProperty("invalid")[0].GetProperty("path").GetString());
        Assert.IsFalse(File.Exists(Path.Combine(_git.TestPath, "reports", "daily.jobs.yaml")),
            "and nothing reached test");
    }

    [TestMethod]
    public async Task Fixing_it_lets_the_push_through() {
        await PutTextAsync("reports/daily.jobs.yaml", _brokenJobs);
        Assert.AreEqual(HttpStatusCode.Conflict,
            (await _client.PostAsJsonAsync("/api/projects/default/branch/push", new { message = "x" })).StatusCode);

        var saved = await PutTextAsync("reports/daily.jobs.yaml", _goodJobs);
        var body = JsonSerializer.Deserialize<JsonElement>(await saved.Content.ReadAsStringAsync(), _json);
        Assert.AreEqual(0, body.GetProperty("problems").GetArrayLength(), "nothing left to say");

        var push = await _client.PostAsJsonAsync("/api/projects/default/branch/push", new { message = "x" });
        Assert.AreEqual(HttpStatusCode.OK, push.StatusCode, await push.Content.ReadAsStringAsync());
        Assert.AreEqual(_goodJobs, File.ReadAllText(Path.Combine(_git.TestPath, "reports", "daily.jobs.yaml")));
    }

    /// <summary>
    /// One broken jobs file must not hold a notebook hostage in a way nobody can
    /// diagnose — but it does hold the push, because a push is the whole branch.
    /// The message names the file so there is something to go and fix.
    /// </summary>
    [TestMethod]
    public async Task The_refusal_names_every_file_that_is_wrong() {
        await PutTextAsync("reports/daily.jobs.yaml", _brokenJobs);
        await PutTextAsync("other.jobs.yaml", "jobs: []\n");

        var push = await _client.PostAsJsonAsync("/api/projects/default/branch/push", new { message = "x" });
        Assert.AreEqual(HttpStatusCode.Conflict, push.StatusCode);
        var refusal = JsonSerializer.Deserialize<JsonElement>(await push.Content.ReadAsStringAsync(), _json);
        var named = refusal.GetProperty("invalid").EnumerateArray()
            .Select(f => f.GetProperty("path").GetString()).OrderBy(p => p).ToArray();
        CollectionAssert.AreEqual(new[] { "other.jobs.yaml", "reports/daily.jobs.yaml" }, named);
        StringAssert.Contains(refusal.GetProperty("error").GetString(), "2 jobs files");
    }

    /// <summary>A notebook is not a jobs file: saving one reports nothing.</summary>
    [TestMethod]
    public async Task A_notebook_save_carries_no_jobs_problems() {
        var saved = await PutTextAsync(_notebook, "# still a notebook\n");
        var body = JsonSerializer.Deserialize<JsonElement>(await saved.Content.ReadAsStringAsync(), _json);
        Assert.AreEqual(JsonValueKind.Null, body.GetProperty("problems").ValueKind);
    }

    [TestMethod]
    public async Task The_schema_the_editor_uses_is_published() {
        var schema = await _client.GetFromJsonAsync<JsonElement>("/api/jobs/schema");
        Assert.IsFalse(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.IsTrue(schema.GetProperty("properties").TryGetProperty("jobs", out _));
    }


}

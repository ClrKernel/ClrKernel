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
/// Two tiers of role over two projects, against a live host.
/// <para>
/// The property under test is the one the spec leans on hardest: a project you
/// have no grant on is not merely refused, it is <em>invisible</em> — absent from
/// every list and 404 on every route that names it. Refusing with 403 would leak
/// the name of every project on the server to anyone willing to guess.
/// </para>
/// </summary>
[TestClass]
public class ProjectRoleTest {
    private string _root;
    private JobsOptions _options;
    private IAuthStore _auth;
    private WebApplication _app;
    private HttpClient _anonymous;
    private IRunStore _store;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [TestInitialize]
    public async Task Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-roles-" + Guid.NewGuid().ToString("N"));
        foreach (var name in new[] { "data", "reports", "finance", "shipping" }) {
            Directory.CreateDirectory(Path.Combine(_root, name));
        }
        foreach (var (project, notebook) in new[] { ("reports", "etl"), ("finance", "close") }) {
            File.WriteAllText(Path.Combine(_root, project, $"{notebook}.nb.md"), "```csharp\n1+1\n```\n");
            File.WriteAllText(Path.Combine(_root, project, $"{notebook}.jobs.yaml"),
                $"notebook: ./{notebook}.nb.md\njobs: [{{name: nightly}}]\n");
        }

        _options = new JobsOptions {
            DataDir = Path.Combine(_root, "data"),
            NotebooksRoot = Path.Combine(_root, "reports"),
        };
        // A third one with the workflow on: running is a test/prod question, and
        // the other two are flat folders where there is no such thing.
        File.WriteAllText(Path.Combine(_root, "shipping", "manifest.nb.md"), "```csharp\n1+1\n```\n");
        File.WriteAllText(Path.Combine(_root, "shipping", "manifest.jobs.yaml"), "jobs:\n  - name: nightly\n");
        new GitService(Path.Combine(_root, "shipping"), NullLogger.Instance).Init();

        ProjectsFile.Write(_options.DataDir, new[] {
            new Project { Slug = "reports", Name = "Reports", Root = Path.Combine(_root, "reports") },
            new Project { Slug = "finance", Name = "Finance", Root = Path.Combine(_root, "finance") },
            new Project {
                Slug = "shipping", Name = "Shipping",
                Root = Path.Combine(_root, "shipping"), GitEnabled = true,
            },
        });

        var dbPath = Path.Combine(_options.DataDir, "test.db");
        var store = EfRunStore.Sqlite(dbPath);
        store.Migrate();
        _store = store;
        _auth = TestAuth.StoreFor(dbPath);

        _app = Program.BuildApp(
            _options, new ProjectRegistry(_options, NullLoggerFactory.Instance), store, _auth);
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();
        _anonymous = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    [TestCleanup]
    public async Task Cleanup() {
        _anonymous?.Dispose();
        if (_app != null) {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TempDirectory.Delete(_root);
    }

    private async Task<HttpClient> ClientFor(UserRole role, params (string Project, ProjectRole Role)[] grants) {
        var client = new HttpClient { BaseAddress = _anonymous.BaseAddress };
        var user = await TestAuth.SignInAsync(_app, client, role, Guid.NewGuid().ToString("N")[..8]);
        foreach (var (project, granted) in grants) {
            await _auth.SetMemberAsync(project, user.Id, granted, DateTime.UtcNow);
        }
        return client;
    }

    private static async Task<string[]> SlugsAsync(HttpClient client) {
        var payload = await client.GetFromJsonAsync<JsonElement>("/api/projects", _json);
        return payload.GetProperty("projects").EnumerateArray()
            .Select(p => p.GetProperty("slug").GetString()).ToArray();
    }

    // --- how the two tiers compose ------------------------------------------

    [TestMethod]
    public void A_grant_raises_access_and_never_lowers_it() {
        var admin = new User { Role = UserRole.ServerAdmin };
        var viewer = new User { Role = UserRole.ServerViewer };
        var user = new User { Role = UserRole.ServerUser };

        Assert.AreEqual(ProjectRole.ProjectAdmin, ProjectAccess.Effective(admin, null));
        Assert.AreEqual(ProjectRole.ProjectAdmin, ProjectAccess.Effective(admin, ProjectRole.ProjectViewer),
            "a grant cannot demote a Server Admin");
        Assert.AreEqual(ProjectRole.ProjectViewer, ProjectAccess.Effective(viewer, null));
        Assert.AreEqual(ProjectRole.ProjectMember, ProjectAccess.Effective(viewer, ProjectRole.ProjectMember));
        Assert.IsNull(ProjectAccess.Effective(user, null), "the baseline sees nothing");
        Assert.AreEqual(ProjectRole.ProjectAdmin, ProjectAccess.Effective(user, ProjectRole.ProjectAdmin));
        Assert.IsNull(ProjectAccess.Effective(new User { Role = UserRole.ServerAdmin, Disabled = true }, null),
            "a disabled account is nobody");
    }

    // --- non-enumerability ---------------------------------------------------

    [TestMethod]
    public async Task A_server_user_sees_only_the_projects_they_were_granted() {
        using var user = await ClientFor(UserRole.ServerUser, ("finance", ProjectRole.ProjectMember));

        var slugs = await SlugsAsync(user);
        CollectionAssert.AreEqual(new[] { "finance" }, slugs, string.Join(",", slugs));

        // Every shape of "reports" is 404 — the same answer a slug nobody
        // registered gets, which is the point.
        foreach (var url in new[] {
            "/api/projects/reports",
            "/api/projects/reports/notebooks",
            "/api/projects/reports/branches/default/notebooks/content?path=etl.nb.md",
            "/api/projects/reports/branches/default/jobs/nightly",
            "/api/projects/reports/members",
        }) {
            Assert.AreEqual(HttpStatusCode.NotFound, (await user.GetAsync(url)).StatusCode, url);
        }
    }

    [TestMethod]
    public async Task Lists_that_span_projects_are_filtered_to_what_you_may_see() {
        using var user = await ClientFor(UserRole.ServerUser, ("finance", ProjectRole.ProjectViewer));

        var jobs = await user.GetFromJsonAsync<JsonElement>("/api/jobs", _json);
        var projects = jobs.GetProperty("jobs").EnumerateArray()
            .Select(j => j.GetProperty("project").GetString()).Distinct().ToArray();
        CollectionAssert.AreEqual(new[] { "finance" }, projects);

        var health = await user.GetFromJsonAsync<JsonElement>("/api/health", _json);
        Assert.AreEqual(1, health.GetProperty("projects").GetInt32());
    }

    [TestMethod]
    public async Task A_server_viewer_reads_every_project_and_writes_to_none() {
        using var viewer = await ClientFor(UserRole.ServerViewer);

        CollectionAssert.AreEquivalent(
            new[] { "reports", "finance", "shipping" }, await SlugsAsync(viewer));
        Assert.AreEqual(HttpStatusCode.OK,
            (await viewer.GetAsync("/api/projects/reports/notebooks")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync("/api/projects/reports/branches/default/jobs",
                new { name = "x", notebook = "etl.nb.md" })).StatusCode);
    }

    // --- what each project role may do --------------------------------------

    private static IEnumerable<(string Method, string Path, object Body, ProjectRole Needs)> Routes() {
        const string p = "/api/projects/finance";
        const string b = p + "/branches/default";
        yield return ("GET", p + "/notebooks", null, ProjectRole.ProjectViewer);
        yield return ("GET", b + "/notebooks/content?path=close.nb.md", null, ProjectRole.ProjectViewer);
        yield return ("GET", b + "/jobs/nightly", null, ProjectRole.ProjectViewer);
        yield return ("POST", b + "/jobs", new { name = "x", notebook = "close.nb.md" },
            ProjectRole.ProjectMember);
        yield return ("PUT", b + "/notebooks/content?path=close.nb.md", "x", ProjectRole.ProjectMember);
        yield return ("POST", b + "/jobs/nightly/run", null, ProjectRole.ProjectMember);
        yield return ("GET", p + "/members", null, ProjectRole.ProjectAdmin);
        yield return ("PUT", p, new { name = "Finance" }, ProjectRole.ProjectAdmin);
        yield return ("POST", p + "/init", null, ProjectRole.ProjectAdmin);
    }

    [TestMethod]
    public async Task Each_route_needs_the_project_role_it_says_it_needs() {
        foreach (var role in new[] {
            ProjectRole.ProjectViewer, ProjectRole.ProjectMember, ProjectRole.ProjectAdmin,
        }) {
            using var client = await ClientFor(UserRole.ServerUser, ("finance", role));
            foreach (var (method, path, body, needs) in Routes()) {
                var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (body != null) {
                    request.Content = body is string text
                        ? new StringContent(text)
                        : JsonContent.Create(body);
                }
                var status = (await client.SendAsync(request)).StatusCode;
                if (role < needs) {
                    Assert.AreEqual(HttpStatusCode.Forbidden, status,
                        $"{role} must not reach {method} {path}");
                } else {
                    Assert.AreNotEqual(HttpStatusCode.Forbidden, status,
                        $"{role} must reach {method} {path}");
                    Assert.AreNotEqual(HttpStatusCode.NotFound, status,
                        $"{role} must be able to see {method} {path}");
                }
            }
        }
    }

    [TestMethod]
    public async Task Members_run_in_test_and_only_admins_run_in_prod() {
        var body = new { cells = new[] { new { kind = "code", tag = "csharp", source = "1+1" } } };
        const string notebook = "manifest.nb.md";

        using var member = await ClientFor(UserRole.ServerUser, ("shipping", ProjectRole.ProjectMember));
        using var admin = await ClientFor(UserRole.ServerUser, ("shipping", ProjectRole.ProjectAdmin));
        using var viewer = await ClientFor(UserRole.ServerUser, ("shipping", ProjectRole.ProjectViewer));

        // Nothing here starts a kernel — there is no clrkernel binary — so what is
        // under test is the refusal, and 403 is the only status that means refused.
        foreach (var (client, branch, refused) in new[] {
            (member, "test", false),
            (member, "prod", true),
            (admin, "prod", false),
            (viewer, "test", true),
        }) {
            var status = (await client.PostAsJsonAsync(
                $"/api/projects/shipping/branches/{branch}/notebooks/run?path={notebook}", body))
                .StatusCode;
            if (refused) {
                Assert.AreEqual(HttpStatusCode.Forbidden, status, $"{branch}");
            } else {
                Assert.AreNotEqual(HttpStatusCode.Forbidden, status, $"{branch}");
            }
        }
    }

    /// <summary>
    /// The same rule, whichever button starts it. A permission only the rerun route
    /// enforced would be worth nothing: the person refused a rerun would press Run
    /// and get the same job at the same HEAD.
    /// </summary>
    [TestMethod]
    public async Task Only_admins_start_a_job_in_prod_by_hand_or_by_rerun() {
        var recorded = await _store.CreateRunAsync(new Run {
            Id = Guid.NewGuid(),
            Project = "shipping",
            Environment = "prod",
            JobName = "nightly",
            NotebookPath = "manifest.nb.md",
            Status = RunStatus.Failed,
            Trigger = RunTrigger.Schedule,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow,
        });

        using var member = await ClientFor(UserRole.ServerUser, ("shipping", ProjectRole.ProjectMember));
        using var admin = await ClientFor(UserRole.ServerUser, ("shipping", ProjectRole.ProjectAdmin));

        foreach (var (client, refused, who) in new[] { (member, true, "member"), (admin, false, "admin") }) {
            foreach (var (what, response) in new[] {
                ("run", await client.PostAsync(
                    "/api/projects/shipping/branches/prod/jobs/nightly/run", null)),
                ("rerun", await client.PostAsJsonAsync(
                    "/api/runs/rerun", new { runIds = new[] { recorded.Id } })),
            }) {
                if (refused) {
                    Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, $"{who} {what}");
                } else {
                    Assert.AreNotEqual(HttpStatusCode.Forbidden, response.StatusCode, $"{who} {what}");
                }
            }
        }

        // And a viewer cannot rerun anywhere — refused before the branch is read.
        using var viewer = await ClientFor(UserRole.ServerUser, ("shipping", ProjectRole.ProjectViewer));
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync("/api/runs/rerun", new { runIds = new[] { recorded.Id } }))
                .StatusCode);

        // A run in a project you cannot see is a run that does not exist.
        using var outsider = await ClientFor(UserRole.ServerUser, ("finance", ProjectRole.ProjectAdmin));
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await outsider.PostAsJsonAsync("/api/runs/rerun", new { runIds = new[] { recorded.Id } }))
                .StatusCode);
    }

    // --- the project's own admin list ---------------------------------------

    [TestMethod]
    public async Task A_project_cannot_be_left_with_no_admins_of_its_own() {
        using var admin = await ClientFor(UserRole.ServerAdmin);
        var alice = await TestAuth.SignInAsync(_app, new HttpClient(), UserRole.ServerUser, "Alice");
        var bob = await TestAuth.SignInAsync(_app, new HttpClient(), UserRole.ServerUser, "Bob");

        await admin.PutAsJsonAsync($"/api/projects/finance/members/{alice.Id}",
            new { role = "ProjectAdmin" });
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await admin.DeleteAsync($"/api/projects/finance/members/{alice.Id}")).StatusCode,
            "the last admin of the project cannot be removed");

        await admin.PutAsJsonAsync($"/api/projects/finance/members/{bob.Id}",
            new { role = "ProjectAdmin" });
        Assert.AreEqual(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/projects/finance/members/{alice.Id}")).StatusCode);

        // A member is not an admin, so removing the remaining one is still refused.
        await admin.PutAsJsonAsync($"/api/projects/finance/members/{alice.Id}",
            new { role = "ProjectMember" });
        Assert.AreEqual(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/projects/finance/members/{alice.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await admin.DeleteAsync($"/api/projects/finance/members/{bob.Id}")).StatusCode);
    }
}

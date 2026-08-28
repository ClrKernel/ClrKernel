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
/// What each role may reach over HTTP.
/// <para>
/// These are the tests that matter most in this feature: "run a cell" is arbitrary
/// code execution on the server, so a Server Viewer being unable to reach it is a
/// security boundary and not a hidden button. Every assertion here calls the
/// endpoint directly, exactly as a viewer with curl would.
/// </para>
/// </summary>
[TestClass]
public class AuthApiTest {
    private string _root;
    private WebApplication _app;
    private EfRunStore _store;
    private IAuthStore _auth;
    private JobsOptions _options;
    private HttpClient _anonymous;

    [TestInitialize]
    public async Task Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-auth-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "notebooks", "etl"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        File.WriteAllText(
            Path.Combine(_root, "notebooks", "etl", "nightly.nb.md"), "```csharp\n1+1\n```\n");

        _options = new JobsOptions {
            DataDir = Path.Combine(_root, "data"),
            NotebooksRoot = Path.Combine(_root, "notebooks"),
        };
        var dbPath = Path.Combine(_options.DataDir, "test.db");
        _store = EfRunStore.Sqlite(dbPath);
        _store.Migrate();
        _auth = TestAuth.StoreFor(dbPath);

        _app = Program.BuildApp(
            _options, new ProjectRegistry(_options, NullLoggerFactory.Instance), _store, _auth);
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
        Directory.Delete(_root, recursive: true);
    }

    private async Task<HttpClient> ClientFor(UserRole role, string name = null) {
        var client = new HttpClient { BaseAddress = _anonymous.BaseAddress };
        await TestAuth.SignInAsync(_app, client, role, name);
        return client;
    }

    /// <summary>Every route that writes or executes, as one table.</summary>
    private static IEnumerable<(string Method, string Path, object Body)> WritingRoutes() {
        yield return ("PUT", "/api/projects/default/branches/default/notebooks/content?path=etl/nightly.nb.md", "x");
        yield return ("PUT", "/api/projects/default/branches/default/notebooks/cells?path=etl/nightly.nb.md",
            new { cells = Array.Empty<object>() });
        yield return ("POST", "/api/projects/default/branches/test/notebooks/session?path=etl/nightly.nb.md", null);
        yield return ("DELETE", "/api/projects/default/branches/test/notebooks/session?path=etl/nightly.nb.md", null);
        yield return ("POST", "/api/projects/default/branches/test/notebooks/run?path=etl/nightly.nb.md",
            new { cells = Array.Empty<object>() });
        yield return ("POST", "/api/projects/default/branches/test/notebooks/sync?path=etl/nightly.nb.md",
            new { cells = Array.Empty<object>() });
        yield return ("POST", "/api/projects/default/branches/test/notebooks/language?path=etl/nightly.nb.md",
            new { kind = "completion", cellId = "c0", line = 0, character = 0 });
        yield return ("POST", "/api/projects/default/branches/test/notebooks/promote?path=etl/nightly.nb.md", null);
        yield return ("POST", "/api/projects/default/branches/default/jobs", new { name = "x", notebook = "etl/nightly.nb.md" });
        yield return ("PUT", "/api/projects/default/branches/default/jobs/x", new { name = "x", notebook = "etl/nightly.nb.md" });
        yield return ("DELETE", "/api/projects/default/branches/default/jobs/x", null);
        yield return ("POST", "/api/projects/default/branches/default/jobs/x/run", null);
        yield return ("POST", "/api/projects/default/branches/default/jobs/x/cancel", null);
        yield return ("PUT", "/api/channels", new { channels = Array.Empty<object>() });
        yield return ("POST", "/api/channels/x/test", null);
        yield return ("PUT", "/api/settings/general", new Dictionary<string, object>());
        yield return ("POST", "/api/projects", new { name = "x", root = "/tmp" });
        yield return ("PUT", "/api/projects/default", new { name = "x" });
        yield return ("DELETE", "/api/projects/default", null);
        yield return ("POST", "/api/projects/default/init", null);
        yield return ("GET", "/api/users", null);
        yield return ("POST", "/api/invites", new { role = "ServerViewer" });
    }

    private static HttpRequestMessage Request(string method, string path, object body) {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body != null) {
            request.Content = body is string text
                ? new StringContent(text)
                : JsonContent.Create(body);
        }
        return request;
    }

    [TestMethod]
    public async Task An_unauthenticated_caller_gets_401_not_a_redirect() {
        var response = await _anonymous.GetAsync("/api/jobs");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.AreEqual(HttpStatusCode.OK, (await _anonymous.GetAsync("/api/health")).StatusCode,
            "health stays open so a container probe works before anyone has signed in");
        Assert.AreEqual(HttpStatusCode.OK, (await _anonymous.GetAsync("/api/auth/session")).StatusCode,
            "the SPA has to be able to ask what this server wants from it");
    }

    [TestMethod]
    public async Task A_fresh_server_reports_that_it_needs_setup() {
        var session = await _anonymous.GetFromJsonAsync<JsonElement>("/api/auth/session");
        Assert.IsTrue(session.GetProperty("needsSetup").GetBoolean());
        Assert.IsFalse(session.GetProperty("authenticated").GetBoolean());
        Assert.IsTrue(session.GetProperty("canSetUp").GetBoolean(),
            "the SPA decides between the setup form and the invite instructions on this");

        await _auth.CreateUserAsync(Guid.NewGuid(), "Ada", UserRole.ServerAdmin);
        session = await _anonymous.GetFromJsonAsync<JsonElement>("/api/auth/session");
        Assert.IsFalse(session.GetProperty("needsSetup").GetBoolean());
    }

    /// <summary>
    /// The rule that decides whether the setup form is worth rendering. A published
    /// container port arrives from the docker bridge, so "the browser is on this
    /// machine" and "the request came from loopback" are not the same question —
    /// which is how somebody gets walked through a form that then 403s.
    /// </summary>
    [TestMethod]
    public void Setup_is_allowed_from_loopback_and_nowhere_else() {
        static Microsoft.AspNetCore.Http.HttpContext From(string address) {
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Connection.RemoteIpAddress = address == null ? null : IPAddress.Parse(address);
            return context;
        }

        Assert.IsTrue(AuthApi.SetupAllowed(From("127.0.0.1")));
        Assert.IsTrue(AuthApi.SetupAllowed(From("::1")));
        Assert.IsTrue(AuthApi.SetupAllowed(From(null)), "an in-memory host has no peer address");
        Assert.IsFalse(AuthApi.SetupAllowed(From("172.17.0.1")), "the docker bridge gateway");
        Assert.IsFalse(AuthApi.SetupAllowed(From("10.0.0.7")));
    }

    /// <summary>Not a redirect with a helpful message — the route stops existing.</summary>
    [TestMethod]
    public async Task Setup_is_gone_once_the_server_has_an_account() {
        await _auth.CreateUserAsync(Guid.NewGuid(), "Ada", UserRole.ServerAdmin);

        var response = await _anonymous.PostAsJsonAsync(
            "/api/auth/setup/begin", new { displayName = "Interloper" });
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Setup_is_open_while_the_server_has_no_account() {
        var response = await _anonymous.PostAsJsonAsync(
            "/api/auth/setup/begin", new { displayName = "Ada" });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "the request comes from loopback, which is where first-run setup is allowed");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsFalse(string.IsNullOrEmpty(body.GetProperty("ceremonyId").GetString()));
        Assert.IsTrue(body.GetProperty("options").TryGetProperty("challenge", out _),
            "the browser needs a challenge to sign");
    }

    [TestMethod]
    public async Task A_viewer_is_refused_every_route_that_writes_or_executes() {
        using var viewer = await ClientFor(UserRole.ServerViewer);

        foreach (var (method, path, body) in WritingRoutes()) {
            var response = await viewer.SendAsync(Request(method, path, body));
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
                $"{method} {path} must be refused for a Server Viewer");
        }
    }

    [TestMethod]
    public async Task A_viewer_can_still_read_everything() {
        using var viewer = await ClientFor(UserRole.ServerViewer);

        foreach (var path in new[] {
            "/api/jobs", "/api/runs", "/api/stats", "/api/projects/default/notebooks", "/api/channels",
            "/api/settings", "/api/projects/default/branches/default/notebooks/content?path=etl/nightly.nb.md",
        }) {
            var response = await viewer.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET {path} is reading");
        }
    }

    [TestMethod]
    public async Task An_admin_reaches_the_routes_a_viewer_cannot() {
        using var admin = await ClientFor(UserRole.ServerAdmin);

        Assert.AreEqual(HttpStatusCode.OK, (await admin.GetAsync("/api/users")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await admin.GetAsync("/api/invites")).StatusCode);
        var created = await admin.PostAsJsonAsync("/api/projects/default/branches/default/jobs",
            new { name = "nightly", notebook = "etl/nightly.nb.md" });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
    }

    [TestMethod]
    public async Task An_invite_round_trips_through_the_api() {
        using var admin = await ClientFor(UserRole.ServerAdmin);

        var response = await admin.PostAsJsonAsync("/api/invites",
            new { role = "ServerViewer", label = "Bob" });
        var code = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString();

        var check = await _anonymous.GetFromJsonAsync<JsonElement>($"/api/auth/invite/{code}");
        Assert.IsTrue(check.GetProperty("valid").GetBoolean());

        var listed = await admin.GetFromJsonAsync<JsonElement>("/api/invites");
        Assert.AreEqual("open", listed.GetProperty("invites")[0].GetProperty("status").GetString());

        Assert.AreEqual(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/invites/{code}")).StatusCode);
        check = await _anonymous.GetFromJsonAsync<JsonElement>($"/api/auth/invite/{code}");
        Assert.IsFalse(check.GetProperty("valid").GetBoolean());
    }

    /// <summary>Every bad code answers the same, so none of them is a probe.</summary>
    [TestMethod]
    public async Task Bad_invite_codes_are_indistinguishable() {
        using var admin = await ClientFor(UserRole.ServerAdmin);
        var response = await admin.PostAsJsonAsync("/api/invites", new { role = "ServerViewer" });
        var code = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString();
        await admin.DeleteAsync($"/api/invites/{code}");

        var messages = new List<string>();
        foreach (var candidate in new[] { code, "never-existed" }) {
            var begin = await _anonymous.PostAsJsonAsync(
                $"/api/auth/invite/{candidate}/begin", new { displayName = "Bob" });
            Assert.AreEqual(HttpStatusCode.BadRequest, begin.StatusCode);
            messages.Add((await begin.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("error").GetString());
        }
        Assert.AreEqual(1, messages.Distinct().Count(),
            "revoked and never-existed must not be told apart");
    }

    [TestMethod]
    public async Task The_last_admin_cannot_be_demoted_through_the_api() {
        using var admin = await ClientFor(UserRole.ServerAdmin, "Ada");
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/users");
        var id = users.GetProperty("users")[0].GetProperty("id").GetGuid();

        var response = await admin.PutAsJsonAsync($"/api/users/{id}/role", new { role = "ServerViewer" });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task An_admin_cannot_disable_or_remove_themselves() {
        using var admin = await ClientFor(UserRole.ServerAdmin, "Ada");
        await _auth.CreateUserAsync(Guid.NewGuid(), "Grace", UserRole.ServerAdmin);
        var me = (await admin.GetFromJsonAsync<JsonElement>("/api/users"))
            .GetProperty("users").EnumerateArray().First(u => u.GetProperty("isYou").GetBoolean());
        var id = me.GetProperty("id").GetGuid();

        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync($"/api/users/{id}/disabled", new { disabled = true })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await admin.DeleteAsync($"/api/users/{id}")).StatusCode);
    }

    /// <summary>Disabling has to end the sessions someone already holds.</summary>
    [TestMethod]
    public async Task Disabling_a_user_ends_their_session_immediately() {
        using var admin = await ClientFor(UserRole.ServerAdmin, "Ada");
        using var viewer = await ClientFor(UserRole.ServerViewer, "Bob");
        Assert.AreEqual(HttpStatusCode.OK, (await viewer.GetAsync("/api/jobs")).StatusCode);

        var bob = (await admin.GetFromJsonAsync<JsonElement>("/api/users"))
            .GetProperty("users").EnumerateArray()
            .First(u => u.GetProperty("displayName").GetString() == "Bob").GetProperty("id").GetGuid();
        Assert.AreEqual(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync($"/api/users/{bob}/disabled", new { disabled = true })).StatusCode);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/jobs")).StatusCode);
    }

    /// <summary>
    /// The dev loop serves the page from Vite on one port and proxies /api to the
    /// server on another, so the browser's origin is not the bind url. A relying
    /// party is a domain and the port is not part of it, so another loopback port
    /// is the same relying party — but only while the relying party *is*
    /// localhost, which is a development configuration by definition.
    /// </summary>
    [TestMethod]
    public void Loopback_origins_are_recognised() {
        Assert.IsTrue(AuthService.IsLoopbackOrigin("http://localhost:5173"));
        Assert.IsTrue(AuthService.IsLoopbackOrigin("http://127.0.0.1:5000"));
        Assert.IsTrue(AuthService.IsLoopbackOrigin("http://[::1]:5000"));

        Assert.IsFalse(AuthService.IsLoopbackOrigin("https://jobs.example.internal"));
        Assert.IsFalse(AuthService.IsLoopbackOrigin("http://localhost.evil.example"),
            "a hostname that merely starts with localhost is somebody else's domain");
        Assert.IsFalse(AuthService.IsLoopbackOrigin("file:///tmp"));
        Assert.IsFalse(AuthService.IsLoopbackOrigin("not a url"));
        Assert.IsFalse(AuthService.IsLoopbackOrigin(null));
    }

    /// <summary>
    /// The cookie's Secure flag, which `Request.IsHttps` alone gets wrong in the
    /// deployment the docs recommend: TLS terminated by a proxy, plain http on the
    /// hop into this process.
    /// </summary>
    [TestMethod]
    public void The_session_cookie_is_secure_whenever_the_origin_is() {
        Assert.IsTrue(AuthApi.SecureCookie(true, null), "TLS straight into the process");
        Assert.IsTrue(AuthApi.SecureCookie(false, new[] { "https://jobs.example.internal" }),
            "TLS terminated by a proxy is still an https origin");
        Assert.IsTrue(AuthApi.SecureCookie(false, new[] { "https://a.example", "https://b.example" }));

        Assert.IsFalse(AuthApi.SecureCookie(false, new[] { "http://localhost:5000" }),
            "a plain-http dev server must not set Secure, or the browser drops the cookie");
        Assert.IsFalse(AuthApi.SecureCookie(false, new[] { "https://a.example", "http://b.example" }),
            "one plain origin is enough to make the flag wrong for somebody");
        Assert.IsFalse(AuthApi.SecureCookie(false, Array.Empty<string>()));
    }

    [TestMethod]
    public async Task Signing_out_ends_the_session() {
        using var admin = await ClientFor(UserRole.ServerAdmin);
        Assert.AreEqual(HttpStatusCode.OK, (await admin.GetAsync("/api/jobs")).StatusCode);

        Assert.AreEqual(HttpStatusCode.OK,
            (await admin.PostAsync("/api/auth/signout", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/jobs")).StatusCode);
    }

    /// <summary>
    /// A browser asking for a page gets sent somewhere it can act; a script asking
    /// for data gets a status code it can branch on.
    /// </summary>
    [TestMethod]
    public async Task A_browser_is_redirected_and_a_script_is_not() {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var browser = new HttpClient(handler) { BaseAddress = _anonymous.BaseAddress };
        browser.DefaultRequestHeaders.Add("Accept", "text/html");

        var response = await browser.GetAsync("/jobs");
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/setup", response.Headers.Location?.OriginalString,
            "an unclaimed server sends every door to the same place");

        await _auth.CreateUserAsync(Guid.NewGuid(), "Ada", UserRole.ServerAdmin);
        response = await browser.GetAsync("/jobs");
        Assert.AreEqual("/signin", response.Headers.Location?.OriginalString);

        // Not a status code: the subject here is the middleware, and whether /signin
        // answers 200 or 404 depends on something else entirely — the built SPA,
        // which this suite does not build and CI builds in another job. Asserting
        // OK made a passing test mean "somebody ran vite on this machine once".
        Assert.AreNotEqual(HttpStatusCode.Redirect, (await browser.GetAsync("/signin")).StatusCode,
            "the page you are being sent to cannot itself redirect");
    }
}

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// The secrets routes, over a live host with the git workflow on.
///
/// <para>
/// The one that matters is <see cref="What_the_page_writes_is_what_a_kernel_reads"/>:
/// the route names a branch <c>mine</c> and the kernel is handed one called
/// <c>user/ada</c>, so a page storing under the word in the URL would write a secret
/// that nothing ever finds. Nothing else in the suite would notice — both halves
/// would pass on their own.
/// </para>
/// </summary>
[TestClass]
public class SecretsApiTest {
    private string _root;
    private WebApplication _app;
    private HttpClient _client;
    private JobsOptions _options;
    private IAuthStore _auth;
    private GitService _git;

    [TestInitialize]
    public async Task Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-secrets-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "notebooks"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));

        _options = new JobsOptions {
            DataDir = Path.Combine(_root, "data"),
            NotebooksRoot = Path.Combine(_root, "notebooks"),
            GitEnabled = true,
            SecretStore = "file",
        };
        // The default path, not a name of this test's own: BranchSecrets is handed
        // RunStoreFactory.ContextFactory, which goes to DefaultSqlitePath — so a
        // test.db here migrates a file the server never opens, and every route 500s
        // on a missing table.
        var store = EfRunStore.Sqlite(_options.DefaultSqlitePath);
        store.Migrate();
        _auth = TestAuth.StoreFor(_options.DefaultSqlitePath);

        var projects = new ProjectRegistry(_options, NullLoggerFactory.Instance);
        _git = projects.GitFor(projects.Default);
        _git.Init();

        // A file store, so a value written through the API is really on disk and
        // really read back — not held in a dictionary this test also owns.
        _app = Program.BuildApp(
            _options, projects, store, _auth, SecretStoreFactory.Create(_options));
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    [TestCleanup]
    public async Task Cleanup() {
        _client?.Dispose();
        if (_app != null) {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TempDirectory.Delete(_root);
    }

    private BranchSecrets Secrets => _app.Services.GetRequiredService<BranchSecrets>();

    private Task<HttpResponseMessage> Set(string branch, string name, string value) =>
        _client.PutAsJsonAsync(
            $"/api/projects/default/branches/{branch}/secrets/{name}", new { value });

    /// <summary>
    /// The seam. The URL says <c>mine</c>; the kernel is started for <c>user/ada</c>
    /// and asks for that branch's environment. Both have to name the same key.
    /// </summary>
    [TestMethod]
    public async Task What_the_page_writes_is_what_a_kernel_reads() {
        var me = await TestAuth.SignInAsync(_app, _client, UserRole.ServerAdmin);

        var saved = await Set("mine", "OPENAI", "sk-mine");
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode,
            saved.StatusCode + " " + await saved.Content.ReadAsStringAsync());

        var environment = await Secrets.EnvironmentForAsync(
            "default", GitService.BranchForUser(me.Username));
        Assert.AreEqual("sk-mine", environment["CLRKERNEL_SECRET_OPENAI"],
            "the branch the URL means is the branch the value is stored under");
    }

    /// <summary>Two branches, one name, two values — and neither leaks into the other.</summary>
    [TestMethod]
    public async Task A_name_on_two_branches_stays_two_secrets() {
        var me = await TestAuth.SignInAsync(_app, _client, UserRole.ServerAdmin);
        Assert.AreEqual(HttpStatusCode.OK, (await Set("mine", "OPENAI", "sk-mine")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await Set("test", "OPENAI", "sk-test")).StatusCode);

        var mine = await Secrets.EnvironmentForAsync("default", GitService.BranchForUser(me.Username));
        var test = await Secrets.EnvironmentForAsync("default", "test");
        Assert.AreEqual("sk-mine", mine["CLRKERNEL_SECRET_OPENAI"]);
        Assert.AreEqual("sk-test", test["CLRKERNEL_SECRET_OPENAI"]);

        // And prod, which was never given one, is handed nothing at all rather than
        // either of those.
        var prod = await Secrets.EnvironmentForAsync("default", "prod");
        Assert.IsFalse(prod.ContainsKey("CLRKERNEL_SECRET_OPENAI"));
    }

    /// <summary>Names and set/not-set. Never a value, not even masked.</summary>
    [TestMethod]
    public async Task The_list_says_whether_it_is_set_and_never_what_it_is() {
        await TestAuth.SignInAsync(_app, _client, UserRole.ServerAdmin);
        await Set("test", "OPENAI", "sk-secret-value");

        var body = await _client.GetStringAsync("/api/projects/default/branches/test/secrets/");
        StringAssert.Contains(body, "OPENAI");
        Assert.IsFalse(body.Contains("sk-secret-value", StringComparison.Ordinal),
            "the value must not travel outwards: " + body);

        var listed = JsonDocument.Parse(body).RootElement.GetProperty("secrets");
        Assert.AreEqual(1, listed.GetArrayLength());
        Assert.IsTrue(listed[0].GetProperty("isSet").GetBoolean());

        // Deleted, and the name goes with it.
        Assert.AreEqual(HttpStatusCode.NoContent,
            (await _client.DeleteAsync("/api/projects/default/branches/test/secrets/OPENAI")).StatusCode);
        var after = await _client.GetFromJsonAsync<JsonElement>(
            "/api/projects/default/branches/test/secrets/");
        Assert.AreEqual(0, after.GetProperty("secrets").GetArrayLength());
    }

    /// <summary>
    /// An environment's secrets belong to the project's admins; a personal branch's
    /// belong to its owner and to nobody else, whatever role they hold.
    /// </summary>
    [TestMethod]
    public async Task Whose_secrets_they_are() {
        var them = await _auth.CreateUserAsync(Guid.NewGuid(), "grace", "Grace", UserRole.ServerUser);
        _git.EnsureUserWorktree(them.Username);

        await TestAuth.SignInAsync(_app, _client, UserRole.ServerAdmin);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Set("user-grace", "OPENAI", "x")).StatusCode,
            "a server admin still does not write into somebody else's branch");
        Assert.AreEqual(HttpStatusCode.Forbidden,
            (await _client.GetAsync("/api/projects/default/branches/user-grace/secrets/")).StatusCode);

        // And an admin does manage the environments — the half that has to still work
        // for the refusal above to mean anything.
        Assert.AreEqual(HttpStatusCode.OK, (await Set("test", "OPENAI", "x")).StatusCode);

        // A member is the other way round: their own branch, never an environment's.
        using var member = new HttpClient { BaseAddress = _client.BaseAddress };
        var ada = await TestAuth.SignInAsync(_app, member, UserRole.ServerUser, "Ada");
        await _auth.SetMemberAsync("default", ada.Id, ProjectRole.ProjectMember, DateTime.UtcNow);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync(
            "/api/projects/default/branches/test/secrets/OPENAI", new { value = "x" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await member.PutAsJsonAsync(
            "/api/projects/default/branches/mine/secrets/OPENAI", new { value = "x" })).StatusCode);
    }

    [TestMethod]
    public async Task A_branch_this_project_does_not_have() {
        await TestAuth.SignInAsync(_app, _client, UserRole.ServerAdmin);
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await _client.GetAsync("/api/projects/default/branches/nope/secrets/")).StatusCode);
    }

    /// <summary>A name that could not survive the trip through an environment variable.</summary>
    [TestMethod]
    public async Task A_name_that_is_not_usable_is_refused_rather_than_folded() {
        await TestAuth.SignInAsync(_app, _client, UserRole.ServerAdmin);
        var reply = await Set("test", "my-key", "x");
        Assert.AreEqual(HttpStatusCode.BadRequest, reply.StatusCode);
        StringAssert.Contains(await reply.Content.ReadAsStringAsync(), "underscore");
    }
}

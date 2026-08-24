using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>
/// The connection store over the real endpoint pipeline.
/// <para>
/// The secret store is a fake, and that is not only for isolation: the real one
/// writes to the developer's own login keychain, so a suite using it would leave
/// test passwords behind on the machine that ran it.
/// </para>
/// </summary>
[TestClass]
public class ConnectionsApiTest {
    private string _root;
    private WebApplication _app;
    private HttpClient _client;
    private EfRunStore _store;
    private JobsOptions _options;
    private InMemorySecretProvider _secrets;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [TestInitialize]
    public async Task Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-conn-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "notebooks"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));

        _options = new JobsOptions {
            DataDir = Path.Combine(_root, "data"),
            NotebooksRoot = Path.Combine(_root, "notebooks"),
        };
        _store = EfRunStore.Sqlite(Path.Combine(_options.DataDir, "test.db"));
        _store.Migrate();
        _secrets = new InMemorySecretProvider();

        _app = Program.BuildApp(
            _options, new ProjectRegistry(_options, NullLoggerFactory.Instance), _store,
            TestAuth.StoreFor(Path.Combine(_options.DataDir, "test.db")),
            SecretStore.ForProviders(_secrets));
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

        try {
            Directory.Delete(_root, recursive: true);
        } catch (IOException) {
            // A file the host still holds; the temp directory is disposable either way.
        }
    }

    // --- the list -----------------------------------------------------------

    [TestMethod]
    public async Task SigningOutHidesEverything() {
        var response = await _client.GetAsync("/api/connections");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task ProvidersDescribeTheirSettings() {
        await SignInAsync(UserRole.ServerAdmin);
        var body = await GetJsonAsync("/api/connections/providers");
        var providers = body.GetProperty("providers").EnumerateArray().ToList();
        Assert.AreEqual(1, providers.Count);
        Assert.AreEqual("SqlServer", providers[0].GetProperty("type").GetString());
        var settings = providers[0].GetProperty("settings").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()).ToList();
        CollectionAssert.Contains(settings, "server");
        CollectionAssert.Contains(settings, "database");
        // A fake in-memory provider does persist, so the form offers a password field.
        Assert.IsTrue(body.GetProperty("canPersistSecrets").GetBoolean());
    }

    // --- scoping ------------------------------------------------------------

    [TestMethod]
    public async Task OnlyAnAdminCreatesASharedConnection() {
        await SignInAsync(UserRole.ServerUser);
        var response = await PostAsync(Shared("warehouse"));
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task EverybodySeesSharedConnections() {
        await SignInAsync(UserRole.ServerAdmin);
        await CreateAsync(Shared("warehouse"));

        await SignInAsync(UserRole.ServerUser);
        var names = await NamesAsync();
        CollectionAssert.AreEqual(new[] { "warehouse" }, names);
    }

    [TestMethod]
    public async Task NobodySeesSomebodyElsesPrivateConnection() {
        await SignInAsync(UserRole.ServerUser, "Grace");
        var mine = await CreateAsync(Private("scratch"));

        // A server admin is an admin of every project and still does not get this.
        await SignInAsync(UserRole.ServerAdmin);
        CollectionAssert.AreEqual(Array.Empty<string>(), await NamesAsync());

        var response = await _client.GetAsync("/api/connections/" + mine.GetProperty("id").GetString());
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode,
            "a connection you cannot see must not be distinguishable from one that does not exist");
    }

    [TestMethod]
    public async Task ScopeCannotChangeAfterCreation() {
        await SignInAsync(UserRole.ServerUser);
        var mine = await CreateAsync(Private("scratch"));

        var body = Private("scratch");
        body["scope"] = "shared";
        var response = await _client.PutAsJsonAsync(
            "/api/connections/" + mine.GetProperty("id").GetString(), body);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- names --------------------------------------------------------------

    [TestMethod]
    public async Task TwoSharedConnectionsCannotShareAName() {
        await SignInAsync(UserRole.ServerAdmin);
        await CreateAsync(Shared("warehouse"));
        var response = await PostAsync(Shared("WAREHOUSE"));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task APrivateConnectionCannotShadowASharedOne() {
        await SignInAsync(UserRole.ServerAdmin);
        await CreateAsync(Shared("warehouse"));

        await SignInAsync(UserRole.ServerUser);
        var response = await PostAsync(Private("warehouse"));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
            "a notebook naming 'warehouse' must not mean a different database per person");
    }

    [TestMethod]
    public async Task TwoPeopleMayEachHaveAPrivateConnectionOfTheSameName() {
        await SignInAsync(UserRole.ServerUser, "Grace");
        await CreateAsync(Private("scratch"));

        await SignInAsync(UserRole.ServerUser, "Alan");
        var response = await PostAsync(Private("scratch"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "refusing would tell each of them the other's connection exists");
    }

    // --- secrets ------------------------------------------------------------

    [TestMethod]
    public async Task APasswordIsStoredAndNeverReadBack() {
        await SignInAsync(UserRole.ServerAdmin);
        var body = Shared("warehouse");
        body["password"] = "hunter2";
        var created = await CreateAsync(body);

        Assert.IsTrue(created.GetProperty("secretConfigured").GetBoolean());
        var payload = created.GetRawText();
        StringAssert.DoesNotMatch(payload, new System.Text.RegularExpressions.Regex("hunter2"),
            "the password must not come back on the response");

        // In the credential store under the reference, and nowhere else.
        Assert.IsTrue(_secrets.TryGet(created.GetProperty("secretRef").GetString(), out var stored));
        Assert.AreEqual("hunter2", stored);
        var file = File.ReadAllText(ConnectionsFile.PathIn(_options.DataDir));
        StringAssert.DoesNotMatch(file, new System.Text.RegularExpressions.Regex("hunter2"),
            "a password written to config is a password that leaks with the config");
    }

    [TestMethod]
    public async Task DeletingAConnectionForgetsItsPassword() {
        await SignInAsync(UserRole.ServerAdmin);
        var body = Shared("warehouse");
        body["password"] = "hunter2";
        var created = await CreateAsync(body);
        var secretRef = created.GetProperty("secretRef").GetString();

        var response = await _client.DeleteAsync("/api/connections/" + created.GetProperty("id").GetString());
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(_secrets.TryGet(secretRef, out _));
    }

    [TestMethod]
    public async Task SwitchingToPromptForgetsTheStoredPassword() {
        await SignInAsync(UserRole.ServerAdmin);
        var body = Shared("warehouse");
        body["password"] = "hunter2";
        var created = await CreateAsync(body);
        var secretRef = created.GetProperty("secretRef").GetString();

        var update = Shared("warehouse");
        update["promptForPassword"] = true;
        var response = await _client.PutAsJsonAsync(
            "/api/connections/" + created.GetProperty("id").GetString(), update);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(_secrets.TryGet(secretRef, out _),
            "the old password would otherwise sit in the keychain with nothing referencing it");
    }

    // --- execution permission -----------------------------------------------

    [TestMethod]
    public async Task ASharedConnectionRefusesNonAdminsUntilAReadOnlyLoginExists() {
        await SignInAsync(UserRole.ServerAdmin);
        var created = await CreateAsync(Shared("warehouse"));
        Assert.IsTrue(created.GetProperty("canExecute").GetBoolean());

        await SignInAsync(UserRole.ServerUser);
        var seen = (await ListAsync()).Single();
        Assert.IsFalse(seen.GetProperty("canExecute").GetBoolean());
        StringAssert.Contains(seen.GetProperty("canExecuteReason").GetString(), "Read-only");
        Assert.IsFalse(seen.GetProperty("canEdit").GetBoolean());
    }

    [TestMethod]
    public async Task AReadOnlyLoginOpensExecutionToEverybody() {
        await SignInAsync(UserRole.ServerAdmin);
        var body = Shared("warehouse");
        body["readOnlyUser"] = "reader";
        body["readOnlyPassword"] = "readonly-pw";
        await CreateAsync(body);

        await SignInAsync(UserRole.ServerUser);
        var seen = (await ListAsync()).Single();
        Assert.IsTrue(seen.GetProperty("canExecute").GetBoolean());
        Assert.IsTrue(seen.GetProperty("readOnlySecretConfigured").GetBoolean());
    }

    [TestMethod]
    public async Task YourOwnConnectionIsYoursToRun() {
        await SignInAsync(UserRole.ServerUser);
        var mine = await CreateAsync(Private("scratch"));
        Assert.IsTrue(mine.GetProperty("canExecute").GetBoolean(),
            "a private connection is the user's own credential against a server they could reach anyway");
        Assert.IsTrue(mine.GetProperty("canEdit").GetBoolean());
    }

    // --- persistence --------------------------------------------------------

    [TestMethod]
    public async Task ConnectionsSurviveARestart() {
        await SignInAsync(UserRole.ServerAdmin);
        await CreateAsync(Shared("warehouse"));

        var reread = ConnectionsFile.Read(_options.DataDir);
        Assert.AreEqual(1, reread.Count);
        Assert.AreEqual("warehouse", reread[0].Name);
        Assert.AreEqual(ConnectionScope.Shared, reread[0].Scope);
        Assert.IsNull(reread[0].OwnerId, "a shared connection belongs to nobody in particular");
    }

    // --- execution ----------------------------------------------------------

    [TestMethod]
    public async Task ANonAdminCannotRunAgainstASharedConnectionWithoutAReadOnlyLogin() {
        await SignInAsync(UserRole.ServerAdmin);
        var created = await CreateAsync(Shared("warehouse"));

        await SignInAsync(UserRole.ServerUser);
        var response = await _client.PostAsJsonAsync(
            $"/api/connections/{created.GetProperty("id").GetString()}/query",
            new Dictionary<string, object> { ["sql"] = "SELECT 1" });
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(0, (await HistoryAsync(created)).Count,
            "a refused query never reached a database and is not an execution");
    }

    [TestMethod]
    public async Task AnUnreachableServerIsAnAnswerRatherThanAFailedRequest() {
        await SignInAsync(UserRole.ServerAdmin);
        var created = await CreateAsync(Unreachable("warehouse", "shared"));

        var result = await RunAsync(created, "SELECT 1");
        Assert.IsFalse(string.IsNullOrEmpty(result.GetProperty("error").GetString()),
            "a connection that cannot be opened belongs in the Messages tab, not in a 500");
        Assert.IsFalse(result.GetProperty("canceled").GetBoolean());
    }

    [TestMethod]
    public async Task RunningAgainstASharedConnectionIsAudited() {
        await SignInAsync(UserRole.ServerAdmin, "Grace");
        var created = await CreateAsync(Unreachable("warehouse", "shared"));
        await RunAsync(created, "SELECT top 10 * FROM dbo.Orders");

        var history = await HistoryAsync(created);
        Assert.AreEqual(1, history.Count);
        Assert.AreEqual("Grace", history[0].GetProperty("actorName").GetString());
        Assert.AreEqual("warehouse", history[0].GetProperty("connectionName").GetString());
        Assert.AreEqual("SELECT top 10 * FROM dbo.Orders", history[0].GetProperty("statement").GetString(),
            "a truncated statement is not evidence");
        Assert.AreEqual("Failed", history[0].GetProperty("outcome").GetString());
        Assert.IsFalse(history[0].GetProperty("leastPrivilege").GetBoolean(),
            "an admin runs as the connection's own login");
    }

    [TestMethod]
    public async Task RunningAgainstYourOwnConnectionIsNotAudited() {
        await SignInAsync(UserRole.ServerUser);
        var created = await CreateAsync(Unreachable("scratch", "private"));
        await RunAsync(created, "SELECT 1");

        Assert.AreEqual(0, (await HistoryAsync(created)).Count,
            "a private connection is the person's own credential; logging it is surveillance, not audit");
    }

    [TestMethod]
    public async Task CancellingAQueryYouDidNotStartDoesNothing() {
        await SignInAsync(UserRole.ServerAdmin);
        var created = await CreateAsync(Unreachable("warehouse", "shared"));

        var response = await _client.PostAsJsonAsync(
            $"/api/connections/{created.GetProperty("id").GetString()}/cancel",
            new Dictionary<string, object> { ["queryId"] = "somebody-elses-query" });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync(), _json);
        Assert.IsFalse(body.GetProperty("cancelled").GetBoolean());
    }

    [TestMethod]
    public void TheLeastPrivilegeLoginIsWhatANonAdminRunsAs() {
        var runner = new QueryRunner(
            SecretStore.ForProviders(new InMemorySecretProvider()),
            NullLogger<QueryRunner>.Instance);
        var connection = new StoredConnection {
            Id = "c1",
            Name = "warehouse",
            Type = "SqlServer",
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["server"] = "dw.db.local",
                ["database"] = "dw",
                ["auth"] = "sql",
                ["user"] = "svc",
            },
            SecretRef = "primary",
            ReadOnlyUser = "reader",
            ReadOnlySecretRef = "reader-secret",
        };

        Assert.AreEqual("svc", runner.SpecFor(connection, leastPrivilege: false).User);
        var restricted = runner.SpecFor(connection, leastPrivilege: true);
        Assert.AreEqual("reader", restricted.User);
        Assert.AreEqual("reader-secret", restricted.SecretRef,
            "the second credential is the read-only boundary; the app-side check is only a message");
    }

    // --- helpers ------------------------------------------------------------

    private Task<User> SignInAsync(UserRole role, string displayName = null) =>
        TestAuth.SignInAsync(_app, _client, role, displayName);

    private static Dictionary<string, object> Shared(string name) => Body(name, "shared");

    private static Dictionary<string, object> Private(string name) => Body(name, "private");

    /// <summary>A connection nothing is listening on, with a one-second connect
    /// timeout — enough to exercise the whole path from stored settings to an open
    /// attempt without needing a database.</summary>
    private static Dictionary<string, object> Unreachable(string name, string scope) {
        var body = Body(name, scope);
        body["settings"] = new Dictionary<string, string> {
            ["connectionString"] = "Server=127.0.0.1,1;Connect Timeout=1;Encrypt=false",
        };
        return body;
    }

    private async Task<JsonElement> RunAsync(JsonElement connection, string sql) {
        var response = await _client.PostAsJsonAsync(
            $"/api/connections/{connection.GetProperty("id").GetString()}/query",
            new Dictionary<string, object> { ["sql"] = sql });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), _json);
    }

    private async Task<List<JsonElement>> HistoryAsync(JsonElement connection) =>
        (await GetJsonAsync($"/api/connections/{connection.GetProperty("id").GetString()}/history"))
            .GetProperty("history").EnumerateArray().ToList();

    private static Dictionary<string, object> Body(string name, string scope) => new() {
        ["name"] = name,
        ["scope"] = scope,
        ["type"] = "SqlServer",
        ["settings"] = new Dictionary<string, string> {
            ["server"] = "dw.db.local",
            ["database"] = "datawarehouse",
            ["auth"] = "sql",
            ["user"] = "svc",
        },
    };

    private Task<HttpResponseMessage> PostAsync(Dictionary<string, object> body) =>
        _client.PostAsJsonAsync("/api/connections", body);

    private async Task<JsonElement> CreateAsync(Dictionary<string, object> body) {
        var response = await PostAsync(body);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), _json);
    }

    private async Task<JsonElement> GetJsonAsync(string url) {
        var response = await _client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), _json);
    }

    private async Task<List<JsonElement>> ListAsync() =>
        (await GetJsonAsync("/api/connections")).GetProperty("connections").EnumerateArray().ToList();

    private async Task<string[]> NamesAsync() =>
        (await ListAsync()).Select(c => c.GetProperty("name").GetString()).ToArray();
}

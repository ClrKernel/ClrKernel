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
/// Personal history and saved queries, over the real endpoint pipeline.
/// <para>
/// Almost every test here is about who can see what. Recording executions against
/// private connections is what makes a personal history useful and is also the
/// thing that would be a betrayal if the read side got it wrong, so the read side
/// is what gets tested.
/// </para>
/// </summary>
[TestClass]
public class QueriesApiTest {
    private string _root;
    private WebApplication _app;
    private HttpClient _client;
    private EfRunStore _store;
    private JobsOptions _options;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [TestInitialize]
    public async Task Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-queries-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "notebooks"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));

        _options = new JobsOptions {
            DataDir = Path.Combine(_root, "data"),
            NotebooksRoot = Path.Combine(_root, "notebooks"),
        };
        _store = EfRunStore.Sqlite(Path.Combine(_options.DataDir, "test.db"));
        _store.Migrate();

        _app = Program.BuildApp(
            _options, new ProjectRegistry(_options, NullLoggerFactory.Instance), _store,
            TestAuth.StoreFor(Path.Combine(_options.DataDir, "test.db")),
            SecretStore.ForProviders(new InMemorySecretProvider()));
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
            // A file the host still holds; the temp directory is disposable.
        }
    }

    // --- history ------------------------------------------------------------

    [TestMethod]
    public async Task YourHistoryIsWhatYouRanWhereverYouRanIt() {
        var grace = await SignInAsync(UserRole.ServerAdmin, "Grace");
        var shared = await CreateConnectionAsync("warehouse", "shared");
        var mine = await CreateConnectionAsync("scratch", "private");
        await RunAsync(shared, "SELECT 1 -- on the shared one");
        await RunAsync(mine, "SELECT 2 -- on my own");

        var history = await HistoryAsync();
        CollectionAssert.AreEquivalent(
            new[] { "SELECT 1 -- on the shared one", "SELECT 2 -- on my own" },
            history.Select(h => h.GetProperty("statement").GetString()).ToArray(),
            "a history that left out your own connections would be no use");
        Assert.IsNotNull(grace);
    }

    [TestMethod]
    public async Task ItIsNewestFirst() {
        await SignInAsync(UserRole.ServerAdmin);
        var connection = await CreateConnectionAsync("warehouse", "shared");
        await RunAsync(connection, "SELECT 'first'");
        await Task.Delay(15);
        await RunAsync(connection, "SELECT 'second'");

        var history = await HistoryAsync();
        StringAssert.Contains(history[0].GetProperty("statement").GetString(), "second");
    }

    [TestMethod]
    public async Task NobodySeesWhatSomebodyElseRanOnTheirOwnConnection() {
        await SignInAsync(UserRole.ServerUser, "Grace");
        var hers = await CreateConnectionAsync("scratch", "private");
        await RunAsync(hers, "SELECT 'hers'");

        // A server admin is an admin of everything and still does not get this.
        await SignInAsync(UserRole.ServerAdmin, "Ada");
        Assert.AreEqual(0, (await HistoryAsync()).Count);
    }

    [TestMethod]
    public async Task NorOnTheConnectionsOwnHistory() {
        var grace = await SignInAsync(UserRole.ServerUser, "Grace");
        var hers = await CreateConnectionAsync("scratch", "private");
        await RunAsync(hers, "SELECT 'hers'");

        // The admin cannot reach that connection at all, which is the first guard;
        // the store's rule is the second, and both have to hold.
        await SignInAsync(UserRole.ServerAdmin, "Ada");
        var response = await _client.GetAsync($"/api/connections/{hers}/history");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.IsNotNull(grace);
    }

    [TestMethod]
    public async Task AnAdminSeesEverybodysRunsAgainstASharedConnection() {
        await SignInAsync(UserRole.ServerAdmin, "Ada");
        var shared = await CreateConnectionAsync("warehouse", "shared", readOnly: true);

        await SignInAsync(UserRole.ServerUser, "Grace");
        await RunAsync(shared, "SELECT 'grace was here'");

        await SignInAsync(UserRole.ServerAdmin, "Ada2");
        var history = await ConnectionHistoryAsync(shared);
        Assert.IsTrue(
            history.Any(h => h.GetProperty("statement").GetString().Contains("grace was here")),
            "who ran that against a shared database is exactly what the audit is for");
    }

    [TestMethod]
    public async Task ButYourOwnHistoryIsStillOnlyYours() {
        await SignInAsync(UserRole.ServerAdmin, "Ada");
        var shared = await CreateConnectionAsync("warehouse", "shared", readOnly: true);

        await SignInAsync(UserRole.ServerUser, "Grace");
        await RunAsync(shared, "SELECT 'grace was here'");

        await SignInAsync(UserRole.ServerAdmin, "Ada2");
        Assert.AreEqual(0, (await HistoryAsync()).Count,
            "the personal panel answers 'what did I run', not 'what did anybody run'");
    }

    // --- saved queries ------------------------------------------------------

    [TestMethod]
    public async Task ASavedQueryComesBackWithItsSql() {
        await SignInAsync(UserRole.ServerUser);
        var saved = await SaveAsync("nightly totals", "private", "SELECT SUM(Total) FROM shop.Orders");

        Assert.AreEqual("nightly totals", saved.GetProperty("name").GetString());
        Assert.AreEqual("SELECT SUM(Total) FROM shop.Orders", saved.GetProperty("sql").GetString());
        var listed = await ListAsync();
        CollectionAssert.AreEqual(
            new[] { "nightly totals" },
            listed.Select(q => q.GetProperty("name").GetString()).ToArray());
    }

    [TestMethod]
    public async Task EverybodySeesASharedOne() {
        await SignInAsync(UserRole.ServerAdmin);
        await SaveAsync("company totals", "shared", "SELECT 1");

        await SignInAsync(UserRole.ServerUser);
        CollectionAssert.AreEqual(
            new[] { "company totals" },
            (await ListAsync()).Select(q => q.GetProperty("name").GetString()).ToArray());
    }

    [TestMethod]
    public async Task NobodySeesSomebodyElsesPrivateOne() {
        await SignInAsync(UserRole.ServerUser, "Grace");
        await SaveAsync("hers", "private", "SELECT 1");

        await SignInAsync(UserRole.ServerAdmin, "Ada");
        Assert.AreEqual(0, (await ListAsync()).Count,
            "the same rule a private connection follows, so there is one rule to remember");
    }

    [TestMethod]
    public async Task OnlyAnAdminSavesASharedOne() {
        await SignInAsync(UserRole.ServerUser);
        var response = await _client.PostAsJsonAsync("/api/queries", new Dictionary<string, object> {
            ["name"] = "company totals",
            ["scope"] = "shared",
            ["sql"] = "SELECT 1",
        });
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task ANonAdminCannotChangeASharedOne() {
        await SignInAsync(UserRole.ServerAdmin);
        var saved = await SaveAsync("company totals", "shared", "SELECT 1");

        await SignInAsync(UserRole.ServerUser);
        var listed = (await ListAsync()).Single();
        Assert.IsFalse(listed.GetProperty("canEdit").GetBoolean());

        var response = await _client.PostAsJsonAsync("/api/queries", new Dictionary<string, object> {
            ["id"] = saved.GetProperty("id").GetString(),
            ["name"] = "hijacked",
            ["scope"] = "shared",
            ["sql"] = "DROP TABLE Orders",
        });
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task SavingOverOneKeepsItsIdentityRatherThanMakingASecond() {
        await SignInAsync(UserRole.ServerUser);
        var saved = await SaveAsync("totals", "private", "SELECT 1");

        var again = await _client.PostAsJsonAsync("/api/queries", new Dictionary<string, object> {
            ["id"] = saved.GetProperty("id").GetString(),
            ["name"] = "totals",
            ["scope"] = "private",
            ["sql"] = "SELECT 2",
        });
        Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);

        var listed = await ListAsync();
        Assert.AreEqual(1, listed.Count);
        Assert.AreEqual("SELECT 2", listed[0].GetProperty("sql").GetString());
    }

    [TestMethod]
    public async Task AQueryCannotChangeWhichListItIsIn() {
        await SignInAsync(UserRole.ServerUser);
        var saved = await SaveAsync("mine", "private", "SELECT 1");

        // Asking for shared is ignored rather than obeyed: publishing somebody's
        // query on a dropdown change is not an undo away.
        var again = await _client.PostAsJsonAsync("/api/queries", new Dictionary<string, object> {
            ["id"] = saved.GetProperty("id").GetString(),
            ["name"] = "mine",
            ["scope"] = "shared",
            ["sql"] = "SELECT 1",
        });
        Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);

        await SignInAsync(UserRole.ServerAdmin);
        Assert.AreEqual(0, (await ListAsync()).Count, "it is still hers");
    }

    [TestMethod]
    public async Task DeletingYourOwnWorks() {
        await SignInAsync(UserRole.ServerUser);
        var saved = await SaveAsync("mine", "private", "SELECT 1");

        var response = await _client.DeleteAsync($"/api/queries/{saved.GetProperty("id").GetString()}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(0, (await ListAsync()).Count);
    }

    [TestMethod]
    public async Task ANameAndSomeSqlAreBothRequired() {
        await SignInAsync(UserRole.ServerUser);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/queries", new Dictionary<string, object> {
                ["name"] = "  ",
                ["scope"] = "private",
                ["sql"] = "SELECT 1",
            })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/queries", new Dictionary<string, object> {
                ["name"] = "empty",
                ["scope"] = "private",
                ["sql"] = "   ",
            })).StatusCode);
    }

    [TestMethod]
    public async Task SigningOutHidesEverything() {
        _client.DefaultRequestHeaders.Remove("Cookie");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/queries")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/queries/history")).StatusCode);
    }

    // --- helpers ------------------------------------------------------------

    private Task<User> SignInAsync(UserRole role, string displayName = null) =>
        TestAuth.SignInAsync(_app, _client, role, displayName);

    /// <summary>A connection nothing is listening on: the run fails, and a failed run
    /// is still a run somebody made and should find in their history.</summary>
    private async Task<string> CreateConnectionAsync(string name, string scope, bool readOnly = false) {
        var body = new Dictionary<string, object> {
            ["name"] = name,
            ["scope"] = scope,
            ["type"] = "SqlServer",
            ["settings"] = new Dictionary<string, string> {
                ["connectionString"] = "Server=127.0.0.1,1;Connect Timeout=1;Encrypt=false",
            },
        };
        if (readOnly) {
            body["readOnlyUser"] = "reader";
            body["readOnlyPassword"] = "readonly-pw";
        }
        var response = await _client.PostAsJsonAsync("/api/connections", body);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return Json(await response.Content.ReadAsStringAsync()).GetProperty("id").GetString();
    }

    private async Task RunAsync(string connectionId, string sql) {
        var response = await _client.PostAsJsonAsync(
            $"/api/connections/{connectionId}/query", new Dictionary<string, object> { ["sql"] = sql });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> SaveAsync(string name, string scope, string sql) {
        var response = await _client.PostAsJsonAsync("/api/queries", new Dictionary<string, object> {
            ["name"] = name,
            ["scope"] = scope,
            ["sql"] = sql,
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return Json(await response.Content.ReadAsStringAsync());
    }

    private async Task<List<JsonElement>> ListAsync() =>
        (await GetAsync("/api/queries")).GetProperty("queries").EnumerateArray().ToList();

    private async Task<List<JsonElement>> HistoryAsync() =>
        (await GetAsync("/api/queries/history")).GetProperty("history").EnumerateArray().ToList();

    private async Task<List<JsonElement>> ConnectionHistoryAsync(string connectionId) =>
        (await GetAsync($"/api/connections/{connectionId}/history"))
            .GetProperty("history").EnumerateArray().ToList();

    private async Task<JsonElement> GetAsync(string url) {
        var response = await _client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return Json(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Json(string body) => JsonSerializer.Deserialize<JsonElement>(body, _json);
}

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.Odbc;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// The Connections area through ODBC, which is the case that is not a database.
/// <para>
/// Everything here comes from <c>GetSchema</c> — the driver's own answer in ADO.NET's
/// shape — so what is asserted is the shallower tree that gives: schemas, objects,
/// columns and a generated SELECT, with no keys, indexes or stored definitions. If a
/// future version starts guessing a dialect instead, these fail.
/// </para>
/// <para>
/// Set <c>CLRKERNEL_STUDIO_TEST_ODBC</c> to a connection string. Missing, these are
/// inconclusive so CI stays green; with <c>CLRKERNEL_TEST_REQUIRE_LIVE=1</c> a missing
/// backend fails instead. It needs an ODBC driver installed on the machine running the
/// tests, which macOS's System.Data.Odbc packaging makes awkward — the practical place
/// to run these is Linux:
/// <code>
/// apt-get install unixodbc odbc-postgresql
/// CLRKERNEL_STUDIO_TEST_ODBC='Driver={PostgreSQL Unicode};Server=postgres;Port=5432;Database=postgres;Uid=postgres;Pwd=devonly' \
///   CLRKERNEL_TEST_REQUIRE_LIVE=1 ./build.sh Test --filter ClassName~OdbcLiveTest
/// </code>
/// </para>
/// </summary>
[TestClass]
public class OdbcLiveTest {
    private const string _connectionVar = "CLRKERNEL_STUDIO_TEST_ODBC";
    private const string _table = "clrkernel_odbc_orders";

    private static string _connectionString;

    private QueryRunner _runner;
    private readonly IConnectionDialect _dialect = new OdbcDialect();
    private StoredConnection _connection;

    [ClassInitialize]
    public static void CreateFixture(TestContext context) {
        _connectionString = Environment.GetEnvironmentVariable(_connectionVar);
        if (string.IsNullOrWhiteSpace(_connectionString)) {
            return;
        }
        // Through ODBC itself, so the fixture needs no second driver and no assumption
        // about which database is on the other end beyond ordinary SQL.
        Execute($"DROP TABLE IF EXISTS {_table}");
        Execute($@"CREATE TABLE {_table} (
                       order_id integer NOT NULL,
                       customer varchar(50),
                       total numeric(18,2) NOT NULL)");
        Execute($"INSERT INTO {_table} (order_id, customer, total) VALUES (1, 'ada', 1.50)");
    }

    [ClassCleanup]
    public static void DropFixture() {
        if (!string.IsNullOrWhiteSpace(_connectionString)) {
            try {
                Execute($"DROP TABLE IF EXISTS {_table}");
            } catch (OdbcException) {
            }
        }
    }

    private static void Execute(string sql) {
        using var live = new OdbcConnection(_connectionString);
        live.Open();
        using var command = new OdbcCommand(sql, live) { CommandTimeout = 60 };
        command.ExecuteNonQuery();
    }

    [TestInitialize]
    public void Setup() {
        if (string.IsNullOrWhiteSpace(_connectionString)) {
            var message = $"Set {_connectionVar} to a connection string to run the live ODBC tests.";
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLRKERNEL_TEST_REQUIRE_LIVE"))) {
                Assert.Fail(message + " CLRKERNEL_TEST_REQUIRE_LIVE is set, so this run was expected "
                    + "to reach a real server.");
            }
            Assert.Inconclusive(message);
        }
        _runner = new QueryRunner(
            SecretStore.ForProviders(new InMemorySecretProvider()), NullLogger<QueryRunner>.Instance);
        _connection = new StoredConnection {
            Id = "live-odbc",
            Name = "live-odbc",
            Type = "Odbc",
            TimeoutSeconds = 60,
            RowCap = 10,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["connectionString"] = _connectionString,
            },
        };
    }

    /// <summary>One entry, because there is no portable way to ask an ODBC driver what
    /// else exists — and a database that opens onto nothing is worse than one that
    /// opens onto itself.</summary>
    [TestMethod]
    public async Task TheTopLevelIsTheOneThingThisConnectionReaches() {
        var databases = await BrowseAsync((live, token) => _dialect.DatabasesAsync(live, token));
        Assert.AreEqual(1, databases.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(databases[0].Name));
    }

    [TestMethod]
    public async Task TheTreeFindsTheFixtureTable() {
        var schemas = await BrowseAsync((live, token) => _dialect.SchemasAsync(live, token));
        Assert.IsTrue(schemas.Count > 0, "a driver with no schema concept still gets one entry");

        var found = new List<string>();
        foreach (var schema in schemas) {
            var objects = await BrowseAsync(
                (live, token) => _dialect.ObjectsAsync(live, schema.Name, token));
            found.AddRange(objects.Select(o => o.Name));
        }
        CollectionAssert.Contains(found, _table);
    }

    [TestMethod]
    public async Task ATablesColumnsComeFromTheDriver() {
        var detail = await DetailAsync();
        var columns = detail.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        CollectionAssert.AreEquivalent(
            new[] { "order_id", "customer", "total" }, columns.Keys.ToArray());
        Assert.IsFalse(columns["order_id"].Nullable);
        Assert.IsTrue(columns["customer"].Nullable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(columns["total"].Type));
    }

    /// <summary>
    /// Deliberately empty, not "this table has none". Keys and indexes have no
    /// portable source through ODBC, and the tree shows nothing rather than claiming
    /// something.
    /// </summary>
    [TestMethod]
    public async Task KeysAndIndexesAreNotClaimed() {
        var detail = await DetailAsync();
        Assert.AreEqual(0, detail.Keys.Count);
        Assert.AreEqual(0, detail.Indexes.Count);
    }

    [TestMethod]
    public async Task CompletionsCarryTheTableAndItsColumns() {
        var completions = await BrowseAsync(
            (live, token) => _dialect.CompletionsAsync(live, token));
        var table = completions.Objects.FirstOrDefault(
            o => string.Equals(o.Name, _table, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(table, "the fixture table is missing from completions");
        CollectionAssert.AreEquivalent(
            new[] { "order_id", "customer", "total" }, table.Columns.ToArray());
    }

    /// <summary>The generated SELECT has to run — it is the only script offered here
    /// that is not a template, and the one place a quoting guess would show.</summary>
    [TestMethod]
    public async Task TheGeneratedSelectRuns() {
        var schema = await SchemaOfAsync();
        var script = await BrowseAsync((live, token) =>
            _dialect.ScriptAsync(live, schema, _table, "table", "select", token));
        StringAssert.Contains(script, _table);

        var result = await RunAsync(script.TrimEnd());
        Assert.IsNull(result.Error, result.Error);
        CollectionAssert.AreEquivalent(
            new[] { "order_id", "customer", "total" }, result.ResultSets[0].Columns.ToArray());
    }

    /// <summary>No CREATE: inventing DDL in a dialect the connection has not told us
    /// would produce a script that does not run on the database it came from.</summary>
    [TestMethod]
    public async Task ThereIsNoCreateScript() {
        var schema = await SchemaOfAsync();
        var script = await BrowseAsync((live, token) =>
            _dialect.ScriptAsync(live, schema, _table, "table", "create", token));
        Assert.IsFalse(script.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase), script);
        StringAssert.Contains(script, "SELECT", "the default is the one script that always works");
    }

    [TestMethod]
    public async Task AFailingStatementComesBackAsAMessage() {
        var result = await RunAsync("SELECT * FROM no_such_table_at_all");
        Assert.IsNotNull(result.Error);
    }

    /// <summary>
    /// The schema the fixture table is actually in, which is the driver's business
    /// rather than ours — psqlODBC reports `public`, and another driver may report
    /// nothing at all and get the placeholder the dialect substitutes.
    /// </summary>
    private async Task<string> SchemaOfAsync() {
        foreach (var schema in await BrowseAsync((live, token) => _dialect.SchemasAsync(live, token))) {
            var objects = await BrowseAsync(
                (live, token) => _dialect.ObjectsAsync(live, schema.Name, token));
            if (objects.Any(o => string.Equals(o.Name, _table, StringComparison.OrdinalIgnoreCase))) {
                return schema.Name;
            }
        }
        Assert.Fail($"no schema listed {_table}");
        return null;
    }

    private async Task<ObjectDetail> DetailAsync() =>
        await BrowseAsync((live, token) =>
            _dialect.DetailAsync(live, SchemaOfAsync().GetAwaiter().GetResult(), _table, token));

    private Task<QueryResult> RunAsync(string sql) =>
        _runner.RunAsync(
            _connection, sql, leastPrivilege: false, Guid.NewGuid(), Guid.NewGuid().ToString("N"),
            password: null, CancellationToken.None);

    private async Task<T> BrowseAsync<T>(Func<DbConnection, CancellationToken, Task<T>> read) {
        var (value, error) = await _runner.BrowseAsync(
            _connection, leastPrivilege: false, password: null, database: null,
            read, CancellationToken.None);
        Assert.IsNull(error, error);
        return value;
    }
}

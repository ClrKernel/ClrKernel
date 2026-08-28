using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Oracle.ManagedDataAccess.Client;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// The Connections area against a real Oracle — the third dialect, and the one whose
/// shape differs most: a connection reaches one service rather than a list of
/// databases, and a schema is a user.
/// <para>
/// Set <c>CLRKERNEL_STUDIO_TEST_ORACLE</c> to a connection string. Missing, these are
/// inconclusive so CI stays green; with <c>CLRKERNEL_TEST_REQUIRE_LIVE=1</c> a missing
/// backend fails instead.
/// <code>
/// docker compose -f dev/docker-compose.dbs.yml up -d oracle
/// CLRKERNEL_STUDIO_TEST_ORACLE='User Id=clrkernel;Password=DevOnly1;Data Source=localhost:41521/FREEPDB1' \
///   CLRKERNEL_TEST_REQUIRE_LIVE=1 ./build.sh Test --filter ClassName~OracleLiveTest
/// </code>
/// </para>
/// </summary>
[TestClass]
public class OracleLiveTest {
    private const string _connectionVar = "CLRKERNEL_STUDIO_TEST_ORACLE";

    /// <summary>The fixture's owner. Oracle has no separate schema object: the login
    /// owns what it creates, and that ownership is the schema.</summary>
    private static string _schema;
    private static string _connectionString;

    private QueryRunner _runner;
    private readonly IConnectionDialect _dialect = new OracleDialect();
    private StoredConnection _connection;

    [ClassInitialize]
    public static void CreateFixture(TestContext context) {
        _connectionString = Environment.GetEnvironmentVariable(_connectionVar);
        if (string.IsNullOrWhiteSpace(_connectionString)) {
            return;
        }
        _schema = new OracleConnectionStringBuilder(_connectionString).UserID.ToUpperInvariant();

        Drop("DROP TABLE order_lines PURGE");
        Drop("DROP TABLE orders PURGE");
        Drop("DROP VIEW active_orders");
        Drop("DROP FUNCTION count_orders");

        Execute(@"CREATE TABLE orders (
                      order_id NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                      customer VARCHAR2(50) NULL,
                      total NUMBER(18,2) NOT NULL)");
        Execute("CREATE INDEX ix_orders_customer ON orders(customer)");
        Execute(@"CREATE TABLE order_lines (
                      order_line_id NUMBER PRIMARY KEY,
                      order_id NUMBER NOT NULL
                          CONSTRAINT fk_order_lines_orders REFERENCES orders(order_id))");
        Execute(@"INSERT INTO orders (customer, total)
                  SELECT 'customer ' || LEVEL, 1.50 FROM dual CONNECT BY LEVEL <= 25");
        Execute("COMMIT");
        Execute("CREATE VIEW active_orders AS SELECT order_id, customer FROM orders WHERE total > 0");
        Execute(@"CREATE FUNCTION count_orders RETURN NUMBER IS n NUMBER;
                  BEGIN SELECT COUNT(*) INTO n FROM orders; RETURN n; END;");
    }

    [ClassCleanup]
    public static void DropFixture() {
        if (string.IsNullOrWhiteSpace(_connectionString)) {
            return;
        }
        Drop("DROP FUNCTION count_orders");
        Drop("DROP VIEW active_orders");
        Drop("DROP TABLE order_lines PURGE");
        Drop("DROP TABLE orders PURGE");
    }

    private static void Execute(string sql) {
        using var live = new OracleConnection(_connectionString);
        live.Open();
        using var command = new OracleCommand(sql, live) { CommandTimeout = 60 };
        command.ExecuteNonQuery();
    }

    /// <summary>Oracle has no DROP IF EXISTS, so a first run has nothing to drop and
    /// says so with ORA-00942 / ORA-04043.</summary>
    private static void Drop(string sql) {
        try {
            Execute(sql);
        } catch (OracleException) {
        }
    }

    [TestInitialize]
    public void Setup() {
        if (string.IsNullOrWhiteSpace(_connectionString)) {
            var message = $"Set {_connectionVar} to a connection string to run the live Oracle tests.";
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLRKERNEL_TEST_REQUIRE_LIVE"))) {
                Assert.Fail(message + " CLRKERNEL_TEST_REQUIRE_LIVE is set, so this run was expected "
                    + "to reach a real server.");
            }
            Assert.Inconclusive(message);
        }
        var secrets = new InMemorySecretProvider();
        secrets.Set("live:oracle", new OracleConnectionStringBuilder(_connectionString).Password);
        _runner = new QueryRunner(
            SecretStore.ForProviders(secrets), NullLogger<QueryRunner>.Instance);
        _connection = new StoredConnection {
            Id = "live-ora",
            Name = "live-ora",
            Type = "Oracle",
            TimeoutSeconds = 60,
            RowCap = 10,
            SecretRef = "live:oracle",
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["connectionString"] = _connectionString,
            },
        };
    }

    /// <summary>One service, so one entry rather than an empty folder — the tree needs
    /// something to open.</summary>
    [TestMethod]
    public async Task TheTopLevelIsTheOneServiceThisConnectionReaches() {
        var databases = await BrowseAsync((live, token) => _dialect.DatabasesAsync(live, token));
        Assert.AreEqual(1, databases.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(databases[0].Name));
    }

    [TestMethod]
    public async Task TheSchemaLevelIsTheUsersThatOwnSomething() {
        var schemas = await BrowseAsync((live, token) => _dialect.SchemasAsync(live, token));
        var names = schemas.Select(s => s.Name).ToArray();
        CollectionAssert.Contains(names, _schema);
        CollectionAssert.DoesNotContain(names, "SYS");
        CollectionAssert.DoesNotContain(names, "SYSTEM");
    }

    [TestMethod]
    public async Task ASchemaListsItsTablesViewsAndFunctionsInOnePass() {
        var objects = await BrowseAsync(
            (live, token) => _dialect.ObjectsAsync(live, _schema, token));
        var byName = objects.ToDictionary(o => o.Name, o => o.Kind, StringComparer.OrdinalIgnoreCase);

        Assert.AreEqual("table", byName["ORDERS"]);
        Assert.AreEqual("table", byName["ORDER_LINES"]);
        Assert.AreEqual("view", byName["ACTIVE_ORDERS"]);
        Assert.AreEqual("function", byName["COUNT_ORDERS"]);
    }

    [TestMethod]
    public async Task ATablesColumnsKeysAndIndexesComeBackTogether() {
        var detail = await BrowseAsync(
            (live, token) => _dialect.DetailAsync(live, _schema, "ORDERS", token));

        var columns = detail.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        Assert.IsTrue(columns["ORDER_ID"].Identity, "GENERATED ALWAYS AS IDENTITY");
        Assert.IsTrue(columns["ORDER_ID"].PrimaryKey);
        Assert.IsFalse(columns["ORDER_ID"].Nullable, "Oracle spells this Y/N, not a boolean");

        Assert.AreEqual("VARCHAR2(50)", columns["CUSTOMER"].Type);
        Assert.IsTrue(columns["CUSTOMER"].Nullable);
        Assert.AreEqual("NUMBER(18,2)", columns["TOTAL"].Type);

        Assert.IsTrue(detail.Keys.Any(k => k.Contains("PRIMARY KEY")), string.Join(" | ", detail.Keys));
        Assert.IsTrue(detail.Indexes.Any(i => i.Contains("IX_ORDERS_CUSTOMER")),
            string.Join(" | ", detail.Indexes));
    }

    [TestMethod]
    public async Task AForeignKeyNamesWhatItPointsAt() {
        var detail = await BrowseAsync(
            (live, token) => _dialect.DetailAsync(live, _schema, "ORDER_LINES", token));
        Assert.IsTrue(
            detail.Keys.Any(k => k.Contains("FK_ORDER_LINES_ORDERS") && k.Contains("ORDERS")),
            string.Join(" | ", detail.Keys));
    }

    [TestMethod]
    public async Task CompletionsCarryEveryTableAndItsColumns() {
        var completions = await BrowseAsync(
            (live, token) => _dialect.CompletionsAsync(live, token));
        var orders = completions.Objects.Single(
            o => o.Schema == _schema && o.Name == "ORDERS");
        CollectionAssert.AreEquivalent(
            new[] { "ORDER_ID", "CUSTOMER", "TOTAL" }, orders.Columns.ToArray());
        Assert.IsFalse(completions.Objects.Any(o => o.Schema == "SYS"));
    }

    /// <summary>Oracle's spelling, and quoted — the catalog reports upper-case names
    /// and an unquoted script would work only by accident.</summary>
    [TestMethod]
    public async Task ScriptsAreInOracleSpelling() {
        var select = await ScriptAsync("ORDERS", "table", "select");
        StringAssert.Contains(select, "FETCH FIRST 1000 ROWS ONLY");
        StringAssert.Contains(select, "\"ORDERS\"");
        Assert.IsFalse(select.Contains("TOP "), "that is SQL Server's");

        var insert = await ScriptAsync("ORDERS", "table", "insert");
        Assert.IsFalse(insert.Contains("\"ORDER_ID\","),
            "an identity column is the server's to fill; naming it always fails");

        var view = await ScriptAsync("ACTIVE_ORDERS", "view", "create");
        StringAssert.Contains(view, "SELECT");
    }

    /// <summary>The generated SELECT has to actually run — that is where a quoting
    /// mistake would hide.</summary>
    [TestMethod]
    public async Task TheGeneratedSelectRuns() {
        var result = await RunAsync(
            (await ScriptAsync("ORDERS", "table", "select")).TrimEnd());
        Assert.IsNull(result.Error, result.Error);
        CollectionAssert.AreEquivalent(
            new[] { "ORDER_ID", "CUSTOMER", "TOTAL" }, result.ResultSets[0].Columns.ToArray());
    }

    [TestMethod]
    public async Task TheRowCapTruncatesRatherThanCounting() {
        var result = await RunAsync("SELECT * FROM orders");
        Assert.IsNull(result.Error, result.Error);
        Assert.AreEqual(10, result.ResultSets[0].Rows.Count, "the connection's cap");
        Assert.IsTrue(result.ResultSets[0].Truncated);
    }

    [TestMethod]
    public async Task AFailingStatementComesBackAsAMessage() {
        var result = await RunAsync("SELECT * FROM no_such_table");
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error, "ORA-");
    }

    private Task<QueryResult> RunAsync(string sql) =>
        _runner.RunAsync(
            _connection, sql, leastPrivilege: false, Guid.NewGuid(), Guid.NewGuid().ToString("N"),
            password: null, CancellationToken.None);

    private Task<string> ScriptAsync(string obj, string kind, string variant) =>
        BrowseAsync((live, token) => _dialect.ScriptAsync(live, _schema, obj, kind, variant, token));

    private async Task<T> BrowseAsync<T>(Func<DbConnection, CancellationToken, Task<T>> read) {
        var (value, error) = await _runner.BrowseAsync(
            _connection, leastPrivilege: false, password: null, database: null,
            read, CancellationToken.None);
        Assert.IsNull(error, error);
        return value;
    }
}

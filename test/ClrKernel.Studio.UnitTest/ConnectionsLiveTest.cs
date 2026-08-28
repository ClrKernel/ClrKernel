using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// The Connections area against a real SQL Server. The object-tree queries, the
/// reader loop and the generated scripts cannot be covered any other way — a closed
/// port proves the error path and nothing else.
/// <para>
/// Set <c>CLRKERNEL_STUDIO_TEST_SQL</c> to a connection string. Missing, these are
/// inconclusive so CI stays green; set <c>CLRKERNEL_TEST_REQUIRE_LIVE=1</c> as well
/// and a missing backend <b>fails</b> instead, which is the same gate the database
/// suite uses and for the same reason: a verification run that silently executed
/// nothing is worse than one that did not run.
/// </para>
/// <para>
/// <c>dev/docker-compose.dbs.yml</c> already defines a server for this:
/// <code>
/// docker compose -f dev/docker-compose.dbs.yml up -d sqlserver
/// CLRKERNEL_STUDIO_TEST_SQL='Server=localhost,51433;Database=master;User Id=sa;Password=DevOnly!Passw0rd;TrustServerCertificate=True' \
///   CLRKERNEL_TEST_REQUIRE_LIVE=1 ./build.sh Test --filter ClassName~ConnectionsLiveTest
/// </code>
/// </para>
/// </summary>
[TestClass]
public class ConnectionsLiveTest {
    private const string _connectionVar = "CLRKERNEL_STUDIO_TEST_SQL";

    /// <summary>
    /// The fixture database, created with a collation that is deliberately <b>not</b>
    /// the server's default.
    /// <para>
    /// That is the whole point of it. The object tree once built its labels by
    /// concatenating catalog strings in SQL, which SQL Server refuses across a
    /// collation boundary — "Cannot resolve collation conflict … in add operator" —
    /// so it worked on every server whose databases matched it and failed on the one
    /// that mattered. Every metadata test below now runs against a mismatched
    /// database, so the regression cannot come back quietly.
    /// </para>
    /// </summary>
    private const string _database = "clrkernel_live_test";
    private const string _collation = "Latin1_General_CI_AS_KS_WS";
    private const string _schema = "shop";

    private static string _connectionString;

    private QueryRunner _runner;
    private readonly IConnectionDialect _dialect = new SqlServerDialect();
    private StoredConnection _connection;

    [ClassInitialize]
    public static void CreateFixture(TestContext context) {
        _connectionString = Environment.GetEnvironmentVariable(_connectionVar);
        if (string.IsNullOrWhiteSpace(_connectionString)) {
            return; // the per-test gate reports this properly
        }
        Execute($@"
            IF DB_ID('{_database}') IS NOT NULL BEGIN
                ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_database}];
            END
            CREATE DATABASE [{_database}] COLLATE {_collation};");

        Execute($@"
            CREATE SCHEMA {_schema};", _database);

        Execute($@"
            CREATE TABLE {_schema}.Orders (
                OrderId int IDENTITY(1,1) NOT NULL,
                Customer nvarchar(50) NULL,
                Total decimal(18,2) NOT NULL,
                CONSTRAINT PK_Orders PRIMARY KEY (OrderId));
            CREATE INDEX IX_Orders_Customer ON {_schema}.Orders(Customer);
            CREATE TABLE {_schema}.OrderLines (
                OrderLineId int NOT NULL PRIMARY KEY,
                OrderId int NOT NULL,
                CONSTRAINT FK_OrderLines_Orders FOREIGN KEY (OrderId)
                    REFERENCES {_schema}.Orders(OrderId));
            INSERT INTO {_schema}.Orders (Customer, Total)
                SELECT TOP 25 'customer ' + CAST(object_id AS nvarchar(20)), 1.50
                FROM sys.all_objects;", _database);

        Execute($@"
            CREATE VIEW {_schema}.ActiveOrders AS
                SELECT OrderId, Customer FROM {_schema}.Orders WHERE Total > 0;", _database);

        Execute($@"
            CREATE PROCEDURE {_schema}.CountOrders AS
                SELECT COUNT(*) FROM {_schema}.Orders;", _database);
    }

    [ClassCleanup]
    public static void DropFixture() {
        if (string.IsNullOrWhiteSpace(_connectionString)) {
            return;
        }
        try {
            Execute($@"
                IF DB_ID('{_database}') IS NOT NULL BEGIN
                    ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{_database}];
                END");
        } catch (SqlException) {
            // A container that is going away anyway. Leaving the database behind is
            // not worth failing a green run over.
        }
    }

    private static void Execute(string sql, string database = null) {
        var builder = new SqlConnectionStringBuilder(_connectionString);
        if (database != null) {
            builder.InitialCatalog = database;
        }
        using var live = new SqlConnection(builder.ConnectionString);
        live.Open();
        // Batches, because CREATE SCHEMA and CREATE VIEW each insist on being first
        // in theirs — the same reason a script file is full of GO.
        foreach (var batch in sql.Split(new[] { "\nGO\n" }, StringSplitOptions.None)) {
            using var command = new SqlCommand(batch, live) { CommandTimeout = 60 };
            command.ExecuteNonQuery();
        }
    }

    [TestInitialize]
    public void Setup() {
        if (string.IsNullOrWhiteSpace(_connectionString)) {
            var message = $"Set {_connectionVar} to a connection string to run the live connection tests.";
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLRKERNEL_TEST_REQUIRE_LIVE"))) {
                Assert.Fail(message + " CLRKERNEL_TEST_REQUIRE_LIVE is set, so this run was expected "
                    + "to reach a real server.");
            }
            Assert.Inconclusive(message);
        }
        _runner = new QueryRunner(
            SecretStore.ForProviders(new InMemorySecretProvider()), NullLogger<QueryRunner>.Instance);
        _connection = new StoredConnection {
            Id = "live",
            Name = "live",
            Type = "SqlServer",
            TimeoutSeconds = 60,
            RowCap = 10,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["connectionString"] = _connectionString,
            },
        };
    }

    // --- running a query ----------------------------------------------------

    [TestMethod]
    public async Task AQueryReturnsItsRowsAndItsMessages() {
        var result = await RunAsync("PRINT 'hello'; SELECT 1 AS n, 'x' AS s;");
        Assert.IsNull(result.Error);
        Assert.AreEqual(1, result.ResultSets.Count);
        CollectionAssert.AreEqual(new[] { "n", "s" }, result.ResultSets[0].Columns.ToArray());
        CollectionAssert.AreEqual(new[] { "number", "string" }, result.ResultSets[0].Types.ToArray());
        CollectionAssert.AreEqual(new[] { "1", "x" }, result.ResultSets[0].Rows[0].ToArray());
        CollectionAssert.Contains(result.Messages.ToArray(), "hello");
    }

    [TestMethod]
    public async Task TwoSelectsBecomeTwoResultSets() {
        var result = await RunAsync("SELECT 1; SELECT 2;");
        Assert.IsNull(result.Error);
        Assert.AreEqual(2, result.ResultSets.Count);
    }

    [TestMethod]
    public async Task TheRowCapStopsShortAndSaysSo() {
        var result = await RunAsync(
            $"SELECT OrderId FROM {_schema}.Orders", _database);
        Assert.IsNull(result.Error);
        Assert.AreEqual(10, result.ResultSets[0].Rows.Count, "the cap is 10 and the table holds 25");
        Assert.IsTrue(result.ResultSets[0].Truncated,
            "the cap reads one row past itself so truncation is known without a COUNT");
    }

    [TestMethod]
    public async Task ANullIsNotAnEmptyString() {
        var result = await RunAsync("SELECT CAST(NULL AS nvarchar(10)) AS n, '' AS e");
        Assert.IsNull(result.ResultSets[0].Rows[0][0]);
        Assert.AreEqual(string.Empty, result.ResultSets[0].Rows[0][1]);
    }

    [TestMethod]
    public async Task AFailingStatementIsAMessageRatherThanAFault() {
        var result = await RunAsync("SELECT * FROM dbo.NoSuchTableAnywhere");
        Assert.IsNotNull(result.Error);
        Assert.IsFalse(result.Canceled, "a failure is not a cancellation");
    }

    [TestMethod]
    public async Task CancellingStopsALongQuery() {
        var queryId = Guid.NewGuid().ToString("N");
        var actor = Guid.NewGuid();
        var running = _runner.RunAsync(
            _connection, "WAITFOR DELAY '00:00:30'; SELECT 1", leastPrivilege: false, actor, queryId,
            password: null, CancellationToken.None);

        var cancelled = false;
        for (var attempt = 0; attempt < 50 && !cancelled; attempt++) {
            await Task.Delay(100);
            cancelled = _runner.Cancel(queryId, actor);
        }
        Assert.IsTrue(cancelled, "the running command should have been found and cancelled");

        var result = await running;
        Assert.IsTrue(result.Canceled);
        Assert.IsTrue(result.ElapsedMs < 25_000, "it should not have waited out the full delay");
    }

    [TestMethod]
    public async Task CancellingSomebodyElsesQueryDoesNothing() {
        var queryId = Guid.NewGuid().ToString("N");
        var running = _runner.RunAsync(
            _connection, "WAITFOR DELAY '00:00:03'; SELECT 1", leastPrivilege: false,
            Guid.NewGuid(), queryId, password: null, CancellationToken.None);
        await Task.Delay(300);

        Assert.IsFalse(_runner.Cancel(queryId, Guid.NewGuid()),
            "cancel is scoped to whoever started the query, or the route is 'stop anyone's query'");
        var result = await running;
        Assert.IsFalse(result.Canceled);
    }

    // --- the object tree, against a differently-collated database -----------

    [TestMethod]
    public async Task TheFixtureIsCollatedUnlikeTheServerOrNoneOfThisProvesAnything() {
        var result = await RunAsync(
            "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))", _database);
        Assert.AreEqual(_collation, result.ResultSets[0].Rows[0][0]);

        var server = await RunAsync("SELECT CONVERT(nvarchar(128), SERVERPROPERTY('Collation'))");
        Assert.AreNotEqual(_collation, server.ResultSets[0].Rows[0][0],
            "the point of the fixture is the mismatch; a matching server would test nothing");
    }

    [TestMethod]
    public async Task TheTreeFindsTheDatabaseAndItsSchema() {
        var databases = await BrowseAsync((live, token) => _dialect.DatabasesAsync(live, token));
        CollectionAssert.Contains(databases.Select(d => d.Name).ToArray(), _database);

        var schemas = await BrowseAsync((live, token) =>
            _dialect.SchemasAsync(live, token));
        CollectionAssert.Contains(schemas.Select(s => s.Name).ToArray(), _schema);
        CollectionAssert.DoesNotContain(schemas.Select(s => s.Name).ToArray(), "sys",
            "the shipped schemas are noise in a tree somebody is looking for their own tables in");
    }

    [TestMethod]
    public async Task ASchemaListsItsTablesViewsAndProgrammabilityInOnePass() {
        var objects = await BrowseAsync((live, token) =>
            _dialect.ObjectsAsync(live, _schema, token));
        var byName = objects.ToDictionary(o => o.Name, o => o.Kind, StringComparer.OrdinalIgnoreCase);

        Assert.AreEqual("table", byName["Orders"]);
        Assert.AreEqual("table", byName["OrderLines"]);
        Assert.AreEqual("view", byName["ActiveOrders"]);
        Assert.AreEqual("procedure", byName["CountOrders"]);
    }

    [TestMethod]
    public async Task ATablesColumnsKeysAndIndexesComeBackTogether() {
        var detail = await BrowseAsync((live, token) =>
            _dialect.DetailAsync(live, _schema, "Orders", token));

        var columns = detail.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual("int", columns["OrderId"].Type);
        Assert.IsTrue(columns["OrderId"].Identity);
        Assert.IsTrue(columns["OrderId"].PrimaryKey);
        Assert.IsFalse(columns["OrderId"].Nullable);

        // sys.columns counts bytes for nvarchar; a script counts characters. Declared
        // as nvarchar(50), so anything reporting 100 has forgotten to halve it.
        Assert.AreEqual("nvarchar(50)", columns["Customer"].Type);
        Assert.IsTrue(columns["Customer"].Nullable);
        Assert.AreEqual("decimal(18,2)", columns["Total"].Type);

        Assert.IsTrue(detail.Keys.Any(k => k.Contains("PK_Orders")), string.Join(" | ", detail.Keys));
        Assert.IsTrue(detail.Indexes.Any(i => i.Contains("IX_Orders_Customer")),
            string.Join(" | ", detail.Indexes));
    }

    [TestMethod]
    public async Task AForeignKeyIsShownWithWhatItPointsAt() {
        var detail = await BrowseAsync((live, token) =>
            _dialect.DetailAsync(live, _schema, "OrderLines", token));
        var foreignKey = detail.Keys.SingleOrDefault(k => k.Contains("FK_OrderLines_Orders"));
        Assert.IsNotNull(foreignKey, string.Join(" | ", detail.Keys));
        StringAssert.Contains(foreignKey, $"{_schema}.Orders");
    }

    [TestMethod]
    public async Task AViewHasColumnsToo() {
        var detail = await BrowseAsync((live, token) =>
            _dialect.DetailAsync(live, _schema, "ActiveOrders", token));
        CollectionAssert.AreEquivalent(
            new[] { "OrderId", "Customer" }, detail.Columns.Select(c => c.Name).ToArray());
    }

    // --- completion ---------------------------------------------------------

    [TestMethod]
    public async Task TheCompletionSchemaIsEveryTableAndViewWithItsColumns() {
        var schema = await BrowseAsync((live, token) =>
            _dialect.CompletionsAsync(live, token));

        Assert.AreEqual(_database, schema.Database);
        Assert.IsFalse(schema.Truncated, "a fixture this small is not near the cap");

        var orders = schema.Objects.Single(o =>
            o.Schema == _schema && o.Name == "Orders");
        Assert.AreEqual("table", orders.Kind);
        CollectionAssert.AreEqual(
            new[] { "OrderId", "Customer", "Total" }, orders.Columns.ToArray(),
            "and in the order the table declares them, not alphabetically");

        var view = schema.Objects.Single(o => o.Name == "ActiveOrders");
        Assert.AreEqual("view", view.Kind);
        CollectionAssert.AreEquivalent(new[] { "OrderId", "Customer" }, view.Columns.ToArray());
    }

    [TestMethod]
    public async Task ItLeavesOutTheThingsThereIsNothingToCompleteAgainst() {
        var schema = await BrowseAsync((live, token) =>
            _dialect.CompletionsAsync(live, token));

        Assert.IsFalse(schema.Objects.Any(o => o.Schema == "sys"),
            "the shipped catalog is not what somebody is typing a query against");
        Assert.IsFalse(schema.Objects.Any(o => o.Name == "CountOrders"),
            "a procedure has no columns to complete and would only pad the payload");
    }

    [TestMethod]
    public async Task EveryObjectItReportsHasAtLeastOneColumn() {
        // The LEFT JOIN is there so an object with no columns still appears; the
        // grouping is what must not drop or merge them.
        var schema = await BrowseAsync((live, token) =>
            _dialect.CompletionsAsync(live, token));
        Assert.IsTrue(schema.Objects.Count >= 3, schema.Objects.Count.ToString());
        foreach (var found in schema.Objects) {
            Assert.IsTrue(found.Columns.Count > 0, $"{found.Schema}.{found.Name}");
        }
    }

    // --- scripting ----------------------------------------------------------

    [TestMethod]
    public async Task ATableScriptsAsGeneratedSqlThatSaysWhatItIs() {
        var script = await ScriptAsync("Orders", "table", "create");
        StringAssert.StartsWith(script, $"CREATE TABLE [{_schema}].[Orders]");
        StringAssert.Contains(script, "[OrderId] int IDENTITY NOT NULL");
        StringAssert.Contains(script, "[Customer] nvarchar(50) NULL");
        StringAssert.Contains(script, "[Total] decimal(18,2) NOT NULL");
        StringAssert.Contains(script, "PRIMARY KEY ([OrderId])");
    }

    [TestMethod]
    public async Task AViewScriptsAsItsStoredDefinition() {
        var script = await ScriptAsync("ActiveOrders", "view", "create");
        StringAssert.Contains(script.ToUpperInvariant(), "CREATE VIEW");
        StringAssert.Contains(script.ToUpperInvariant(), "SELECT");
    }

    [TestMethod]
    public async Task AProcedureScriptsAsItsStoredDefinition() {
        var script = await ScriptAsync("CountOrders", "procedure", "create");
        StringAssert.Contains(script.ToUpperInvariant(), "CREATE PROCEDURE");
    }

    [TestMethod]
    public async Task DropNamesTheRightKindOfThing() {
        StringAssert.StartsWith(await ScriptAsync("Orders", "table", "drop"),
            $"DROP TABLE [{_schema}].[Orders]");
        StringAssert.StartsWith(await ScriptAsync("ActiveOrders", "view", "drop"),
            $"DROP VIEW [{_schema}].[ActiveOrders]");
        StringAssert.StartsWith(await ScriptAsync("CountOrders", "procedure", "drop"),
            $"DROP PROCEDURE [{_schema}].[CountOrders]");
    }

    [TestMethod]
    public async Task SelectNamesTheColumnsRatherThanStar() {
        var script = await ScriptAsync("Orders", "table", "select");
        StringAssert.StartsWith(script, "SELECT TOP 1000 [OrderId]");
        StringAssert.Contains(script, "[Customer]");
        StringAssert.Contains(script, $"FROM [{_schema}].[Orders]");
    }

    [TestMethod]
    public async Task InsertAndUpdateLeaveTheIdentityColumnOut() {
        // Naming an identity column in an INSERT produces a statement that always
        // fails, so SSMS omits it and so do we.
        var insert = await ScriptAsync("Orders", "table", "insert");
        StringAssert.StartsWith(insert, $"INSERT INTO [{_schema}].[Orders] ([Customer], [Total])");
        StringAssert.Contains(insert, "<Customer, nvarchar(50),>");
        Assert.IsFalse(insert.Contains("OrderId"), insert);

        var update = await ScriptAsync("Orders", "table", "update");
        StringAssert.StartsWith(update, $"UPDATE [{_schema}].[Orders]");
        StringAssert.Contains(update, "<search condition,,>");
        Assert.IsFalse(update.Contains("[OrderId] ="), update);
    }

    [TestMethod]
    public async Task DeleteIsAStatementYouHaveToFinish() {
        var script = await ScriptAsync("Orders", "table", "delete");
        StringAssert.StartsWith(script, $"DELETE FROM [{_schema}].[Orders]");
        StringAssert.Contains(script, "<search condition,,>");
    }

    /// <summary>A generated script has to be one the server accepts — which is the
    /// half a string comparison cannot check.</summary>
    [TestMethod]
    public async Task TheGeneratedCreateTableIsSqlTheServerAccepts() {
        var script = await ScriptAsync("Orders", "table", "create");
        var rewritten = script.Replace($"[{_schema}].[Orders]", $"[{_schema}].[Orders_Copy]");

        var result = await RunAsync(rewritten, _database);
        Assert.IsNull(result.Error, result.Error);
        try {
            var detail = await BrowseAsync((live, token) =>
                _dialect.DetailAsync(live, _schema, "Orders_Copy", token));
            Assert.AreEqual(3, detail.Columns.Count);
        } finally {
            await RunAsync($"DROP TABLE {_schema}.Orders_Copy", _database);
        }
    }

    // --- helpers ------------------------------------------------------------

    private Task<QueryResult> RunAsync(string sql, string database = null) {
        var connection = _connection;
        if (database != null) {
            var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = database };
            connection = _connection.Clone();
            connection.Settings["connectionString"] = builder.ConnectionString;
        }
        return _runner.RunAsync(
            connection, sql, leastPrivilege: false, Guid.NewGuid(), Guid.NewGuid().ToString("N"),
            password: null, CancellationToken.None);
    }

    private Task<string> ScriptAsync(string obj, string kind, string variant) =>
        BrowseAsync((live, token) => _dialect.ScriptAsync(
            live, _schema, obj, kind, variant, token));

    /// <summary>Browses inside <paramref name="database"/>, which is now part of
    /// opening rather than a step afterwards — PostgreSQL has no other way.</summary>
    private async Task<T> BrowseAsync<T>(
        Func<DbConnection, CancellationToken, Task<T>> read, string database = null) {
        var (value, error) = await _runner.BrowseAsync(
            _connection, leastPrivilege: false, password: null, database ?? _database,
            read, CancellationToken.None);
        Assert.IsNull(error, error);
        return value;
    }
}

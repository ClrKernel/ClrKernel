using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// The Connections area against a real PostgreSQL — the second dialect, which is
/// what proves the first one was an abstraction rather than a rename.
/// <para>
/// The difference that shaped the interface is asserted here: PostgreSQL has no
/// <c>USE</c>, so browsing another database is a second connection. If
/// <c>Open</c> ever stops taking the database, these fail rather than quietly
/// browsing whichever one the connection happened to name.
/// </para>
/// <para>
/// Set <c>CLRKERNEL_STUDIO_TEST_POSTGRES</c> to a connection string. Missing, these
/// are inconclusive so CI stays green; with <c>CLRKERNEL_TEST_REQUIRE_LIVE=1</c> a
/// missing backend fails instead.
/// <code>
/// docker compose -f dev/docker-compose.dbs.yml up -d postgres
/// CLRKERNEL_STUDIO_TEST_POSTGRES='Host=localhost;Port=55432;Database=postgres;Username=postgres;Password=devonly' \
///   CLRKERNEL_TEST_REQUIRE_LIVE=1 ./build.sh Test --filter ClassName~PostgresLiveTest
/// </code>
/// </para>
/// </summary>
[TestClass]
public class PostgresLiveTest {
    private const string _connectionVar = "CLRKERNEL_STUDIO_TEST_POSTGRES";
    private const string _database = "clrkernel_live_test";
    private const string _schema = "shop";

    private static string _connectionString;

    private QueryRunner _runner;
    private readonly IConnectionDialect _dialect = new PostgresDialect();
    private StoredConnection _connection;

    [ClassInitialize]
    public static void CreateFixture(TestContext context) {
        _connectionString = Environment.GetEnvironmentVariable(_connectionVar);
        if (string.IsNullOrWhiteSpace(_connectionString)) {
            return; // the per-test gate reports this properly
        }
        // CREATE DATABASE cannot run inside a transaction or against itself, so the
        // drop-and-make pair happens on the connection's own database.
        Execute($@"DROP DATABASE IF EXISTS {_database} WITH (FORCE);");
        Execute($@"CREATE DATABASE {_database};");

        Execute($@"CREATE SCHEMA {_schema};", _database);
        Execute($@"
            CREATE TABLE {_schema}.orders (
                order_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                customer varchar(50) NULL,
                total numeric(18,2) NOT NULL);
            CREATE INDEX ix_orders_customer ON {_schema}.orders(customer);
            CREATE TABLE {_schema}.order_lines (
                order_line_id integer PRIMARY KEY,
                order_id integer NOT NULL
                    CONSTRAINT fk_order_lines_orders REFERENCES {_schema}.orders(order_id));
            INSERT INTO {_schema}.orders (customer, total)
                SELECT 'customer ' || g, 1.50 FROM generate_series(1, 25) g;
            CREATE VIEW {_schema}.active_orders AS
                SELECT order_id, customer FROM {_schema}.orders WHERE total > 0;
            CREATE FUNCTION {_schema}.count_orders() RETURNS bigint
                LANGUAGE sql AS $$ SELECT count(*) FROM shop.orders $$;", _database);
    }

    [ClassCleanup]
    public static void DropFixture() {
        if (string.IsNullOrWhiteSpace(_connectionString)) {
            return;
        }
        try {
            Execute($"DROP DATABASE IF EXISTS {_database} WITH (FORCE);");
        } catch (NpgsqlException) {
            // A container that is going away anyway.
        }
    }

    private static void Execute(string sql, string database = null) {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        if (database != null) {
            builder.Database = database;
        }
        using var live = new NpgsqlConnection(builder.ConnectionString);
        live.Open();
        using var command = new NpgsqlCommand(sql, live) { CommandTimeout = 60 };
        command.ExecuteNonQuery();
    }

    [TestInitialize]
    public void Setup() {
        if (string.IsNullOrWhiteSpace(_connectionString)) {
            var message = $"Set {_connectionVar} to a connection string to run the live PostgreSQL tests.";
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLRKERNEL_TEST_REQUIRE_LIVE"))) {
                Assert.Fail(message + " CLRKERNEL_TEST_REQUIRE_LIVE is set, so this run was expected "
                    + "to reach a real server.");
            }
            Assert.Inconclusive(message);
        }
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        var secrets = new InMemorySecretProvider();
        secrets.Set("live:postgres", builder.Password);
        _runner = new QueryRunner(
            SecretStore.ForProviders(secrets), NullLogger<QueryRunner>.Instance);
        _connection = new StoredConnection {
            Id = "live-pg",
            Name = "live-pg",
            Type = "Postgres",
            TimeoutSeconds = 60,
            RowCap = 10,
            SecretRef = "live:postgres",
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["server"] = builder.Host,
                ["port"] = builder.Port.ToString(),
                ["database"] = builder.Database,
                ["user"] = builder.Username,
            },
        };
    }

    /// <summary>
    /// The whole reason the database is part of opening: the connection names
    /// <c>postgres</c>, and every tree query below reaches the fixture database only
    /// because <c>Open</c> was told to.
    /// </summary>
    [TestMethod]
    public async Task BrowsingAnotherDatabaseOpensASecondConnection() {
        var databases = await BrowseAsync((live, token) => _dialect.DatabasesAsync(live, token));
        CollectionAssert.Contains(databases.Select(d => d.Name).ToArray(), _database);

        var connected = await BrowseAsync(
            (live, _) => Task.FromResult(live.Database), _database);
        Assert.AreEqual(_database, connected);

        // Templates are folders that error when clicked, so they are not offered.
        CollectionAssert.DoesNotContain(databases.Select(d => d.Name).ToArray(), "template0");
    }

    [TestMethod]
    public async Task TheTreeFindsTheSchemaAndSkipsTheSystemOnes() {
        var schemas = await BrowseAsync((live, token) => _dialect.SchemasAsync(live, token), _database);
        var names = schemas.Select(s => s.Name).ToArray();
        CollectionAssert.Contains(names, _schema);
        CollectionAssert.DoesNotContain(names, "pg_catalog");
        CollectionAssert.DoesNotContain(names, "information_schema");
    }

    [TestMethod]
    public async Task ASchemaListsItsTablesViewsAndFunctionsInOnePass() {
        var objects = await BrowseAsync(
            (live, token) => _dialect.ObjectsAsync(live, _schema, token), _database);
        var byName = objects.ToDictionary(o => o.Name, o => o.Kind, StringComparer.OrdinalIgnoreCase);

        Assert.AreEqual("table", byName["orders"]);
        Assert.AreEqual("table", byName["order_lines"]);
        Assert.AreEqual("view", byName["active_orders"]);
        Assert.AreEqual("function", byName["count_orders"]);
    }

    [TestMethod]
    public async Task ATablesColumnsKeysAndIndexesComeBackTogether() {
        var detail = await BrowseAsync(
            (live, token) => _dialect.DetailAsync(live, _schema, "orders", token), _database);

        var columns = detail.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        // format_type is what psql prints, so the list and the generated CREATE agree.
        Assert.AreEqual("integer", columns["order_id"].Type);
        Assert.IsTrue(columns["order_id"].Identity, "GENERATED ALWAYS AS IDENTITY");
        Assert.IsTrue(columns["order_id"].PrimaryKey);
        Assert.IsFalse(columns["order_id"].Nullable);

        Assert.AreEqual("character varying(50)", columns["customer"].Type);
        Assert.IsTrue(columns["customer"].Nullable);
        Assert.AreEqual("numeric(18,2)", columns["total"].Type);

        Assert.IsTrue(detail.Keys.Any(k => k.Contains("PRIMARY KEY")), string.Join(" | ", detail.Keys));
        Assert.IsTrue(detail.Indexes.Any(i => i.Contains("ix_orders_customer")),
            string.Join(" | ", detail.Indexes));
    }

    [TestMethod]
    public async Task AForeignKeyNamesWhatItPointsAt() {
        var detail = await BrowseAsync(
            (live, token) => _dialect.DetailAsync(live, _schema, "order_lines", token), _database);
        Assert.IsTrue(
            detail.Keys.Any(k => k.Contains("fk_order_lines_orders") && k.Contains("shop.orders")),
            string.Join(" | ", detail.Keys));
    }

    [TestMethod]
    public async Task CompletionsCarryEveryTableAndItsColumns() {
        var completions = await BrowseAsync(
            (live, token) => _dialect.CompletionsAsync(live, token), _database);

        Assert.AreEqual(_database, completions.Database);
        var orders = completions.Objects.Single(
            o => o.Schema == _schema && o.Name == "orders");
        CollectionAssert.AreEquivalent(
            new[] { "order_id", "customer", "total" }, orders.Columns.ToArray());
        Assert.IsTrue(completions.Objects.Any(o => o.Name == "active_orders" && o.Kind == "view"));
        Assert.IsFalse(completions.Objects.Any(o => o.Schema == "pg_catalog"));
    }

    /// <summary>Quoted, and LIMIT rather than TOP — a script that does not run on the
    /// database it was generated for is worse than none.</summary>
    [TestMethod]
    public async Task ScriptsAreInPostgresSpelling() {
        var select = await ScriptAsync("orders", "table", "select");
        StringAssert.Contains(select, "LIMIT 1000");
        StringAssert.Contains(select, "\"shop\".\"orders\"");

        var insert = await ScriptAsync("orders", "table", "insert");
        Assert.IsFalse(insert.Contains("order_id\","),
            "an identity column is the server's to fill; naming it always fails");

        var create = await ScriptAsync("orders", "table", "create");
        StringAssert.Contains(create, "CREATE TABLE");
        StringAssert.Contains(create, "PRIMARY KEY");

        var view = await ScriptAsync("active_orders", "view", "create");
        StringAssert.Contains(view, "SELECT");
    }

    /// <summary>The generated SELECT has to actually run — a script nobody can execute
    /// is where quoting mistakes hide.</summary>
    [TestMethod]
    public async Task TheGeneratedSelectRuns() {
        var result = await RunAsync(await ScriptAsync("orders", "table", "select"));
        Assert.IsNull(result.Error, result.Error);
        Assert.AreEqual(1, result.ResultSets.Count);
        CollectionAssert.AreEquivalent(
            new[] { "order_id", "customer", "total" }, result.ResultSets[0].Columns.ToArray());
    }

    [TestMethod]
    public async Task TheRowCapTruncatesRatherThanCounting() {
        var result = await RunAsync($"SELECT * FROM {_schema}.orders");
        Assert.IsNull(result.Error, result.Error);
        Assert.AreEqual(10, result.ResultSets[0].Rows.Count, "the connection's cap");
        Assert.IsTrue(result.ResultSets[0].Truncated);
    }

    /// <summary>A failing statement is an answer, and PostgreSQL's hint is the useful
    /// half of it.</summary>
    [TestMethod]
    public async Task AFailingStatementComesBackAsAMessage() {
        var result = await RunAsync("SELECT * FROM shop.no_such_table");
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error, "no_such_table");
    }

    private Task<QueryResult> RunAsync(string sql) {
        var connection = new StoredConnection {
            Id = _connection.Id,
            Name = _connection.Name,
            Type = _connection.Type,
            TimeoutSeconds = _connection.TimeoutSeconds,
            RowCap = _connection.RowCap,
            SecretRef = _connection.SecretRef,
            Settings = new Dictionary<string, string>(_connection.Settings, StringComparer.OrdinalIgnoreCase) {
                ["database"] = _database,
            },
        };
        return _runner.RunAsync(
            connection, sql, leastPrivilege: false, Guid.NewGuid(), Guid.NewGuid().ToString("N"),
            password: null, CancellationToken.None);
    }

    private Task<string> ScriptAsync(string obj, string kind, string variant) =>
        BrowseAsync((live, token) => _dialect.ScriptAsync(
            live, _schema, obj, kind, variant, token), _database);

    private async Task<T> BrowseAsync<T>(
        Func<DbConnection, CancellationToken, Task<T>> read, string database = null) {
        var (value, error) = await _runner.BrowseAsync(
            _connection, leastPrivilege: false, password: null, database, read, CancellationToken.None);
        Assert.IsNull(error, error);
        return value;
    }
}

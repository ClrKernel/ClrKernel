using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>
/// The Connections area against a real SQL Server. The object-tree queries and the
/// reader loop cannot be covered any other way — a closed port proves the error path
/// and nothing else — so these exist to be run rather than to pass by default.
/// <para>
/// Set <c>CLRKERNEL_JOBS_TEST_SQL</c> to a connection string. Missing, they are
/// inconclusive so CI stays green; set <c>CLRKERNEL_TEST_REQUIRE_LIVE=1</c> as well and
/// a missing backend <b>fails</b> instead, which is the same gate the database suite
/// uses and for the same reason: a verification run that silently executed nothing is
/// worse than one that did not run.
/// </para>
/// </summary>
[TestClass]
public class ConnectionsLiveTest {
    private const string _connectionVar = "CLRKERNEL_JOBS_TEST_SQL";

    private QueryRunner _runner;
    private StoredConnection _connection;

    [TestInitialize]
    public void Setup() {
        var connectionString = Environment.GetEnvironmentVariable(_connectionVar);
        if (string.IsNullOrWhiteSpace(connectionString)) {
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
            TimeoutSeconds = 30,
            RowCap = 10,
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["connectionString"] = connectionString,
            },
        };
    }

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
        // 25 rows against a cap of 10.
        var result = await RunAsync(
            "SELECT TOP 25 ROW_NUMBER() OVER (ORDER BY object_id) AS n FROM sys.objects");
        Assert.IsNull(result.Error);
        Assert.AreEqual(10, result.ResultSets[0].Rows.Count);
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

        // Give the command time to be registered and to reach the server.
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

    // --- the object tree ----------------------------------------------------

    [TestMethod]
    public async Task TheTreeWalksDownToAColumn() {
        var databases = await BrowseAsync((live, token) => SqlServerMetadata.DatabasesAsync(live, token));
        Assert.IsTrue(databases.Count > 0, "a login that can connect can see at least one database");

        var database = databases[0].Name;
        var schemas = await BrowseAsync((live, token) =>
            SqlServerMetadata.SchemasAsync(live, database, token));
        CollectionAssert.DoesNotContain(schemas.Select(s => s.Name).ToArray(), "sys",
            "the shipped schemas are noise in a tree somebody is looking for their own tables in");

        // sys.objects is guaranteed to exist and to be readable, so walk to it rather
        // than to whatever this particular server happens to hold.
        var detail = await BrowseAsync((live, token) =>
            SqlServerMetadata.DetailAsync(live, database, "sys", "objects", token));
        Assert.IsTrue(detail.Columns.Count > 0);
        Assert.IsTrue(detail.Columns.Any(c => c.Name == "object_id"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(detail.Columns[0].Type));
    }

    [TestMethod]
    public async Task AViewScriptsAsItsDefinitionAndATableAsGeneratedSql() {
        var view = await ScriptAsync("INFORMATION_SCHEMA", "TABLES", "view", "create");
        StringAssert.Contains(view.ToUpperInvariant(), "SELECT");

        var table = await ScriptAsync("sys", "objects", "table", "create");
        StringAssert.StartsWith(table, "CREATE TABLE [sys].[objects]");
    }

    [TestMethod]
    public async Task ScriptAsProducesTheUsualStatements() {
        StringAssert.StartsWith(await ScriptAsync("sys", "objects", "table", "drop"),
            "DROP TABLE [sys].[objects]");
        StringAssert.StartsWith(await ScriptAsync("INFORMATION_SCHEMA", "TABLES", "view", "drop"),
            "DROP VIEW [INFORMATION_SCHEMA].[TABLES]");

        var select = await ScriptAsync("sys", "objects", "table", "select");
        StringAssert.StartsWith(select, "SELECT TOP 1000 [name]");
        StringAssert.Contains(select, "FROM [sys].[objects]");

        // sys.objects has no identity column, so this only proves the shape; the
        // identity exclusion is covered by the unit test over the same rule.
        var insert = await ScriptAsync("sys", "objects", "table", "insert");
        StringAssert.StartsWith(insert, "INSERT INTO [sys].[objects] (");
        StringAssert.Contains(insert, "<name, sysname,>");

        StringAssert.StartsWith(await ScriptAsync("sys", "objects", "table", "delete"),
            "DELETE FROM [sys].[objects]");
    }

    [TestMethod]
    public async Task ADatabaseWithADifferentCollationStillOpensItsTables() {
        // The bug this covers: the keys and indexes were built by concatenating
        // catalog strings in SQL, which is a collation conflict the server refuses
        // outright on any database whose collation differs from its own. tempdb is
        // the server's, so this is the shape check; a differently-collated database
        // is what actually reproduced it.
        var databases = await BrowseAsync((live, token) => SqlServerMetadata.DatabasesAsync(live, token));
        foreach (var database in databases.Take(5)) {
            var detail = await BrowseAsync((live, token) =>
                SqlServerMetadata.DetailAsync(live, database.Name, "sys", "objects", token));
            Assert.IsTrue(detail.Columns.Count > 0, database.Name);
        }
    }

    private Task<string> ScriptAsync(string schema, string obj, string kind, string variant) =>
        BrowseAsync((live, token) => SqlServerMetadata.ScriptAsync(
            live, "master", schema, obj, kind, variant, token));

    private Task<QueryResult> RunAsync(string sql) =>
        _runner.RunAsync(
            _connection, sql, leastPrivilege: false, Guid.NewGuid(), Guid.NewGuid().ToString("N"),
            password: null, CancellationToken.None);

    private async Task<T> BrowseAsync<T>(
        Func<Microsoft.Data.SqlClient.SqlConnection, CancellationToken, Task<T>> read) {
        var (value, error) = await _runner.BrowseAsync(
            _connection, leastPrivilege: false, password: null, read, CancellationToken.None);
        Assert.IsNull(error, error);
        return value;
    }
}

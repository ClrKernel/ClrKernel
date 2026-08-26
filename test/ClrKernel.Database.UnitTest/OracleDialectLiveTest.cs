using System;
using System.IO;
using System.Linq;
using ClrKernel.Core.Scripting;
using ClrKernel.Database;
using ClrKernel.Database.Provider.SqlServer;
using ClrKernel.Language.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// <c>#!oraclesql</c> against a real Oracle, through the path a notebook takes:
/// a <c>connections.json</c> node this session has no first-party client for,
/// opened by <see cref="DataSourceCatalog"/> through the provider package, read
/// back as a grid.
/// <para>
/// Everything else about the dialects is covered against fakes. This is the one
/// that could not be: whether a statement written in one dialect actually reaches
/// a database of the matching kind, and whether a mismatched pairing is refused
/// before it gets there.
/// </para>
/// <para>
/// Bring the database up first — it is in the dev compose file:
/// <code>
/// docker compose -f dev/docker-compose.dbs.yml up -d oracle
/// CLRKERNEL_TEST_ORACLE="User Id=clrkernel;Password=DevOnly1;Data Source=localhost:41521/FREEPDB1" \
///   dotnet test test/ClrKernel.Database.UnitTest -f net8.0 --filter ClassName~OracleDialectLiveTest
/// </code>
/// </para>
/// </summary>
[TestClass]
public class OracleDialectLiveTest {
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CLRKERNEL_TEST_ORACLE");

    private string _dir;
    private string _previousDirectory;
    private SqlSession _session;
    private OracleSqlCellLanguage _oracle;
    private SqlCellLanguage _tsql;

    /// <summary>Fixed rather than unique: Oracle object names are short, and a
    /// leftover from a failed run is dropped on the way in.</summary>
    private const string _table = "CLRKERNEL_DIALECT_TEST";

    [TestInitialize]
    public void Setup() {
        LiveTestGate.Require(ConnectionString, "CLRKERNEL_TEST_ORACLE", "the Oracle dialect tests");

        // The config file is found relative to the working directory, which is how
        // a notebook finds one beside itself.
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _previousDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "connections.json"),
            "{ \"erp\": { \"$type\": \"Oracle\", \"connectionString\": "
            + System.Text.Json.JsonSerializer.Serialize(ConnectionString) + " } }");

        // One session, the dialects sharing it — the composition root's shape.
        _tsql = new SqlCellLanguage();
        _session = _tsql.Session;
        _oracle = new OracleSqlCellLanguage(_session);

        Run($"BEGIN EXECUTE IMMEDIATE 'DROP TABLE {_table}'; EXCEPTION WHEN OTHERS THEN NULL; END;");
    }

    [TestCleanup]
    public void Teardown() {
        if (_session != null) {
            try {
                Run($"BEGIN EXECUTE IMMEDIATE 'DROP TABLE {_table}'; EXCEPTION WHEN OTHERS THEN NULL; END;");
            } catch (Exception) {
                // A cleanup that cannot reach the database is not a failing test.
            }
        }
        if (_previousDirectory != null) {
            Directory.SetCurrentDirectory(_previousDirectory);
        }
        // Setup may not have got this far: the gate skips (or fails) before the
        // directory exists, and a teardown throwing on top of that buries the
        // message that says which environment variable is missing.
        if (_dir == null) {
            return;
        }
        try {
            Directory.Delete(_dir, recursive: true);
        } catch (IOException) {
        }
    }

    /// <summary>Runs a statement the way an <c>#!oraclesql</c> cell does.</summary>
    private DisplayData Run(string sql) =>
        _session.Execute("-- connections erp\n" + sql, _oracle);

    private static string TextOf(DisplayData display) =>
        display?.Data?.Values.OfType<string>().FirstOrDefault() ?? display?.ToString() ?? string.Empty;

    [TestMethod]
    public void An_oracle_dialect_cell_runs_on_an_oracle_connection() {
        // The whole point of the feature, in one call: a dialect this kernel has
        // no first-party client for, on a connection type it does not model,
        // reaching a real database.
        var display = Run("SELECT 1 AS one FROM DUAL");

        var text = TextOf(display);
        StringAssert.Contains(text, "erp", "the summary names the connection it ran on");
        StringAssert.Contains(text, "result set");
    }

    [TestMethod]
    public void Rows_come_back_and_statements_take_effect() {
        Run($"CREATE TABLE {_table} (ID NUMBER(10), NAME VARCHAR2(50))");
        Run($"INSERT INTO {_table} (ID, NAME) VALUES (1, 'Ada')");
        Run($"INSERT INTO {_table} (ID, NAME) VALUES (2, 'Grace')");
        Run("COMMIT");

        var source = DataSourceCatalog.Open("Oracle", "erp");
        Assert.AreEqual(2, source.Scalar<int>($"SELECT COUNT(*) FROM {_table}"));
        var rows = source.Query($"SELECT NAME FROM {_table} ORDER BY ID").Results();
        Assert.AreEqual(2, rows.Count);

        // And through the cell path, which is what renders the grid.
        StringAssert.Contains(TextOf(Run($"SELECT * FROM {_table} ORDER BY ID")), "result set");
    }

    [TestMethod]
    public void Oracle_only_syntax_runs_that_a_t_sql_parser_would_have_rejected() {
        // NVL, DUAL and ROWNUM in one statement. This is the case the dialect
        // split exists for: T-SQL's parser has opinions about all three, and the
        // old #!sql cell would have refused this before it ever left the process.
        var display = Run(
            "SELECT NVL(NULL, 'fallback') AS value FROM DUAL WHERE ROWNUM <= 1");

        StringAssert.Contains(TextOf(display), "result set");
    }

    [TestMethod]
    public void A_t_sql_cell_is_refused_on_the_oracle_connection() {
        // The compatibility check, against a connection that really exists — so
        // the refusal is about the pairing and cannot be a missing-connection
        // error wearing a different hat.
        var refusal = Assert.ThrowsExactly<SqlCellException>(
            () => _session.Execute("-- connections erp\nSELECT 1", _tsql));

        StringAssert.Contains(refusal.Message, "T-SQL");
        StringAssert.Contains(refusal.Message, "erp");
        StringAssert.Contains(refusal.Message, "Oracle");
        StringAssert.Contains(refusal.Message, "SqlServer, Odbc, Jdbc");
    }

    [TestMethod]
    public void The_driver_s_own_error_is_what_reaches_the_cell() {
        var failure = Assert.ThrowsExactly<SqlCellException>(
            () => Run("SELECT * FROM A_TABLE_THAT_IS_NOT_THERE"));

        // ORA-00942, in Oracle's words. No invented message number: every provider
        // numbers its errors differently and a shape borrowed from SQL Server
        // would be a lie about what the driver said.
        StringAssert.Contains(failure.Message, "Oracle error on 'erp'");
        StringAssert.Contains(failure.Message, "ORA-");
    }

    [TestMethod]
    public void The_editor_flags_the_mismatch_before_anything_is_run() {
        // Same disagreement, from the diagnostics path — with a config file that
        // really has the node, which is what makes the provider type knowable.
        var flagged = _tsql.Services.Diagnose("-- connections erp\nSELECT 1");

        Assert.AreEqual(1, flagged.Count);
        Assert.AreEqual(2, flagged[0].Severity, "a warning; the cell and the connection are each fine");
        StringAssert.Contains(flagged[0].Message, "Oracle");

        // And says nothing when the pairing is right.
        Assert.AreEqual(0, _oracle.Services.Diagnose("-- connections erp\nSELECT 1 FROM DUAL").Count);
    }
}

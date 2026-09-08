using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using ClrKernel.Core.Secrets;
using ClrKernel.Database;
using ClrKernel.Database.Provider.SqlServer;
using ClrKernel.Language.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

// ---- Offline unit tests (no database) -------------------------------------

[TestClass]
public class FluentSqlResultsTest {
    private static DataTable Sample() {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(int));
        t.Columns.Add("Name", typeof(string));
        t.Columns.Add("Amount", typeof(decimal));
        t.Rows.Add(1, "Ann", 10.5m);
        t.Rows.Add(2, "Ben", 20.0m);
        return t;
    }

    [TestMethod]
    public void Results_is_a_display_concept_that_renders_as_the_grid() {
        var results = new DataResults(Sample());
        // A concept, not a bundle: the registry converts it (registrations made
        // by DataResults itself), and the engine bundles it at the boundary.
        Assert.IsInstanceOfType(results, typeof(IDisplayValue));
        StringAssert.Contains(results.ToHtml().Html, "Amount");
        Assert.AreEqual("2 rows", results.ToText().Text);
    }

    [TestMethod]
    public void Results_enumerates_as_dynamic_rows() {
        var results = new DataResults(Sample());
        Assert.AreEqual(2, results.Count);

        var names = new List<string>();
        foreach (var row in results) {
            names.Add((string)row.Name);      // member access by column
        }
        CollectionAssert.AreEqual(new[] { "Ann", "Ben" }, names);

        dynamic first = results[0];
        Assert.AreEqual(1, (int)first.Id);
        Assert.AreEqual(1, (int)first["Id"]); // index access by column
    }

    [TestMethod]
    public void Results_dynamic_row_returns_null_for_dbnull() {
        var t = new DataTable();
        t.Columns.Add("Note", typeof(string));
        t.Rows.Add(DBNull.Value);
        dynamic row = new DataResults(t)[0];
        Assert.IsNull(row.Note);
    }

    [TestMethod]
    public void As_maps_rows_to_records() {
        var people = new DataResults(Sample()).As<Person>();
        Assert.AreEqual(2, people.Count);
        Assert.AreEqual("Ann", people[0].Name);
        Assert.AreEqual(20.0m, people[1].Amount);
    }

    public record Person(int Id, string Name, decimal Amount);
}

[TestClass]
public class FluentSqlMappingTest {
    private static DataTable Table() {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(int));
        t.Columns.Add("Name", typeof(string));
        t.Rows.Add(7, "Zoe");
        return t;
    }

    public record Rec(int Id, string Name);
    public class Poco { public int Id { get; set; } public string Name { get; set; } }

    [TestMethod]
    public void Maps_to_record_via_constructor() {
        var r = ObjectMapper.Map<Rec>(Table()).Single();
        Assert.AreEqual(7, r.Id);
        Assert.AreEqual("Zoe", r.Name);
    }

    [TestMethod]
    public void Maps_to_class_via_properties() {
        var p = ObjectMapper.Map<Poco>(Table()).Single();
        Assert.AreEqual(7, p.Id);
        Assert.AreEqual("Zoe", p.Name);
    }

    public record Widened(long Id, string Name);
    public record Keyed(Guid Key);

    private static DataTable Of(params (string Name, Type Type, object Value)[] cells) {
        var t = new DataTable();
        foreach (var c in cells) {
            t.Columns.Add(c.Name, c.Type);
        }
        t.Rows.Add(cells.Select(c => c.Value).ToArray());
        return t;
    }

    /// <summary>
    /// The column's type is the driver's business and the record's is the
    /// notebook's, and they differ constantly: ODBC returns a number as text,
    /// SQLite widens to Int64, `uniqueidentifier` arrives as a string.
    ///
    /// <para>
    /// Dapper on its own refuses every one of these — it wants a constructor whose
    /// signature matches the column types — so each of these cases fails with "a
    /// parameterless default constructor or one matching signature is required"
    /// if the by-name constructor map or the Guid handler is removed.
    /// </para>
    /// </summary>
    [TestMethod]
    public void A_records_parameter_types_need_not_match_the_columns() {
        var widened = ObjectMapper.Map<Widened>(
            Of(("Id", typeof(int), 7), ("Name", typeof(string), "Zoe"))).Single();
        Assert.AreEqual(7L, widened.Id, "an int column into a long parameter");

        var fromText = ObjectMapper.Map<Rec>(
            Of(("Id", typeof(string), "7"), ("Name", typeof(string), "Zoe"))).Single();
        Assert.AreEqual(7, fromText.Id, "a number a driver returned as text");

        var guid = Guid.NewGuid();
        Assert.AreEqual(guid,
            ObjectMapper.Map<Keyed>(Of(("Key", typeof(string), guid.ToString()))).Single().Key,
            "a uniqueidentifier a driver returned as text");
    }

    /// <summary>Nulls land as the type's default rather than throwing.</summary>
    [TestMethod]
    public void Null_columns_map_to_null_or_default() {
        var row = ObjectMapper.Map<Rec>(
            Of(("Id", typeof(int), DBNull.Value), ("Name", typeof(string), DBNull.Value))).Single();

        Assert.AreEqual(0, row.Id, "DBNull into a non-nullable int is default(int)");
        Assert.IsNull(row.Name);
    }

    /// <summary>Column order and case are the database's, not the type's.</summary>
    [TestMethod]
    public void Columns_match_by_name_whatever_their_case() {
        var row = ObjectMapper.Map<Rec>(
            Of(("NAME", typeof(string), "Zoe"), ("ID", typeof(int), 7))).Single();

        Assert.AreEqual(7, row.Id);
        Assert.AreEqual("Zoe", row.Name);
    }

    public record Stamped(int Id, DateTimeOffset At);

    /// <summary>
    /// `SELECT a.Id, b.Id FROM a JOIN b` — a real query, and one the reflection
    /// mapper this replaced threw on: it built a dictionary keyed by column name
    /// and a join has two called Id. First wins now, as it does everywhere else.
    /// </summary>
    [TestMethod]
    public void A_join_that_repeats_a_column_name_maps_rather_than_throwing() {
        using var reader = new RepeatedNameReader();

        var row = ObjectMapper.Map<Rec>(reader).Single();

        Assert.AreEqual(7, row.Id, "the first Id, not an exception");
        Assert.AreEqual("Zoe", row.Name);
    }

    /// <summary>
    /// `SELECT *` returns more columns than the record has, and in the table's
    /// order rather than the record's. Both are ordinary; Dapper's constructor
    /// matching is positional and exact, so the columns are lined up before it
    /// sees them.
    /// </summary>
    [TestMethod]
    public void Extra_columns_and_their_order_do_not_matter() {
        var row = ObjectMapper.Map<Rec>(Of(
            ("Extra", typeof(string), "ignored"),
            ("Name", typeof(string), "Zoe"),
            ("Id", typeof(int), 7),
            ("Another", typeof(int), 99))).Single();

        Assert.AreEqual(7, row.Id);
        Assert.AreEqual("Zoe", row.Name);
    }

    /// <summary>A timestamp a driver returned as text — which ODBC does.</summary>
    [TestMethod]
    public void A_datetimeoffset_held_as_text_is_parsed() {
        var row = ObjectMapper.Map<Stamped>(Of(
            ("Id", typeof(int), 7),
            ("At", typeof(string), "2026-03-14T10:00:00+00:00"))).Single();

        Assert.AreEqual(DateTimeOffset.Parse("2026-03-14T10:00:00+00:00"), row.At);
    }

    /// <summary>Two columns called Id, which a join produces and a DataTable cannot hold.</summary>
    private sealed class RepeatedNameReader : IDataReader {
        private int _row;
        public int FieldCount => 3;
        public string GetName(int i) => i == 1 ? "Name" : "Id";
        public object GetValue(int i) => i switch { 0 => 7, 1 => "Zoe", _ => 9 };
        public Type GetFieldType(int i) => i == 1 ? typeof(string) : typeof(int);
        public bool Read() => _row++ == 0;
        public bool IsDBNull(int i) => false;
        public void Dispose() { }
        public int Depth => 0;
        public bool IsClosed => false;
        public int RecordsAffected => 0;
        public void Close() { }
        public DataTable GetSchemaTable() => null;
        public bool NextResult() => false;
        public object this[int i] => GetValue(i);
        public object this[string name] => GetValue(GetOrdinal(name));
        public bool GetBoolean(int i) => default;
        public byte GetByte(int i) => default;
        public long GetBytes(int i, long o, byte[] b, int bo, int l) => 0;
        public char GetChar(int i) => default;
        public long GetChars(int i, long o, char[] b, int bo, int l) => 0;
        public IDataReader GetData(int i) => null;
        public string GetDataTypeName(int i) => GetFieldType(i).Name;
        public DateTime GetDateTime(int i) => default;
        public decimal GetDecimal(int i) => default;
        public double GetDouble(int i) => default;
        public float GetFloat(int i) => default;
        public Guid GetGuid(int i) => default;
        public short GetInt16(int i) => default;
        public int GetInt32(int i) => (int)GetValue(i);
        public long GetInt64(int i) => default;
        public int GetOrdinal(string name) =>
            string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        public string GetString(int i) => (string)GetValue(i);
        public int GetValues(object[] values) => 0;
    }

    public record class Checkpoint {
        public DateOnly CheckpointValue;
    }
    public record class Shift {
        public TimeOnly StartsAt { get; set; }
    }

    /// <summary>
    /// A public field, which is how somebody writing a throwaway type in a cell
    /// declares it. The reflection mapper this replaced read properties only, so a
    /// record of fields came back empty with nothing said.
    /// </summary>
    [TestMethod]
    public void Public_fields_are_filled_not_only_properties() {
        var row = ObjectMapper.Map<Checkpoint>(
            Of(("CheckpointValue", typeof(DateTime), new DateTime(2010, 12, 29)))).Single();

        Assert.AreEqual(new DateOnly(2010, 12, 29), row.CheckpointValue);
    }

    /// <summary>
    /// `date` and `time` columns arrive as DateTime and TimeSpan, and DateOnly is
    /// what you write when the time is not part of the answer. Dapper converts
    /// neither on its own — both threw "Error parsing column 0".
    /// </summary>
    [TestMethod]
    public void DateOnly_and_TimeOnly_are_converted_from_what_a_driver_returns() {
        Assert.AreEqual(new DateOnly(2010, 12, 29),
            ObjectMapper.Map<Checkpoint>(
                Of(("CheckpointValue", typeof(DateTime), new DateTime(2010, 12, 29)))).Single().CheckpointValue);

        Assert.AreEqual(new TimeOnly(9, 30),
            ObjectMapper.Map<Shift>(
                Of(("StartsAt", typeof(TimeSpan), new TimeSpan(9, 30, 0)))).Single().StartsAt);
    }

    /// <summary>
    /// A type no column can fill is a mistake, and returning a row of defaults
    /// hides it: `SELECT OrderDate` into a type whose member is `CheckpointValue`
    /// came back as 1/1/0001, which looks like an answer. The refusal names both
    /// sides and says how to fix it.
    /// </summary>
    [TestMethod]
    public void A_type_no_column_can_fill_is_refused_rather_than_returned_empty() {
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => ObjectMapper.Map<Checkpoint>(
            Of(("OrderDate", typeof(DateTime), new DateTime(2010, 12, 29)))));

        StringAssert.Contains(e.Message, "OrderDate", "the columns it did get");
        StringAssert.Contains(e.Message, "CheckpointValue", "the members it could not fill");
        StringAssert.Contains(e.Message, "AS CheckpointValue", "and what to do about it");
    }

    /// <summary>
    /// Partial matches stay legal — a type is often wider than one query, and only
    /// *nothing* matching is always a mistake.
    /// </summary>
    [TestMethod]
    public void A_type_wider_than_the_query_still_maps_what_it_can() {
        var row = ObjectMapper.Map<Poco>(Of(("Id", typeof(int), 7))).Single();

        Assert.AreEqual(7, row.Id);
        Assert.IsNull(row.Name);
    }

    [TestMethod]
    public void Maps_scalar_from_first_column() {
        var ids = ObjectMapper.Map<int>(Table());
        CollectionAssert.AreEqual(new[] { 7 }, ids.ToArray());
    }

    [TestMethod]
    public void ValueConverter_handles_null_nullable_enum_and_guid() {
        Assert.AreEqual(0, ValueConverter.To<int>(DBNull.Value));
        Assert.IsNull(ValueConverter.To<int?>(DBNull.Value));
        Assert.AreEqual(StringComparison.Ordinal, ValueConverter.To<StringComparison>("Ordinal"));
        var g = Guid.NewGuid();
        Assert.AreEqual(g, ValueConverter.To<Guid>(g.ToString()));
        Assert.AreEqual(5L, ValueConverter.To<long>(5));
    }
}

[TestClass]
public class FluentSqlParameterTest {
    [TestMethod]
    public void Binds_anonymous_object_as_named_parameters() {
        using var cmd = new SqlCommand();
        ParameterBinder.Bind(cmd, new { id = 5, name = "x" });
        Assert.AreEqual(5, cmd.Parameters["@id"].Value);
        Assert.AreEqual("x", cmd.Parameters["@name"].Value);
    }

    [TestMethod]
    public void Binds_dictionary_and_maps_null_to_dbnull() {
        using var cmd = new SqlCommand();
        ParameterBinder.Bind(cmd, new Dictionary<string, object> { ["@a"] = null, ["b"] = 2 });
        Assert.AreEqual(DBNull.Value, cmd.Parameters["@a"].Value);
        Assert.AreEqual(2, cmd.Parameters["@b"].Value);
    }
}

[TestClass]
public class FluentSqlTableDefinitionTest {
    [TestMethod]
    public void Generates_sqlserver_create_table_from_schema() {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(int));
        t.Columns.Add("Name", typeof(string));
        t.Columns.Add("When", typeof(DateTime));
        using var reader = t.CreateDataReader();
        var ddl = SqlServerTableDefinition.Generate(reader.GetSchemaTable(), "dbo.Target");
        StringAssert.StartsWith(ddl, "CREATE TABLE [dbo].[Target] (");
        StringAssert.Contains(ddl, "[Id] int");
        StringAssert.Contains(ddl, "nvarchar");     // SQL Server uses nvarchar (unlike Fabric)
        StringAssert.Contains(ddl, "datetime2");
    }

    [TestMethod]
    public void Money_columns_reporting_the_unspecified_scale_sentinel_become_decimal_19_4() {
        // A live SqlDataReader over a money column (e.g. AdventureWorksDW's
        // DimProduct.ListPrice) reports NumericPrecision 19, NumericScale 255 —
        // the TDS "unspecified" sentinel. Reproduce that schema row by hand.
        var schema = new DataTable();
        schema.Columns.Add("ColumnName", typeof(string));
        schema.Columns.Add("DataType", typeof(Type));
        schema.Columns.Add("ColumnSize", typeof(int));
        schema.Columns.Add("NumericPrecision", typeof(int));
        schema.Columns.Add("NumericScale", typeof(int));
        schema.Columns.Add("AllowDBNull", typeof(bool));
        schema.Rows.Add("ListPrice", typeof(decimal), 8, 19, 255, true);
        schema.Rows.Add("Weird", typeof(decimal), 8, 255, 255, true);
        schema.Rows.Add("Exact", typeof(decimal), 8, 10, 2, false);

        var ddl = SqlServerTableDefinition.Generate(schema, "dbo.Target");

        StringAssert.Contains(ddl, "[ListPrice] decimal(19,4) NULL");
        StringAssert.Contains(ddl, "[Weird] decimal(18,4) NULL");
        StringAssert.Contains(ddl, "[Exact] decimal(10,2) NOT NULL");
    }
}

[TestClass]
public class FluentSqlFactoryTest {
    [TestMethod]
    public void Connection_defaults_to_integrated_and_names_by_target() {
        var db = new SqlSession().Connection("srv", "db");
        Assert.AreEqual("srv/db", db.Name);
        Assert.AreEqual(SqlAuthMode.Integrated, db.Spec.Auth);
    }

    [TestMethod]
    public void Connection_with_user_uses_sql_login_and_secret_ref() {
        var db = new SqlSession().Connection("srv", "db", "svc", "sql:mysecret");
        Assert.AreEqual(SqlAuthMode.SqlPassword, db.Spec.Auth);
        Assert.AreEqual("svc", db.Spec.User);
        Assert.AreEqual("sql:mysecret", db.Spec.SecretRef);
    }

    [TestMethod]
    public void AzureConnection_uses_entra_default() {
        Assert.AreEqual(SqlAuthMode.AzureAdDefault, new SqlSession().AzureConnection("srv", "db").Spec.Auth);
    }
}

/// <summary>
/// The D7 rebase (P4b): <c>SqlDatabase</c>/<c>SqlQuery</c>/<c>SqlTable</c> now derive from
/// <c>DataSource</c>/<c>DataSourceQuery</c>/<c>DataSourceTable</c>. Everything here runs
/// without a server — building a handle and walking the fluent chain never opens a
/// connection — so it covers the wiring that would otherwise only be provable on Windows:
/// that the covariant overrides are reached, and that secret resolution still happens at
/// open time rather than at construction.
/// </summary>
[TestClass]
public class FluentSqlInheritanceTest {
    private static SqlDatabase Handle() => new SqlSession().Connection("srv", "db");

    [TestMethod]
    public void Fluent_chain_stays_sql_typed_through_the_base_class() {
        var db = Handle();
        Assert.IsInstanceOfType(db, typeof(DataSource), "SqlDatabase should be a DataSource");

        // Each hop must reach the SQL override, not the base implementation — that is what
        // keeps SqlDataReader/SqlBulkCopy reachable from a plain Query(...)/Table(...) call.
        SqlQuery query = db.Query("select 1");
        SqlTable table = db.Table("dbo.Orders");
        SqlQuery fromTable = table.Query();

        Assert.AreEqual("select 1", query.Sql);
        Assert.AreEqual("select * from dbo.Orders", fromTable.Sql);
        Assert.AreSame(db, table.Database);
        Assert.AreSame(db, table.DataSource, "the base's DataSource and the SQL Database are one object");
    }

    [TestMethod]
    public void Virtual_dispatch_survives_an_upcast_to_the_base() {
        // A `new`-hiding implementation would silently hand back the base types here.
        DataSource upcast = Handle();
        Assert.IsInstanceOfType(upcast.Query("select 1"), typeof(SqlQuery));
        Assert.IsInstanceOfType(upcast.Table("dbo.Orders"), typeof(SqlTable));
        Assert.IsInstanceOfType(upcast.Table("dbo.Orders").Query(), typeof(SqlQuery));
    }

    [TestMethod]
    public void Name_tracks_the_spec_rather_than_being_captured_at_construction() {
        var db = Handle();
        Assert.AreEqual("srv/db", db.Name);
        db.Spec.Name = "renamed";
        Assert.AreEqual("renamed", db.Name,
            "Name is an override reading the spec — the base captures its name in the constructor");
    }

    [TestMethod]
    public void Secret_is_resolved_when_opening_not_when_the_handle_is_built() {
        // Constructing must not touch the credential store...
        var db = new SqlSession().Connection("srv", "db", "svc", "sql:does-not-exist");
        Assert.AreEqual("srv/db", db.Name);

        // ...but opening must, and must still surface as SqlCellException rather than the
        // raw SecretNotFoundException. This runs entirely inside the connection factory,
        // before any network contact.
        var e = Assert.ThrowsExactly<SqlCellException>(() => db.Open());
        Assert.IsInstanceOfType(e.InnerException, typeof(SecretNotFoundException));
    }
}

[TestClass]
public class FluentSqlEngineTest {
    [TestMethod]
    public async System.Threading.Tasks.Task Sql_connection_is_usable_from_a_csharp_cell() {
        var engine = new ClrKernel.Core.Scripting.InteractiveScriptEngine(
            System.IO.Directory.GetCurrentDirectory(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        // Exercises the full path: SqlServer helper -> Connection(...) -> SqlDatabase. The
        // language and provider packages are separate assemblies, so this also proves
        // the cell's script contribution imports both.
        var result = await engine.ExecuteAsync("#!csharp\nSqlServer.Connection(\"Server01.yourdomain.local\", \"AdventureWorksDW2025\").Name");
        var text = result is DisplayData d && d.Data.TryGetValue("text/plain", out var t) ? t?.ToString() : result?.ToString();
        StringAssert.Contains(text, "Server01.yourdomain.local/AdventureWorksDW2025");
    }
}

[TestClass]
public class SqlConnectVariableTest {
    [TestMethod]
    public void Auto_variable_from_valid_name() {
        Assert.AreEqual("analytics",
            SqlDirectives.ParseConnect("#!sql-connect --name analytics --server s --database d").Variable);
    }

    [TestMethod]
    public void No_variable_when_name_is_not_an_identifier_or_is_a_keyword() {
        Assert.IsNull(SqlDirectives.ParseConnect("#!sql-connect --name sql-warehouse --server s").Variable);
        Assert.IsNull(SqlDirectives.ParseConnect("#!sql-connect --name default --server s").Variable);
    }

    [TestMethod]
    public void Explicit_var_as_and_no_var() {
        Assert.AreEqual("dw", SqlDirectives.ParseConnect("#!sql-connect --name sql-warehouse --var dw --server s").Variable);
        Assert.AreEqual("dw", SqlDirectives.ParseConnect("#!sql-connect --name analytics --as dw --server s").Variable);
        Assert.IsNull(SqlDirectives.ParseConnect("#!sql-connect --name analytics --no-var --server s").Variable);
    }

    [TestMethod]
    public void Invalid_var_throws() {
        Assert.ThrowsExactly<FormatException>(
            () => SqlDirectives.ParseConnect("#!sql-connect --name a --var 1bad --server s"));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Engine_binds_variable_usable_from_a_csharp_cell() {
        var engine = new ClrKernel.Core.Scripting.InteractiveScriptEngine(
            System.IO.Directory.GetCurrentDirectory(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        // Registers the connection AND binds `dw` (a SqlDatabase) — no server contact.
        await engine.ExecuteAsync("#!sql-connect --name analytics --var dw --server s --database d --default");
        var result = await engine.ExecuteAsync("#!csharp\ndw.Name");
        var text = result is DisplayData d && d.Data.TryGetValue("text/plain", out var t) ? t?.ToString() : result?.ToString();
        StringAssert.Contains(text, "analytics");
    }
}

// ---- Live integration tests (gated on CLRKERNEL_TEST_SQL) ------------------

[TestClass]
public class FluentSqlIntegrationTest {
    private static string ConnectionString => Environment.GetEnvironmentVariable("CLRKERNEL_TEST_SQL");

    private static SqlDatabase Db() => new SqlSession().ConnectionString(ConnectionString);

    [TestInitialize]
    public void RequireServer() {
        LiveTestGate.Require(ConnectionString, "CLRKERNEL_TEST_SQL", "fluent SQL integration tests");
    }

    public record Order(int Id, string Customer, decimal Total);

    // ---- The two P4b behaviour changes ----------------------------------------
    // Both were bugs in the SQL-specific implementations the D7 rebase deleted, so on
    // pre-rebase code these pass silently by doing the wrong thing. They are the reason
    // "it behaves as it did before" is the failure signal for this phase, not the success
    // one — which makes them worth asserting rather than eyeballing.

    [TestMethod]
    public void DefaultCommandTimeout_is_honoured_by_Scalar() {
        var db = Db();
        db.DefaultCommandTimeout = 1;

        // Pre-rebase, SqlDatabase.Scalar passed a null timeout and ignored the property it
        // documented: the 5s wait would finish inside the 30s default and return 1.
        var e = Assert.ThrowsExactly<SqlException>(() => db.Scalar<int>("waitfor delay \'00:00:05\'; select 1"));
        Assert.AreEqual(-2, e.Number, "expected a command timeout (SqlClient reports -2), not another server error");
    }

    [TestMethod]
    public void Transaction_query_honours_its_row_limit() {
        var db = Db();
        db.Execute("IF OBJECT_ID(\'dbo.FluentTxLimit\') IS NOT NULL DROP TABLE dbo.FluentTxLimit;");
        db.Execute("CREATE TABLE dbo.FluentTxLimit (Name NVARCHAR(20));");
        db.Execute("INSERT INTO dbo.FluentTxLimit VALUES (\'alpha\'),(\'beta\'),(\'gamma\');");

        using var tx = db.Transaction();
        var results = tx.Query("select Name from dbo.FluentTxLimit order by Name", null, limit: 2);

        // limit caps the rendered grid, not the data — every row stays enumerable.
        Assert.AreEqual(3, results.Count, "all rows remain available regardless of the preview limit");

        var html = results.ToHtml().Html;
        StringAssert.Contains(html, "alpha");
        StringAssert.Contains(html, "beta");
        Assert.IsFalse(html.Contains("gamma"),
            "the third row is past the limit; pre-rebase the SQL transaction accepted this argument and dropped it");
    }


    [TestMethod]
    public void Query_results_grid_rows_typed_and_parameters() {
        var db = Db();
        db.Execute("IF OBJECT_ID('dbo.FluentOrders') IS NOT NULL DROP TABLE dbo.FluentOrders;");
        db.Execute("CREATE TABLE dbo.FluentOrders (Id INT, Customer NVARCHAR(50), Total DECIMAL(18,2));");
        db.Execute("INSERT INTO dbo.FluentOrders VALUES (1,'Ann',10),(2,'Ben',20),(3,'Cy',30);");

        var results = db.Query("select * from dbo.FluentOrders order by Id").Results();
        Assert.AreEqual(3, results.Count);
        Assert.AreEqual("Ann", (string)results[0].Customer);

        var typed = db.Query("select * from dbo.FluentOrders order by Id").Results<Order>();
        Assert.AreEqual(30m, typed[2].Total);

        var filtered = db.Query("select * from dbo.FluentOrders where Id >= @min", new { min = 2 }).Results();
        Assert.AreEqual(2, filtered.Count);

        Assert.AreEqual(3, db.Scalar<int>("select count(*) from dbo.FluentOrders"));
    }

    [TestMethod]
    public void Table_bulkcopy_create_if_missing_and_exists() {
        var db = Db();
        db.Execute("IF OBJECT_ID('dbo.FluentSrc') IS NOT NULL DROP TABLE dbo.FluentSrc;");
        db.Execute("IF OBJECT_ID('dbo.FluentDst') IS NOT NULL DROP TABLE dbo.FluentDst;");
        db.Execute("CREATE TABLE dbo.FluentSrc (Id INT, Name NVARCHAR(50));");
        db.Execute("INSERT INTO dbo.FluentSrc VALUES (1,'a'),(2,'b');");

        Assert.IsFalse(db.Table("dbo.FluentDst").Exists());
        var result = db.Table("dbo.FluentDst")
            .BulkCopyFrom(db.Query("select * from dbo.FluentSrc"), new BulkCopyOptions(), createIfMissing: true);
        Assert.AreEqual(2, result.RowsCopied);
        Assert.IsTrue(db.Table("dbo.FluentDst").Exists());
        Assert.AreEqual(2, db.Table("dbo.FluentDst").Count());
    }

    [TestMethod]
    public void Transaction_rolls_back_on_dispose() {
        var db = Db();
        db.Execute("IF OBJECT_ID('dbo.FluentTx') IS NOT NULL DROP TABLE dbo.FluentTx;");
        db.Execute("CREATE TABLE dbo.FluentTx (Id INT);");
        using (var tx = db.Transaction()) {
            tx.Execute("INSERT INTO dbo.FluentTx VALUES (1),(2);");
            // no Commit -> Dispose rolls back
        }
        Assert.AreEqual(0, db.Scalar<int>("select count(*) from dbo.FluentTx"));

        using (var tx = db.Transaction()) {
            tx.Execute("INSERT INTO dbo.FluentTx VALUES (9);");
            tx.Commit();
        }
        Assert.AreEqual(1, db.Scalar<int>("select count(*) from dbo.FluentTx"));
    }
}

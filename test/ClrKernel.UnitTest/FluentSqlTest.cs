using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using ClrKernel.Data;
using ClrKernel.Primitives;
using ClrKernel.Sql;
using ClrKernel.Sql.Etl;
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
    public void Results_renders_interactive_grid_and_is_display_data() {
        var results = new DataResults(Sample());
        Assert.IsInstanceOfType(results, typeof(DisplayData)); // engine renders it directly
        StringAssert.Contains((string)results.Data["text/html"], "Amount");
        Assert.AreEqual("2 rows", results.Data["text/plain"]);
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

[TestClass]
public class FluentSqlEngineTest {
    [TestMethod]
    public async System.Threading.Tasks.Task Sql_connection_is_usable_from_a_csharp_cell() {
        var engine = new ClrKernel.Core.InteractiveScriptEngine(
            System.IO.Directory.GetCurrentDirectory(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        // Exercises the full path: Sql helper -> Connection(...) -> SqlDatabase, with
        // ClrKernel.Sql imported so the fluent types resolve in a cell.
        var result = await engine.ExecuteAsync("#!csharp\nSql.Connection(\"Server01.yourdomain.local\", \"AdventureWorksDW2025\").Name");
        var text = result is DisplayData d && d.Data.TryGetValue("text/plain", out var t) ? t?.ToString() : result?.ToString();
        StringAssert.Contains(text, Server01.yourdomain.local/AdventureWorksDW2025 );
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
        var engine = new ClrKernel.Core.InteractiveScriptEngine(
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
        if (string.IsNullOrWhiteSpace(ConnectionString)) {
            Assert.Inconclusive("Set CLRKERNEL_TEST_SQL to run fluent SQL integration tests.");
        }
    }

    public record Order(int Id, string Customer, decimal Total);

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

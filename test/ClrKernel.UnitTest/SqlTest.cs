using System;
using System.IO;
using System.Threading.Tasks;
using ClrKernel.Core;
using ClrKernel.Core.Secrets;
using ClrKernel.Primitives;
using ClrKernel.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class SqlSecretTest {
    [TestMethod]
    public void InMemory_provider_round_trips() {
        var p = new InMemorySecretProvider();
        p.Set("sql:analytics", "s3cret");
        Assert.IsTrue(p.TryGet("sql:analytics", out var got));
        Assert.AreEqual("s3cret", got);
        p.Delete("sql:analytics");
        Assert.IsFalse(p.TryGet("sql:analytics", out _));
    }

    [TestMethod]
    public void Environment_provider_maps_key_to_var_name() {
        Assert.AreEqual("CLRKERNEL_SECRET_SQL_ANALYTICS", EnvironmentSecretProvider.EnvName("sql:analytics"));
    }

    [TestMethod]
    public void Store_resolves_from_memory_and_throws_when_missing() {
        var store = SecretStore.ForProviders(new InMemorySecretProvider());
        store.Store("sql:dw", "pw");
        Assert.AreEqual("pw", store.Resolve("sql:dw"));

        var threw = false;
        try {
            store.Resolve("sql:missing");
        } catch (SecretNotFoundException) {
            threw = true;
        }
        Assert.IsTrue(threw, "missing secret should throw SecretNotFoundException");
    }
}

[TestClass]
public class SqlConnectionSpecTest {
    [TestMethod]
    public void SqlPassword_injects_user_and_resolved_secret_only() {
        var mem = new InMemorySecretProvider();
        var store = SecretStore.ForProviders(mem);
        var spec = new SqlConnectionSpec {
            Name = "analytics",
            Server = "pg",
            Database = "reports",
            Auth = SqlAuthMode.SqlPassword,
            User = "sa",
        };
        mem.Set(spec.EffectiveSecretRef, "p@ss");

        var cs = spec.BuildConnectionString(store);
        var parsed = new SqlConnectionStringBuilder(cs);
        Assert.AreEqual("pg", parsed.DataSource);
        Assert.AreEqual("reports", parsed.InitialCatalog);
        Assert.AreEqual("sa", parsed.UserID);
        Assert.AreEqual("p@ss", parsed.Password);
    }

    [TestMethod]
    public void Default_secret_ref_is_derived_from_name() {
        var spec = new SqlConnectionSpec { Name = "warehouse", Auth = SqlAuthMode.SqlPassword };
        Assert.AreEqual("sql:warehouse", spec.EffectiveSecretRef);
        Assert.IsTrue(spec.NeedsSecret);
    }

    [TestMethod]
    public void Missing_password_secret_surfaces_a_clear_error() {
        var store = SecretStore.ForProviders(new InMemorySecretProvider());
        var spec = new SqlConnectionSpec { Name = "x", Server = "s", Auth = SqlAuthMode.SqlPassword, User = "u" };
        var threw = false;
        try {
            spec.BuildConnectionString(store);
        } catch (SecretNotFoundException) {
            threw = true;
        }
        Assert.IsTrue(threw);
    }
}

[TestClass]
public class SqlDirectivesTest {
    [TestMethod]
    public void ParseConnect_reads_structured_flags() {
        var d = SqlDirectives.ParseConnect(
            "#!sql-connect --name analytics --server sql-warehouse --database dw --auth sql --user sa --default");
        Assert.AreEqual("analytics", d.Spec.Name);
        Assert.AreEqual("sql-warehouse", d.Spec.Server);
        Assert.AreEqual("dw", d.Spec.Database);
        Assert.AreEqual(SqlAuthMode.SqlPassword, d.Spec.Auth);
        Assert.AreEqual("sa", d.Spec.User);
        Assert.IsTrue(d.IsDefault);
    }

    [TestMethod]
    public void ParseConnect_defaults_to_integrated_without_user() {
        var d = SqlDirectives.ParseConnect("#!sql-connect --name warehouse --server dw");
        Assert.AreEqual(SqlAuthMode.Integrated, d.Spec.Auth);
    }

    [TestMethod]
    public void ParseConnect_rejects_inline_password() {
        var threw = false;
        try {
            SqlDirectives.ParseConnect("#!sql-connect --name x --server s --user u --password hunter2");
        } catch (FormatException) {
            threw = true;
        }
        Assert.IsTrue(threw, "committing a password inline must be rejected");
    }

    [TestMethod]
    public void ParseConnect_honors_quoted_connection_string() {
        var d = SqlDirectives.ParseConnect(
            "#!sql-connect --name raw --connection-string \"Server=host;Database=db;Encrypt=True\"");
        Assert.AreEqual("raw", d.Spec.Name);
        StringAssert.Contains(d.Spec.RawConnectionString, "Server=host");
    }

    [TestMethod]
    public void ParseCell_reads_connection_comment_selector() {
        var req = SqlDirectives.ParseCell("-- connections analytics\nSELECT 1");
        Assert.AreEqual("analytics", req.ConnectionName);
    }

    [TestMethod]
    public void ParseCell_without_selector_yields_null_connection() {
        var req = SqlDirectives.ParseCell("SELECT TOP 10 * FROM dbo.Orders");
        Assert.IsNull(req.ConnectionName);
    }

    [TestMethod]
    public void SelectorConnection_reads_inline_flag() {
        Assert.AreEqual("dw", SqlDirectives.SelectorConnection("#!sql --connections dw"));
        Assert.IsNull(SqlDirectives.SelectorConnection("#!sql"));
    }
}

[TestClass]
public class TSqlSyntaxTest {
    [TestMethod]
    public void Valid_select_has_no_diagnostics() {
        Assert.IsTrue(TSqlSyntax.IsValid("SELECT TOP 5 Id, Name FROM dbo.Customers WHERE Id > 10;"));
    }

    [TestMethod]
    public void Invalid_sql_reports_a_diagnostic() {
        var diags = TSqlSyntax.Check("SELECT FROM WHERE;");
        Assert.IsTrue(diags.Count > 0, "malformed SQL should produce a diagnostic");
        Assert.IsFalse(string.IsNullOrEmpty(diags[0].Message));
    }

    [TestMethod]
    public void Merge_statement_is_recognized() {
        var sql = "MERGE dbo.Target AS t USING dbo.Source AS s ON (t.Id = s.Id) " +
                  "WHEN MATCHED THEN UPDATE SET t.V = s.V " +
                  "WHEN NOT MATCHED THEN INSERT (Id, V) VALUES (s.Id, s.V);";
        Assert.IsTrue(TSqlSyntax.IsValid(sql));
    }
}

[TestClass]
public class SqlLanguageTest {
    [TestMethod]
    public void Completion_filters_by_prefix() {
        var completion = SqlLanguage.Complete("SEL", 3);
        Assert.IsTrue(completion.Items.Exists(i => i.Label == "SELECT"));
        Assert.IsFalse(completion.Items.Exists(i => i.Label == "FROM"));
    }

    [TestMethod]
    public void Hover_describes_a_known_keyword() {
        var hover = SqlLanguage.Hover("MERGE", 1);
        Assert.IsNotNull(hover);
        StringAssert.Contains(hover.Markdown, "MERGE");
    }
}

[TestClass]
public class SqlImportTest {
    [TestMethod]
    public void Markdown_sql_fence_becomes_sql_block() {
        var md = "# Title\n\n```sql\nSELECT 1\n```\n";
        var blocks = NotebookImporter.ParseMarkdown(md);
        Assert.AreEqual(1, blocks.Count);
        StringAssert.StartsWith(blocks[0], "#!sql\n");
        StringAssert.Contains(blocks[0], "SELECT 1");
    }

    [TestMethod]
    public void Markdown_sql_connect_fence_is_passed_through() {
        var md = "```sql\n#!sql-connect --name a --server s\n```\n";
        var blocks = NotebookImporter.ParseMarkdown(md);
        Assert.AreEqual(1, blocks.Count);
        StringAssert.StartsWith(blocks[0], "#!sql-connect");
    }

    [TestMethod]
    public void Dib_sql_section_becomes_sql_block() {
        var dib = "#!csharp\nvar x = 1;\n#!sql\nSELECT 2\n";
        var blocks = NotebookImporter.ParseDib(dib);
        Assert.AreEqual(2, blocks.Count);
        Assert.AreEqual("var x = 1;", blocks[0]);
        StringAssert.StartsWith(blocks[1], "#!sql\n");
    }
}

[TestClass]
public class SqlEngineRoutingTest {
    [TestMethod]
    public async Task Sql_connect_registers_a_named_connection() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        var result = await engine.ExecuteAsync("#!sql-connect --name analytics --server dw --database reports");
        var dd = result as DisplayData;
        Assert.IsNotNull(dd, "#!sql-connect should return a confirmation");
        Assert.IsTrue(engine.Sql.Connections.TryGet("analytics", out var spec));
        Assert.AreEqual("dw", spec.Server);
        Assert.AreEqual("analytics", engine.Sql.Connections.DefaultName);
    }

    [TestMethod]
    public async Task Sql_cell_with_bad_syntax_fails_before_connecting() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        await engine.ExecuteAsync("#!sql-connect --name analytics --server dw");
        var threw = false;
        try {
            await engine.ExecuteAsync("#!sql\nSELECT FROM WHERE;");
        } catch (SqlCellException e) {
            threw = true;
            StringAssert.Contains(e.Message, "syntax");
        }
        Assert.IsTrue(threw, "a syntax error should be caught before any connection attempt");
    }

    [TestMethod]
    public async Task Sql_cell_without_any_connection_reports_guidance() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        var threw = false;
        try {
            await engine.ExecuteAsync("#!sql\nSELECT 1");
        } catch (InvalidOperationException e) {
            threw = true;
            StringAssert.Contains(e.Message, "connection");
        }
        Assert.IsTrue(threw);
    }
}

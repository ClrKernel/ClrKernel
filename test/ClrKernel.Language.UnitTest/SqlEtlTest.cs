using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using ClrKernel.Language.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class SqlEtlDirectivesTest {
    [TestMethod]
    public void ParseBulk_reads_flags_and_defaults_to_same_connection() {
        var d = SqlEtlDirectives.ParseBulk(
            "#!sql-bulk --from analytics --query \"SELECT * FROM dbo.Orders\" --to warehouse --table dbo.Orders --batch-size 5000 --truncate");
        Assert.AreEqual("analytics", d.FromConnection);
        Assert.AreEqual("warehouse", d.ToConnection);
        Assert.AreEqual("dbo.Orders", d.Table);
        StringAssert.Contains(d.Query, "SELECT * FROM dbo.Orders");
        Assert.AreEqual(5000, d.Options.BatchSize);
        Assert.IsTrue(d.Options.TruncateFirst);
    }

    [TestMethod]
    public void ParseBulk_defaults_to_from_when_to_omitted() {
        var d = SqlEtlDirectives.ParseBulk("#!sql-bulk --from wh --from-table stg.X --table dbo.X");
        Assert.AreEqual("wh", d.ToConnection);
        StringAssert.Contains(d.SourceQuery, "SELECT * FROM [stg].[X]");
    }

    [TestMethod]
    public void ParseBulk_requires_source() {
        Assert.ThrowsExactly<FormatException>(() =>
            SqlEtlDirectives.ParseBulk("#!sql-bulk --from a --table dbo.X"));
    }

    [TestMethod]
    public void ParseMerge_reads_keys_and_columns() {
        var d = SqlEtlDirectives.ParseMerge(
            "#!sql-merge --connection wh --target dbo.Customers --source stg.Customers --on Id,Region --update Name,Email --delete");
        Assert.AreEqual("wh", d.Connection);
        Assert.AreEqual("dbo.Customers", d.Spec.Target);
        CollectionAssert.AreEqual(new[] { "Id", "Region" }, d.Spec.KeyColumns.ToArray());
        CollectionAssert.AreEqual(new[] { "Name", "Email" }, d.Spec.UpdateColumns.ToArray());
        Assert.IsTrue(d.Spec.DeleteNotMatchedBySource);
    }

    [TestMethod]
    public void ParseMerge_requires_on() {
        Assert.ThrowsExactly<FormatException>(() =>
            SqlEtlDirectives.ParseMerge("#!sql-merge --connection wh --target t --source s"));
    }
}

[TestClass]
public class SqlEtlRoutingTest {
    [TestMethod]
    public async Task Sql_merge_magic_routes_and_validates() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        // Missing --on is caught by the parser, proving the cell routed to #!sql-merge.
        var threw = false;
        try {
            await engine.ExecuteAsync("#!sql-merge --connection wh --target t --source s");
        } catch (FormatException) {
            threw = true;
        }
        Assert.IsTrue(threw);
    }

    [TestMethod]
    public async Task Sql_bulk_magic_routes_and_validates() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        var threw = false;
        try {
            await engine.ExecuteAsync("#!sql-bulk --from a --table dbo.X"); // missing source
        } catch (FormatException) {
            threw = true;
        }
        Assert.IsTrue(threw);
    }

    [TestMethod]
    public async Task Sql_merge_without_connection_reports_guidance() {
        var engine = new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);
        var threw = false;
        try {
            await engine.ExecuteAsync("#!sql-merge --connection nope --target t --source s --on Id");
        } catch (InvalidOperationException e) {
            threw = true;
            StringAssert.Contains(e.Message, "connection");
        }
        Assert.IsTrue(threw);
    }
}

using System;
using System.IO;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// Cell-selector dispatch order. Several magics are prefixes of others
/// (<c>#!sql</c> is a prefix of <c>#!sql-connect</c>, <c>#!sql-bulk</c>,
/// <c>#!sql-merge</c>, <c>#!sql-run</c> and <c>#!sql-deploy</c>; <c>#!dax</c> of
/// <c>#!dax-connect</c>), so the longer selector must always be matched first.
/// These assert on observable dispatch through <see cref="InteractiveScriptEngine"/>
/// rather than on any internal matcher, so they keep their meaning when the
/// dispatch mechanism changes.
/// </summary>
[TestClass]
public class CellSelectorOrderingTest {
    private static InteractiveScriptEngine NewEngine() =>
        new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);

    /// <summary>Runs a cell and returns the exception it raised, or null.</summary>
    private static async Task<Exception> Raised(InteractiveScriptEngine engine, string cell) {
        try {
            await engine.ExecuteAsync(cell);
            return null;
        } catch (Exception e) {
            return e;
        }
    }

    // --- #!sql-connect must win over #!sql -----------------------------------

    [TestMethod]
    public async Task Sql_connect_is_matched_before_sql() {
        var engine = NewEngine();
        var result = await engine.ExecuteAsync(
            "#!sql-connect --name analytics --server dw --database reports");

        // Had #!sql won, the body "-connect --name ..." would have gone to T-SQL.
        var dd = result as DisplayData;
        Assert.IsNotNull(dd, "#!sql-connect must dispatch to the connect handler");
        StringAssert.Contains((string)dd.Data["text/plain"], "Connected");
    }

    [TestMethod]
    public async Task Sql_cell_is_not_matched_as_sql_connect() {
        var engine = NewEngine();
        var e = await Raised(engine, "#!sql\nselect 1");

        // No connection is registered, so the #!sql handler must complain about
        // that. Reaching the C# compiler or the connect handler would not.
        Assert.IsInstanceOfType(e, typeof(InvalidOperationException),
            $"#!sql with no connection should report a missing connection, got: {e?.GetType().Name}");
    }

    // --- the #!sql-* verbs must win over #!sql -------------------------------

    [TestMethod]
    public async Task Sql_bulk_is_matched_before_sql() {
        var engine = NewEngine();
        var e = await Raised(engine,
            "#!sql-bulk --from nosuchsource --query \"select 1\" --to nosuchtarget --table dbo.T");

        // The bulk handler resolves its --from connection *by name*. Had #!sql
        // won, the body would be a T-SQL comment and the failure would be the
        // generic "No SQL connection is configured" — which never names a
        // connection. So the name in the message is the proof of dispatch.
        Assert.IsNotNull(e, "#!sql-bulk with unknown connections should fail");
        StringAssert.Contains(e.Message, "nosuchsource",
            $"#!sql-bulk must dispatch to the bulk handler, got: {e.GetType().Name}: {e.Message}");
    }

    [TestMethod]
    public async Task Sql_merge_is_matched_before_sql() {
        var engine = NewEngine();
        var e = await Raised(engine,
            "#!sql-merge --connection nosuchconn --target dbo.A --source dbo.B --on Id");

        Assert.IsNotNull(e, "#!sql-merge with an unknown connection should fail");
        StringAssert.Contains(e.Message, "nosuchconn",
            $"#!sql-merge must dispatch to the merge handler, got: {e.GetType().Name}: {e.Message}");
    }

    [TestMethod]
    public async Task Sql_deploy_is_matched_before_sql() {
        var engine = NewEngine();
        var missing = Path.Combine(Path.GetTempPath(), "clrkernel-no-such-deploy-dir");
        var e = await Raised(engine, $"#!sql-deploy --path \"{missing}\"");

        // The deploy handler validates its --path; #!sql would never look at one.
        Assert.IsNotNull(e, "#!sql-deploy on a missing path should fail");
        StringAssert.Contains(e.Message, "clrkernel-no-such-deploy-dir");
    }

    // --- #!dax-connect must win over #!dax -----------------------------------

    [TestMethod]
    public async Task Dax_connect_is_matched_before_dax() {
        var engine = NewEngine();
        var result = await engine.ExecuteAsync(
            "#!dax-connect --name analytics --server ssas --database DW --default");

        var dd = result as DisplayData;
        Assert.IsNotNull(dd, "#!dax-connect must dispatch to the connect handler");
        StringAssert.Contains((string)dd.Data["text/plain"], "analytics");
    }

    [TestMethod]
    public async Task Dax_cell_is_not_matched_as_dax_connect() {
        var engine = NewEngine();
        var e = await Raised(engine, "#!dax\nEVALUATE 'Sales'");

        Assert.IsInstanceOfType(e, typeof(InvalidOperationException),
            $"#!dax with no cube should report a missing cube, got: {e?.GetType().Name}");
        StringAssert.Contains(e.Message, "cube");
    }

    // --- a cell with no selector still reaches C# ----------------------------

    [TestMethod]
    public async Task Unmatched_cell_falls_through_to_csharp() {
        var engine = NewEngine();
        var result = await engine.ExecuteAsync("1 + 1");
        Assert.AreEqual("2", (result as DisplayData)?.Data["text/plain"]?.ToString());
    }
}

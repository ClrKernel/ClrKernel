using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// The language-service seam: editor features reached through
/// <c>ICellLanguage.Services</c> rather than through a per-language branch in the
/// LSP host. These go through the same path the host uses, so the adapters are
/// covered — the LSP harness only exercises C#.
/// </summary>
[TestClass]
public class CellLanguageServicesTest {
    private static InteractiveScriptEngine NewEngine() =>
        new InteractiveScriptEngine(Directory.GetCurrentDirectory(), NullLogger.Instance);

    [TestMethod]
    public void Languages_expose_services_by_id() {
        var engine = NewEngine();
        Assert.IsNotNull(engine.Languages.ById("sql")?.Services, "sql should provide editor features");
        Assert.IsNotNull(engine.Languages.ById("dax")?.Services, "dax should provide editor features");
        Assert.IsNotNull(engine.Languages.ById("powershell")?.Services);
        // Not every cell language has editor features.
        Assert.IsNull(engine.Languages.ById("mermaid")?.Services);
        Assert.IsNull(engine.Languages.ById("http")?.Services);
    }

    [TestMethod]
    public async Task Sql_completion_offers_session_connections() {
        var engine = NewEngine();
        await engine.ExecuteAsync("#!sql-connect --name analytics --server dw --database reports");

        var services = engine.Languages.ById("sql").Services;
        const string code = "-- connections ";
        var result = await services.CompleteAsync(code, code.Length, new LanguageServiceContext());

        Assert.IsTrue(result.Items.Any(i => i.Label == "analytics"),
            "a connection registered in this session should be offered by completion");
    }

    [TestMethod]
    public async Task Sql_completion_sees_steps_declared_in_sibling_cells() {
        var engine = NewEngine();
        var services = engine.Languages.ById("sql").Services;

        // The editor supplies the other open SQL cells; a -- step declared in one
        // must be offerable to -- needs in another.
        var context = new LanguageServiceContext(new[] { "-- step extract\nselect 1" });
        const string code = "-- needs ";
        var result = await services.CompleteAsync(code, code.Length, context);

        Assert.IsTrue(result.Items.Any(i => i.Label == "extract"),
            "a -- step from an open sibling cell should be offered to -- needs");
    }

    [TestMethod]
    public void Sql_diagnostics_flow_through_the_contract() {
        var engine = NewEngine();
        var services = engine.Languages.ById("sql").Services;

        Assert.AreEqual(0, services.Diagnose("select 1").Count, "valid T-SQL should be clean");

        var bad = services.Diagnose("selct * frm");
        Assert.IsTrue(bad.Count > 0, "a syntax error should be reported");
        Assert.IsFalse(string.IsNullOrWhiteSpace(bad[0].Message));
    }

    [TestMethod]
    public async Task Dax_completion_offers_registered_cubes() {
        var engine = NewEngine();
        await engine.ExecuteAsync("#!dax-connect --name analytics --server ssas --database DW --default");

        var services = engine.Languages.ById("dax").Services;
        const string code = "-- connections ";
        var result = await services.CompleteAsync(code, code.Length, new LanguageServiceContext());

        Assert.IsTrue(result.Items.Any(i => i.Label == "analytics"),
            "a cube registered in this session should be offered by completion");
    }
}

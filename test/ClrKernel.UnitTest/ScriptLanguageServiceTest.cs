using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.LanguageServices;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class ScriptLanguageServiceTest {
    private static async Task<InteractiveScriptEngine> EngineWithAsync(params string[] cells) {
        InteractiveScriptEngine.RefsFilePath = null;
        var engine = new InteractiveScriptEngine(System.IO.Path.GetTempPath(), NullLogger.Instance);
        foreach (var cell in cells) {
            await engine.ExecuteAsync(cell);
        }
        return engine;
    }

    private static readonly ScriptLanguageService _svc = new();

    [TestMethod]
    public async Task Completes_members_of_a_bcl_type() {
        var engine = await EngineWithAsync();
        const string code = "Console.";
        var result = await _svc.GetCompletionsAsync(engine.SnapshotState(), code, code.Length);
        var labels = result.Items.Select(i => i.Label).ToList();
        CollectionAssert.Contains(labels, "WriteLine");
        CollectionAssert.Contains(labels, "ReadLine");
    }

    [TestMethod]
    public async Task Completes_a_variable_from_a_prior_cell() {
        var engine = await EngineWithAsync("var greeting = \"hello\";");
        const string code = "greet";
        var result = await _svc.GetCompletionsAsync(engine.SnapshotState(), code, code.Length);
        var labels = result.Items.Select(i => i.Label).ToList();
        CollectionAssert.Contains(labels, "greeting");
    }

    [TestMethod]
    public async Task Completes_members_of_a_prior_cell_variable() {
        var engine = await EngineWithAsync("var greeting = \"hello\";");
        const string code = "greeting.";
        var result = await _svc.GetCompletionsAsync(engine.SnapshotState(), code, code.Length);
        var labels = result.Items.Select(i => i.Label).ToList();
        CollectionAssert.Contains(labels, "ToUpper");
        CollectionAssert.Contains(labels, "Length");
    }

    [TestMethod]
    public async Task Completes_GetVariable_helper_via_using_static() {
        var engine = await EngineWithAsync();
        const string code = "GetVar";
        var result = await _svc.GetCompletionsAsync(engine.SnapshotState(), code, code.Length);
        var labels = result.Items.Select(i => i.Label).ToList();
        CollectionAssert.Contains(labels, "GetVariable");
    }

    [TestMethod]
    public async Task Hover_reports_the_type_of_a_variable() {
        var engine = await EngineWithAsync("var count = 42;");
        const string code = "count";
        var hover = await _svc.GetHoverAsync(engine.SnapshotState(), code, 2);
        Assert.IsNotNull(hover);
        StringAssert.Contains(hover.Markdown, "count");
        StringAssert.Contains(hover.Markdown, "int");
    }

    [TestMethod]
    public async Task Signature_help_lists_overloads_and_active_parameter() {
        var engine = await EngineWithAsync();
        const string code = "Console.WriteLine(";
        var help = await _svc.GetSignatureHelpAsync(engine.SnapshotState(), code, code.Length);
        Assert.IsNotNull(help);
        Assert.IsTrue(help.Signatures.Count > 0, "expected WriteLine overloads");
        Assert.IsTrue(help.Signatures.Any(s => s.Label.Contains("WriteLine")));
        Assert.AreEqual(0, help.ActiveParameter);
    }

    [TestMethod]
    public async Task Completion_replace_span_covers_the_partial_word() {
        var engine = await EngineWithAsync();
        const string code = "Conso";
        var result = await _svc.GetCompletionsAsync(engine.SnapshotState(), code, code.Length);
        Assert.AreEqual(0, result.ReplaceStart);
        Assert.AreEqual(5, result.ReplaceLength);
        CollectionAssert.Contains(result.Items.Select(i => i.Label).ToList(), "Console");
    }
}

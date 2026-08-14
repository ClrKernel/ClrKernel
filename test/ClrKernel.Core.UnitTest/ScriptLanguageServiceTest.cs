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

[TestClass]
public class DefinitionTest {
    private static readonly ScriptLanguageService _svc = new();

    private static async Task<InteractiveScriptEngine> EngineWithAsync(params string[] cells) {
        InteractiveScriptEngine.RefsFilePath = null;
        var engine = new InteractiveScriptEngine(System.IO.Path.GetTempPath(), NullLogger.Instance);
        foreach (var cell in cells) {
            await engine.ExecuteAsync(cell);
        }
        return engine;
    }

    [TestMethod]
    public async Task A_symbol_defined_in_an_earlier_cell_reports_its_defining_line() {
        var engine = await EngineWithAsync("int Add(int a, int b) => a + b;");
        const string code = "Add(1, 2)";
        var defs = await _svc.GetDefinitionsAsync(engine.SnapshotState(), code, 1);
        Assert.AreEqual(1, defs.Count);
        Assert.IsFalse(defs[0].InCurrentCell);
        StringAssert.Contains(defs[0].SourceLine, "int Add(int a, int b)");
        Assert.AreEqual("Add", defs[0].SourceLine.Substring(defs[0].ColumnInLine, defs[0].Length));
    }

    [TestMethod]
    public async Task A_symbol_defined_in_the_current_cell_reports_cell_offsets() {
        var engine = await EngineWithAsync();
        const string code = "var total = 5;\nvar doubled = total * 2;";
        var defs = await _svc.GetDefinitionsAsync(engine.SnapshotState(), code, code.IndexOf("total * 2") + 1);
        Assert.AreEqual(1, defs.Count);
        Assert.IsTrue(defs[0].InCurrentCell);
        Assert.AreEqual("total", code.Substring(defs[0].Start, defs[0].Length));
    }

    [TestMethod]
    public async Task A_metadata_symbol_yields_no_source_definition() {
        var engine = await EngineWithAsync();
        const string code = "Console.WriteLine(1)";
        var defs = await _svc.GetDefinitionsAsync(engine.SnapshotState(), code, code.IndexOf("WriteLine") + 1);
        Assert.AreEqual(0, defs.Count, "BCL symbols have no source to jump to");
    }

    [TestMethod]
    public async Task A_variable_from_an_earlier_cell_resolves_to_its_declaration_line() {
        var engine = await EngineWithAsync("var answer = 42;");
        const string code = "answer + 1";
        var defs = await _svc.GetDefinitionsAsync(engine.SnapshotState(), code, 2);
        Assert.AreEqual(1, defs.Count);
        Assert.IsFalse(defs[0].InCurrentCell);
        StringAssert.Contains(defs[0].SourceLine, "var answer = 42;");
    }
}

using System.Threading.Tasks;
using ClrKernel.Core.LanguageServices;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// A cell that declares extension methods can't run as a script submission
/// (CS1109 — script classes are nested), so the engine compiles it as a real
/// class library and references it. These tests cover execution, redefinition,
/// and the language-service surface (completion + Go to Definition).
/// </summary>
[TestClass]
public class CellLibraryTest {
    private static async Task<InteractiveScriptEngine> EngineAsync() {
        InteractiveScriptEngine.RefsFilePath = null;
        var engine = new InteractiveScriptEngine(System.IO.Path.GetTempPath(), NullLogger.Instance);
        await engine.ExecuteAsync(
            "public static class CellExt { " +
            "/// <summary>Doubles a number.</summary>\n" +
            "public static int Twice(this int x) => x * 2; }");
        return engine;
    }

    [TestMethod]
    public async Task An_extension_method_defined_in_a_cell_executes() {
        var engine = await EngineAsync();
        var result = (DisplayData)await engine.ExecuteAsync("21.Twice()");
        Assert.AreEqual("42", result.Data["text/plain"]?.ToString());
    }

    [TestMethod]
    public async Task Redefining_the_extension_class_replaces_it() {
        var engine = await EngineAsync();
        await engine.ExecuteAsync(
            "public static class CellExt { public static int Twice(this int x) => x * 3; }");
        var result = (DisplayData)await engine.ExecuteAsync("21.Twice()");
        Assert.AreEqual("63", result.Data["text/plain"]?.ToString());
    }

    [TestMethod]
    public async Task A_cell_mixing_statements_and_extension_classes_gets_a_clear_error() {
        InteractiveScriptEngine.RefsFilePath = null;
        var engine = new InteractiveScriptEngine(System.IO.Path.GetTempPath(), NullLogger.Instance);
        var e = await Assert.ThrowsExactlyAsync<System.InvalidOperationException>(() => engine.ExecuteAsync(
            "var x = 1;\npublic static class Mixed { public static int Twice(this int y) => y * 2; }"));
        StringAssert.Contains(e.Message, "cell of its own");
    }

    [TestMethod]
    public async Task The_extension_method_completes_and_has_definition() {
        var engine = await EngineAsync();
        var svc = new ScriptLanguageService();

        const string code = "21.Tw";
        var completions = await svc.GetCompletionsAsync(engine.SnapshotState(), code, code.Length);
        Assert.IsTrue(System.Linq.Enumerable.Any(completions.Items, i => i.Label == "Twice"),
            "cell-defined extension method should complete");

        const string call = "var n = 21.Twice();";
        var defs = await svc.GetDefinitionsAsync(engine.SnapshotState(), call, call.IndexOf("Twice") + 1);
        Assert.IsNotNull(defs.Metadata, "F12 on a cell-defined extension method should decompile");
        StringAssert.Contains(defs.Metadata.Text, "Twice");
    }
}

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.LanguageServices;
using ClrKernel.Core.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// A package a notebook #r's can name a type the same as one ClrKernel imports
/// by default (here <c>DisplayTable</c>, from <c>ClrKernel.Core.Primitives</c>).
/// The cell's own <c>using</c> must win, as it does in a script submission —
/// in execution, in a cell compiled as a library, and in completion.
/// </summary>
[TestClass]
public class ImportPriorityTest {
    private static readonly string _theirs = TheirLibrary();

    private static string TheirLibrary() {
        var path = Path.Combine(Path.GetTempPath(), "clrkernel-their-lib-" + System.Guid.NewGuid().ToString("N") + ".dll");
        var emit = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(path),
                new[] { CSharpSyntaxTree.ParseText(
                    "namespace Their { public class DisplayTable { public string Who => \"theirs\"; } }") },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .Emit(path);
        Assert.IsTrue(emit.Success);
        return path;
    }

    private static async Task<InteractiveScriptEngine> EngineAsync() {
        InteractiveScriptEngine.RefsFilePath = null;
        var engine = new InteractiveScriptEngine(Path.GetTempPath(), NullLogger.Instance);
        await engine.ExecuteAsync($"#r \"{_theirs}\"");
        return engine;
    }

    private static string Text(object result) => ((DisplayData)result).Data["text/plain"]?.ToString();

    [TestMethod]
    public async Task A_cell_using_beats_a_default_import() {
        var engine = await EngineAsync();
        Assert.AreEqual("theirs", Text(await engine.ExecuteAsync("using Their;\nnew DisplayTable().Who")));
    }

    [TestMethod]
    public async Task A_using_from_an_earlier_cell_still_beats_a_default_import() {
        var engine = await EngineAsync();
        await engine.ExecuteAsync("using Their;");
        Assert.AreEqual("theirs", Text(await engine.ExecuteAsync("new DisplayTable().Who")));
    }

    [TestMethod]
    public async Task A_library_cell_using_beats_a_default_import() {
        var engine = await EngineAsync();
        await engine.ExecuteAsync(
            "using Their;\npublic static class TheirExt { public static string W(this int i) => new DisplayTable().Who; }");
        Assert.AreEqual("theirs", Text(await engine.ExecuteAsync("1.W()")));
    }

    [TestMethod]
    public async Task Completion_offers_the_members_of_the_cells_type() {
        var engine = await EngineAsync();
        const string code = "using Their;\nnew DisplayTable().";
        var result = await new ScriptLanguageService().GetCompletionsAsync(engine.SnapshotState(), code, code.Length);
        CollectionAssert.Contains(result.Items.Select(i => i.Label).ToList(), "Who");
    }

    [TestMethod]
    public async Task Without_a_using_the_default_import_still_answers() {
        var engine = await EngineAsync();
        var result = await engine.ExecuteAsync("typeof(DisplayTable).Namespace");
        Assert.AreEqual("ClrKernel.Core.Primitives", Text(result));
    }
}

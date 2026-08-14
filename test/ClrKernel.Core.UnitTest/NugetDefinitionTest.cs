using System.Threading.Tasks;
using ClrKernel.Core.ExtensionServer.Lsp;
using ClrKernel.Core.LanguageServices;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class NugetDefinitionTest {
    // These download Humanizer from nuget.org: on a machine without network the
    // load fails and the test skips rather than lying either way.
    private static async Task<InteractiveScriptEngine> EngineWithPackageAsync() {
        InteractiveScriptEngine.RefsFilePath = null;
        var engine = new InteractiveScriptEngine(System.IO.Path.GetTempPath(), NullLogger.Instance);
        try {
            await engine.ExecuteAsync("#r \"nuget: Humanizer\"");
        } catch (System.Exception e) {
            Assert.Inconclusive("nuget restore unavailable: " + e.Message);
        }
        return engine;
    }

    [TestMethod]
    public async Task A_nuget_symbol_decompiles_even_with_the_directive_in_the_cell() {
        var engine = await EngineWithPackageAsync();
        // The user's real cell shape: #r line, using, and the call all in ONE cell -
        // exactly the text the LSP sends as the current document for analysis.
        const string code = "#r \"nuget: Humanizer\"\nusing Humanizer;\nSystem.Console.WriteLine(System.TimeSpan.FromDays(45).Humanize());";
        var svc = new ScriptLanguageService();
        var result = await svc.GetDefinitionsAsync(engine.SnapshotState(), code, code.IndexOf("Humanize()") + 1);
        Assert.AreEqual(0, result.Locations.Count);
        Assert.IsNotNull(result.Metadata, "nuget symbol should decompile");
        StringAssert.Contains(result.Metadata.Text, "Humanize");
    }

    [TestMethod]
    public async Task The_full_lsp_path_serves_a_metadata_link_for_a_nuget_symbol() {
        var server = new LspServer(NullLoggerFactory.Instance);
        const string cell = "#r \"nuget: Humanizer\"\nusing Humanizer;\nSystem.Console.WriteLine(System.TimeSpan.FromDays(45).Humanize());";
        // run the cell like clrkernel/execute would
        try {
            await server.Execute(new ExecuteParams { CellId = "c1", Code = cell });
        } catch (System.Exception e) {
            Assert.Inconclusive("nuget restore unavailable: " + e.Message);
        }
        server.DidOpen(new DidOpenTextDocumentParams {
            TextDocument = new TextDocumentItem { Uri = "cell:1", LanguageId = "csharp-script", Text = cell },
        });
        var line = 2;
        var character = cell.Split('\n')[2].IndexOf("Humanize()") + 1;
        var links = await server.Definition(new TextDocumentPositionParams {
            TextDocument = new TextDocumentIdentifier { Uri = "cell:1" },
            Position = new Position { Line = line, Character = character },
        });
        Assert.AreEqual(1, links.Count, "a nuget symbol must resolve to a decompiled-source link");
        StringAssert.StartsWith(links[0].TargetUri, "clrkernel-metadata:/");
    }
}

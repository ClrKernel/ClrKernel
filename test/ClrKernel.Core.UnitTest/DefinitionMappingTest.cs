using System.Collections.Generic;
using ClrKernel.Core.ExtensionServer.Lsp;
using ClrKernel.Core.LanguageServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

/// <summary>
/// How service definitions become LSP locations: current-cell offsets map directly;
/// an executed submission's defining line is found again in whichever open cell
/// still contains it, so Go to Definition lands in the visible notebook.
/// </summary>
[TestClass]
public class DefinitionMappingTest {
    [TestMethod]
    public void A_current_cell_definition_maps_by_offset_and_frames_the_full_declaration() {
        const string code = "int Add(int a)\n{\n    return a;\n}\nAdd(1)";
        var locations = LspServer.MapDefinitions(
            new[] { new DefinitionLocationDto(true, 4, 3, null, 0, FullStart: 0, FullLength: code.IndexOf("}") + 1) },
            "cell-1", code, new List<(string, string)> { ("cell-1", code) });
        Assert.AreEqual(1, locations.Count);
        Assert.AreEqual("cell-1", locations[0].TargetUri);
        Assert.AreEqual(4, locations[0].TargetSelectionRange.Start.Character, "selection is the name token");
        Assert.AreEqual(0, locations[0].TargetRange.Start.Line);
        Assert.AreEqual(3, locations[0].TargetRange.End.Line, "the peek frames the whole body");
    }

    [TestMethod]
    public void An_executed_definition_is_found_in_the_open_cell_that_contains_its_line() {
        var docs = new List<(string, string)> {
            ("cell-2", "Add(1, 2)"),
            ("cell-1", "// helpers\nint Add(int a, int b) => a + b;"),
        };
        var locations = LspServer.MapDefinitions(
            new[] { new DefinitionLocationDto(false, 0, 3, "int Add(int a, int b) => a + b;", 4) },
            "cell-2", "Add(1, 2)", docs);
        Assert.AreEqual(1, locations.Count);
        Assert.AreEqual("cell-1", locations[0].TargetUri);
        Assert.AreEqual(1, locations[0].TargetSelectionRange.Start.Line);
        Assert.AreEqual(4, locations[0].TargetSelectionRange.Start.Character);
        Assert.AreEqual(7, locations[0].TargetSelectionRange.End.Character);
    }

    [TestMethod]
    public void A_reindented_line_still_matches_with_the_column_shifted() {
        var docs = new List<(string, string)> {
            ("cell-1", "    int Add(int a, int b) => a + b;"), // re-indented since execution
        };
        var locations = LspServer.MapDefinitions(
            new[] { new DefinitionLocationDto(false, 0, 3, "int Add(int a, int b) => a + b;", 4) },
            "cell-1", "", docs);
        Assert.AreEqual(1, locations.Count);
        Assert.AreEqual(8, locations[0].TargetSelectionRange.Start.Character, "column shifts by the indentation delta");
    }

    [TestMethod]
    public void A_line_no_open_cell_contains_yields_no_location() {
        var locations = LspServer.MapDefinitions(
            new[] { new DefinitionLocationDto(false, 0, 3, "int Gone() => 1;", 4) },
            "cell-1", "Gone()", new List<(string, string)> { ("cell-1", "Gone()") });
        Assert.AreEqual(0, locations.Count);
    }
}

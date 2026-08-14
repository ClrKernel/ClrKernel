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
    public void A_current_cell_definition_maps_by_offset() {
        const string code = "var x = 1;\nx + 1";
        var locations = LspServer.MapDefinitions(
            new[] { new DefinitionLocationDto(true, 4, 1, null, 0) },
            "cell-1", code, new List<(string, string)> { ("cell-1", code) });
        Assert.AreEqual(1, locations.Count);
        Assert.AreEqual("cell-1", locations[0].Uri);
        Assert.AreEqual(0, locations[0].Range.Start.Line);
        Assert.AreEqual(4, locations[0].Range.Start.Character);
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
        Assert.AreEqual("cell-1", locations[0].Uri);
        Assert.AreEqual(1, locations[0].Range.Start.Line);
        Assert.AreEqual(4, locations[0].Range.Start.Character);
        Assert.AreEqual(7, locations[0].Range.End.Character);
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
        Assert.AreEqual(8, locations[0].Range.Start.Character, "column shifts by the indentation delta");
    }

    [TestMethod]
    public void A_line_no_open_cell_contains_yields_no_location() {
        var locations = LspServer.MapDefinitions(
            new[] { new DefinitionLocationDto(false, 0, 3, "int Gone() => 1;", 4) },
            "cell-1", "Gone()", new List<(string, string)> { ("cell-1", "Gone()") });
        Assert.AreEqual(0, locations.Count);
    }
}

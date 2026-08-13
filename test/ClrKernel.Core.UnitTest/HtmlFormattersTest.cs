using System.Collections.Generic;
using ClrKernel.Core.Primitives;
using ClrKernel.Formatting.Html;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel;

/// <summary>
/// The plugin's registrations, exercised purely through the registry — the way every
/// producer reaches them. Registered per-class and unregistered after, so the tests
/// that assert "no HTML formatter exists" elsewhere in this assembly stay honest.
/// </summary>
[TestClass]
public class HtmlFormattersTest {
    [ClassInitialize]
    public static void RegisterPlugin(TestContext _) => HtmlFormatters.RegisterDefaults();

    [ClassCleanup]
    public static void UnregisterPlugin() => HtmlFormatters.UnregisterDefaults();

    [TestMethod]
    public void AnObjectGetsTheRichResultFormatterRender() {
        var html = new DisplayObject(new { Name = "x", Count = 3 }).ToHtml().Html;
        StringAssert.Contains(html, "clrkernel-result", "the type-badge wrapper should be present");
        StringAssert.Contains(html, "Name");
        StringAssert.Contains(html, "x");
    }

    [TestMethod]
    public void DisplayingAnObjectNowMatchesTheTrailingValueRender() {
        // The unification this whole refactor exists for: Display(x) and a bare
        // trailing x produce the same HTML.
        var value = new { Name = "x", Count = 3 };
        var displayed = DisplayDataPackager.Pack(new DisplayObject(value));
        var trailing = ResultFormatter.Format(value);
        Assert.AreEqual(trailing.Data["text/html"], displayed.Data["text/html"]);
        Assert.AreEqual(trailing.Data["text/plain"], displayed.Data["text/plain"]);
    }

    [TestMethod]
    public void ATableRendersAsTheInteractiveGrid() {
        var table = new DisplayTable(null,
            new[] { "Id", "Name" },
            new IReadOnlyList<string>[] { new[] { "1", "Ada" }, new[] { "2", "Bo" } },
            new[] { InteractiveTable.Number, InteractiveTable.Text });
        var html = table.ToHtml().Html;
        StringAssert.Contains(html, "clrkernel-table", "the grid wrapper should be present");
        StringAssert.Contains(html, "Ada");
    }

    [TestMethod]
    public void ATableGetsAReadableTextForm() {
        var table = new DisplayTable(null,
            new[] { "Id", "Name" },
            new IReadOnlyList<string>[] { new[] { "1", "Ada" } });
        var text = table.ToText().Text;
        StringAssert.Contains(text, "Id\tName");
        StringAssert.Contains(text, "1\tAda");
    }

    [TestMethod]
    public void ConsoleTextRendersAnsiColourAsHtmlAndStripsItFromText() {
        var coloured = new DisplayConsoleText("\u001b[31mred\u001b[0m");
        StringAssert.Contains(coloured.ToHtml().Html, "ansi");
        Assert.AreEqual("red", coloured.ToText().Text);
    }

    [TestMethod]
    public void ProgressRendersABar() {
        var html = new DisplayProgress("loading", null, 25, 100).ToHtml().Html;
        StringAssert.Contains(html, "width:25%");
        StringAssert.Contains(html, "loading");
    }

    [TestMethod]
    public void PreferringTableRoutesAnObjectThroughTheExtractor() {
        var rows = new[] { new { Id = 1, Name = "Ada" }, new { Id = 2, Name = "Bo" } };
        var data = DisplayDataPackager.Pack(new DisplayObject(rows, typeof(DisplayTable)));
        StringAssert.Contains((string)data.Data["text/html"], "clrkernel-table",
            "DisplayTable preference must produce the grid, not the plain result render");
    }

    [TestMethod]
    public void DisplayingNullStillRendersBothMimeTypes() {
        var data = DisplayDataPackager.Pack(new DisplayObject(null));
        Assert.AreEqual("null", data.Data["text/plain"]);
        StringAssert.Contains((string)data.Data["text/html"], "null");
    }

    [TestMethod]
    public void ABadgeRendersAsAPill() {
        var info = new DisplayBadge("fcst", "12 ms").ToHtml().Html;
        StringAssert.Contains(info, "border-radius:10px");
        StringAssert.Contains(info, "#0969da", "default tone is the informational blue");
        var success = new DisplayBadge("MERGE t", "done", DisplayBadge.Success).ToHtml().Html;
        StringAssert.Contains(success, "#1a7f37", "success tone is green");
    }

    [TestMethod]
    public void RegisterDefaultsIsIdempotent() {
        var first = HtmlFormatters.RegisterDefaults();
        var second = HtmlFormatters.RegisterDefaults();
        Assert.AreSame(first, second);
    }
}

[TestClass]
public class TableExtractorTest {
    [TestMethod]
    public void SequencesOfObjectsBecomePropertyColumns() {
        var table = TableExtractor.Extract(new[] { new { Id = 1, Name = "Ada" }, new { Id = 2, Name = "Bo" } });
        CollectionAssert.AreEqual(new[] { "Id", "Name" }, (System.Collections.ICollection)table.Columns);
        Assert.AreEqual(2, table.Rows.Count);
        Assert.AreEqual("Ada", table.Rows[0][1]);
        Assert.AreEqual(InteractiveTable.Number, table.Types[0]);
        Assert.AreEqual(2, table.TotalRows);
    }

    [TestMethod]
    public void ScalarSequencesGetASingleValueColumn() {
        var table = TableExtractor.Extract(new[] { 1, 2, 3 });
        CollectionAssert.AreEqual(new[] { "Value" }, (System.Collections.ICollection)table.Columns);
        Assert.AreEqual("2", table.Rows[1][0]);
    }

    [TestMethod]
    public void DictionaryRowsUnionTheirKeys() {
        var table = TableExtractor.Extract(new List<IDictionary<string, object>> {
            new Dictionary<string, object> { ["a"] = 1 },
            new Dictionary<string, object> { ["a"] = 2, ["b"] = "x" },
        });
        CollectionAssert.AreEqual(new[] { "a", "b" }, (System.Collections.ICollection)table.Columns);
        Assert.AreEqual("x", table.Rows[1][1]);
    }

    [TestMethod]
    public void ADataTableKeepsItsSchema() {
        var source = new System.Data.DataTable();
        source.Columns.Add("N", typeof(int));
        source.Rows.Add(7);
        var table = TableExtractor.Extract(source);
        Assert.AreEqual(InteractiveTable.Number, table.Types[0]);
        Assert.AreEqual("7", table.Rows[0][0]);
        Assert.AreEqual(1, table.TotalRows);
    }

    [TestMethod]
    public void ASingleObjectBecomesAOneRowTable() {
        var table = TableExtractor.Extract(new { Id = 1, Name = "Ada" });
        Assert.AreEqual(1, table.Rows.Count);
        Assert.AreEqual("Ada", table.Rows[0][1]);
    }

    [TestMethod]
    public void TruncationMarksTheTotalUnknown() {
        var table = TableExtractor.Extract(System.Linq.Enumerable.Range(0, 5000));
        Assert.AreEqual(1000, table.Rows.Count);
        Assert.AreEqual(-1, table.TotalRows, "-1 = truncated with remainder uncounted");
    }
}

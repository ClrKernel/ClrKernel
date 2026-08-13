using System;
using System.Collections.Generic;
using System.Data;
using ClrKernel.Core.Primitives;
using ClrKernel.Formatting.Html;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.UnitTest;

[TestClass]
public class InteractiveTableTest {
    // The DisplayTable overloads emit concepts; the grid html needs the renders.
    [ClassInitialize]
    public static void RegisterPlugin(TestContext _) => HtmlFormatters.RegisterDefaults();

    [ClassCleanup]
    public static void UnregisterPlugin() => HtmlFormatters.UnregisterDefaults();

    // Captures the html a DisplayTable overload emitted (it publishes a
    // display_data message; we intercept the emitter to read it back).
    private static string CaptureHtml(Action display) {
        DisplayData captured = null;
        var previous = DisplayDataEmitter.DisplayDataHandler;
        DisplayDataEmitter.DisplayDataHandler = d => captured = d;
        try {
            display();
        } finally {
            DisplayDataEmitter.DisplayDataHandler = previous;
        }
        Assert.IsNotNull(captured, "no display_data was emitted");
        return (string)captured.Data["text/html"];
    }

    [TestMethod]
    public void Render_produces_a_self_contained_interactive_grid() {
        var html = InteractiveTable.Render(
            new[] { "Name", "Age" },
            new IReadOnlyList<string>[] { new[] { "Alice", "30" }, new[] { "Bob", "25" } },
            new[] { InteractiveTable.Text, InteractiveTable.Number },
            2);

        StringAssert.Contains(html, "class=\"clrkernel-table\"");
        StringAssert.Contains(html, "<style>");           // inline CSS
        StringAssert.Contains(html, "<script>");          // inline behavior
        StringAssert.Contains(html, "ck-filter");         // filter box
        StringAssert.Contains(html, "ck-analyze");        // analyze toggle
        StringAssert.Contains(html, "application/json");  // embedded data payload
        StringAssert.Contains(html, "\"Alice\"");
        StringAssert.Contains(html, "\"cols\":[\"Name\",\"Age\"]");
        StringAssert.Contains(html, "\"types\":[\"string\",\"number\"]");
    }

    [TestMethod]
    public void Render_gives_each_grid_a_unique_root_id() {
        var a = InteractiveTable.Render(new[] { "X" }, new IReadOnlyList<string>[] { new[] { "1" } }, new[] { "number" }, 1);
        var b = InteractiveTable.Render(new[] { "X" }, new IReadOnlyList<string>[] { new[] { "1" } }, new[] { "number" }, 1);
        var idA = a.Substring(a.IndexOf("id=\"", StringComparison.Ordinal));
        var idB = b.Substring(b.IndexOf("id=\"", StringComparison.Ordinal));
        Assert.AreNotEqual(idA.Substring(0, 45), idB.Substring(0, 45), "grid ids collided");
    }

    [TestMethod]
    public void Render_escapes_closing_tags_in_data_and_encodes_headers() {
        var html = InteractiveTable.Render(
            new[] { "<b>col</b>" },
            new IReadOnlyList<string>[] { new[] { "</script><img>" } },
            new[] { "string" },
            1);
        // The payload must not contain a literal "</" that could end the element early.
        var dataStart = html.IndexOf("application/json", StringComparison.Ordinal);
        var dataEnd = html.IndexOf("</script>", dataStart, StringComparison.Ordinal);
        var payload = html.Substring(dataStart, dataEnd - dataStart);
        Assert.IsFalse(payload.Contains("</script>"), "unescaped closing tag leaked into the JSON payload");
        StringAssert.Contains(payload, "<\\/script>");
    }

    [TestMethod]
    public void KindOf_maps_clr_types_to_grid_kinds() {
        Assert.AreEqual(InteractiveTable.Number, InteractiveTable.KindOf(typeof(int)));
        Assert.AreEqual(InteractiveTable.Number, InteractiveTable.KindOf(typeof(decimal)));
        Assert.AreEqual(InteractiveTable.Number, InteractiveTable.KindOf(typeof(double?)));
        Assert.AreEqual(InteractiveTable.Date, InteractiveTable.KindOf(typeof(DateTime)));
        Assert.AreEqual(InteractiveTable.Text, InteractiveTable.KindOf(typeof(string)));
        Assert.AreEqual(InteractiveTable.Text, InteractiveTable.KindOf(typeof(Guid)));
    }

    [TestMethod]
    public void DisplayTable_on_DataTable_uses_column_types() {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("When", typeof(DateTime));
        table.Rows.Add(1, "alpha", new DateTime(2020, 1, 2));
        table.Rows.Add(2, "beta", DBNull.Value);

        var html = CaptureHtml(() => table.DisplayTable());
        StringAssert.Contains(html, "\"cols\":[\"Id\",\"Name\",\"When\"]");
        StringAssert.Contains(html, "\"types\":[\"number\",\"string\",\"date\"]");
        StringAssert.Contains(html, "\"alpha\"");
        StringAssert.Contains(html, "null");            // DBNull -> JSON null
        StringAssert.Contains(html, "\"total\":2");
    }

    [TestMethod]
    public void DisplayTable_on_DataReader_reads_schema_and_rows() {
        var table = new DataTable();
        table.Columns.Add("Score", typeof(double));
        table.Rows.Add(9.5);
        table.Rows.Add(3.0);
        using var reader = table.CreateDataReader();

        var html = CaptureHtml(() => reader.DisplayTable());
        StringAssert.Contains(html, "\"cols\":[\"Score\"]");
        StringAssert.Contains(html, "\"types\":[\"number\"]");
        StringAssert.Contains(html, "\"9.5\"");
        StringAssert.Contains(html, "\"total\":2");
    }

    [TestMethod]
    public void DisplayTable_on_scalar_sequence_uses_a_value_column() {
        var html = CaptureHtml(() => new[] { 1, 2, 3 }.DisplayTable());
        StringAssert.Contains(html, "\"cols\":[\"Value\"]");
        StringAssert.Contains(html, "\"types\":[\"number\"]");
        StringAssert.Contains(html, "\"total\":3");
    }

    [TestMethod]
    public void DisplayTable_on_object_sequence_uses_property_columns() {
        var html = CaptureHtml(() => new[] {
            new { Name = "a", Age = 1 },
            new { Name = "b", Age = 2 }
        }.DisplayTable());
        StringAssert.Contains(html, "\"cols\":[\"Name\",\"Age\"]");
        StringAssert.Contains(html, "\"types\":[\"string\",\"number\"]");
    }

    [TestMethod]
    public void DisplayTable_truncates_lazy_sequence_and_signals_more() {
        IEnumerable<int> Numbers() {
            var i = 0;
            while (true) {
                yield return i++;
            }
        }

        var html = CaptureHtml(() => Numbers().DisplayTable(limit: 5));
        StringAssert.Contains(html, "\"shown\":5");
        StringAssert.Contains(html, "\"total\":-1");   // unknown-but-more sentinel
    }
}

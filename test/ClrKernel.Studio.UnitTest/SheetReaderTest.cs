using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// Spreadsheets as rows of display text, for the Preview tab.
/// <para>
/// The workbook these read is written by openpyxl rather than by the library
/// under test, so "it reads what Excel writes" is what is being asserted, not
/// "it agrees with itself".
/// </para>
/// </summary>
[TestClass]
public class SheetReaderTest {
    private static string Fixture(string name) =>
        Path.Combine(Path.GetDirectoryName(typeof(SheetReaderTest).Assembly.Location)!, "fixtures", name);

    /// <summary>
    /// The one that decides the whole approach: a date is stored as a serial
    /// number, and a viewer that shows 46095 where the spreadsheet shows a date is
    /// not a viewer. `GetFormattedString` is why this reads a workbook rather than
    /// unzipping one.
    /// </summary>
    [TestMethod]
    public void A_workbook_reads_as_the_text_the_spreadsheet_shows() {
        var sheets = SheetReader.Read(Fixture("sample.xlsx"));

        Assert.AreEqual(2, sheets.Count, "both tabs");
        Assert.AreEqual("Numbers", sheets[0].Name);
        Assert.AreEqual("Notes", sheets[1].Name);

        var rows = sheets[0].Rows;
        CollectionAssert.AreEqual(new[] { "name", "when", "amount", "note" }, rows[0].ToArray());
        Assert.AreEqual("alpha", rows[1][0]);
        StringAssert.Contains(rows[1][1], "2026", "a date, not the number it is stored as");
        Assert.IsFalse(rows[1][1].StartsWith("460"), $"serial number leaked: {rows[1][1]}");
        Assert.AreEqual("1,234.50", rows[1][2], "the cell's own number format");
        Assert.AreEqual("quoted, comma", rows[1][3]);

        Assert.IsNull(rows[3][1], "an empty cell is empty, not \"\"");
        Assert.AreEqual(4, sheets[0].TotalRows);
    }

    [TestMethod]
    public void Csv_quoting_follows_the_rules_a_spreadsheet_wrote_it_with() {
        var page = SheetReader.Delimited(
            "a,b,c\n\"has, comma\",\"has \"\"quotes\"\"\",plain\n\"multi\nline\",,end\n", ',', "x.csv");

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, page.Rows[0].ToArray());
        CollectionAssert.AreEqual(
            new[] { "has, comma", "has \"quotes\"", "plain" }, page.Rows[1].ToArray());
        CollectionAssert.AreEqual(
            new[] { "multi\nline", null, "end" }, page.Rows[2].ToArray(),
            "a newline inside quotes is one cell, and an empty field is empty");
        Assert.AreEqual(3, page.TotalRows, "the trailing newline does not add a row");
    }

    [TestMethod]
    public void Tabs_are_the_same_reader_with_another_delimiter() {
        var page = SheetReader.Delimited("a\tb\n1\t2\n", '\t', "x.tsv");
        CollectionAssert.AreEqual(new[] { "1", "2" }, page.Rows[1].ToArray());
    }

    /// <summary>
    /// A big file is capped and says so. Showing 1000 rows and reporting 1000 is
    /// the failure worth preventing: it is indistinguishable from a short file.
    /// </summary>
    [TestMethod]
    public void A_long_file_is_capped_and_reports_its_real_length() {
        var text = string.Join("\n", Enumerable.Range(1, SheetReader.RowLimit + 500).Select(i => $"{i},x"));
        var page = SheetReader.Delimited(text, ',', "big.csv");

        Assert.AreEqual(SheetReader.RowLimit, page.Rows.Count);
        Assert.AreEqual(SheetReader.RowLimit + 500, page.TotalRows);
    }

    [TestMethod]
    public void Only_the_formats_it_can_actually_read() {
        Assert.IsTrue(SheetReader.Handles("a/b.csv"));
        Assert.IsTrue(SheetReader.Handles("B.TSV"));
        Assert.IsTrue(SheetReader.Handles("x.xlsx"));
        Assert.IsTrue(SheetReader.Handles("macro.xlsm"));

        // The old OLE2 format is a different file entirely; half-reading it would
        // be worse than saying no.
        Assert.IsFalse(SheetReader.Handles("old.xls"));
        Assert.IsFalse(SheetReader.Handles("notes.md"));

        Assert.IsTrue(SheetReader.IsWorkbook("x.xlsx"), "binary: no Source tab over it");
        Assert.IsFalse(SheetReader.IsWorkbook("x.csv"), "text: keeps Source and Diff");
    }
}

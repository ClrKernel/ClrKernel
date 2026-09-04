using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;

namespace ClrKernel.Studio;

/// <summary>One tab of a workbook, or the whole of a delimited file.</summary>
public sealed class SheetPage {
    public string Name { get; init; }

    /// <summary>Cells as display text — what the spreadsheet shows, not what it
    /// stores. A date is "14/03/2026", never 46095.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = Array.Empty<string[]>();

    /// <summary>Rows before the cap, so the view can say "first 1000 of 40000"
    /// rather than quietly showing a thousand and calling it the file.</summary>
    public int TotalRows { get; init; }
}

/// <summary>
/// Spreadsheets as rows of text, for the Preview tab.
///
/// <para>
/// Server-side, like <c>/notebooks/cells</c>, so the browser never needs its own
/// copy of a format. That matters more here than for notebooks: an xlsx is a zip
/// of XML with a string table, a style table and dates stored as numbers, and the
/// alternative is shipping a parser to the browser and getting the dates wrong.
/// </para>
/// </summary>
public static class SheetReader {
    /// <summary>
    /// Rows returned per sheet. A 10 MB workbook is a small download and a very
    /// large table, and the browser has to lay out every row it is given.
    /// </summary>
    public const int RowLimit = 1000;

    /// <summary>Columns per row, for the same reason.</summary>
    public const int ColumnLimit = 200;

    /// <summary>The extensions this reads, lower-case with the dot.</summary>
    public static bool Handles(string path) => Kind(path) != null;

    private static string Kind(string path) => Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch {
        ".csv" => "csv",
        ".tsv" or ".tab" => "tsv",
        // `.xls` is the older OLE2 format — a different thing entirely, and out of
        // scope rather than half-read.
        ".xlsx" or ".xlsm" => "excel",
        _ => null,
    };

    /// <summary>Whether the file is binary, and so has no Source tab to offer.</summary>
    public static bool IsWorkbook(string path) => Kind(path) == "excel";

    public static IReadOnlyList<SheetPage> Read(string path) => Kind(path) switch {
        "csv" => new[] { Delimited(File.ReadAllText(path), ',', Path.GetFileName(path)) },
        "tsv" => new[] { Delimited(File.ReadAllText(path), '\t', Path.GetFileName(path)) },
        "excel" => Workbook(path),
        _ => throw new InvalidOperationException($"Not a spreadsheet: {path}"),
    };

    private static IReadOnlyList<SheetPage> Workbook(string path) {
        // Read-only stream: the file is in a git worktree somebody else may be
        // writing, and this must never take a write lock on it.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var workbook = new XLWorkbook(stream);
        var pages = new List<SheetPage>();
        foreach (var sheet in workbook.Worksheets) {
            var used = sheet.LastRowUsed();
            var total = used?.RowNumber() ?? 0;
            var width = Math.Min(sheet.LastColumnUsed()?.ColumnNumber() ?? 0, ColumnLimit);
            var rows = new List<IReadOnlyList<string>>();
            foreach (var row in sheet.Rows(1, Math.Min(total, RowLimit))) {
                var cells = new string[width];
                for (var i = 0; i < width; i++) {
                    // GetFormattedString, not Value: it applies the cell's number
                    // format, which is the difference between a date and the serial
                    // number a date is stored as.
                    var text = row.Cell(i + 1).GetFormattedString();
                    cells[i] = text.Length == 0 ? null : text;
                }
                rows.Add(cells);
            }
            pages.Add(new SheetPage { Name = sheet.Name, Rows = rows, TotalRows = total });
        }
        return pages;
    }

    /// <summary>
    /// A delimited file, quoted per RFC 4180: a field may contain the delimiter, a
    /// newline, or a doubled quote, and only a quote that opens a field counts.
    /// </summary>
    public static SheetPage Delimited(string text, char delimiter, string name) {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        var total = 0;

        void EndField() {
            row.Add(field.Length == 0 ? null : field.ToString());
            field.Clear();
        }
        void EndRow() {
            EndField();
            total++;
            if (rows.Count < RowLimit) {
                rows.Add(row.Count > ColumnLimit ? row.Take(ColumnLimit).ToList() : new List<string>(row));
            }
            row.Clear();
        }

        for (var i = 0; i < (text ?? string.Empty).Length; i++) {
            var c = text[i];
            if (quoted) {
                if (c != '"') {
                    field.Append(c);
                } else if (i + 1 < text.Length && text[i + 1] == '"') {
                    field.Append('"');
                    i++;
                } else {
                    quoted = false;
                }
                continue;
            }
            if (c == '"' && field.Length == 0) {
                quoted = true;
            } else if (c == delimiter) {
                EndField();
            } else if (c == '\n') {
                EndRow();
            } else if (c != '\r') {
                field.Append(c);
            }
        }
        // A trailing newline ends the last row rather than starting an empty one.
        if (field.Length > 0 || row.Count > 0) {
            EndRow();
        }
        return new SheetPage { Name = name, Rows = rows, TotalRows = total };
    }
}

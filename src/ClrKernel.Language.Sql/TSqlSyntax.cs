using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ClrKernel.Language.Sql;
/// <summary>
/// A single T-SQL syntax problem. Positions are 0-based (line, character) to
/// match LSP ranges; <see cref="EndColumn"/> is exclusive.
/// </summary>
public sealed class SqlDiagnostic {
    public int Line { get; set; }
    public int Column { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public int Number { get; set; }
    public string Message { get; set; }
}

/// <summary>
/// T-SQL syntax validation via Microsoft's ScriptDom parser (the same grammar
/// SSMS/ADS use). Used for live diagnostics in the editor and for a pre-flight
/// check before a cell executes. Parsing is offline and side-effect-free.
/// </summary>
public static class TSqlSyntax {
    /// <summary>Parses SQL and returns any syntax errors (empty when valid).</summary>
    public static IReadOnlyList<SqlDiagnostic> Check(string sql) {
        var results = new List<SqlDiagnostic>();
        if (string.IsNullOrWhiteSpace(sql)) {
            return results;
        }

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        parser.Parse(reader, out var errors);
        if (errors == null) {
            return results;
        }

        foreach (var e in errors) {
            var startLine = e.Line - 1 < 0 ? 0 : e.Line - 1;
            var startCol = e.Column - 1 < 0 ? 0 : e.Column - 1;
            var (endLine, endCol) = TokenEnd(sql, startLine, startCol);
            results.Add(new SqlDiagnostic {
                Line = startLine,
                Column = startCol,
                EndLine = endLine,
                EndColumn = endCol,
                Number = e.Number,
                Message = e.Message,
            });
        }
        return results;
    }

    /// <summary>True when the SQL has no syntax errors.</summary>
    public static bool IsValid(string sql) => Check(sql).Count == 0;

    // ScriptDom reports a point, not a span. Extend the squiggle to the end of
    // the token at (line, col) so the underline is visible; fall back to +1.
    private static (int endLine, int endColumn) TokenEnd(string sql, int line, int col) {
        var lines = sql.Replace("\r\n", "\n").Split('\n');
        if (line >= lines.Length) {
            return (line, col + 1);
        }
        var text = lines[line];
        if (col >= text.Length) {
            return (line, col + 1);
        }
        var i = col;
        var first = text[i];
        if (char.IsLetterOrDigit(first) || first == '_' || first == '@' || first == '#' || first == '[') {
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' ||
                                       text[i] == '@' || text[i] == '#' || text[i] == '[' || text[i] == ']')) {
                i++;
            }
        } else {
            i++;
        }
        return (line, i);
    }
}

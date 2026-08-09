using System;
using System.Linq;
using System.Text;

namespace ClrKernel.Sql.Etl;
/// <summary>
/// Safely quotes SQL Server identifiers so table/column names from user input
/// can be interpolated into generated T-SQL without injection. A plain name
/// becomes <c>[name]</c> (embedded <c>]</c> doubled); a dotted name like
/// <c>dbo.Orders</c> becomes <c>[dbo].[Orders]</c>. Already-bracketed parts are
/// accepted and re-normalized.
/// </summary>
public static class SqlIdentifier {
    /// <summary>Quotes a possibly multi-part identifier (schema.table.column).</summary>
    public static string Quote(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Identifier is empty.", nameof(name));
        }
        var parts = SplitParts(name);
        return string.Join(".", parts.Select(QuotePart));
    }

    /// <summary>Quotes a single identifier part (no dots interpreted).</summary>
    public static string QuotePart(string part) {
        var trimmed = part.Trim();
        if (trimmed.StartsWith("[") && trimmed.EndsWith("]") && trimmed.Length >= 2) {
            trimmed = trimmed.Substring(1, trimmed.Length - 2);
        }
        if (trimmed.Length == 0) {
            throw new ArgumentException($"Invalid identifier part in '{part}'.");
        }
        return "[" + trimmed.Replace("]", "]]") + "]";
    }

    // Splits on dots that are outside brackets, so "[a.b].c" -> "[a.b]", "c".
    private static string[] SplitParts(string name) {
        var parts = new System.Collections.Generic.List<string>();
        var sb = new StringBuilder();
        var inBracket = false;
        foreach (var c in name) {
            if (c == '[') {
                inBracket = true;
                sb.Append(c);
            } else if (c == ']') {
                inBracket = false;
                sb.Append(c);
            } else if (c == '.' && !inBracket) {
                parts.Add(sb.ToString());
                sb.Clear();
            } else {
                sb.Append(c);
            }
        }
        parts.Add(sb.ToString());
        return parts.ToArray();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClrKernel.Database;

/// <summary>
/// Splits and quotes a qualified table name the way T-SQL reads it: a bracketed part is
/// atomic, so <c>[Mart].[COMPANY.Dimension.Forecast]</c> is two parts, not four, and
/// <c>]]</c> inside brackets is a literal <c>]</c>. Unbracketed parts split on dots.
/// </summary>
public static class TableName {
    /// <summary>The name's parts, unquoted: <c>dbo.T</c> and <c>[dbo].[T]</c> both give <c>dbo</c>, <c>T</c>.</summary>
    public static IReadOnlyList<string> Parts(string qualified) {
        if (string.IsNullOrWhiteSpace(qualified)) {
            throw new ArgumentException("table name is required.", nameof(qualified));
        }
        var parts = new List<string>();
        var part = new StringBuilder();
        var bracketed = false;
        for (var i = 0; i < qualified.Length; i++) {
            var c = qualified[i];
            if (bracketed) {
                if (c == ']' && i + 1 < qualified.Length && qualified[i + 1] == ']') {
                    part.Append(']');
                    i++;
                } else if (c == ']') {
                    bracketed = false;
                } else {
                    part.Append(c);
                }
            } else if (c == '[') {
                bracketed = true;
            } else if (c == '.') {
                parts.Add(part.ToString().Trim());
                part.Clear();
            } else {
                part.Append(c);
            }
        }
        parts.Add(part.ToString().Trim());
        return parts;
    }

    /// <summary>Every part bracket-quoted: <c>Mart.[COMPANY.Dimension.Forecast]</c> → <c>[Mart].[COMPANY.Dimension.Forecast]</c>.</summary>
    public static string Quote(string qualified) => string.Join(".", Parts(qualified).Select(QuotePart));

    /// <summary>One identifier in brackets, with <c>]</c> doubled.</summary>
    public static string QuotePart(string name) => "[" + (name ?? string.Empty).Replace("]", "]]") + "]";

    /// <summary>The last two parts — schema null when the name has one part.</summary>
    public static (string Schema, string Name) SchemaAndName(string qualified) {
        var parts = Parts(qualified);
        return parts.Count >= 2 ? (parts[parts.Count - 2], parts[parts.Count - 1]) : (null, parts[0]);
    }
}

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ClrKernel.Database.Provider.SqlServer;
/// <summary>
/// Splits a T-SQL script into batches on the <c>GO</c> separator (its own line,
/// case-insensitive, optionally followed by a repeat count). <c>GO</c> is a
/// client batch separator, not T-SQL, so a single command cannot run a script
/// that contains it — deployment splits first, then runs each batch.
/// </summary>
public static class GoBatchSplitter {
    private static readonly Regex _go = new Regex(
        @"^\s*GO\s*(?:\d+)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<string> Split(string script) {
        var batches = new List<string>();
        if (string.IsNullOrWhiteSpace(script)) {
            return batches;
        }
        var current = new List<string>();
        foreach (var line in script.Replace("\r\n", "\n").Split('\n')) {
            if (_go.IsMatch(line)) {
                AddBatch(batches, current);
                current.Clear();
            } else {
                current.Add(line);
            }
        }
        AddBatch(batches, current);
        return batches;
    }

    private static void AddBatch(List<string> batches, List<string> lines) {
        var text = string.Join("\n", lines).Trim();
        if (text.Length > 0) {
            batches.Add(text);
        }
    }
}

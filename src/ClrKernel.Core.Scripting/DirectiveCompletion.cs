using System;
using System.Collections.Generic;
using System.Linq;

namespace ClrKernel.Core.Scripting;

/// <summary>One generated completion candidate for a directive line.</summary>
public sealed class DirectiveCompletionItem {
    public string Label { get; init; }
    public string Kind { get; init; }
    public string Detail { get; init; }
}

/// <summary>A generated completion list plus the span the inserted text replaces.</summary>
public sealed class DirectiveCompletionList {
    public int ReplaceStart { get; init; }
    public int ReplaceLength { get; init; }
    public List<DirectiveCompletionItem> Items { get; } = new();
}

/// <summary>
/// Completion for <c>#!</c> lines, generated from the same
/// <see cref="DirectiveDefinition"/> tables the parsers bind against — the flag
/// vocabulary in completions can no longer drift from what actually parses.
/// Languages keep their own statement-level completion (T-SQL keywords, DAX
/// functions) and their comment-directive dialects; this owns only the magic line.
/// </summary>
public static class DirectiveCompletion {
    /// <summary>
    /// Completes a magic line ("#!…") at the cursor.
    /// </summary>
    /// <param name="definitions">The language's directive tables.</param>
    /// <param name="lineToCursor">The current line's text up to the cursor.</param>
    /// <param name="lineStart">Offset of the line start in the document (result spans are absolute).</param>
    /// <param name="roleValues">Resolves a <see cref="DirectiveParameter.ValueRole"/> to live
    /// names (registered connections, cubes). Null when the language has no such context.</param>
    public static DirectiveCompletionList Complete(
        IReadOnlyList<DirectiveDefinition> definitions,
        string lineToCursor,
        int lineStart,
        Func<string, IEnumerable<string>> roleValues = null) {
        definitions ??= Array.Empty<DirectiveDefinition>();
        lineToCursor ??= string.Empty;
        var leadingWs = lineToCursor.Length - lineToCursor.TrimStart().Length;
        var afterWs = lineToCursor.Substring(leadingWs);
        var endsWithSpace = lineToCursor.Length > 0 && char.IsWhiteSpace(lineToCursor[lineToCursor.Length - 1]);
        var tokens = afterWs.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        // Still typing the magic name (first token, no trailing space).
        if (tokens.Count <= 1 && !endsWithSpace) {
            var partial = tokens.Count == 1 ? tokens[0] : "#!";
            var start = lineStart + lineToCursor.Length - partial.Length;
            return Build(start, partial.Length, definitions
                .Select(d => d.Selector)
                .Where(s => s.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .Select(s => new DirectiveCompletionItem { Label = s, Kind = "magic", Detail = "cell magic" }));
        }

        var definition = definitions.FirstOrDefault(d =>
            string.Equals(d.Selector, tokens[0], StringComparison.OrdinalIgnoreCase));
        var current = endsWithSpace ? "" : tokens[tokens.Count - 1];
        var prev = endsWithSpace ? tokens[tokens.Count - 1] : (tokens.Count >= 2 ? tokens[tokens.Count - 2] : "");
        var start2 = lineStart + lineToCursor.Length - current.Length;
        if (definition == null) {
            return new DirectiveCompletionList { ReplaceStart = start2, ReplaceLength = current.Length };
        }

        // A value position right after a flag with known values.
        var prevParameter = definition.Find(prev);
        if (prevParameter?.ValueRole != null && roleValues != null) {
            return Build(start2, current.Length, roleValues(prevParameter.ValueRole)
                .Where(n => n.StartsWith(current, StringComparison.OrdinalIgnoreCase))
                .Select(n => new DirectiveCompletionItem { Label = n, Kind = prevParameter.ValueRole, Detail = prevParameter.ValueRole }));
        }
        if (prevParameter?.EnumValues is { Count: > 0 } values) {
            return Build(start2, current.Length, values
                .Where(v => v.StartsWith(current, StringComparison.OrdinalIgnoreCase))
                .Select(v => new DirectiveCompletionItem { Label = v, Kind = "value", Detail = prevParameter.ValueDetail ?? "value" }));
        }

        // Otherwise offer the directive's flags (canonical names; forbidden ones stay unadvertised).
        return Build(start2, current.Length, definition.Parameters
            .Where(p => p.Kind != DirectiveParameterKind.Forbidden)
            .Select(p => p.Name)
            .Where(f => f.StartsWith(current, StringComparison.OrdinalIgnoreCase))
            .Select(f => new DirectiveCompletionItem { Label = f, Kind = "flag", Detail = "flag" }));
    }

    private static DirectiveCompletionList Build(int start, int length, IEnumerable<DirectiveCompletionItem> items) {
        var list = new DirectiveCompletionList { ReplaceStart = start, ReplaceLength = length };
        list.Items.AddRange(items.OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>
    /// Validates every directive line in a cell against the definitions: a bad
    /// flag surfaces as an editor diagnostic instead of a FormatException at run
    /// time. A line is checked when it starts with a known selector as a whole
    /// token (longest selector first, mirroring cell dispatch).
    /// </summary>
    public static IReadOnlyList<DiagnosticResult> Check(
        IReadOnlyList<DirectiveDefinition> definitions, string text) {
        var diagnostics = new List<DiagnosticResult>();
        if (definitions == null || definitions.Count == 0 || string.IsNullOrEmpty(text)) {
            return diagnostics;
        }
        var bySelectorLength = definitions.OrderByDescending(d => d.Selector.Length).ToList();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++) {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("#!", StringComparison.Ordinal)) {
                continue;
            }
            var definition = bySelectorLength.FirstOrDefault(d =>
                trimmed.StartsWith(d.Selector, StringComparison.OrdinalIgnoreCase) &&
                (trimmed.Length == d.Selector.Length || char.IsWhiteSpace(trimmed[d.Selector.Length])));
            if (definition == null) {
                continue; // an unknown #! line may belong to another language
            }
            try {
                DirectiveParser.Parse(definition, trimmed);
            } catch (FormatException e) {
                diagnostics.Add(new DiagnosticResult {
                    Line = i + 1,
                    Column = lines[i].Length - trimmed.Length + 1,
                    EndLine = i + 1,
                    EndColumn = lines[i].Length + 1,
                    Message = e.Message,
                });
            }
        }
        return diagnostics;
    }
}

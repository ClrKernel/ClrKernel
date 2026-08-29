using System;
using System.Collections.Generic;
using System.Linq;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Sql;
/// <summary>Hover info for a SQL token: markdown plus the span it covers.</summary>
public sealed class SqlHover {
    public string Markdown { get; set; }
    public int Start { get; set; }
    public int Length { get; set; }
}

/// <summary>A completion list with the span the inserted text replaces.</summary>
public sealed class SqlCompletion {
    public int ReplaceStart { get; set; }
    public int ReplaceLength { get; set; }
    public List<SqlCompletionItem> Items { get; } = new List<SqlCompletionItem>();
}

/// <summary>One completion candidate.</summary>
public sealed class SqlCompletionItem {
    public string Label { get; set; }
    public string InsertText { get; set; }
    public string Kind { get; set; } // keyword | function | type | operator
    public string Detail { get; set; }
}

/// <summary>Context that makes SQL completion aware of the session: the names of
/// registered connections and pipeline steps (used to complete <c>--connection</c>,
/// <c>-- needs</c>, etc.). Empty by default so the parser layer stays offline.</summary>
public sealed class SqlCompletionContext {
    public IReadOnlyList<string> ConnectionNames { get; set; } = new List<string>();
    public IReadOnlyList<string> StepNames { get; set; } = new List<string>();
    public static readonly SqlCompletionContext Empty = new SqlCompletionContext();
}

/// <summary>
/// SQL language service: context-aware completion that guides new authors through
/// the <c>#!sql-*</c> magics, their flags, connection names, and the
/// <c>-- step</c> / <c>-- needs</c> / <c>-- connections</c> directives — and falls
/// back to T-SQL keyword/function/type completion inside statements. Also provides
/// hover text. Offline except for the optional session context.
/// </summary>
public static class SqlLanguage {
    public static SqlCompletion Complete(string code, int offset) => Complete(code, offset, null);

    /// <summary>Completion in T-SQL — the dialect <c>#!sql</c> has always been.</summary>
    public static SqlCompletion Complete(string code, int offset, SqlCompletionContext context) =>
        Complete(code, offset, context, SqlVocabulary.TSql);

    /// <summary>Completion in one dialect: its own keywords, functions and types,
    /// and nobody else's.</summary>
    public static SqlCompletion Complete(
        string code, int offset, SqlCompletionContext context, SqlVocabulary vocabulary) {
        var ctx = context ?? SqlCompletionContext.Empty;
        var words = vocabulary ?? SqlVocabulary.TSql;
        var text = code ?? string.Empty;
        if (offset < 0) {
            offset = 0;
        }
        if (offset > text.Length) {
            offset = text.Length;
        }

        var lineStart = offset;
        while (lineStart > 0 && text[lineStart - 1] != '\n') {
            lineStart--;
        }
        var lineToCursor = text.Substring(lineStart, offset - lineStart);
        var left = lineToCursor.TrimStart();

        if (left.StartsWith("#!")) {
            return CompleteMagicLine(lineToCursor, lineStart, ctx);
        }
        if (left.StartsWith("--")) {
            return CompleteDirectiveLine(lineToCursor, lineStart, ctx);
        }
        return CompleteStatement(text, offset, words);
    }

    private static SqlCompletion CompleteStatement(string text, int offset, SqlVocabulary words) {
        var start = offset;
        while (start > 0 && IsWordChar(text[start - 1])) {
            start--;
        }
        var prefix = text.Substring(start, offset - start);

        var completion = new SqlCompletion { ReplaceStart = start, ReplaceLength = offset - start };
        IEnumerable<SqlCompletionItem> pool = words.Keywords
            .Select(k => new SqlCompletionItem { Label = k, InsertText = k, Kind = "keyword", Detail = "keyword" })
            .Concat(words.Functions.Select(f => new SqlCompletionItem { Label = f, InsertText = f, Kind = "function", Detail = "built-in function" }))
            .Concat(words.Types.Select(t => new SqlCompletionItem { Label = t, InsertText = t, Kind = "type", Detail = "data type" }));

        if (prefix.Length > 0) {
            pool = pool.Where(i => i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var item in pool.OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase)) {
            completion.Items.Add(item);
        }
        return completion;
    }

    // Completion on a "#!..." line, generated from the same DirectiveDefinition
    // tables the parsers bind against — the flag vocabulary cannot drift.
    private static SqlCompletion CompleteMagicLine(string lineToCursor, int lineStart, SqlCompletionContext ctx) {
        var generated = DirectiveCompletion.Complete(
            SqlDirectives.AllDefinitions, lineToCursor, lineStart,
            role => role == "connection" ? ctx.ConnectionNames : Enumerable.Empty<string>());
        var completion = new SqlCompletion { ReplaceStart = generated.ReplaceStart, ReplaceLength = generated.ReplaceLength };
        completion.Items.AddRange(generated.Items.Select(i =>
            new SqlCompletionItem { Label = i.Label, InsertText = i.Label, Kind = i.Kind, Detail = i.Detail }));
        return completion;
    }

    // Completion on a "-- ..." directive line: the directive keyword, then step
    // names after "-- needs" or connection names after "-- connections".
    private static SqlCompletion CompleteDirectiveLine(string lineToCursor, int lineStart, SqlCompletionContext ctx) {
        var leadingWs = lineToCursor.Length - lineToCursor.TrimStart().Length;
        var rest = lineToCursor.Substring(leadingWs + 2); // after "--"
        var restTrimStart = rest.TrimStart();
        var wsAfterDashes = rest.Length - restTrimStart.Length;
        var endsWithSpace = lineToCursor.Length > 0 && char.IsWhiteSpace(lineToCursor[lineToCursor.Length - 1]);
        var tokens = restTrimStart.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        // Typing the directive keyword itself (none complete yet).
        if (tokens.Count == 0 || (tokens.Count == 1 && !endsWithSpace)) {
            var partial = tokens.Count == 1 ? tokens[0] : "";
            var start = lineStart + leadingWs + 2 + wsAfterDashes;
            return Build(start, partial.Length, _directives
                .Where(dw => dw.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .Select(dw => Item(dw, "directive", "cell directive")));
        }

        var keyword = tokens[0].ToLowerInvariant();
        // Current partial word (after last space/comma).
        var lastDelim = Math.Max(lineToCursor.LastIndexOf(' '), Math.Max(lineToCursor.LastIndexOf('\t'), lineToCursor.LastIndexOf(',')));
        var current = endsWithSpace ? "" : lineToCursor.Substring(lastDelim + 1);
        var start2 = lineStart + lineToCursor.Length - current.Length;

        if (keyword == "needs" || keyword == "depends-on") {
            return Build(start2, current.Length, ctx.StepNames
                .Where(n => n.StartsWith(current, StringComparison.OrdinalIgnoreCase))
                .Select(n => Item(n, "step", "pipeline step")));
        }
        if (keyword == "connections" || keyword == "connection") {
            return Build(start2, current.Length, ctx.ConnectionNames
                .Where(n => n.StartsWith(current, StringComparison.OrdinalIgnoreCase))
                .Select(n => Item(n, "connection", "connection")));
        }
        return new SqlCompletion { ReplaceStart = start2, ReplaceLength = current.Length };
    }

    private static SqlCompletion Build(int start, int length, IEnumerable<SqlCompletionItem> items) {
        var completion = new SqlCompletion { ReplaceStart = start, ReplaceLength = length };
        foreach (var item in items.OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase)) {
            completion.Items.Add(item);
        }
        return completion;
    }

    private static SqlCompletionItem Item(string label, string kind, string detail) =>
        new SqlCompletionItem { Label = label, InsertText = label, Kind = kind, Detail = detail };

    public static SqlHover Hover(string code, int offset) => Hover(code, offset, SqlVocabulary.TSql);

    public static SqlHover Hover(string code, int offset, SqlVocabulary vocabulary) {
        var words = vocabulary ?? SqlVocabulary.TSql;
        var text = code ?? string.Empty;
        if (offset < 0 || offset > text.Length) {
            return null;
        }
        var start = offset;
        while (start > 0 && IsWordChar(text[start - 1])) {
            start--;
        }
        var end = offset;
        while (end < text.Length && IsWordChar(text[end])) {
            end++;
        }
        if (end <= start) {
            return null;
        }
        var word = text.Substring(start, end - start);
        var upper = word.ToUpperInvariant();

        string md = null;
        if (words.Docs.TryGetValue(upper, out var doc)) {
            md = doc;
        } else if (words.Keywords.Contains(upper)) {
            md = $"**{upper}** — {words.Label} keyword.";
        } else if (words.Functions.Contains(upper)) {
            md = $"**{upper}** — built-in {words.Label} function.";
        } else if (words.Types.Contains(upper)) {
            md = $"**{upper}** — {words.Label} data type.";
        }
        if (md == null) {
            return null;
        }
        return new SqlHover { Markdown = md, Start = start, Length = end - start };
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static readonly string[] _directives = { "connections", "step", "needs" };

}

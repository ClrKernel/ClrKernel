using System;
using System.Collections.Generic;
using System.Linq;

namespace ClrKernel.AnalysisServices;
/// <summary>Hover info for a DAX token: markdown plus the covered span.</summary>
public sealed class DaxHover {
    public string Markdown { get; set; }
    public int Start { get; set; }
    public int Length { get; set; }
}

/// <summary>A completion candidate.</summary>
public sealed class DaxCompletionItem {
    public string Label { get; set; }
    public string InsertText { get; set; }
    public string Kind { get; set; }
    public string Detail { get; set; }
}

/// <summary>A completion list with the span the inserted text replaces.</summary>
public sealed class DaxCompletion {
    public int ReplaceStart { get; set; }
    public int ReplaceLength { get; set; }
    public List<DaxCompletionItem> Items { get; } = new List<DaxCompletionItem>();
}

/// <summary>Session context for DAX completion: the names of registered cubes.</summary>
public sealed class DaxCompletionContext {
    public IReadOnlyList<string> CubeNames { get; set; } = new List<string>();
    public static readonly DaxCompletionContext Empty = new DaxCompletionContext();
}

/// <summary>
/// DAX language service: context-aware completion (the <c>#!dax</c>/<c>#!dax-connect</c>
/// magics and flags, cube names, the <c>-- connections</c> directive) plus DAX
/// keyword/function completion and hover. Offline except for the cube names.
/// </summary>
public static class DaxLanguage {
    public static DaxCompletion Complete(string code, int offset) => Complete(code, offset, null);

    public static DaxCompletion Complete(string code, int offset, DaxCompletionContext context) {
        var ctx = context ?? DaxCompletionContext.Empty;
        var text = code ?? string.Empty;
        offset = Math.Max(0, Math.Min(offset, text.Length));

        var lineStart = offset;
        while (lineStart > 0 && text[lineStart - 1] != '\n') {
            lineStart--;
        }
        var lineToCursor = text.Substring(lineStart, offset - lineStart);
        var left = lineToCursor.TrimStart();

        if (left.StartsWith("#!")) {
            return CompleteMagic(lineToCursor, lineStart, ctx);
        }
        if (left.StartsWith("--") || left.StartsWith("//")) {
            return CompleteDirective(lineToCursor, lineStart, ctx);
        }
        return CompleteDax(text, offset);
    }

    public static DaxHover Hover(string code, int offset) {
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
        var word = text.Substring(start, end - start).ToUpperInvariant();
        string md = null;
        if (_docs.TryGetValue(word, out var doc)) {
            md = doc;
        } else if (_functions.Contains(word)) {
            md = $"**{word}** — DAX function.";
        } else if (_keywords.Contains(word)) {
            md = $"**{word}** — DAX keyword.";
        }
        return md == null ? null : new DaxHover { Markdown = md, Start = start, Length = end - start };
    }

    private static DaxCompletion CompleteDax(string text, int offset) {
        var start = offset;
        while (start > 0 && IsWordChar(text[start - 1])) {
            start--;
        }
        var prefix = text.Substring(start, offset - start);
        IEnumerable<DaxCompletionItem> pool = _keywords
            .Select(k => Item(k, "keyword", "keyword"))
            .Concat(_functions.Select(f => Item(f, "function", "DAX function")));
        if (prefix.Length > 0) {
            pool = pool.Where(i => i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        return Build(start, offset - start, pool);
    }

    private static DaxCompletion CompleteMagic(string lineToCursor, int lineStart, DaxCompletionContext ctx) {
        var leadingWs = lineToCursor.Length - lineToCursor.TrimStart().Length;
        var afterWs = lineToCursor.Substring(leadingWs);
        var endsWithSpace = lineToCursor.Length > 0 && char.IsWhiteSpace(lineToCursor[lineToCursor.Length - 1]);
        var tokens = afterWs.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        if (tokens.Count <= 1 && !endsWithSpace) {
            var partial = tokens.Count == 1 ? tokens[0] : "#!";
            var start = lineStart + lineToCursor.Length - partial.Length;
            return Build(start, partial.Length, _magics
                .Where(m => m.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .Select(m => Item(m, "magic", "cell magic")));
        }

        var magic = tokens[0].ToLowerInvariant();
        var current = endsWithSpace ? "" : tokens[tokens.Count - 1];
        var prev = endsWithSpace ? tokens[tokens.Count - 1] : (tokens.Count >= 2 ? tokens[tokens.Count - 2] : "");
        var start2 = lineStart + lineToCursor.Length - current.Length;

        if (_connectionFlags.Contains(prev.ToLowerInvariant())) {
            return Build(start2, current.Length, ctx.CubeNames
                .Where(n => n.StartsWith(current, StringComparison.OrdinalIgnoreCase))
                .Select(n => Item(n, "cube", "cube")));
        }
        var flags = _magicFlags.TryGetValue(magic, out var f) ? f : Array.Empty<string>();
        return Build(start2, current.Length, flags
            .Where(fl => fl.StartsWith(current, StringComparison.OrdinalIgnoreCase))
            .Select(fl => Item(fl, "flag", "flag")));
    }

    private static DaxCompletion CompleteDirective(string lineToCursor, int lineStart, DaxCompletionContext ctx) {
        var leadingWs = lineToCursor.Length - lineToCursor.TrimStart().Length;
        var rest = lineToCursor.Substring(leadingWs + 2);
        var restTrimStart = rest.TrimStart();
        var wsAfter = rest.Length - restTrimStart.Length;
        var endsWithSpace = lineToCursor.Length > 0 && char.IsWhiteSpace(lineToCursor[lineToCursor.Length - 1]);
        var tokens = restTrimStart.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        if (tokens.Count == 0 || (tokens.Count == 1 && !endsWithSpace)) {
            var partial = tokens.Count == 1 ? tokens[0] : "";
            var start = lineStart + leadingWs + 2 + wsAfter;
            return Build(start, partial.Length, _directives
                .Where(d => d.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .Select(d => Item(d, "directive", "cell directive")));
        }

        var keyword = tokens[0].ToLowerInvariant();
        var lastDelim = Math.Max(lineToCursor.LastIndexOf(' '), Math.Max(lineToCursor.LastIndexOf('\t'), lineToCursor.LastIndexOf(',')));
        var current = endsWithSpace ? "" : lineToCursor.Substring(lastDelim + 1);
        var start2 = lineStart + lineToCursor.Length - current.Length;
        if (keyword is "connections" or "connection" or "cube") {
            return Build(start2, current.Length, ctx.CubeNames
                .Where(n => n.StartsWith(current, StringComparison.OrdinalIgnoreCase))
                .Select(n => Item(n, "cube", "cube")));
        }
        return new DaxCompletion { ReplaceStart = start2, ReplaceLength = current.Length };
    }

    private static DaxCompletion Build(int start, int length, IEnumerable<DaxCompletionItem> items) {
        var completion = new DaxCompletion { ReplaceStart = start, ReplaceLength = length };
        foreach (var item in items.OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase)) {
            completion.Items.Add(item);
        }
        return completion;
    }

    private static DaxCompletionItem Item(string label, string kind, string detail) =>
        new DaxCompletionItem { Label = label, InsertText = label, Kind = kind, Detail = detail };

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static readonly string[] _magics = { "#!dax", "#!dax-connect" };
    private static readonly string[] _directives = { "connections", "cube" };
    private static readonly HashSet<string> _connectionFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "--connections", "--connection", "--cube", "-c",
    };
    private static readonly Dictionary<string, string[]> _magicFlags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
        ["#!dax"] = new[] { "--connections" },
        ["#!dax-connect"] = new[] {
            "--name", "--server", "--database", "--auth", "--user", "--secret",
            "--fabric", "--workspace", "--model", "--azure-as", "--connection-string", "--default",
        },
    };

    private static readonly HashSet<string> _keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "EVALUATE", "DEFINE", "MEASURE", "VAR", "RETURN", "ORDER", "BY", "START", "AT",
        "ASC", "DESC", "TABLE", "COLUMN", "IN", "NOT", "TRUE", "FALSE", "BLANK",
    };

    private static readonly HashSet<string> _functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "CALCULATE", "CALCULATETABLE", "FILTER", "ALL", "ALLEXCEPT", "ALLSELECTED", "VALUES", "DISTINCT",
        "RELATED", "RELATEDTABLE", "SUM", "SUMX", "AVERAGE", "AVERAGEX", "MIN", "MINX", "MAX", "MAXX",
        "COUNT", "COUNTA", "COUNTROWS", "COUNTX", "DISTINCTCOUNT", "DIVIDE", "IF", "SWITCH", "AND", "OR",
        "SUMMARIZE", "SUMMARIZECOLUMNS", "ADDCOLUMNS", "SELECTCOLUMNS", "TOPN", "RANKX", "ROW", "GROUPBY",
        "CROSSJOIN", "UNION", "EXCEPT", "INTERSECT", "NATURALINNERJOIN", "TREATAS", "USERELATIONSHIP",
        "DATEADD", "DATESBETWEEN", "DATESYTD", "DATESMTD", "DATESQTD", "TOTALYTD", "TOTALMTD", "TOTALQTD",
        "SAMEPERIODLASTYEAR", "PARALLELPERIOD", "PREVIOUSMONTH", "PREVIOUSYEAR", "ENDOFMONTH", "STARTOFMONTH",
        "FORMAT", "CONCATENATE", "CONCATENATEX", "LEFT", "RIGHT", "MID", "LEN", "UPPER", "LOWER", "TRIM",
        "ISBLANK", "ISFILTERED", "ISCROSSFILTERED", "HASONEVALUE", "SELECTEDVALUE", "LOOKUPVALUE", "EARLIER",
        "CONTAINS", "CONTAINSROW", "EXCEPT", "GENERATE", "GENERATEALL", "VAR",
    };

    private static readonly Dictionary<string, string> _docs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["EVALUATE"] = "**EVALUATE** `<table-expression>` — returns a table (the top-level DAX query statement).",
        ["CALCULATE"] = "**CALCULATE(expression, filter1, filter2, …)** — evaluates an expression in a modified filter context.",
        ["FILTER"] = "**FILTER(table, condition)** — returns the rows of `table` where `condition` is true.",
        ["SUMMARIZECOLUMNS"] = "**SUMMARIZECOLUMNS(groupBy…, filters…, name, expression, …)** — groups and aggregates for a query.",
        ["DIVIDE"] = "**DIVIDE(numerator, denominator [, alternate])** — safe division (returns `alternate`/BLANK on divide-by-zero).",
        ["DATEADD"] = "**DATEADD(dates, number, interval)** — shifts a set of dates by an interval (DAY/MONTH/QUARTER/YEAR).",
        ["TOPN"] = "**TOPN(n, table, orderBy, [order])** — the top `n` rows of a table by an expression.",
    };
}

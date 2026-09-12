using System;
using System.Collections.Generic;
using System.Linq;

namespace ClrKernel.Language.Kql;

/// <summary>
/// The static half of KQL editor features: the tabular operators, the keywords,
/// the functions people reach for, each with one line of help. Offline. Table and
/// column names come from the session's schema and are folded in by the services.
/// </summary>
public static class KqlLanguage {
    /// <summary>Operators that follow a <c>|</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> Operators = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["where"] = "Filters rows by a predicate: `| where Col > 5`.",
        ["summarize"] = "Aggregates: `| summarize count(), avg(X) by Key`.",
        ["project"] = "Keeps (and renames or computes) the listed columns.",
        ["project-away"] = "Drops the listed columns.",
        ["project-keep"] = "Keeps the listed columns, in the table's order.",
        ["project-rename"] = "Renames columns: `| project-rename New = Old`.",
        ["project-reorder"] = "Moves the listed columns to the front.",
        ["extend"] = "Adds computed columns: `| extend Total = Price * Qty`.",
        ["take"] = "Returns up to N rows, no particular order.",
        ["limit"] = "Alias of take.",
        ["top"] = "The first N rows by an ordering: `| top 10 by Count desc`.",
        ["top-nested"] = "Hierarchical top-N.",
        ["sort"] = "Sorts: `| sort by Col desc`.",
        ["order"] = "Alias of sort.",
        ["distinct"] = "Distinct combinations of the listed columns.",
        ["count"] = "The number of rows.",
        ["join"] = "Joins with another table: `| join kind=inner (T2) on Key`.",
        ["union"] = "Concatenates tables.",
        ["lookup"] = "A join that only adds columns from a lookup table.",
        ["mv-expand"] = "Expands a dynamic array or bag into rows.",
        ["mv-apply"] = "Applies a subquery to each dynamic value.",
        ["parse"] = "Extracts values from a string column by pattern.",
        ["parse-where"] = "Like parse, dropping rows that do not match.",
        ["render"] = "Renders a chart: `| render timechart`.",
        ["serialize"] = "Marks the row order as significant (for row_number, prev, next).",
        ["make-series"] = "A time series per group: `| make-series count() on Ts step 1h`.",
        ["evaluate"] = "Invokes a plugin: `| evaluate bag_unpack(Props)`.",
        ["invoke"] = "Calls a tabular function with the table as its first argument.",
        ["as"] = "Names the intermediate result.",
        ["getschema"] = "The columns and their types.",
        ["sample"] = "N rows sampled at random.",
        ["scan"] = "Sequence matching across rows.",
        ["fork"] = "Runs several subqueries over the same input.",
        ["search"] = "Full-text search across columns or tables.",
        ["find"] = "Finds rows matching a predicate across tables.",
        ["range"] = "Generates a table of values: `range x from 1 to 10 step 1`.",
        ["print"] = "One row of scalar expressions: `print x = 1`.",
        ["datatable"] = "An inline table literal.",
        ["externaldata"] = "Reads a table from external storage.",
        ["materialize"] = "Caches a subquery's result for reuse within the query.",
    };

    public static readonly IReadOnlyDictionary<string, string> Functions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["count()"] = "Aggregate: number of rows.",
        ["countif(predicate)"] = "Aggregate: rows where the predicate holds.",
        ["dcount(expr)"] = "Aggregate: estimated distinct count.",
        ["sum(expr)"] = "Aggregate: sum.",
        ["avg(expr)"] = "Aggregate: average.",
        ["min(expr)"] = "Aggregate: minimum.",
        ["max(expr)"] = "Aggregate: maximum.",
        ["percentile(expr, p)"] = "Aggregate: the p-th percentile.",
        ["make_list(expr)"] = "Aggregate: a dynamic array of the values.",
        ["make_set(expr)"] = "Aggregate: a dynamic array of the distinct values.",
        ["arg_max(expr, *)"] = "Aggregate: the row where expr is greatest.",
        ["arg_min(expr, *)"] = "Aggregate: the row where expr is smallest.",
        ["bin(value, roundTo)"] = "Rounds down to a multiple of roundTo — the usual time bucket.",
        ["ago(timespan)"] = "now() minus the timespan: `ago(1d)`.",
        ["now()"] = "The current UTC time.",
        ["startofday(datetime)"] = "The day's start.",
        ["startofmonth(datetime)"] = "The month's start.",
        ["datetime_diff(period, a, b)"] = "Whole periods between two datetimes.",
        ["format_datetime(dt, format)"] = "Formats a datetime.",
        ["tostring(expr)"] = "Converts to string.",
        ["toint(expr)"] = "Converts to int (null when it cannot).",
        ["tolong(expr)"] = "Converts to long.",
        ["todouble(expr)"] = "Converts to real.",
        ["todatetime(expr)"] = "Converts to datetime.",
        ["totimespan(expr)"] = "Converts to timespan.",
        ["todynamic(expr)"] = "Parses JSON text into a dynamic value.",
        ["parse_json(expr)"] = "Parses JSON text into a dynamic value.",
        ["strcat(a, b, ...)"] = "Concatenates strings.",
        ["strlen(s)"] = "Length of a string.",
        ["substring(s, start, length)"] = "A substring.",
        ["split(s, delimiter)"] = "Splits into a dynamic array.",
        ["replace_string(s, lookup, rewrite)"] = "Replaces every occurrence.",
        ["extract(regex, captureGroup, s)"] = "The regex capture group's match.",
        ["tolower(s)"] = "Lower case.",
        ["toupper(s)"] = "Upper case.",
        ["trim(regex, s)"] = "Trims matches from both ends.",
        ["isempty(s)"] = "True for an empty or null string.",
        ["isnotempty(s)"] = "True for a non-empty string.",
        ["isnull(expr)"] = "True for null.",
        ["iff(cond, a, b)"] = "a when cond, else b.",
        ["case(cond1, a, cond2, b, else)"] = "The first branch whose condition holds.",
        ["coalesce(a, b, ...)"] = "The first non-null argument.",
        ["array_length(arr)"] = "Elements in a dynamic array.",
        ["pack_array(a, b, ...)"] = "A dynamic array of the arguments.",
        ["bag_pack(k1, v1, ...)"] = "A dynamic property bag.",
        ["row_number()"] = "The row's index (after serialize).",
        ["prev(column)"] = "The previous row's value (after serialize).",
        ["next(column)"] = "The next row's value (after serialize).",
        ["hash(expr)"] = "A hash of the value.",
        ["rand()"] = "A random real in [0, 1).",
        ["geo_distance_2points(lon1, lat1, lon2, lat2)"] = "Distance in metres.",
    };

    public static readonly IReadOnlyList<string> Keywords = new[] {
        "let", "set", "by", "on", "kind", "asc", "desc", "nulls first", "nulls last", "between", "in", "!in",
        "has", "!has", "has_any", "has_all", "contains", "!contains", "startswith", "!startswith", "endswith",
        "!endswith", "matches regex", "and", "or", "not", "true", "false", "null", "inner", "outer", "leftouter",
        "rightouter", "fullouter", "leftanti", "rightanti", "leftsemi", "rightsemi", "innerunique", "step", "from",
        "to", "default", "with", "toscalar", "typeof", "bool", "int", "long", "real", "decimal", "string",
        "datetime", "timespan", "dynamic", "guid",
    };

    /// <summary>Help for the word under the cursor: an operator, a function, or nothing.</summary>
    public static string Describe(string word) {
        if (string.IsNullOrWhiteSpace(word)) {
            return null;
        }
        if (Operators.TryGetValue(word, out var op)) {
            return $"```kql\n| {word}\n```\n\n{op}";
        }
        var function = Functions.FirstOrDefault(f => string.Equals(NameOf(f.Key), word, StringComparison.OrdinalIgnoreCase));
        return function.Key == null ? null : $"```kql\n{function.Key}\n```\n\n{function.Value}";
    }

    /// <summary>The bare name of a function signature: <c>bin(value, roundTo)</c> → <c>bin</c>.</summary>
    public static string NameOf(string signature) {
        var paren = signature.IndexOf('(');
        return paren < 0 ? signature : signature.Substring(0, paren);
    }
}

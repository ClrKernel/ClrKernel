using System;
using System.Collections.Generic;
using System.Linq;

namespace ClrKernel.Sql.Etl;
/// <summary>A parsed <c>#!sql-bulk</c> magic.</summary>
public sealed class BulkDirective {
    public string FromConnection { get; set; }
    public string ToConnection { get; set; }
    public string Query { get; set; }
    public string FromTable { get; set; }
    public string Table { get; set; }
    public BulkCopyOptions Options { get; } = new BulkCopyOptions();

    /// <summary>The SELECT used to read the source (explicit query or SELECT * FROM table).</summary>
    public string SourceQuery =>
        !string.IsNullOrWhiteSpace(Query) ? Query : "SELECT * FROM " + SqlIdentifier.Quote(FromTable);
}

/// <summary>A parsed <c>#!sql-merge</c> magic.</summary>
public sealed class MergeDirective {
    public string Connection { get; set; }
    public MergeSpec Spec { get; } = new MergeSpec();
}

/// <summary>Parses the <c>#!sql-bulk</c> and <c>#!sql-merge</c> magics.</summary>
public static class SqlEtlDirectives {
    public static BulkDirective ParseBulk(string line) {
        var tokens = Tokenize(line, "#!sql-bulk");
        var d = new BulkDirective();
        for (var i = 0; i < tokens.Count; i++) {
            var t = tokens[i];
            string Next() => i + 1 < tokens.Count ? tokens[++i] : throw new FormatException($"Missing value for {t}.");
            switch (t.ToLowerInvariant()) {
                case "--from": d.FromConnection = Next(); break;
                case "--to": d.ToConnection = Next(); break;
                case "--query": case "-q": d.Query = Next(); break;
                case "--from-table": d.FromTable = Next(); break;
                case "--table": case "--to-table": d.Table = Next(); break;
                case "--batch-size": d.Options.BatchSize = ParseInt(Next(), t); break;
                case "--timeout": d.Options.TimeoutSeconds = ParseInt(Next(), t); break;
                case "--notify-after": d.Options.NotifyAfter = ParseInt(Next(), t); break;
                case "--truncate": d.Options.TruncateFirst = true; break;
                case "--create": case "--create-if-missing": d.Options.CreateIfMissing = true; break;
                case "--no-lock": d.Options.TableLock = false; break;
                case "--keep-identity": d.Options.KeepIdentity = true; break;
                case "--keep-nulls": d.Options.KeepNulls = true; break;
                case "--no-progress": d.Options.ShowProgress = false; break;
                case "--map": {
                        var kv = Next();
                        var eq = kv.IndexOf('=');
                        if (eq <= 0) {
                            throw new FormatException($"--map expects source=dest, got '{kv}'.");
                        }
                        d.Options.ColumnMappings[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                        break;
                    }
                default: throw new FormatException($"Unknown #!sql-bulk flag '{t}'.");
            }
        }
        if (string.IsNullOrWhiteSpace(d.FromConnection)) {
            throw new FormatException("#!sql-bulk requires --from.");
        }
        if (string.IsNullOrWhiteSpace(d.Table)) {
            throw new FormatException("#!sql-bulk requires --table.");
        }
        if (string.IsNullOrWhiteSpace(d.Query) && string.IsNullOrWhiteSpace(d.FromTable)) {
            throw new FormatException("#!sql-bulk requires --query or --from-table.");
        }
        if (string.IsNullOrWhiteSpace(d.ToConnection)) {
            d.ToConnection = d.FromConnection;
        }
        return d;
    }

    public static MergeDirective ParseMerge(string line) {
        var tokens = Tokenize(line, "#!sql-merge");
        var d = new MergeDirective();
        for (var i = 0; i < tokens.Count; i++) {
            var t = tokens[i];
            string Next() => i + 1 < tokens.Count ? tokens[++i] : throw new FormatException($"Missing value for {t}.");
            switch (t.ToLowerInvariant()) {
                case "--connection": case "-c": d.Connection = Next(); break;
                case "--target": d.Spec.Target = Next(); break;
                case "--source": d.Spec.Source = Next(); break;
                case "--on": d.Spec.KeyColumns = SplitList(Next()); break;
                case "--update": d.Spec.UpdateColumns = SplitList(Next()); break;
                case "--insert": d.Spec.InsertColumns = SplitList(Next()); break;
                case "--delete": d.Spec.DeleteNotMatchedBySource = true; break;
                case "--source-is-query": d.Spec.SourceIsQuery = true; break;
                default: throw new FormatException($"Unknown #!sql-merge flag '{t}'.");
            }
        }
        if (string.IsNullOrWhiteSpace(d.Spec.Target)) {
            throw new FormatException("#!sql-merge requires --target.");
        }
        if (string.IsNullOrWhiteSpace(d.Spec.Source)) {
            throw new FormatException("#!sql-merge requires --source.");
        }
        if (d.Spec.KeyColumns == null || d.Spec.KeyColumns.Count == 0) {
            throw new FormatException("#!sql-merge requires --on <key[,key...]>.");
        }
        return d;
    }

    private static List<string> Tokenize(string line, string selector) {
        var trimmed = (line ?? string.Empty).TrimStart();
        if (trimmed.StartsWith(selector, StringComparison.OrdinalIgnoreCase)) {
            trimmed = trimmed.Substring(selector.Length);
        }
        return SqlDirectives.Tokenize(trimmed);
    }

    private static List<string> SplitList(string value) =>
        value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private static int ParseInt(string value, string flag) =>
        int.TryParse(value, out var n) ? n : throw new FormatException($"{flag} expects a number, got '{value}'.");
}

using System;
using System.Collections.Generic;
using System.Linq;
using ClrKernel.Core.Scripting;
using ClrKernel.Database.Provider.SqlServer;

namespace ClrKernel.Language.Sql;
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
    /// <summary>The declarative shape of <c>#!sql-bulk</c>.</summary>
    public static readonly DirectiveDefinition BulkDefinition = new() {
        Selector = "#!sql-bulk",
        Description = "Bulk-copies rows between connections.",
        Parameters = new DirectiveParameter[] {
            new() { Name = "--from", Required = true, ValueRole = "connection", Description = "Source connection name." },
            new() { Name = "--to", ValueRole = "connection", Description = "Destination connection (defaults to --from)." },
            new() { Name = "--query", Aliases = new[] { "-q" }, Description = "Source SELECT." },
            new() { Name = "--from-table", Description = "Source table (SELECT * alternative to --query)." },
            new() { Name = "--table", Aliases = new[] { "--to-table" }, Required = true, Description = "Destination table." },
            new() { Name = "--batch-size", Description = "Rows per batch." },
            new() { Name = "--timeout", Description = "Bulk-copy timeout in seconds." },
            new() { Name = "--notify-after", Description = "Progress notification interval in rows." },
            new() { Name = "--truncate", Kind = DirectiveParameterKind.Flag, Description = "Truncate the destination first." },
            new() { Name = "--create", Aliases = new[] { "--create-if-missing" }, Kind = DirectiveParameterKind.Flag, Description = "Create the destination table when missing." },
            new() { Name = "--no-lock", Kind = DirectiveParameterKind.Flag, Description = "Skip the table lock." },
            new() { Name = "--keep-identity", Kind = DirectiveParameterKind.Flag, Description = "Preserve identity values." },
            new() { Name = "--keep-nulls", Kind = DirectiveParameterKind.Flag, Description = "Preserve NULLs over column defaults." },
            new() { Name = "--no-progress", Kind = DirectiveParameterKind.Flag, Description = "Suppress the progress bar." },
            new() { Name = "--map", Kind = DirectiveParameterKind.KeyValue, Repeatable = true, KeyValueHint = "source=dest", Description = "Column mapping (source=dest)." },
        },
    };

    /// <summary>The declarative shape of <c>#!sql-merge</c>.</summary>
    public static readonly DirectiveDefinition MergeDefinition = new() {
        Selector = "#!sql-merge",
        Description = "MERGEs a source table or query into a target table.",
        Parameters = new DirectiveParameter[] {
            new() { Name = "--connection", Aliases = new[] { "-c" }, ValueRole = "connection", Description = "Connection name." },
            new() { Name = "--target", Required = true, Description = "Target table." },
            new() { Name = "--source", Required = true, Description = "Source table (or query with --source-is-query)." },
            new() { Name = "--on", Required = true, RequiredLabel = "--on <key[,key...]>", Description = "Key columns (comma-separated)." },
            new() { Name = "--update", Description = "Columns to update (comma-separated)." },
            new() { Name = "--insert", Description = "Columns to insert (comma-separated)." },
            new() { Name = "--delete", Kind = DirectiveParameterKind.Flag, Description = "Delete target rows not matched by source." },
            new() { Name = "--source-is-query", Kind = DirectiveParameterKind.Flag, Description = "Treat --source as a query." },
        },
    };

    public static BulkDirective ParseBulk(string line) {
        var args = DirectiveParser.Parse(BulkDefinition, line);
        var d = new BulkDirective {
            FromConnection = args.Get("--from"),
            ToConnection = args.Get("--to"),
            Query = args.Get("--query"),
            FromTable = args.Get("--from-table"),
            Table = args.Get("--table"),
        };
        if (args.Has("--batch-size")) {
            d.Options.BatchSize = ParseInt(args.Get("--batch-size"), "--batch-size");
        }
        if (args.Has("--timeout")) {
            d.Options.TimeoutSeconds = ParseInt(args.Get("--timeout"), "--timeout");
        }
        if (args.Has("--notify-after")) {
            d.Options.NotifyAfter = ParseInt(args.Get("--notify-after"), "--notify-after");
        }
        if (args.Has("--truncate")) {
            d.Options.TruncateFirst = true;
        }
        if (args.Has("--create")) {
            d.Options.CreateIfMissing = true;
        }
        if (args.Has("--no-lock")) {
            d.Options.TableLock = false;
        }
        if (args.Has("--keep-identity")) {
            d.Options.KeepIdentity = true;
        }
        if (args.Has("--keep-nulls")) {
            d.Options.KeepNulls = true;
        }
        if (args.Has("--no-progress")) {
            d.Options.ShowProgress = false;
        }
        foreach (var kv in args.KeyValues("--map")) {
            d.Options.ColumnMappings[kv.Key] = kv.Value;
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
        var args = DirectiveParser.Parse(MergeDefinition, line);
        var d = new MergeDirective { Connection = args.Get("--connection") };
        d.Spec.Target = args.Get("--target");
        d.Spec.Source = args.Get("--source");
        d.Spec.KeyColumns = SplitList(args.Get("--on"));
        if (d.Spec.KeyColumns.Count == 0) {
            throw new FormatException("#!sql-merge requires --on <key[,key...]>.");
        }
        if (args.Has("--update")) {
            d.Spec.UpdateColumns = SplitList(args.Get("--update"));
        }
        if (args.Has("--insert")) {
            d.Spec.InsertColumns = SplitList(args.Get("--insert"));
        }
        d.Spec.DeleteNotMatchedBySource = args.Has("--delete");
        d.Spec.SourceIsQuery = args.Has("--source-is-query");
        return d;
    }

    private static List<string> SplitList(string value) =>
        value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private static int ParseInt(string value, string flag) =>
        int.TryParse(value, out var n) ? n : throw new FormatException($"{flag} expects a number, got '{value}'.");
}

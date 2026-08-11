using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

namespace ClrKernel.DataEngineering;

/// <summary>What a <see cref="TableAction"/> does to its target table.</summary>
public enum TableActionKind {
    /// <summary>Append rows from the source.</summary>
    Insert,

    /// <summary>Delete rows, all of them or those matching <see cref="TableAction.Where"/>.</summary>
    Delete,

    /// <summary>Empty the table.</summary>
    Truncate,

    /// <summary>Upsert from the source on <see cref="TableAction.KeyColumns"/>.</summary>
    Merge,

    /// <summary>Empty the table, then load the source into it.</summary>
    TruncateInsert,

    /// <summary>Delete a segment (all rows, or those matching the predicate), then load the source.</summary>
    DeleteInsert,
}

/// <summary>
/// Where the rows for a load come from. Deliberately narrow: either a query the target's own
/// engine can run, or rows already in memory. A provider is free to recognise
/// <see cref="Kind"/> == <see cref="TableSourceKind.Query"/> and push the whole load down to the
/// server (SQL Server's <c>INSERT … SELECT</c>) rather than streaming rows through the client.
/// </summary>
public sealed class TableSource {
    private readonly Func<IDataReader> _openReader;

    private TableSource(TableSourceKind kind, string connection, string text, Func<IDataReader> openReader) {
        Kind = kind;
        Connection = connection;
        Text = text;
        _openReader = openReader;
    }

    public TableSourceKind Kind { get; }

    /// <summary>The named connection the query runs against; null when the rows are in memory.</summary>
    public string Connection { get; }

    /// <summary>The query, or the source table name. Null when the rows are in memory.</summary>
    public string Text { get; }

    /// <summary>Rows from a query on a named connection.</summary>
    public static TableSource Query(string connection, string sql) {
        if (string.IsNullOrWhiteSpace(sql)) {
            throw new ArgumentException("A query source needs SQL.", nameof(sql));
        }
        return new TableSource(TableSourceKind.Query, connection, sql, null);
    }

    /// <summary>Every row of a table on a named connection.</summary>
    public static TableSource Table(string connection, string table) {
        if (string.IsNullOrWhiteSpace(table)) {
            throw new ArgumentException("A table source needs a table name.", nameof(table));
        }
        return new TableSource(TableSourceKind.Table, connection, table, null);
    }

    /// <summary>
    /// Rows from an already-open or lazily-opened reader. The factory is called once per
    /// execution, so an action can be retried.
    /// </summary>
    public static TableSource Rows(Func<IDataReader> openReader) {
        if (openReader is null) {
            throw new ArgumentNullException(nameof(openReader));
        }
        return new TableSource(TableSourceKind.Rows, null, null, openReader);
    }

    /// <summary>Opens the in-memory reader. Only valid for <see cref="TableSourceKind.Rows"/>.</summary>
    public IDataReader OpenReader() =>
        _openReader != null
            ? _openReader()
            : throw new InvalidOperationException($"A {Kind} source has no client-side reader; the provider runs it on the server.");

    public override string ToString() =>
        Kind == TableSourceKind.Rows ? "rows"
            : string.IsNullOrEmpty(Connection) ? Text
            : $"{Connection}: {Text}";
}

public enum TableSourceKind { Query, Table, Rows }

/// <summary>
/// A declarative description of one load into one table — <em>what</em> should happen, never how.
/// A provider turns it into whatever its engine does best: SQL Server into <c>SqlBulkCopy</c> plus
/// a <c>MERGE</c>, Fabric into Parquet staged on OneLake then delete-and-insert, Oracle into its
/// own direct-path load. Construct one through the factory methods so an action can't be built in
/// a shape its kind doesn't allow.
/// </summary>
/// <remarks>
/// This type is data, not behaviour: it can be built, logged, compared and put in a pipeline step
/// without any database being reachable. <see cref="ITableActionTarget"/> is the half that executes.
/// </remarks>
public sealed class TableAction {
    private TableAction(TableActionKind kind, string table, TableSource source, string where, IReadOnlyList<string> keyColumns) {
        Kind = kind;
        Table = table;
        Source = source;
        Where = where;
        KeyColumns = keyColumns ?? Array.Empty<string>();
    }

    public TableActionKind Kind { get; }

    /// <summary>The table being written to.</summary>
    public string Table { get; }

    /// <summary>Where the rows come from; null for <see cref="TableActionKind.Delete"/> and
    /// <see cref="TableActionKind.Truncate"/>.</summary>
    public TableSource Source { get; }

    /// <summary>
    /// An optional predicate scoping the delete half of the action, written in the target engine's
    /// dialect. Null means "every row".
    /// </summary>
    public string Where { get; }

    /// <summary>The columns that identify a row, for <see cref="TableActionKind.Merge"/>.</summary>
    public IReadOnlyList<string> KeyColumns { get; }

    /// <summary>True when the action removes rows before (or instead of) writing any.</summary>
    public bool DeletesRows =>
        Kind is TableActionKind.Delete or TableActionKind.Truncate
             or TableActionKind.TruncateInsert or TableActionKind.DeleteInsert;

    /// <summary>Append the source's rows.</summary>
    public static TableAction Insert(string table, TableSource source) =>
        new TableAction(TableActionKind.Insert, Named(table), Required(source), null, null);

    /// <summary>Delete rows — all of them, or those matching <paramref name="where"/>.</summary>
    public static TableAction Delete(string table, string where = null) =>
        new TableAction(TableActionKind.Delete, Named(table), null, Trimmed(where), null);

    /// <summary>Empty the table.</summary>
    public static TableAction Truncate(string table) =>
        new TableAction(TableActionKind.Truncate, Named(table), null, null, null);

    /// <summary>
    /// Upsert the source on <paramref name="keyColumns"/>. <paramref name="where"/> scopes which
    /// existing rows participate, for providers that support it.
    /// </summary>
    public static TableAction Merge(string table, TableSource source, IEnumerable<string> keyColumns, string where = null) {
        var keys = (keyColumns ?? Enumerable.Empty<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList();
        if (keys.Count == 0) {
            throw new ArgumentException("A merge needs at least one key column.", nameof(keyColumns));
        }
        return new TableAction(TableActionKind.Merge, Named(table), Required(source), Trimmed(where), keys);
    }

    /// <summary>Empty the table, then load the source — a full reload.</summary>
    public static TableAction TruncateInsert(string table, TableSource source) =>
        new TableAction(TableActionKind.TruncateInsert, Named(table), Required(source), null, null);

    /// <summary>
    /// Delete a segment, then load the source into it — the incremental-reload shape (e.g.
    /// "replace everything for 2026"). A null <paramref name="where"/> deletes every row, which
    /// differs from <see cref="TruncateInsert"/> only in being a logged delete.
    /// </summary>
    public static TableAction DeleteInsert(string table, TableSource source, string where = null) =>
        new TableAction(TableActionKind.DeleteInsert, Named(table), Required(source), Trimmed(where), null);

    public override string ToString() {
        var scope = Where is null ? string.Empty : $" where {Where}";
        var from = Source is null ? string.Empty : $" from {Source}";
        var keys = KeyColumns.Count == 0 ? string.Empty : $" on {string.Join(", ", KeyColumns)}";
        return $"{Kind} {Table}{keys}{scope}{from}";
    }

    private static string Named(string table) =>
        string.IsNullOrWhiteSpace(table)
            ? throw new ArgumentException("A table action needs a target table.", nameof(table))
            : table.Trim();

    private static TableSource Required(TableSource source) =>
        source ?? throw new ArgumentNullException(nameof(source), "This action loads rows and needs a source.");

    private static string Trimmed(string where) =>
        string.IsNullOrWhiteSpace(where) ? null : where.Trim();
}

/// <summary>The outcome of one executed <see cref="TableAction"/>.</summary>
public sealed class TableActionResult {
    public TableActionResult(TableAction action) {
        Action = action;
    }

    public TableAction Action { get; }

    /// <summary>Rows written by the load half of the action.</summary>
    public long RowsWritten { get; set; }

    /// <summary>Rows removed by the delete/truncate half. -1 when the engine can't say
    /// (<c>TRUNCATE</c> generally can't).</summary>
    public long RowsDeleted { get; set; }

    public long ElapsedMs { get; set; }

    /// <summary>How the provider actually did it — e.g. "SqlBulkCopy + MERGE". Useful because the
    /// whole point of the model is that this differs per engine.</summary>
    public string Strategy { get; set; }

    public override string ToString() {
        var parts = new List<string>();
        if (RowsDeleted > 0) {
            parts.Add($"{RowsDeleted} deleted");
        }
        if (RowsWritten > 0) {
            parts.Add($"{RowsWritten} written");
        }
        if (parts.Count == 0) {
            parts.Add("no rows");
        }
        parts.Add($"{ElapsedMs} ms");
        var how = string.IsNullOrEmpty(Strategy) ? string.Empty : $" via {Strategy}";
        return $"{Action.Table}: {string.Join(" • ", parts)}{how}";
    }
}

/// <summary>
/// A destination that can carry out <see cref="TableAction"/>s. Each provider implements this the
/// way its engine loads data — that difference is the reason the model exists.
/// </summary>
public interface ITableActionTarget {
    /// <summary>
    /// Executes the action, or throws <see cref="NotSupportedException"/> if this engine has no way
    /// to perform it.
    /// </summary>
    TableActionResult Execute(TableAction action);

    /// <summary>
    /// Whether this target can perform <paramref name="kind"/>, so a caller can choose a different
    /// shape rather than discovering the gap mid-pipeline.
    /// </summary>
    bool Supports(TableActionKind kind);
}

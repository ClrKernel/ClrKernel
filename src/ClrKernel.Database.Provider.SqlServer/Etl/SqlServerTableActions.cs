using System;
using ClrKernel.DataEngineering;

namespace ClrKernel.Database.Provider.SqlServer;

/// <summary>
/// The pure half of SQL Server's <see cref="ITableActionTarget"/> implementation: turning a
/// declarative <see cref="TableAction"/> into the T-SQL (or the <see cref="MergeSpec"/>) that
/// carries it out. Separated from <see cref="SqlServerTableTarget"/> so the translation is unit
/// tested without a server — the execution half can't be.
/// </summary>
public static class SqlServerTableActions {
    /// <summary>
    /// The statement that removes rows for <paramref name="action"/>, or null when the action
    /// doesn't delete anything.
    /// </summary>
    /// <remarks>
    /// <c>TRUNCATE</c> is used only where the action asks for it. <see cref="TableActionKind.DeleteInsert"/>
    /// with no predicate stays a <c>DELETE</c> rather than being "optimised" into a truncate: they
    /// differ in logging, identity reseed, and whether a trigger fires, and silently swapping them
    /// would be a behaviour change the caller didn't ask for.
    /// </remarks>
    public static string DeleteStatement(TableAction action) {
        if (action is null) {
            throw new ArgumentNullException(nameof(action));
        }

        switch (action.Kind) {
            case TableActionKind.Truncate:
            case TableActionKind.TruncateInsert:
                return $"truncate table {action.Table}";
            case TableActionKind.Delete:
            case TableActionKind.DeleteInsert:
                return action.Where is null
                    ? $"delete from {action.Table}"
                    : $"delete from {action.Table} where {action.Where}";
            default:
                return null;
        }
    }

    /// <summary>The <c>SELECT</c> that reads the action's source, for the reader-based load path.</summary>
    public static string SourceQuery(TableSource source) {
        if (source is null) {
            throw new ArgumentNullException(nameof(source));
        }
        return source.Kind switch {
            TableSourceKind.Query => source.Text,
            TableSourceKind.Table => $"select * from {source.Text}",
            _ => throw new InvalidOperationException(
                $"A {source.Kind} source is read directly, not through a query."),
        };
    }

    /// <summary>
    /// Maps a <see cref="TableActionKind.Merge"/> action onto a <see cref="MergeSpec"/>.
    /// <c>UpdateColumns</c>/<c>InsertColumns</c> are left null when not
    /// supplied, which is the signal for the caller to introspect the target's schema first.
    /// </summary>
    public static MergeSpec ToMergeSpec(TableAction action) {
        if (action is null) {
            throw new ArgumentNullException(nameof(action));
        }
        if (action.Kind != TableActionKind.Merge) {
            throw new ArgumentException($"Expected a Merge action, got {action.Kind}.", nameof(action));
        }

        // SQL Server's MERGE reads its source on the server, so the source has to be something the
        // target's engine can name. Client-side rows would need staging into a temp table first —
        // deliberately not done here (see SqlServerTableTarget) rather than invented untested.
        if (action.Source.Kind == TableSourceKind.Rows) {
            throw new NotSupportedException(
                "SQL Server merges server-side, so it cannot merge from in-memory rows directly. " +
                "Insert them into a staging table first, then merge from that table.");
        }

        // Source text is passed through raw: MergeBuilder parenthesises a query itself and quotes
        // a table name, so pre-wrapping here would just be a second set of parens.
        var spec = new MergeSpec {
            Target = action.Table,
            Source = action.Source.Text,
            SourceIsQuery = action.Source.Kind == TableSourceKind.Query,
        };
        foreach (var key in action.KeyColumns) {
            spec.KeyColumns.Add(key);
        }
        return spec;
    }
}

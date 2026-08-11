using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using ClrKernel.Core.Secrets;
using ClrKernel.DataEngineering;
using Microsoft.Data.SqlClient;

namespace ClrKernel.Database.Provider.SqlServer;

/// <summary>
/// SQL Server's implementation of the table-action model: <c>SqlBulkCopy</c> for loads,
/// <c>TRUNCATE</c>/<c>DELETE</c> for the removal half, and a server-side <c>MERGE</c> for upserts.
/// <para>
/// This is a mapping layer, not new machinery — it routes each <see cref="TableAction"/> to the
/// bulk-copy and MERGE code the <c>#!sql-bulk</c> / <c>#!sql-merge</c> magics already use, so
/// there is one implementation of the mechanics rather than two that can drift. The translation
/// itself lives in <see cref="SqlServerTableActions"/> and is unit tested; everything here needs a
/// server.
/// </para>
/// </summary>
public sealed class SqlServerTableTarget : ITableActionTarget {
    private readonly SqlConnectionRegistry _registry;
    private readonly SecretStore _secrets;
    private readonly string _connection;

    /// <param name="registry">Resolves connection names — both the target's and any source's.</param>
    /// <param name="secrets">Resolves passwords at execution time.</param>
    /// <param name="connection">The target connection's name; null uses the registry's default.</param>
    public SqlServerTableTarget(SqlConnectionRegistry registry, SecretStore secrets = null, string connection = null) {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _secrets = secrets ?? new SecretStore();
        _connection = connection;
    }

    /// <summary>
    /// SQL Server can do every action. The one gap is a <see cref="TableActionKind.Merge"/> whose
    /// source is in-memory rows or lives on another connection: MERGE reads its source on the
    /// server, so those need staging first. <see cref="Execute"/> says so rather than guessing.
    /// </summary>
    public bool Supports(TableActionKind kind) => true;

    public TableActionResult Execute(TableAction action) {
        if (action is null) {
            throw new ArgumentNullException(nameof(action));
        }

        var stopwatch = Stopwatch.StartNew();
        var result = new TableActionResult(action) { RowsDeleted = 0 };

        if (action.Kind == TableActionKind.Merge) {
            Merge(action, result);
        } else {
            var deleteSql = SqlServerTableActions.DeleteStatement(action);
            if (deleteSql != null) {
                var affected = Target().Execute(deleteSql);
                // TRUNCATE reports nothing; -1 distinguishes "unknown" from "zero rows".
                result.RowsDeleted = deleteSql.StartsWith("truncate", StringComparison.OrdinalIgnoreCase) ? -1 : affected;
            }

            if (action.Source != null) {
                result.RowsWritten = Load(action);
            }
        }

        stopwatch.Stop();
        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        result.Strategy ??= Describe(action);
        return result;
    }

    // Streams the source into the target with SqlBulkCopy. A query source is read from its own
    // connection, so a cross-server load is just two connections and one reader.
    private long Load(TableAction action) {
        var target = Target();
        switch (action.Source.Kind) {
            case TableSourceKind.Rows: {
                    using var reader = action.Source.OpenReader();
                    return target.Table(action.Table).BulkCopyFrom(reader).RowsCopied;
                }
            default: {
                    var source = Db(action.Source.Connection);
                    using var reader = source.Query(SqlServerTableActions.SourceQuery(action.Source)).OpenReader();
                    return target.Table(action.Table).BulkCopyFrom(reader).RowsCopied;
                }
        }
    }

    private void Merge(TableAction action, TableActionResult result) {
        MergeSourceMustBeOnTarget(action);

        var spec = SqlServerTableActions.ToMergeSpec(action);
        using var connection = Open();

        // MERGE needs the full column list; introspect the target when the caller hasn't named one.
        var columns = IntrospectColumns(connection, spec.Target);
        spec.UpdateColumns = columns
            .Where(c => !spec.KeyColumns.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        spec.InsertColumns = columns.ToList();

        var sql = MergeBuilder.Build(spec);
        try {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            if (reader.Read()) {
                result.RowsWritten = ReadCount(reader, "Inserted") + ReadCount(reader, "Updated");
                result.RowsDeleted = ReadCount(reader, "Deleted");
            }
        } catch (SqlException e) {
            throw new SqlCellException($"MERGE into {spec.Target} failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// MERGE reads its source on the server, so the source has to resolve to the same connection as
    /// the target. Compare the <em>resolved</em> specs, not the raw names: naming the default
    /// connection explicitly is the same connection as leaving it out, and refusing that would send
    /// the caller off to stage a table that is already on the right server.
    /// </summary>
    internal void MergeSourceMustBeOnTarget(TableAction action) {
        if (action.Source.Kind == TableSourceKind.Rows || string.IsNullOrEmpty(action.Source.Connection)) {
            return;
        }

        var sourceName = _registry.Resolve(action.Source.Connection).Name;
        var targetName = _registry.Resolve(_connection).Name;
        if (string.Equals(sourceName, targetName, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        throw new NotSupportedException(
            "SQL Server merges on the server, so the source must live on the target connection. " +
            $"'{sourceName}' is not '{targetName}' — load it into a staging table there first " +
            "(Insert or TruncateInsert), then merge from that.");
    }

    private SqlDatabase Target() => Db(_connection);

    private SqlDatabase Db(string name) => new SqlDatabase(_registry.Resolve(name ?? _connection), _secrets);

    private SqlConnection Open() => Target().Open();

    private static string Describe(TableAction action) => action.Kind switch {
        TableActionKind.Truncate => "TRUNCATE",
        TableActionKind.Delete => "DELETE",
        TableActionKind.Insert => "SqlBulkCopy",
        TableActionKind.TruncateInsert => "TRUNCATE + SqlBulkCopy",
        TableActionKind.DeleteInsert => "DELETE + SqlBulkCopy",
        TableActionKind.Merge => "MERGE",
        _ => action.Kind.ToString(),
    };

    private static IReadOnlyList<string> IntrospectColumns(SqlConnection connection, string target) {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT c.name FROM sys.columns c " +
            "WHERE c.object_id = OBJECT_ID(@t) AND c.is_computed = 0 AND c.is_identity = 0 " +
            "ORDER BY c.column_id";
        var p = command.CreateParameter();
        p.ParameterName = "@t";
        p.Value = target;
        command.Parameters.Add(p);
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private static long ReadCount(IDataReader reader, string column) {
        for (var i = 0; i < reader.FieldCount; i++) {
            if (string.Equals(reader.GetName(i), column, StringComparison.OrdinalIgnoreCase)) {
                return reader.IsDBNull(i) ? 0 : Convert.ToInt64(reader.GetValue(i));
            }
        }
        return 0;
    }
}

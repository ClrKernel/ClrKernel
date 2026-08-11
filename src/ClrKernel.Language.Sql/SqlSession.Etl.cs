using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Secrets;
using ClrKernel.Database.Provider.SqlServer;
using Microsoft.Data.SqlClient;

namespace ClrKernel.Language.Sql;

/// <summary>
/// ETL operations on the session's connections: bulk copy and MERGE (upsert),
/// exposed both as a C# API (for #!csharp cells, via <c>Sql</c>) and as the
/// <c>#!sql-bulk</c> / <c>#!sql-merge</c> cell magics. Passwords resolve from the
/// secret store at execution time.
/// </summary>
public sealed partial class SqlSession {
    /// <summary>Opens a live connection to a registered connection by name.</summary>
    public SqlConnection OpenConnection(string connectionName) {
        var spec = _registry.Resolve(connectionName);
        string connectionString;
        try {
            connectionString = spec.BuildConnectionString(_secrets);
        } catch (SecretNotFoundException e) {
            throw new SqlCellException(e.Message, e);
        }
        var connection = new SqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    // --- Bulk copy ---------------------------------------------------------

    /// <summary>Bulk-copies a streaming reader into <paramref name="table"/>.</summary>
    public BulkCopyResult BulkCopy(string connectionName, string table, IDataReader source, BulkCopyOptions options = null) {
        using var connection = OpenConnection(connectionName);
        return BulkCopyRunner.Execute(connection, table, source, options);
    }

    /// <summary>Bulk-copies a DataTable into <paramref name="table"/>.</summary>
    public BulkCopyResult BulkCopy(string connectionName, string table, DataTable data, BulkCopyOptions options = null) {
        using var connection = OpenConnection(connectionName);
        return BulkCopyRunner.Execute(connection, table, data, options);
    }

    /// <summary>Bulk-copies a sequence (POCOs, anonymous types, or scalar "array
    /// variables") into <paramref name="table"/>.</summary>
    public BulkCopyResult BulkCopy<T>(string connectionName, string table, IEnumerable<T> rows, BulkCopyOptions options = null) {
        return BulkCopy(connectionName, table, DataTableBuilder.FromRows(rows), options);
    }

    /// <summary>Bulk-copies dictionary rows (a column per key) into <paramref name="table"/>.</summary>
    public BulkCopyResult BulkCopy(string connectionName, string table, IEnumerable<IDictionary<string, object>> rows, BulkCopyOptions options = null) {
        return BulkCopy(connectionName, table, DataTableBuilder.FromDictionaries(rows), options);
    }

    /// <summary>Runs a <c>#!sql-bulk</c> magic: reads from one connection and
    /// bulk-copies into a table on another (or the same) connection.</summary>
    public DisplayData ExecuteBulk(string directiveLine) {
        var d = SqlEtlDirectives.ParseBulk(directiveLine);
        using var source = OpenConnection(d.FromConnection);
        using var command = source.CreateCommand();
        command.CommandText = d.SourceQuery;
        command.CommandTimeout = d.Options.TimeoutSeconds;
        using var reader = command.ExecuteReader();

        BulkCopyResult result;
        try {
            using var destination = OpenConnection(d.ToConnection);
            result = BulkCopyRunner.Execute(destination, d.Table, reader, d.Options);
        } catch (SqlException e) {
            throw new SqlCellException($"Bulk copy into {d.Table} failed: {e.Message}", e);
        }

        var text = $"{result.RowsCopied:N0} rows → {d.ToConnection}.{d.Table} ({result.ElapsedMs:N0} ms)";
        return new DisplayData(text);
    }

    // --- MERGE (upsert) ----------------------------------------------------

    /// <summary>Builds and runs a MERGE, returning per-action counts. When
    /// <see cref="MergeSpec.UpdateColumns"/> is null, the target schema is
    /// introspected to fill update/insert columns (excluding identity/computed).</summary>
    public MergeResult Merge(string connectionName, MergeSpec spec) {
        if (spec == null) {
            throw new ArgumentNullException(nameof(spec));
        }
        using var connection = OpenConnection(connectionName);

        if (spec.UpdateColumns == null) {
            var columns = IntrospectColumns(connection, spec.Target);
            var keys = spec.KeyColumns ?? new List<string>();
            spec.UpdateColumns = columns
                .Where(c => !keys.Contains(c, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (spec.InsertColumns == null || spec.InsertColumns.Count == 0) {
                spec.InsertColumns = columns.ToList();
            }
        }

        var sql = MergeBuilder.Build(spec);
        var stopwatch = Stopwatch.StartNew();
        var result = new MergeResult();
        try {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            if (reader.Read()) {
                result.Inserted = ReadCount(reader, "Inserted");
                result.Updated = ReadCount(reader, "Updated");
                result.Deleted = ReadCount(reader, "Deleted");
            }
        } catch (SqlException e) {
            throw new SqlCellException($"MERGE into {spec.Target} failed: {e.Message}", e);
        }
        stopwatch.Stop();
        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    /// <summary>Runs a <c>#!sql-merge</c> magic and returns a summary.</summary>
    public DisplayData ExecuteMerge(string directiveLine) {
        var d = SqlEtlDirectives.ParseMerge(directiveLine);
        var result = Merge(d.Connection, d.Spec);
        var text = $"{d.Spec.Target}: {result}";
        var html =
            "<div style=\"font:12px/1.5 -apple-system,Segoe UI,sans-serif;color:#57606a\">" +
            $"<span style=\"display:inline-block;padding:1px 6px;border-radius:10px;background:#dafbe1;color:#1a7f37;margin-right:6px\">MERGE {Encode(d.Spec.Target)}</span>" +
            $"inserted {result.Inserted:N0} · updated {result.Updated:N0} · deleted {result.Deleted:N0} · {result.ElapsedMs:N0} ms</div>";
        return new DisplayData(text, html);
    }

    private static IReadOnlyList<string> IntrospectColumns(SqlConnection connection, string target) {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT c.name FROM sys.columns c " +
            "WHERE c.object_id = OBJECT_ID(@target) AND c.is_computed = 0 AND c.is_identity = 0 " +
            "ORDER BY c.column_id";
        var p = command.CreateParameter();
        p.ParameterName = "@target";
        p.Value = target;
        command.Parameters.Add(p);

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            names.Add(reader.GetString(0));
        }
        if (names.Count == 0) {
            throw new SqlCellException(
                $"Could not read columns for '{target}'. Check the table exists and the name is schema-qualified.");
        }
        return names;
    }

    private static long ReadCount(IDataReader reader, string column) {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal));
    }
}

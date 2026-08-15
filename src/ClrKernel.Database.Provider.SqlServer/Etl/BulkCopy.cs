using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using ClrKernel.Core.Primitives;
using Microsoft.Data.SqlClient;

namespace ClrKernel.Database.Provider.SqlServer;
/// <summary>Options for a bulk copy into a SQL Server table.</summary>
public sealed class BulkCopyOptions {
    /// <summary>Rows per batch sent to the server (0 = one batch).</summary>
    public int BatchSize { get; set; } = 10000;

    /// <summary>Per-operation timeout in seconds (0 = no timeout).</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>TRUNCATE the destination before copying.</summary>
    public bool TruncateFirst { get; set; }

    /// <summary>Create the destination table (from the source schema) if it doesn't exist.</summary>
    public bool CreateIfMissing { get; set; }

    /// <summary>Take a bulk update lock on the destination (faster for big loads).</summary>
    public bool TableLock { get; set; } = true;

    /// <summary>Preserve source identity values instead of letting the server assign them.</summary>
    public bool KeepIdentity { get; set; }

    /// <summary>Keep NULLs rather than applying column defaults.</summary>
    public bool KeepNulls { get; set; }

    /// <summary>Fire the progress callback every N rows.</summary>
    public int NotifyAfter { get; set; } = 5000;

    /// <summary>Show a live progress bar.</summary>
    public bool ShowProgress { get; set; } = true;

    /// <summary>Progress bar label (defaults to the destination table).</summary>
    public string ProgressLabel { get; set; }

    /// <summary>Explicit source-column → destination-column mappings. When empty,
    /// columns map by name (DataTable/dictionary source) or by ordinal (reader).</summary>
    public Dictionary<string, string> ColumnMappings { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Outcome of a bulk copy.</summary>
public sealed class BulkCopyResult {
    public long RowsCopied { get; set; }
    public long ElapsedMs { get; set; }
    public string Table { get; set; }
    public override string ToString() => $"{RowsCopied:N0} rows → {Table} ({ElapsedMs:N0} ms)";
}

/// <summary>Runs SqlBulkCopy against an open connection, with progress and options.</summary>
public static class BulkCopyRunner {
    /// <summary>Copies from a DataTable (row count known → determinate progress).</summary>
    public static BulkCopyResult Execute(SqlConnection connection, string table, DataTable data, BulkCopyOptions options) {
        options ??= new BulkCopyOptions();
        if (options.CreateIfMissing) {
            using var schemaReader = data.CreateDataReader();
            CreateIfMissing(connection, table, schemaReader);
        }
        // Named source columns → map by name (robust to column order), unless the
        // caller supplied explicit mappings.
        if (options.ColumnMappings.Count == 0) {
            foreach (DataColumn col in data.Columns) {
                options.ColumnMappings[col.ColumnName] = col.ColumnName;
            }
        }
        var total = data.Rows.Count;
        return Run(connection, table, options, total, bulk => bulk.WriteToServer(data), () => total);
    }

    /// <summary>Copies from a streaming reader (total unknown → indeterminate progress).</summary>
    public static BulkCopyResult Execute(SqlConnection connection, string table, IDataReader reader, BulkCopyOptions options) {
        options ??= new BulkCopyOptions();
        // Build the CREATE from the reader's own schema before we start streaming its rows.
        if (options.CreateIfMissing) {
            CreateIfMissing(connection, table, reader);
        }
        var counting = new CountingDataReader(reader);
        return Run(connection, table, options, 0, bulk => bulk.WriteToServer(counting), () => counting.RowsRead);
    }

    // Creates the destination from the source reader's schema when it doesn't already
    // exist (mirrors SqlTable.BulkCopyFrom's createIfMissing, for the #!sql-bulk magic).
    private static void CreateIfMissing(SqlConnection connection, string table, IDataReader schemaSource) {
        if (TableExists(connection, table)) {
            return;
        }
        using var create = connection.CreateCommand();
        create.CommandText = SqlServerTableDefinition.Generate(schemaSource.GetSchemaTable(), table);
        create.ExecuteNonQuery();
    }

    private static bool TableExists(SqlConnection connection, string table) {
        using var command = connection.CreateCommand();
        command.CommandText = "select convert(bit, iif(object_id(@tableName) is not null, 1, 0))";
        var p = command.CreateParameter();
        p.ParameterName = "@tableName";
        p.Value = table;
        command.Parameters.Add(p);
        return command.ExecuteScalar() is bool b && b;
    }

    private static BulkCopyResult Run(
        SqlConnection connection, string table, BulkCopyOptions options,
        long total, Action<SqlBulkCopy> write, Func<long> finalCount) {
        if (options.TruncateFirst) {
            using var truncate = connection.CreateCommand();
            truncate.CommandText = "TRUNCATE TABLE " + SqlIdentifier.Quote(table);
            truncate.ExecuteNonQuery();
        }

        var flags = SqlBulkCopyOptions.Default;
        if (options.TableLock) {
            flags |= SqlBulkCopyOptions.TableLock;
        }
        if (options.KeepIdentity) {
            flags |= SqlBulkCopyOptions.KeepIdentity;
        }
        if (options.KeepNulls) {
            flags |= SqlBulkCopyOptions.KeepNulls;
        }

        DisplayProgress progress = null;
        if (options.ShowProgress) {
            progress = new DisplayProgress(options.ProgressLabel ?? ("Bulk copy → " + table), total: total).Show();
        }

        var stopwatch = Stopwatch.StartNew();
        using (var bulk = new SqlBulkCopy(connection, flags, null)) {
            bulk.DestinationTableName = SqlIdentifier.Quote(table);
            bulk.BatchSize = options.BatchSize;
            bulk.BulkCopyTimeout = options.TimeoutSeconds;
            bulk.NotifyAfter = options.NotifyAfter > 0 ? options.NotifyAfter : 5000;
            foreach (var map in options.ColumnMappings) {
                bulk.ColumnMappings.Add(map.Key, map.Value);
            }
            if (progress != null) {
                bulk.SqlRowsCopied += (_, e) => progress.Report(e.RowsCopied);
            }
            write(bulk);
        }
        stopwatch.Stop();

        var copied = finalCount();
        progress?.Done(copied);
        return new BulkCopyResult { RowsCopied = copied, ElapsedMs = stopwatch.ElapsedMilliseconds, Table = table };
    }
}

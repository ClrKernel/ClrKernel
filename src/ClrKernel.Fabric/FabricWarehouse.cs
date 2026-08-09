using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Data.SqlClient;

namespace ClrKernel.Fabric;

/// <summary>Result of a <see cref="FabricWarehouse.BulkInsert(IDataReader, string, bool, string)"/>.</summary>
public sealed class BulkInsertResult {
    public string Table { get; set; }
    public int RowCount { get; set; }
    public bool TableCreated { get; set; }
    public override string ToString() =>
        $"{RowCount:N0} row(s) → {Table}{(TableCreated ? " (table created)" : string.Empty)}";
}

/// <summary>
/// A Fabric Warehouse SQL endpoint. Runs T-SQL over an Entra-authenticated
/// connection, and bulk-inserts a data reader by staging Parquet to a lakehouse
/// and loading it with <c>OPENROWSET</c>.
/// </summary>
public sealed partial class FabricWarehouse {
    private const string _sqlScope = "https://database.windows.net/.default";

    internal FabricWorkspace Workspace { get; }
    public Guid Id { get; }
    public string Name { get; }
    /// <summary>The SQL endpoint host reported by Fabric (used as <c>Server=</c>).</summary>
    public string Server { get; }
    private FabricLakehouse _staging;

    internal FabricWarehouse(FabricWorkspace workspace, Guid id, string name, string server) {
        Workspace = workspace;
        Id = id;
        Name = name;
        Server = server;
    }

    /// <summary>Sets the lakehouse used to stage Parquet during bulk-insert.</summary>
    public FabricWarehouse WithStaging(string lakehouseName) {
        _staging = Workspace.Lakehouse(lakehouseName);
        return this;
    }

    /// <summary>Sets the lakehouse used to stage Parquet during bulk-insert.</summary>
    public FabricWarehouse WithStaging(FabricLakehouse lakehouse) {
        _staging = lakehouse ?? throw new ArgumentNullException(nameof(lakehouse));
        return this;
    }

    /// <summary>Opens an Entra-authenticated connection to the warehouse.</summary>
    public SqlConnection OpenConnection() {
        var conn = OpenConnectionCore();
        conn.Open();
        return conn;
    }

    private SqlConnection OpenConnectionCore() {
        var builder = new SqlConnectionStringBuilder {
            DataSource = Server,
            InitialCatalog = Name,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
        };
        var cred = Workspace.Connection.Credential;
        var conn = new SqlConnection(builder.ConnectionString) {
            AccessTokenCallback = async (_, ct) => {
                var token = await cred.GetTokenAsync(new TokenRequestContext(new[] { _sqlScope }), ct).ConfigureAwait(false);
                return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
            },
        };
        return conn;
    }

    /// <summary>Runs a query and returns the rows as a <see cref="DataTable"/>.</summary>
    public DataTable Query(string sql) {
        if (string.IsNullOrWhiteSpace(sql)) {
            throw new ArgumentException("sql is required.", nameof(sql));
        }

        using var conn = OpenConnection();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
        using var reader = cmd.ExecuteReader();
        var table = new DataTable();
        table.Load(reader);
        return table;
    }

    /// <summary>Executes a non-query statement and returns rows affected.</summary>
    public int Execute(string sql) {
        if (string.IsNullOrWhiteSpace(sql)) {
            throw new ArgumentException("sql is required.", nameof(sql));
        }

        using var conn = OpenConnection();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Bulk-inserts the reader's rows into <paramref name="table"/> by staging Parquet
    /// to the staging lakehouse and loading it with <c>OPENROWSET</c>. When
    /// <paramref name="createIfMissing"/> is true and the table doesn't exist, it is
    /// created from the reader's schema (Fabric-compatible types).
    /// </summary>
    public BulkInsertResult BulkInsert(IDataReader reader, string table, bool createIfMissing = false, string stagingLakehouse = null) =>
        BulkInsertAsync(reader, table, createIfMissing, stagingLakehouse).GetAwaiter().GetResult();

    /// <inheritdoc cref="BulkInsert(IDataReader, string, bool, string)"/>
    public async Task<BulkInsertResult> BulkInsertAsync(
        IDataReader reader, string table, bool createIfMissing = false,
        string stagingLakehouse = null, CancellationToken cancellationToken = default) {
        if (reader is null) {
            throw new ArgumentNullException(nameof(reader));
        }

        if (string.IsNullOrWhiteSpace(table)) {
            throw new ArgumentException("table is required.", nameof(table));
        }

        var staging = stagingLakehouse != null ? Workspace.Lakehouse(stagingLakehouse) : _staging;
        if (staging is null) {
            throw new InvalidOperationException(
                "No staging lakehouse configured. Call WithStaging(\"<lakehouse>\") or pass stagingLakehouse.");
        }

        var result = new BulkInsertResult { Table = table };

        if (createIfMissing && !TableExists(table)) {
            Execute(WarehouseTableDefinition.Generate(reader, table));
            result.TableCreated = true;
        }

        // Stage Parquet to a temp file (Parquet writing needs a seekable stream), then upload to OneLake.
        var relativePath = $"Staging-BulkInsert/{Guid.NewGuid():N}.parquet";
        var tempFile = Path.Combine(Path.GetTempPath(), $"clrkernel-{Guid.NewGuid():N}.parquet");
        try {
            await using (var local = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite)) {
                var write = await FabricParquet.WriteAsync(reader, local, cancellationToken: cancellationToken).ConfigureAwait(false);
                result.RowCount = write.RowCount;
                local.Position = 0;
                var file = staging.FileClient(relativePath);
                await file.UploadAsync(local, overwrite: true, cancellationToken).ConfigureAwait(false);
            }

            var url = staging.OneLakeUrl(relativePath);
            var quoted = WarehouseTableDefinition.QuoteTable(table);
            Execute($"INSERT INTO {quoted}\nSELECT * FROM OPENROWSET(BULK '{url}', FORMAT = 'PARQUET') AS staged");
        } finally {
            TryDeleteStaged(staging, relativePath);
            TryDeleteLocal(tempFile);
        }
        return result;
    }

    /// <summary>True if a (schema-qualified) table exists in the warehouse.</summary>
    public bool TableExists(string table) {
        var (schema, name) = SplitTable(table);
        using var conn = OpenConnection();
        using var cmd = new SqlCommand(
            "SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @n AND (@s IS NULL OR TABLE_SCHEMA = @s)", conn);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@s", (object)schema ?? DBNull.Value);
        return cmd.ExecuteScalar() != null;
    }

    private static (string Schema, string Name) SplitTable(string table) {
        var parts = table.Replace("[", "").Replace("]", "").Split('.');
        return parts.Length >= 2 ? (parts[^2], parts[^1]) : (null, parts[^1]);
    }

    private static void TryDeleteStaged(FabricLakehouse staging, string relativePath) {
        try { staging.FileClient(relativePath).DeleteIfExists(); } catch { /* best effort */ }
    }

    private static void TryDeleteLocal(string path) {
        try { if (File.Exists(path)) { File.Delete(path); } } catch { /* best effort */ }
    }
}

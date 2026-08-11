using System;
using System.Collections.Generic;
using System.Data;
using ClrKernel.Database;
using ClrKernel.Sql.Etl;
using Microsoft.Data.SqlClient;

namespace ClrKernel.Sql;

/// <summary>
/// A reference to a table on a <see cref="SqlDatabase"/>. Reads as a query source
/// (<see cref="Query"/> / <see cref="Results"/>) and writes as a bulk-copy target
/// (<see cref="BulkCopyFrom(SqlQuery, BulkCopyOptions, bool)"/> and overloads).
/// </summary>
public sealed class SqlTable {
    internal SqlTable(SqlDatabase database, string name) {
        Database = database ?? throw new ArgumentNullException(nameof(database));
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("table is required.", nameof(name)) : name;
    }

    /// <summary>The owning database.</summary>
    public SqlDatabase Database { get; }

    /// <summary>The table name (optionally schema-qualified, e.g. <c>dbo.Orders</c>).</summary>
    public string Name { get; }

    /// <summary>True if the table exists.</summary>
    public bool Exists() =>
        Database.Scalar<bool>("select convert(bit, iif(object_id(@tableName) is not null, 1, 0))", new { tableName = Name });

    /// <summary>The row count.</summary>
    public long Count() => Database.Scalar<long>($"select count_big(*) from {Name}");

    /// <summary>A <c>select * from &lt;table&gt;</c> query.</summary>
    public SqlQuery Query() => Database.Query($"select * from {Name}");

    /// <summary>Reads all rows (interactive grid + enumerable).</summary>
    public DataResults Results(int limit = 1000) => Query().Results(limit);

    /// <summary>Truncates the table.</summary>
    public int Truncate() => Database.Execute($"truncate table {Name}");

    /// <summary>Bulk-copies a streaming query's rows into this table.</summary>
    public BulkCopyResult BulkCopyFrom(SqlQuery source, BulkCopyOptions options = null, bool createIfMissing = false) {
        if (source == null) {
            throw new ArgumentNullException(nameof(source));
        }
        using var reader = source.OpenReader();
        return BulkCopyFrom(reader, options, createIfMissing);
    }

    /// <summary>Bulk-copies materialized results into this table.</summary>
    public BulkCopyResult BulkCopyFrom(DataResults source, BulkCopyOptions options = null, bool createIfMissing = false) {
        if (source == null) {
            throw new ArgumentNullException(nameof(source));
        }
        return BulkCopyFrom(source.Table, options, createIfMissing);
    }

    /// <summary>Bulk-copies a <see cref="DataTable"/> into this table.</summary>
    public BulkCopyResult BulkCopyFrom(DataTable data, BulkCopyOptions options = null, bool createIfMissing = false) {
        if (data == null) {
            throw new ArgumentNullException(nameof(data));
        }
        using var connection = Database.Open();
        EnsureTable(connection, createIfMissing, data.CreateDataReader);
        return BulkCopyRunner.Execute(connection, Name, data, options ?? new BulkCopyOptions());
    }

    /// <summary>Bulk-copies a streaming reader into this table.</summary>
    public BulkCopyResult BulkCopyFrom(IDataReader reader, BulkCopyOptions options = null, bool createIfMissing = false) {
        if (reader == null) {
            throw new ArgumentNullException(nameof(reader));
        }
        using var connection = Database.Open();
        // For createIfMissing on a live reader we build the CREATE from the reader's
        // own schema (no extra query) before streaming its rows.
        if (createIfMissing && !TableExists(connection)) {
            Execute(connection, SqlServerTableDefinition.Generate(reader.GetSchemaTable(), Name));
        }
        return BulkCopyRunner.Execute(connection, Name, reader, options ?? new BulkCopyOptions());
    }

    /// <summary>Bulk-copies a sequence of POCOs/records/anonymous types into this table.</summary>
    public BulkCopyResult BulkCopyFrom<T>(IEnumerable<T> rows, BulkCopyOptions options = null, bool createIfMissing = false) {
        if (rows == null) {
            throw new ArgumentNullException(nameof(rows));
        }
        return BulkCopyFrom(DataTableBuilder.FromRows(rows), options, createIfMissing);
    }

    private void EnsureTable(SqlConnection connection, bool createIfMissing, Func<IDataReader> schemaReaderFactory) {
        if (!createIfMissing || TableExists(connection)) {
            return;
        }
        using var schemaReader = schemaReaderFactory();
        Execute(connection, SqlServerTableDefinition.Generate(schemaReader.GetSchemaTable(), Name));
    }

    private bool TableExists(SqlConnection connection) {
        using var command = connection.CreateCommand();
        command.CommandText = "select convert(bit, iif(object_id(@tableName) is not null, 1, 0))";
        var p = command.CreateParameter();
        p.ParameterName = "@tableName";
        p.Value = Name;
        command.Parameters.Add(p);
        return ValueConverter.To<bool>(command.ExecuteScalar());
    }

    private static void Execute(SqlConnection connection, string sql) {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

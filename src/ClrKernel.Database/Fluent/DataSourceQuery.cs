using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;

namespace ClrKernel.Database;

/// <summary>
/// A lazy query bound to a <see cref="DataSource"/>. Nothing runs until you call
/// <see cref="Results(int)"/> (materialized rows that also render as an interactive
/// grid), <see cref="Results{T}"/> (typed objects), or <see cref="OpenReader"/> (a
/// streaming reader).
/// </summary>
public sealed class DataSourceQuery {
    private readonly DataSource _database;
    private readonly object _parameters;

    internal DataSourceQuery(DataSource database, string sql, object parameters) {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        Sql = string.IsNullOrWhiteSpace(sql) ? throw new ArgumentException("sql is required.", nameof(sql)) : sql;
        _parameters = parameters;
    }

    /// <summary>The query text.</summary>
    public string Sql { get; }

    /// <summary>Per-query command timeout (seconds); falls back to the database default.</summary>
    public int? CommandTimeout { get; set; }

    /// <summary>Opens a streaming reader on its own connection (closed when disposed).</summary>
    public DbDataReader OpenReader() {
        var connection = _database.Open();
        try {
            var command = DataSource.CreateCommand(
                connection, null, Sql, _parameters, _database.EffectiveTimeout(CommandTimeout));
            return command.ExecuteReader(CommandBehavior.CloseConnection);
        } catch {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs the query and returns all rows. Renders as an interactive grid when it's a
    /// cell's value, and is enumerable as dynamic rows in code.
    /// </summary>
    public DataResults Results(int limit = 1000) {
        using var reader = OpenReader();
        var table = new DataTable();
        table.Load(reader);
        return new DataResults(table, limit);
    }

    /// <summary>Runs the query and maps each row to <typeparamref name="T"/>
    /// (a record, class with settable properties, or a scalar type).</summary>
    public IReadOnlyList<T> Results<T>() {
        using var reader = OpenReader();
        return ObjectMapper.Map<T>(reader);
    }
}

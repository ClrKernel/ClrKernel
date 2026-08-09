using System;
using System.Collections.Generic;
using System.Data;
using ClrKernel.Data;
using Microsoft.Data.SqlClient;

namespace ClrKernel.Sql;

/// <summary>
/// A lazy SQL query bound to a <see cref="SqlDatabase"/>. Nothing runs until you
/// call <see cref="Results(int)"/> (materialized rows that also render as an
/// interactive grid), <see cref="Results{T}"/> (typed objects), or
/// <see cref="OpenReader"/> (a streaming reader, e.g. to feed a bulk copy).
/// </summary>
public sealed class SqlQuery {
    private readonly SqlDatabase _database;
    private readonly object _parameters;

    internal SqlQuery(SqlDatabase database, string sql, object parameters) {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        Sql = string.IsNullOrWhiteSpace(sql) ? throw new ArgumentException("sql is required.", nameof(sql)) : sql;
        _parameters = parameters;
    }

    /// <summary>The query text.</summary>
    public string Sql { get; }

    /// <summary>Per-query command timeout (seconds); falls back to the database default.</summary>
    public int? CommandTimeout { get; set; }

    /// <summary>
    /// Opens a streaming reader on its own connection (closed when the reader is
    /// disposed). Use this to pipe rows into a bulk copy without buffering.
    /// </summary>
    public SqlDataReader OpenReader() {
        var connection = _database.Open();
        try {
            var command = SqlDatabase.CreateCommand(
                connection, null, Sql, _parameters, _database.EffectiveTimeout(CommandTimeout));
            return command.ExecuteReader(CommandBehavior.CloseConnection);
        } catch {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs the query and returns all rows. The result renders as an interactive
    /// grid when it's a cell's value, and is also enumerable as dynamic rows
    /// (<c>foreach (var r in results) { … r.OrderId … }</c>).
    /// </summary>
    /// <param name="limit">Max rows shown in the grid preview; all rows remain enumerable.</param>
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

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ClrKernel.Database;

/// <summary>
/// A reference to a table on a <see cref="DataSource"/>. Reads as a query source
/// (<see cref="Query"/> / <see cref="Results"/>) and writes via generic,
/// parameterized batch <see cref="Insert{T}"/> (provider-agnostic — SQL Server's
/// bulk copy lives on the SQL-specific table type).
/// </summary>
public class DataSourceTable {
    internal DataSourceTable(DataSource database, string name) {
        DataSource = database ?? throw new ArgumentNullException(nameof(database));
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("table is required.", nameof(name)) : name;
    }

    /// <summary>The owning database.</summary>
    public DataSource DataSource { get; }

    /// <summary>The table name (optionally schema-qualified).</summary>
    public string Name { get; }

    /// <summary>A <c>select * from &lt;table&gt;</c> query.</summary>
    public virtual DataSourceQuery Query() => DataSource.Query($"select * from {Name}");

    /// <summary>Reads all rows (interactive grid + enumerable).</summary>
    public DataResults Results(int limit = 1000) => Query().Results(limit);

    /// <summary>The row count.</summary>
    public virtual long Count() => DataSource.Scalar<long>($"select count(*) from {Name}");

    /// <summary>
    /// Inserts rows with parameterized <c>INSERT</c> statements, batched. Columns come
    /// from each row's dictionary keys or the element type's public properties. Works on
    /// any ADO.NET provider (no bulk-copy dependency).
    /// </summary>
    public int Insert<T>(IEnumerable<T> rows, int batchSize = 200) {
        if (rows == null) {
            throw new ArgumentNullException(nameof(rows));
        }
        if (batchSize < 1) {
            throw new ArgumentException("batchSize must be at least 1.", nameof(batchSize));
        }

        var materialized = rows.Where(r => r != null).Cast<object>().ToList();
        if (materialized.Count == 0) {
            return 0;
        }

        var columns = ColumnsOf(materialized[0]);
        if (columns.Count == 0) {
            throw new InvalidOperationException("Could not determine columns to insert from the row type.");
        }

        var affected = 0;
        using var connection = DataSource.Open();
        foreach (var batch in Batch(materialized, batchSize)) {
            affected += InsertBatch(connection, columns, batch);
        }
        return affected;
    }

    private int InsertBatch(DbConnection connection, IReadOnlyList<string> columns, IReadOnlyList<object> batch) {
        var sql = new StringBuilder("insert into ").Append(Name).Append(" (")
            .Append(string.Join(", ", columns)).Append(") values ");
        using var command = connection.CreateCommand();
        var p = 0;
        for (var r = 0; r < batch.Count; r++) {
            if (r > 0) {
                sql.Append(", ");
            }
            sql.Append('(');
            var values = ValuesOf(batch[r]);
            for (var c = 0; c < columns.Count; c++) {
                if (c > 0) {
                    sql.Append(", ");
                }
                var name = "@p" + p++;
                sql.Append(name);
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                values.TryGetValue(columns[c], out var value);
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
            sql.Append(')');
        }
        command.CommandText = sql.ToString();
        return command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ColumnsOf(object row) {
        if (row is IDictionary<string, object> dict) {
            return dict.Keys.ToList();
        }
        return row.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(pr => pr.CanRead && pr.GetIndexParameters().Length == 0)
            .Select(pr => pr.Name)
            .ToList();
    }

    private static IDictionary<string, object> ValuesOf(object row) {
        if (row is IDictionary<string, object> dict) {
            return dict;
        }
        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in row.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (property.CanRead && property.GetIndexParameters().Length == 0) {
                map[property.Name] = property.GetValue(row);
            }
        }
        return map;
    }

    private static IEnumerable<IReadOnlyList<object>> Batch(IReadOnlyList<object> items, int size) {
        for (var i = 0; i < items.Count; i += size) {
            yield return items.Skip(i).Take(size).ToList();
        }
    }
}

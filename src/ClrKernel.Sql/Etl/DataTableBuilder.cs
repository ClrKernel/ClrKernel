using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;

namespace ClrKernel.Sql.Etl;
/// <summary>
/// Turns in-memory collections ("array variables") into a <see cref="DataTable"/>
/// that SqlBulkCopy can stream to a table. Handles the common shapes a notebook
/// produces: scalars (<c>int[]</c>, <c>string[]</c>, dates, Guids…), POCOs /
/// anonymous types (a column per public property), and dictionaries (a column
/// per key). Nullable and DBNull are handled.
/// </summary>
public static class DataTableBuilder {
    /// <summary>Builds a DataTable from a typed sequence.</summary>
    public static DataTable FromRows<T>(IEnumerable<T> rows, string scalarColumnName = "Value") {
        if (rows == null) {
            throw new ArgumentNullException(nameof(rows));
        }
        var list = rows.Cast<object>().ToList();
        return FromObjects(list, typeof(T), scalarColumnName);
    }

    /// <summary>Builds a DataTable from a non-generic sequence of dictionaries.</summary>
    public static DataTable FromDictionaries(IEnumerable<IDictionary<string, object>> rows) {
        if (rows == null) {
            throw new ArgumentNullException(nameof(rows));
        }
        var table = new DataTable();
        var list = rows.ToList();
        // Columns = union of keys, in first-seen order.
        foreach (var row in list) {
            foreach (var key in row.Keys) {
                if (!table.Columns.Contains(key)) {
                    table.Columns.Add(key, typeof(object));
                }
            }
        }
        foreach (var row in list) {
            var dr = table.NewRow();
            foreach (DataColumn col in table.Columns) {
                dr[col] = row.TryGetValue(col.ColumnName, out var v) ? (v ?? DBNull.Value) : DBNull.Value;
            }
            table.Rows.Add(dr);
        }
        return table;
    }

    private static DataTable FromObjects(List<object> rows, Type elementType, string scalarColumnName) {
        var table = new DataTable();

        // Dictionaries: delegate to the dictionary path.
        if (typeof(IDictionary<string, object>).IsAssignableFrom(elementType) ||
            (rows.FirstOrDefault(r => r != null) is IDictionary<string, object>)) {
            return FromDictionaries(rows.Where(r => r != null).Cast<IDictionary<string, object>>());
        }

        var effectiveType = elementType == typeof(object)
            ? rows.FirstOrDefault(r => r != null)?.GetType() ?? typeof(object)
            : elementType;

        if (IsScalar(effectiveType)) {
            table.Columns.Add(scalarColumnName, UnderlyingType(effectiveType));
            foreach (var r in rows) {
                table.Rows.Add(r ?? (object)DBNull.Value);
            }
            return table;
        }

        // POCO / anonymous type: a column per readable public property.
        var props = effectiveType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();
        if (props.Length == 0) {
            // Fall back to a single ToString column.
            table.Columns.Add(scalarColumnName, typeof(string));
            foreach (var r in rows) {
                table.Rows.Add(r?.ToString() ?? (object)DBNull.Value);
            }
            return table;
        }

        foreach (var p in props) {
            table.Columns.Add(p.Name, UnderlyingType(p.PropertyType));
        }
        foreach (var r in rows) {
            var dr = table.NewRow();
            foreach (var p in props) {
                dr[p.Name] = (r == null ? null : p.GetValue(r)) ?? DBNull.Value;
            }
            table.Rows.Add(dr);
        }
        return table;
    }

    private static bool IsScalar(Type type) {
        var t = UnderlyingType(type);
        return t.IsPrimitive
            || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)
            || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t == typeof(Guid)
            || t == typeof(byte[]) || t.IsEnum;
    }

    private static Type UnderlyingType(Type type) =>
        Nullable.GetUnderlyingType(type) ?? type;
}

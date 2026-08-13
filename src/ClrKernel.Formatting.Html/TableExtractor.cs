using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using ClrKernel.Core.Primitives;

namespace ClrKernel.Formatting.Html;

/// <summary>
/// Shapes an arbitrary value into the <see cref="DisplayTable"/> concept: data readers
/// and DataTables by schema, dictionary rows by key union, sequences by the element
/// type's public properties (scalars get a single Value column), and any other object
/// as a one-row table of its properties. <c>TotalRows = -1</c> means the source was
/// truncated at the limit with the remainder uncounted ("first N+").
/// </summary>
public static class TableExtractor {
    private const int _limit = 1000;
    private const int _unknownTotal = -1;

    public static DisplayTable Extract(DisplayObject source) => Extract(source.Value);

    public static DisplayTable Extract(object value, int limit = _limit) {
        switch (value) {
            case null:
                return new DisplayTable(null, new[] { "Value" }, Array.Empty<IReadOnlyList<string>>(), null, 0);
            case DisplayTable table:
                return table;
            case IDataReader reader:
                return FromReader(reader, limit);
            case DataTable dataTable:
                return FromDataTable(dataTable, limit);
            case IEnumerable<IDictionary<string, object>> dictionaryRows:
                return FromDictionaryRows(value, dictionaryRows, limit);
            case string _:
                break; // a string is enumerable, but one row of chars helps no one
            case IEnumerable sequence:
                return FromSequence(value, sequence, limit);
        }
        return FromSingleObject(value);
    }

    private static DisplayTable FromReader(IDataReader reader, int limit) {
        var fieldCount = reader.FieldCount;
        var columns = new string[fieldCount];
        var types = new string[fieldCount];
        for (var i = 0; i < fieldCount; i++) {
            columns[i] = reader.GetName(i);
            Type fieldType;
            try {
                fieldType = reader.GetFieldType(i);
            } catch {
                fieldType = typeof(string);
            }
            types[i] = InteractiveTable.KindOf(fieldType);
        }

        var rows = new List<IReadOnlyList<string>>();
        var total = 0;
        while (reader.Read()) {
            total++;
            if (rows.Count >= limit) {
                continue; // keep counting for the "N of M" label, stop materializing
            }
            var row = new string[fieldCount];
            for (var i = 0; i < fieldCount; i++) {
                row[i] = InteractiveTable.CellText(reader.GetValue(i));
            }
            rows.Add(row);
        }
        return new DisplayTable(reader, columns, rows, types, total);
    }

    private static DisplayTable FromDataTable(DataTable table, int limit) {
        var columns = new string[table.Columns.Count];
        var types = new string[table.Columns.Count];
        for (var i = 0; i < table.Columns.Count; i++) {
            columns[i] = table.Columns[i].ColumnName;
            types[i] = InteractiveTable.KindOf(table.Columns[i].DataType);
        }

        var rows = new List<IReadOnlyList<string>>();
        var total = table.Rows.Count;
        var take = Math.Min(total, limit);
        for (var r = 0; r < take; r++) {
            var dataRow = table.Rows[r];
            var row = new string[table.Columns.Count];
            for (var c = 0; c < table.Columns.Count; c++) {
                row[c] = InteractiveTable.CellText(dataRow[c]);
            }
            rows.Add(row);
        }
        return new DisplayTable(table, columns, rows, types, total);
    }

    private static DisplayTable FromDictionaryRows(object value, IEnumerable<IDictionary<string, object>> source, int limit) {
        var taken = source.Take(limit + 1).ToList();
        var truncated = taken.Count > limit;
        if (truncated) {
            taken.RemoveAt(taken.Count - 1);
        }

        // Columns: union of keys, in first-seen order. Column type is inferred
        // from the first non-null value seen for that key.
        var columns = new List<string>();
        var columnIndex = new Dictionary<string, int>();
        var kinds = new List<string>();
        var kindLocked = new List<bool>();
        foreach (var row in taken) {
            foreach (var pair in row) {
                if (!columnIndex.ContainsKey(pair.Key)) {
                    columnIndex[pair.Key] = columns.Count;
                    columns.Add(pair.Key);
                    kinds.Add(InteractiveTable.Text);
                    kindLocked.Add(false);
                }
                var idx = columnIndex[pair.Key];
                if (!kindLocked[idx] && pair.Value != null && !(pair.Value is DBNull)) {
                    kinds[idx] = InteractiveTable.KindOf(pair.Value.GetType());
                    kindLocked[idx] = true;
                }
            }
        }

        var cells = new List<IReadOnlyList<string>>();
        foreach (var row in taken) {
            var cellRow = new string[columns.Count];
            foreach (var pair in row) {
                cellRow[columnIndex[pair.Key]] = InteractiveTable.CellText(pair.Value);
            }
            cells.Add(cellRow);
        }
        return new DisplayTable(value, columns, cells, kinds, truncated ? _unknownTotal : taken.Count);
    }

    private static DisplayTable FromSequence(object value, IEnumerable source, int limit) {
        var items = new List<object>();
        var truncated = false;
        foreach (var item in source) {
            if (items.Count > limit) {
                truncated = true;
                items.RemoveAt(items.Count - 1);
                break;
            }
            items.Add(item);
        }

        // Element shape comes from the first non-null item at runtime (the static
        // element type is unknown here).
        var elementType = items.FirstOrDefault(i => i != null)?.GetType();
        var properties = elementType != null && !IsScalar(elementType)
            ? ReadableProperties(elementType)
            : Array.Empty<PropertyInfo>();

        string[] columns;
        string[] types;
        if (properties.Length == 0) {
            columns = new[] { "Value" };
            types = new[] { elementType != null ? InteractiveTable.KindOf(elementType) : InteractiveTable.Text };
        } else {
            columns = properties.Select(p => p.Name).ToArray();
            types = properties.Select(p => InteractiveTable.KindOf(p.PropertyType)).ToArray();
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (var item in items) {
            if (properties.Length == 0) {
                rows.Add(new[] { InteractiveTable.CellText(item) });
            } else {
                rows.Add(properties.Select(p => SafeGet(p, item)).ToArray());
            }
        }
        return new DisplayTable(value, columns, rows, types, truncated ? _unknownTotal : items.Count);
    }

    private static DisplayTable FromSingleObject(object value) {
        var properties = ReadableProperties(value.GetType());
        if (properties.Length == 0) {
            return new DisplayTable(
                value,
                new[] { "Value" },
                new IReadOnlyList<string>[] { new[] { InteractiveTable.CellText(value) } },
                new[] { InteractiveTable.KindOf(value.GetType()) },
                1);
        }
        return new DisplayTable(
            value,
            properties.Select(p => p.Name).ToArray(),
            new IReadOnlyList<string>[] { properties.Select(p => SafeGet(p, value)).ToArray() },
            properties.Select(p => InteractiveTable.KindOf(p.PropertyType)).ToArray(),
            1);
    }

    private static PropertyInfo[] ReadableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

    private static string SafeGet(PropertyInfo property, object target) {
        try {
            return InteractiveTable.CellText(property.GetValue(target));
        } catch (Exception e) {
            return "<error: " + e.GetBaseException().Message + ">";
        }
    }

    private static bool IsScalar(Type type) {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
            || type == typeof(Guid);
    }
}

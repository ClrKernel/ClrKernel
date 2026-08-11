using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;

namespace ClrKernel.Database;

/// <summary>
/// Maps query rows to <typeparamref name="T"/>: a scalar type (single column),
/// a class with settable properties, or a record / immutable type whose
/// constructor parameters match column names (case-insensitive).
/// </summary>
internal static class ObjectMapper {
    public static IReadOnlyList<T> Map<T>(IDataReader reader) {
        var columns = Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(reader.GetName, i => i, StringComparer.OrdinalIgnoreCase);
        var rows = new List<T>();
        var materialize = BuildMaterializer<T>(columns.Keys);
        while (reader.Read()) {
            rows.Add(materialize(name => columns.TryGetValue(name, out var i) ? reader.GetValue(i) : null,
                                 () => reader.GetValue(0)));
        }
        return rows;
    }

    public static IReadOnlyList<T> Map<T>(DataTable table) {
        var columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
        var index = columns.ToDictionary(c => c, c => table.Columns[c].Ordinal, StringComparer.OrdinalIgnoreCase);
        var rows = new List<T>();
        var materialize = BuildMaterializer<T>(columns);
        foreach (DataRow row in table.Rows) {
            rows.Add(materialize(name => index.TryGetValue(name, out var i) ? row[i] : null,
                                 () => row[0]));
        }
        return rows;
    }

    private delegate T RowFactory<out T>(Func<string, object> byName, Func<object> firstColumn);

    private static RowFactory<T> BuildMaterializer<T>(IEnumerable<string> columnNames) {
        var type = typeof(T);

        if (ValueConverter.IsScalar(type)) {
            return (_, first) => ValueConverter.To<T>(first());
        }

        var columns = new HashSet<string>(columnNames, StringComparer.OrdinalIgnoreCase);

        // Records / immutable types: a constructor whose parameters all match columns.
        var ctor = type.GetConstructors()
            .Where(c => c.GetParameters().Length > 0 &&
                        c.GetParameters().All(p => columns.Contains(p.Name)))
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (ctor != null) {
            var parameters = ctor.GetParameters();
            return (byName, _) => {
                var args = new object[parameters.Length];
                for (var i = 0; i < parameters.Length; i++) {
                    args[i] = ValueConverter.To(parameters[i].ParameterType, byName(parameters[i].Name));
                }
                return (T)ctor.Invoke(args);
            };
        }

        // Classes with a parameterless constructor: set writable properties by name.
        var writable = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && columns.Contains(p.Name))
            .ToArray();
        return (byName, _) => {
            var instance = Activator.CreateInstance<T>();
            foreach (var property in writable) {
                property.SetValue(instance, ValueConverter.To(property.PropertyType, byName(property.Name)));
            }
            return instance;
        };
    }
}

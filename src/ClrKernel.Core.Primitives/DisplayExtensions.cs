using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;

namespace ClrKernel.Core.Primitives {
    /// <summary>
    /// Display helpers available in notebook cells via the ClrKernel.Core.Primitives
    /// namespace import. Kept separate from <see cref="DisplayDataEmitter"/> (which
    /// the kernel imports with <c>using static</c>) so extension-method resolution
    /// stays unambiguous.
    /// <para>
    /// The <c>DisplayTable</c> overloads render an interactive grid (sort, filter,
    /// and an Analyze panel of per-column stats) via <see cref="InteractiveTable"/>
    /// — a self-contained HTML/JS output that works in VS Code notebooks,
    /// JupyterLab, and plain <c>.nb.md</c> HTML previews without a custom renderer.
    /// </para>
    /// </summary>
    public static class DisplayExtensions {
        // A total row count is unknown (lazy source truncated at the limit); the
        // grid shows "first N+" rather than "N of M".
        private const int _unknownTotal = -1;

        /// <summary>
        /// Displays content and returns a handle that can update it in place
        /// (e.g. <c>var dv = "".DisplayAs("text/html"); dv.Update(html);</c>).
        /// </summary>
        public static DisplayedValue DisplayAs(this string content, string mimeType) {
            var displayId = Guid.NewGuid().ToString("N");
            var data = new DisplayData();
            data.Data[mimeType] = content ?? "";
            data.Transient["display_id"] = displayId;
            DisplayDataEmitter.Emit(data);

            // Capture the current update handler: it is bound to the executing
            // cell's parent message, so later background updates still publish
            // against the right output even after the cell completes.
            var update = DisplayDataEmitter.UpdateDisplayDataHandler;
            return new DisplayedValue(displayId, mimeType, d => update?.Invoke(d));
        }

        /// <summary>
        /// Displays a value (ToString form) and returns an updatable handle.
        /// Mirrors .NET Interactive's object.Display() for migrated notebooks.
        /// </summary>
        public static DisplayedValue Display(this object value, string mimeType = "text/plain") {
            return (value?.ToString() ?? "").DisplayAs(mimeType);
        }

        /// <summary>
        /// Renders an ADO.NET data reader (e.g. a <c>Microsoft.Data.SqlClient</c>
        /// SQL Server query result) as an interactive grid — sortable, filterable,
        /// with an Analyze panel — and returns an updatable handle. Column types
        /// come from the reader's schema, so numeric and date columns sort and
        /// summarize correctly. The reader is consumed (rows are read up to
        /// <paramref name="limit"/>, but remaining rows are still counted for the
        /// "showing first N of M" label).
        /// </summary>
        public static DisplayedValue DisplayTable(this IDataReader reader, int limit = 1000) {
            if (reader == null) {
                throw new ArgumentNullException(nameof(reader));
            }

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

            return InteractiveTable.Render(columns, rows, types, total).DisplayAs("text/html");
        }

        /// <summary>
        /// Renders a <see cref="DataTable"/> as an interactive grid (sort, filter,
        /// Analyze) and returns an updatable handle. Column types come from
        /// <see cref="DataColumn.DataType"/>.
        /// </summary>
        public static DisplayedValue DisplayTable(this DataTable table, int limit = 1000) {
            if (table == null) {
                throw new ArgumentNullException(nameof(table));
            }

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

            return InteractiveTable.Render(columns, rows, types, total).DisplayAs("text/html");
        }

        /// <summary>
        /// Renders dictionary rows (column name → value, e.g. data-reader
        /// previews) as an interactive grid; columns come from the keys in order
        /// of first appearance.
        /// </summary>
        public static DisplayedValue DisplayTable(this IEnumerable<IDictionary<string, object>> rows, int limit = 1000) {
            var taken = rows.Take(limit + 1).ToList();
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

            var total = truncated ? _unknownTotal : taken.Count;
            return InteractiveTable.Render(columns, cells, kinds, total).DisplayAs("text/html");
        }

        /// <summary>
        /// Renders a sequence as an interactive grid (columns from the element
        /// type's public properties; scalar sequences get a single Value column)
        /// and returns an updatable handle. Mirrors .NET Interactive's
        /// DisplayTable(), but sortable/filterable with an Analyze panel.
        /// </summary>
        public static DisplayedValue DisplayTable<T>(this IEnumerable<T> source, int limit = 1000) {
            // Sequences of dictionary rows render by key, not by Dictionary's own
            // properties (overload resolution prefers this generic method for
            // List<Dictionary<...>>, so re-dispatch at runtime).
            if (source is IEnumerable<IDictionary<string, object>> dictionaryRows) {
                return DisplayTable(dictionaryRows, limit);
            }

            var materialized = source.Take(limit + 1).ToList();
            var truncated = materialized.Count > limit;
            if (truncated) {
                materialized.RemoveAt(materialized.Count - 1);
            }

            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .ToArray();

            // Scalar element types (int, string, DateTime, ...) have no useful
            // public columns — render a single Value column instead.
            var scalar = properties.Length == 0 || IsScalar(typeof(T));

            string[] columns;
            string[] types;
            if (scalar) {
                columns = new[] { "Value" };
                types = new[] { InteractiveTable.KindOf(typeof(T)) };
            } else {
                columns = properties.Select(p => p.Name).ToArray();
                types = properties.Select(p => InteractiveTable.KindOf(p.PropertyType)).ToArray();
            }

            var rows = new List<IReadOnlyList<string>>();
            foreach (var item in materialized) {
                if (scalar) {
                    rows.Add(new[] { InteractiveTable.CellText(item) });
                } else {
                    var row = new string[properties.Length];
                    for (var i = 0; i < properties.Length; i++) {
                        try {
                            row[i] = InteractiveTable.CellText(properties[i].GetValue(item));
                        } catch (Exception e) {
                            row[i] = "<error: " + e.GetBaseException().Message + ">";
                        }
                    }
                    rows.Add(row);
                }
            }

            var total = truncated ? _unknownTotal : materialized.Count;
            return InteractiveTable.Render(columns, rows, types, total).DisplayAs("text/html");
        }

        private static bool IsScalar(Type type) {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) {
                type = underlying;
            }
            return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
                || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
                || type == typeof(Guid);
        }
    }
}

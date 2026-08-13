using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Dynamic;
using System.Linq;
using ClrKernel.Core.Primitives;

namespace ClrKernel.Database;

/// <summary>
/// Materialized query results. As a cell value it is a display concept
/// (<see cref="IDisplayValue"/>): this class registers its own conversion to
/// <see cref="DisplayTable"/>, so the registered renderers draw it as the
/// interactive grid while a plain-text host shows the row count. In code it
/// enumerates as dynamic rows — <c>foreach (var r in results) Console.WriteLine(r.OrderId)</c>
/// or <c>results[0]["OrderId"]</c>. The full <see cref="DataTable"/> is available via
/// <see cref="Table"/>.
/// </summary>
public sealed class DataResults : IDisplayValue, IEnumerable<object> {
    static DataResults() {
        // Concept-to-concept conversions (no rendering): how these results become
        // tabular data, and their short text form.
        DisplayFormatters.Register<DataResults, DisplayTable>(r => r.ToDisplayTable(r.Table, r._limit));
        DisplayFormatters.Register<DataResults, DisplayText>(r => new DisplayText(
            $"{r.Count:N0} row{(r.Count == 1 ? string.Empty : "s")}"));
    }

    private readonly int _limit;

    /// <summary>The underlying data (all rows, regardless of the grid preview limit).</summary>
    public DataTable Table { get; }

    object IDisplayValue.Value => Table;

    public DataResults(DataTable table, int limit = 1000) {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        _limit = limit;
    }

    /// <summary>Number of rows.</summary>
    public int Count => Table.Rows.Count;

    /// <summary>The row at <paramref name="index"/> as a dynamic object.</summary>
    public dynamic this[int index] => new DynamicRow(Table.Rows[index], Table.Columns);

    /// <summary>Enumerates rows as dynamic objects (member/index access by column).</summary>
    public IEnumerator<dynamic> GetEnumerator() {
        foreach (DataRow row in Table.Rows) {
            yield return new DynamicRow(row, Table.Columns);
        }
    }

    IEnumerator<object> IEnumerable<object>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Maps the rows to <typeparamref name="T"/> (record, class, or scalar).</summary>
    public IReadOnlyList<T> As<T>() => ObjectMapper.Map<T>(Table);

    private DisplayTable ToDisplayTable(DataTable table, int limit) {
        var columns = new string[table.Columns.Count];
        var types = new string[table.Columns.Count];
        for (var i = 0; i < table.Columns.Count; i++) {
            columns[i] = table.Columns[i].ColumnName;
            types[i] = DisplayTable.KindOf(table.Columns[i].DataType);
        }

        var rows = new List<IReadOnlyList<string>>();
        var take = Math.Min(table.Rows.Count, Math.Max(0, limit));
        for (var r = 0; r < take; r++) {
            var dataRow = table.Rows[r];
            var cells = new string[table.Columns.Count];
            for (var c = 0; c < table.Columns.Count; c++) {
                cells[c] = DisplayTable.CellText(dataRow[c]);
            }
            rows.Add(cells);
        }

        return new DisplayTable(table, columns, rows, types, table.Rows.Count);
    }

    /// <summary>A DataRow surfaced as <c>dynamic</c>: member access and indexing by column.</summary>
    private sealed class DynamicRow : DynamicObject {
        private readonly DataRow _row;
        private readonly DataColumnCollection _columns;

        public DynamicRow(DataRow row, DataColumnCollection columns) {
            _row = row;
            _columns = columns;
        }

        public object this[string column] => Normalize(_row[column]);

        public override bool TryGetMember(GetMemberBinder binder, out object result) {
            if (_columns.Contains(binder.Name)) {
                result = Normalize(_row[binder.Name]);
                return true;
            }
            result = null;
            return false;
        }

        public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object result) {
            if (indexes.Length == 1 && indexes[0] is string name && _columns.Contains(name)) {
                result = Normalize(_row[name]);
                return true;
            }
            if (indexes.Length == 1 && indexes[0] is int ordinal) {
                result = Normalize(_row[ordinal]);
                return true;
            }
            result = null;
            return false;
        }

        public override IEnumerable<string> GetDynamicMemberNames() =>
            _columns.Cast<DataColumn>().Select(c => c.ColumnName);

        private static object Normalize(object value) => value is DBNull ? null : value;
    }
}

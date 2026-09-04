using System;
using System.Data;

namespace ClrKernel.Database;

/// <summary>
/// A reader showing a chosen subset of another reader's columns, in a chosen order.
///
/// <para>
/// This exists for Dapper's constructor mapping, which is positional: it wants a
/// constructor whose parameters match the result's columns one for one, in order.
/// A notebook does not write queries that way — <c>SELECT *</c> returns more
/// columns than the record has and in the table's order, not the record's — so
/// the columns are lined up here and Dapper is handed the shape it asks for.
/// </para>
/// <para>
/// Read-through, not a copy: <see cref="Read"/> and the value accessors go
/// straight to the underlying reader, so streaming a large result still streams.
/// </para>
/// </summary>
internal sealed class ProjectedReader : IDataReader {
    private readonly IDataReader _inner;
    private readonly int[] _ordinals;

    public ProjectedReader(IDataReader inner, int[] ordinals) {
        _inner = inner;
        _ordinals = ordinals;
    }

    private int Source(int i) => _ordinals[i];

    public int FieldCount => _ordinals.Length;
    public string GetName(int i) => _inner.GetName(Source(i));
    public Type GetFieldType(int i) => _inner.GetFieldType(Source(i));
    public string GetDataTypeName(int i) => _inner.GetDataTypeName(Source(i));
    public object GetValue(int i) => _inner.GetValue(Source(i));
    public bool IsDBNull(int i) => _inner.IsDBNull(Source(i));

    public int GetOrdinal(string name) {
        for (var i = 0; i < _ordinals.Length; i++) {
            if (string.Equals(GetName(i), name, StringComparison.OrdinalIgnoreCase)) {
                return i;
            }
        }
        throw new IndexOutOfRangeException(name);
    }

    public int GetValues(object[] values) {
        var n = Math.Min(values.Length, _ordinals.Length);
        for (var i = 0; i < n; i++) {
            values[i] = GetValue(i);
        }
        return n;
    }

    public bool Read() => _inner.Read();
    public bool NextResult() => _inner.NextResult();
    public int Depth => _inner.Depth;
    public bool IsClosed => _inner.IsClosed;
    public int RecordsAffected => _inner.RecordsAffected;

    // Not disposed or closed here: this borrows the reader, and whoever opened it
    // still owns closing it.
    public void Dispose() { }
    public void Close() { }

    public DataTable GetSchemaTable() => _inner.GetSchemaTable();
    public object this[int i] => GetValue(i);
    public object this[string name] => GetValue(GetOrdinal(name));

    public bool GetBoolean(int i) => _inner.GetBoolean(Source(i));
    public byte GetByte(int i) => _inner.GetByte(Source(i));
    public long GetBytes(int i, long offset, byte[] buffer, int bufferOffset, int length) =>
        _inner.GetBytes(Source(i), offset, buffer, bufferOffset, length);
    public char GetChar(int i) => _inner.GetChar(Source(i));
    public long GetChars(int i, long offset, char[] buffer, int bufferOffset, int length) =>
        _inner.GetChars(Source(i), offset, buffer, bufferOffset, length);
    public IDataReader GetData(int i) => _inner.GetData(Source(i));
    public DateTime GetDateTime(int i) => _inner.GetDateTime(Source(i));
    public decimal GetDecimal(int i) => _inner.GetDecimal(Source(i));
    public double GetDouble(int i) => _inner.GetDouble(Source(i));
    public float GetFloat(int i) => _inner.GetFloat(Source(i));
    public Guid GetGuid(int i) => _inner.GetGuid(Source(i));
    public short GetInt16(int i) => _inner.GetInt16(Source(i));
    public int GetInt32(int i) => _inner.GetInt32(Source(i));
    public long GetInt64(int i) => _inner.GetInt64(Source(i));
    public string GetString(int i) => _inner.GetString(Source(i));
}

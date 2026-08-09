using System;
using System.Collections;
using System.Data;
using System.Data.Common;
using JdbcTypes = java.sql.Types;

namespace ClrKernel.Data.Jdbc;

// Ported from Integrator.Databases.Jdbc. Adapts a java.sql.ResultSet to DbDataReader.
public class JdbcDataReaderEnumerator : IEnumerator {
    private readonly JdbcDataReader _reader;
    public JdbcDataReaderEnumerator(JdbcDataReader reader) { _reader = reader; }
    public object Current => _reader;
    public bool MoveNext() => _reader.Read();
    public void Reset() => throw new NotImplementedException();
}

public class JdbcDataReader : DbDataReader {
    private java.sql.ResultSet _resultSet;
    private readonly string[] _fields;
    private readonly Type[] _types;
    private readonly string[] _typeNames;
    private readonly int[] _jdbcTypes;
    private DataTable _schemaTable;

    public JdbcDataReader(java.sql.ResultSet resultSet) {
        _resultSet = resultSet;
        var metadata = resultSet.getMetaData();
        var columnCount = metadata.getColumnCount();
        _fields = new string[columnCount];
        _types = new Type[columnCount];
        _typeNames = new string[columnCount];
        _jdbcTypes = new int[columnCount];
        for (var i = 0; i < columnCount; i++) {
            var columnIndex = i + 1;
            var columnJdbcType = metadata.getColumnType(columnIndex);
            _fields[i] = metadata.getColumnName(columnIndex);
            _types[i] = JdbcTypeToClrType(columnJdbcType);
            _typeNames[i] = metadata.getColumnTypeName(columnIndex);
            _jdbcTypes[i] = columnJdbcType;
        }
    }

    public override DataTable GetSchemaTable() {
        if (_schemaTable != null) {
            return _schemaTable;
        }
        _schemaTable = new DataTable {
            Columns = {
                { "ColumnName", typeof(string) },
                { "ColumnOrdinal", typeof(int) },
                { "BaseColumnName", typeof(string) },
                { "DataType", typeof(Type) },
                { "ProviderType", typeof(Type) },
                { "AllowDBNull", typeof(bool) },
                { "ColumnSize", typeof(int) },
            },
        };
        for (var i = 0; i < _fields.Length; i++) {
            var row = _schemaTable.Rows.Add();
            var name = string.IsNullOrWhiteSpace(_fields[i]) ? "Column " + i : _fields[i];
            var type = _types[i];
            row[0] = name;
            row[1] = i;
            row[2] = name;
            row[3] = type;
            row[4] = type;
            row[5] = true;
            row[6] = 0;
        }
        return _schemaTable;
    }

    public override int FieldCount => _fields.Length;
    public override bool NextResult() => false;
    public override bool Read() => _resultSet.next();
    public override string GetName(int i) => _fields[i];
    public override int GetOrdinal(string name) => Array.IndexOf(_fields, name);
    public override bool IsDBNull(int i) => GetValue(i) == DBNull.Value;
    public override void Close() => _resultSet?.close();
    public override object GetValue(int i) => JdbcResultToClrObject(i);
    public override IEnumerator GetEnumerator() => new JdbcDataReaderEnumerator(this);

    protected override void Dispose(bool disposing) {
        if (disposing) {
            Close();
        }
        _resultSet = null;
        base.Dispose(disposing);
    }

    public override object this[int i] => GetValue(i);
    public override object this[string name] => GetValue(Array.IndexOf(_fields, name));
    public override Type GetFieldType(int i) => _types[i];
    public override string GetDataTypeName(int i) => _typeNames[i];
    public override bool GetBoolean(int i) => _resultSet.getBoolean(i + 1);
    public override byte GetByte(int i) => _resultSet.getByte(i + 1);
    public override long GetBytes(int i, long fieldOffset, byte[] buffer, int bufferoffset, int length) => throw new NotImplementedException();
    public override char GetChar(int i) => _resultSet.getString(i + 1)[0];
    public override long GetChars(int i, long fieldoffset, char[] buffer, int bufferoffset, int length) => throw new NotImplementedException();
    public override DateTime GetDateTime(int i) => ToDateTime(_resultSet.getDate(i + 1));
    public override decimal GetDecimal(int i) => ToDecimal(_resultSet.getBigDecimal(i + 1));
    public override double GetDouble(int i) => _resultSet.getDouble(i + 1);
    public override float GetFloat(int i) => _resultSet.getFloat(i + 1);
    public override short GetInt16(int i) => _resultSet.getShort(i + 1);
    public override int GetInt32(int i) => _resultSet.getInt(i + 1);
    public override long GetInt64(int i) => _resultSet.getLong(i + 1);
    public override string GetString(int i) => _resultSet.getString(i + 1);
    public override Guid GetGuid(int i) => Guid.Parse(_resultSet.getString(i + 1));

    public override int GetValues(object[] values) {
        if (values is null) {
            throw new ArgumentNullException(nameof(values));
        }
        var columns = Math.Min(values.Length, _fields.Length);
        for (var i = 0; i < columns; i++) {
            values[i] = GetValue(i);
        }
        return columns;
    }

    public override int Depth => 0;
    public override int RecordsAffected => -1;
    public override bool IsClosed => _resultSet.isClosed();
    public override bool HasRows => throw new NotImplementedException();

    // java.sql.Types → CLR type (https://docs.oracle.com/javase/8/docs/api/java/sql/Types.html)
    private static Type JdbcTypeToClrType(int type) =>
        type switch {
            JdbcTypes.BIT => typeof(bool),
            JdbcTypes.TINYINT => typeof(byte),
            JdbcTypes.SMALLINT => typeof(short),
            JdbcTypes.INTEGER => typeof(int),
            JdbcTypes.BIGINT => typeof(long),
            JdbcTypes.DOUBLE => typeof(double),
            JdbcTypes.FLOAT => typeof(float),
            JdbcTypes.REAL => typeof(float),
            JdbcTypes.CHAR => typeof(string),
            JdbcTypes.NCHAR => typeof(string),
            JdbcTypes.LONGNVARCHAR => typeof(string),
            JdbcTypes.LONGVARCHAR => typeof(string),
            JdbcTypes.NVARCHAR => typeof(string),
            JdbcTypes.VARCHAR => typeof(string),
            JdbcTypes.DECIMAL => typeof(decimal),
            JdbcTypes.NUMERIC => typeof(decimal),
            JdbcTypes.DATE => typeof(DateOnly),
            JdbcTypes.TIME => typeof(TimeOnly),
            JdbcTypes.TIMESTAMP => typeof(DateTime),
            JdbcTypes.VARBINARY => typeof(byte[]),
            JdbcTypes.BINARY => typeof(byte[]),
            _ => typeof(object),
        };

    private object JdbcResultToClrObject(int i) {
        var type = _jdbcTypes[i];
        var columnIndex = i + 1;

        object ReadDate() => _resultSet.getDate(columnIndex) is { } value ? DateOnly.FromDateTime(ToDateTime(value)) : null;
        object ReadTime() => _resultSet.getTime(columnIndex) is { } value ? TimeOnly.FromDateTime(ToDateTime(value)) : null;
        object ReadTimeStamp() => _resultSet.getTimestamp(columnIndex) is { } value ? ToDateTime(value) : (object)null;
        object ReadDecimal() => _resultSet.getBigDecimal(columnIndex) is { } value ? ToDecimal(value) : (object)null;

        object result = type switch {
            JdbcTypes.BIT => _resultSet.getBoolean(columnIndex),
            JdbcTypes.TINYINT => _resultSet.getByte(columnIndex),
            JdbcTypes.SMALLINT => _resultSet.getShort(columnIndex),
            JdbcTypes.INTEGER => _resultSet.getInt(columnIndex),
            JdbcTypes.BIGINT => _resultSet.getLong(columnIndex),
            JdbcTypes.DOUBLE => _resultSet.getDouble(columnIndex),
            JdbcTypes.FLOAT => _resultSet.getFloat(columnIndex),
            JdbcTypes.REAL => _resultSet.getFloat(columnIndex),
            JdbcTypes.CHAR => _resultSet.getString(columnIndex),
            JdbcTypes.NCHAR => _resultSet.getString(columnIndex),
            JdbcTypes.LONGNVARCHAR => _resultSet.getString(columnIndex),
            JdbcTypes.LONGVARCHAR => _resultSet.getString(columnIndex),
            JdbcTypes.NVARCHAR => _resultSet.getString(columnIndex),
            JdbcTypes.VARCHAR => _resultSet.getString(columnIndex),
            JdbcTypes.DECIMAL => ReadDecimal(),
            JdbcTypes.NUMERIC => ReadDecimal(),
            JdbcTypes.DATE => ReadDate(),
            JdbcTypes.TIME => ReadTime(),
            JdbcTypes.TIMESTAMP => ReadTimeStamp(),
            JdbcTypes.VARBINARY => _resultSet.getBytes(columnIndex),
            JdbcTypes.BINARY => _resultSet.getBytes(columnIndex),
            _ => _resultSet.getObject(columnIndex),
        };

        var wasNull = _resultSet.wasNull() || result == null;
        return wasNull ? DBNull.Value : result;
    }

    private static DateTime ToDateTime(java.util.Date date) =>
        DateTimeOffset.FromUnixTimeMilliseconds(date.getTime()).LocalDateTime;

    private static decimal ToDecimal(java.math.BigDecimal d) =>
        decimal.Parse(d.toString(), System.Globalization.NumberStyles.Float);
}

using System;
using System.Data;
using System.Text;

namespace ClrKernel.Database.Provider.SqlServer;

/// <summary>
/// Builds a SQL Server <c>CREATE TABLE</c> from a data reader's schema table —
/// used by <see cref="SqlTable"/>'s <c>createIfMissing</c> bulk-copy option.
/// </summary>
internal static class SqlServerTableDefinition {
    public static string Generate(DataTable schema, string tableName) {
        if (schema == null) {
            throw new InvalidOperationException("The data reader did not provide a schema table.");
        }

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(QuoteTable(tableName)).AppendLine(" (");
        var first = true;
        foreach (DataRow col in schema.Rows) {
            if (!first) {
                sb.AppendLine(",");
            }
            first = false;
            var name = (string)col["ColumnName"];
            var type = (Type)col["DataType"];
            var size = GetInt(col, "ColumnSize");
            var precision = GetInt(col, "NumericPrecision");
            var scale = GetInt(col, "NumericScale");
            var nullable = !schema.Columns.Contains("AllowDBNull") || col["AllowDBNull"] is DBNull || (bool)col["AllowDBNull"];
            sb.Append("    ").Append(Quote(name)).Append(' ')
                .Append(SqlType(type, size, precision, scale))
                .Append(nullable ? " NULL" : " NOT NULL");
        }
        sb.AppendLine().Append(')');
        return sb.ToString();
    }

    private static string SqlType(Type type, int size, int precision, int scale) {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(bool)) {
            return "bit";
        }
        if (t == typeof(byte)) {
            return "tinyint";
        }
        if (t == typeof(sbyte) || t == typeof(short)) {
            return "smallint";
        }
        if (t == typeof(int)) {
            return "int";
        }
        if (t == typeof(long)) {
            return "bigint";
        }
        if (t == typeof(float)) {
            return "real";
        }
        if (t == typeof(double)) {
            return "float";
        }
        if (t == typeof(decimal)) {
            var p = precision > 0 ? precision : 18;
            var s = scale >= 0 ? scale : 2;
            return $"decimal({p},{s})";
        }
        if (t == typeof(DateTime)) {
            return "datetime2";
        }
        if (t == typeof(DateTimeOffset)) {
            return "datetimeoffset";
        }
        if (t == typeof(TimeSpan)) {
            return "time";
        }
        if (t == typeof(Guid)) {
            return "uniqueidentifier";
        }
        if (t == typeof(byte[])) {
            return size > 0 && size < 8000 ? $"varbinary({size})" : "varbinary(max)";
        }
        var len = size > 0 && size < 4000 ? size.ToString() : "max";
        return $"nvarchar({len})";
    }

    private static int GetInt(DataRow row, string column) {
        if (!row.Table.Columns.Contains(column) || row[column] is DBNull) {
            return -1;
        }
        try {
            return Convert.ToInt32(row[column]);
        } catch {
            return -1;
        }
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    private static string QuoteTable(string name) {
        var parts = name.Replace("[", "").Replace("]", "").Split('.');
        for (var i = 0; i < parts.Length; i++) {
            parts[i] = Quote(parts[i]);
        }
        return string.Join(".", parts);
    }
}

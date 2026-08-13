using System;
using System.Data;
using System.Text;

namespace ClrKernel.Database.Provider.Fabric;

/// <summary>
/// Generates a Fabric-Warehouse-compatible <c>CREATE TABLE</c> from a data
/// reader's schema, and rewrites SQL Server definitions to Fabric-supported
/// types. Fabric Warehouse doesn't support <c>nvarchar</c> or <c>datetime</c>
/// (among others); UTF-8 <c>varchar</c> and <c>datetime2</c> are used instead.
/// </summary>
public static class WarehouseTableDefinition {
    public const string Utf8Collation = "Latin1_General_100_CI_AS_KS_WS_SC_UTF8";

    /// <summary>Builds a CREATE TABLE for the reader's columns.</summary>
    public static string Generate(IDataReader reader, string tableName) {
        var schema = reader.GetSchemaTable()
            ?? throw new InvalidOperationException("The data reader did not provide a schema table.");

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
                .Append(FabricType(type, size, precision, scale))
                .Append(nullable ? " NULL" : " NOT NULL");
        }
        sb.AppendLine().Append(')');
        return sb.ToString();
    }

    /// <summary>Rewrites a SQL Server CREATE definition to Fabric-supported types
    /// (nvarchar(max) → UTF-8 varchar(max); datetime → datetime2(3)).</summary>
    public static string ToFabricTypes(string definition) {
        if (string.IsNullOrEmpty(definition)) {
            return definition;
        }
        return definition
            .Replace("nvarchar(max)", "varchar(max) collate " + Utf8Collation)
            .Replace("datetime", "datetime2(3)");
    }

    internal static string FabricType(Type type, int size, int precision, int scale) {
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
            // money/smallmoney sources report the TDS "unspecified" sentinel (255)
            // as their scale (sometimes precision too); decimal(19,255) is invalid.
            var p = precision > 0 && precision <= 38 ? precision : 38;
            var s = scale >= 0 && scale <= p ? scale : 6;
            return $"decimal({p},{s})";
        }
        if (t == typeof(DateTime)) {
            return "datetime2(3)";
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
        // strings and anything else → UTF-8 varchar
        var len = size > 0 && size < 8000 ? size.ToString() : "max";
        return $"varchar({len}) collate {Utf8Collation}";
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

    internal static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    internal static string QuoteTable(string name) {
        var parts = name.Replace("[", "").Replace("]", "").Split('.');
        for (var i = 0; i < parts.Length; i++) {
            parts[i] = Quote(parts[i]);
        }
        return string.Join(".", parts);
    }
}

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace ClrKernel.Jobs;

/// <summary>One node in the object tree.</summary>
public sealed class MetadataNode {
    public string Name { get; set; }

    /// <summary>What it is, which is also which folder the tree files it under:
    /// <c>database</c>, <c>schema</c>, <c>table</c>, <c>view</c>, <c>procedure</c>,
    /// <c>function</c>.</summary>
    public string Kind { get; set; }
}

/// <summary>A column, key or index of one object — the leaf detail, fetched together
/// because opening a table to look at its columns and then not knowing its keys is
/// two round trips for one question.</summary>
public sealed class ObjectDetail {
    public IReadOnlyList<ColumnDetail> Columns { get; set; } = Array.Empty<ColumnDetail>();
    public IReadOnlyList<string> Keys { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Indexes { get; set; } = Array.Empty<string>();
}

public sealed class ColumnDetail {
    public string Name { get; set; }

    /// <summary>Rendered the way a script would spell it — <c>nvarchar(50)</c>,
    /// <c>decimal(18,2)</c> — rather than as a bare type name.</summary>
    public string Type { get; set; }

    public bool Nullable { get; set; }
    public bool PrimaryKey { get; set; }
    public bool Identity { get; set; }
}

/// <summary>
/// What SQL Server can say about itself, at the four levels the tree walks:
/// databases, schemas, the objects in a schema, and one object's columns and keys.
/// <para>
/// ponytail: no interface, because there is one implementation. The contract that
/// matters is the route's — a connection type with nothing here answers an empty
/// list, and the tree shows the connection as a leaf rather than as a folder that
/// opens onto nothing. A second provider turns this class into the first
/// implementation of one; adding the abstraction before that is inventing a shape
/// nobody has pushed on yet.
/// </para>
/// </summary>
public static class SqlServerMetadata {
    /// <summary>Whether this connection type can be browsed at all.</summary>
    public static bool Supports(string type) =>
        string.Equals(type, "SqlServer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Databases this login can actually open. A database it cannot reach is
    /// a folder that errors when clicked, so it is not offered.</summary>
    public static Task<IReadOnlyList<MetadataNode>> DatabasesAsync(
        SqlConnection live, CancellationToken cancellationToken) =>
        NodesAsync(live,
            "SELECT name FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name",
            "database", cancellationToken);

    public static async Task<IReadOnlyList<MetadataNode>> SchemasAsync(
        SqlConnection live, string database, CancellationToken cancellationToken) {
        await UseAsync(live, database, cancellationToken).ConfigureAwait(false);
        return await NodesAsync(live,
            // The ones SQL Server ships with are noise in a tree somebody is looking
            // for their own tables in.
            @"SELECT s.name FROM sys.schemas s
              WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest', 'db_owner',
                                   'db_accessadmin', 'db_securityadmin', 'db_ddladmin',
                                   'db_backupoperator', 'db_datareader', 'db_datawriter',
                                   'db_denydatareader', 'db_denydatawriter')
              ORDER BY s.name",
            "schema", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every object in one schema in a single pass. The tree groups them into
    /// Tables, Views and Programmability itself — three round trips to fill three
    /// folders that are always opened together is three times the latency.</summary>
    public static async Task<IReadOnlyList<MetadataNode>> ObjectsAsync(
        SqlConnection live, string database, string schema, CancellationToken cancellationToken) {
        await UseAsync(live, database, cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand(
            @"SELECT o.name,
                     CASE o.type WHEN 'U' THEN 'table' WHEN 'V' THEN 'view'
                                 WHEN 'P' THEN 'procedure' ELSE 'function' END AS kind
              FROM sys.objects o
              JOIN sys.schemas s ON s.schema_id = o.schema_id
              WHERE s.name = @schema AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
              ORDER BY o.name", live);
        command.Parameters.AddWithValue("@schema", schema);
        var nodes = new List<MetadataNode>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            nodes.Add(new MetadataNode { Name = reader.GetString(0), Kind = reader.GetString(1) });
        }
        return nodes;
    }

    public static async Task<ObjectDetail> DetailAsync(
        SqlConnection live, string database, string schema, string obj, CancellationToken cancellationToken) {
        await UseAsync(live, database, cancellationToken).ConfigureAwait(false);
        return new ObjectDetail {
            Columns = await ColumnsAsync(live, schema, obj, cancellationToken).ConfigureAwait(false),
            Keys = await StringsAsync(live,
                @"SELECT kc.name + ' (' + kc.type_desc + ')'
                  FROM sys.key_constraints kc
                  JOIN sys.objects o ON o.object_id = kc.parent_object_id
                  JOIN sys.schemas s ON s.schema_id = o.schema_id
                  WHERE s.name = @schema AND o.name = @object
                  UNION ALL
                  SELECT fk.name + ' -> ' + OBJECT_SCHEMA_NAME(fk.referenced_object_id)
                         + '.' + OBJECT_NAME(fk.referenced_object_id)
                  FROM sys.foreign_keys fk
                  JOIN sys.objects o ON o.object_id = fk.parent_object_id
                  JOIN sys.schemas s ON s.schema_id = o.schema_id
                  WHERE s.name = @schema AND o.name = @object",
                schema, obj, cancellationToken).ConfigureAwait(false),
            Indexes = await StringsAsync(live,
                @"SELECT i.name + CASE WHEN i.is_unique = 1 THEN ' (unique)' ELSE '' END
                  FROM sys.indexes i
                  JOIN sys.objects o ON o.object_id = i.object_id
                  JOIN sys.schemas s ON s.schema_id = o.schema_id
                  WHERE s.name = @schema AND o.name = @object AND i.name IS NOT NULL
                  ORDER BY i.name",
                schema, obj, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// The object's definition as text. Views, procedures and functions keep theirs on
    /// the server; a table has none, so one is generated from its columns — which is
    /// closer to what somebody wants than the alternative of refusing.
    /// </summary>
    public static async Task<string> ScriptAsync(
        SqlConnection live, string database, string schema, string obj, string kind,
        CancellationToken cancellationToken) {
        await UseAsync(live, database, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(kind, "table", StringComparison.OrdinalIgnoreCase)) {
            using var command = new SqlCommand(
                "SELECT OBJECT_DEFINITION(OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@object)))", live);
            command.Parameters.AddWithValue("@schema", schema);
            command.Parameters.AddWithValue("@object", obj);
            var definition = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return definition as string
                ?? "-- No definition is available. It may be encrypted, or this login cannot see it.";
        }

        var columns = await ColumnsAsync(live, schema, obj, cancellationToken).ConfigureAwait(false);
        var script = new StringBuilder()
            .Append("CREATE TABLE ").Append(Quote(schema)).Append('.').Append(Quote(obj)).AppendLine(" (");
        for (var i = 0; i < columns.Count; i++) {
            var column = columns[i];
            script.Append("    ").Append(Quote(column.Name)).Append(' ').Append(column.Type)
                .Append(column.Identity ? " IDENTITY" : string.Empty)
                .Append(column.Nullable ? " NULL" : " NOT NULL")
                .AppendLine(i == columns.Count - 1 ? string.Empty : ",");
        }
        var keys = columns.Where(c => c.PrimaryKey).Select(c => Quote(c.Name)).ToList();
        if (keys.Count > 0) {
            script.Append("    , PRIMARY KEY (").Append(string.Join(", ", keys)).AppendLine(")");
        }
        return script.AppendLine(");").ToString();
    }

    // --- the queries --------------------------------------------------------

    private static async Task<IReadOnlyList<ColumnDetail>> ColumnsAsync(
        SqlConnection live, string schema, string obj, CancellationToken cancellationToken) {
        using var command = new SqlCommand(
            @"SELECT c.name, t.name AS type_name, c.max_length, c.precision, c.scale,
                     c.is_nullable, c.is_identity,
                     CAST(CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS bit) AS is_key
              FROM sys.columns c
              JOIN sys.objects o ON o.object_id = c.object_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
              JOIN sys.types t ON t.user_type_id = c.user_type_id
              LEFT JOIN (
                  SELECT ic.object_id, ic.column_id
                  FROM sys.index_columns ic
                  JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                  WHERE i.is_primary_key = 1
              ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
              WHERE s.name = @schema AND o.name = @object
              ORDER BY c.column_id", live);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@object", obj);

        var columns = new List<ColumnDetail>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            columns.Add(new ColumnDetail {
                Name = reader.GetString(0),
                Type = TypeText(reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4)),
                Nullable = reader.GetBoolean(5),
                Identity = reader.GetBoolean(6),
                PrimaryKey = reader.GetBoolean(7),
            });
        }
        return columns;
    }

    private static async Task<IReadOnlyList<string>> StringsAsync(
        SqlConnection live, string sql, string schema, string obj, CancellationToken cancellationToken) {
        using var command = new SqlCommand(sql, live);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@object", obj);
        var values = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    private static async Task<IReadOnlyList<MetadataNode>> NodesAsync(
        SqlConnection live, string sql, string kind, CancellationToken cancellationToken) {
        using var command = new SqlCommand(sql, live);
        var nodes = new List<MetadataNode>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            nodes.Add(new MetadataNode { Name = reader.GetString(0), Kind = kind });
        }
        return nodes;
    }

    /// <summary>
    /// Switches the open connection to a database.
    /// <para>
    /// <see cref="SqlConnection.ChangeDatabase"/> rather than <c>USE</c> in the text:
    /// the name arrives from the client, and a database name is an identifier that
    /// cannot be parameterized — building <c>USE [</c> + name + <c>]</c> is the
    /// injection this whole file otherwise avoids.
    /// </para>
    /// </summary>
    private static Task UseAsync(SqlConnection live, string database, CancellationToken cancellationToken) {
        if (!string.IsNullOrWhiteSpace(database)
            && !string.Equals(live.Database, database, StringComparison.OrdinalIgnoreCase)) {
            return live.ChangeDatabaseAsync(database, cancellationToken);
        }
        return Task.CompletedTask;
    }

    /// <summary>The type as a script would write it, so the columns list and the
    /// generated CREATE TABLE agree.</summary>
    private static string TypeText(string name, short maxLength, byte precision, byte scale) {
        switch (name.ToLowerInvariant()) {
            case "decimal":
            case "numeric":
                return $"{name}({precision},{scale})";
            case "datetime2":
            case "datetimeoffset":
            case "time":
                return $"{name}({scale})";
            case "char":
            case "varchar":
            case "binary":
            case "varbinary":
                return $"{name}({(maxLength == -1 ? "max" : maxLength.ToString())})";
            case "nchar":
            case "nvarchar":
                // sys.columns counts bytes; a script counts characters.
                return $"{name}({(maxLength == -1 ? "max" : (maxLength / 2).ToString())})";
            default:
                return name;
        }
    }

    private static string Quote(string identifier) =>
        "[" + (identifier ?? string.Empty).Replace("]", "]]") + "]";
}

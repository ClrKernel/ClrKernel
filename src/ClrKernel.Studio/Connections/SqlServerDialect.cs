using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using ClrKernel.Database;
using ClrKernel.Database.Provider.SqlServer;
using Microsoft.Data.SqlClient;
namespace ClrKernel.Studio;

/// <summary>One node in the object tree.</summary>
public sealed class MetadataNode {
    public string Name { get; set; }

    /// <summary>What it is, which is also which folder the tree files it under:
    /// <c>database</c>, <c>schema</c>, <c>table</c>, <c>view</c>, <c>procedure</c>,
    /// <c>function</c>.</summary>
    public string Kind { get; set; }
}

/// <summary>
/// One object and its column names — the shape completion needs, which is
/// deliberately not the shape the tree needs. The tree asks about one object at a
/// time because somebody is clicking; completion needs everything at once because
/// somebody is typing and will not wait.
/// </summary>
public sealed class CompletionObject {
    public string Schema { get; set; }
    public string Name { get; set; }

    /// <summary><c>table</c> or <c>view</c>.</summary>
    public string Kind { get; set; }

    public IReadOnlyList<string> Columns { get; set; } = Array.Empty<string>();
}

/// <summary>Everything a database offers to complete against.</summary>
public sealed class CompletionSchema {
    public string Database { get; set; }
    public IReadOnlyList<CompletionObject> Objects { get; set; } = Array.Empty<CompletionObject>();

    /// <summary>The database was too big to send whole. What is here is usable; it
    /// is just not all of it, and the editor says so rather than quietly completing
    /// against half a schema.</summary>
    public bool Truncated { get; set; }
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
/// The second provider arrived, so this is now the first implementation of
/// <see cref="IConnectionDialect"/> rather than the only thing there is. The
/// connection string still comes from the provider package's own mapping; what is
/// here is the catalog.
/// </para>
/// </summary>
public sealed class SqlServerDialect : IConnectionDialect {
    public string Type => "SqlServer";

    /// <summary>
    /// The database is put into the connection string rather than switched afterwards.
    /// SQL Server would allow either; PostgreSQL allows only this, and one shape for
    /// both is worth more than SQL Server's extra option.
    /// </summary>
    public DbConnection Open(RawConnectionNode node, SecretStore secrets, string database) {
        var spec = SqlConnectionConfig.FromNode(node);
        var builder = new SqlConnectionStringBuilder(
            spec.BuildConnectionString(secrets ?? new SecretStore()));
        // On the built string rather than on the node: a connection saved as a raw
        // connection string is passed through untouched by the mapping, so a database
        // set on the node would be dropped and the tree would browse whichever one the
        // string happens to name.
        if (!string.IsNullOrWhiteSpace(database)) {
            builder.InitialCatalog = database;
        }
        // Only when the connection has not already asked for something: a raw
        // connection string somebody pasted is theirs, and second-guessing what it
        // says is how a deliberate setting silently stops applying.
        if (builder.LoadBalanceTimeout == 0) {
            builder.LoadBalanceTimeout = IConnectionDialect.PoolLifetimeSeconds;
        }
        return new SqlConnection(builder.ConnectionString);
    }

    /// <summary>PRINT and RAISERROR(…, 0, …) arrive here, not in the reader.</summary>
    public IDisposable OnInfoMessage(DbConnection live, Action<string> message) {
        if (live is not SqlConnection sql) {
            return null;
        }
        void Handler(object _, SqlInfoMessageEventArgs e) {
            foreach (SqlError error in e.Errors) {
                message(error.Message);
            }
        }
        sql.InfoMessage += Handler;
        return new Unsubscribe(() => sql.InfoMessage -= Handler);
    }

    public IEnumerable<string> ExtraErrors(DbException error) =>
        error is SqlException sql
            ? sql.Errors.Cast<SqlError>().Select(e => e.Message)
            : Array.Empty<string>();

    public void ClearPool(DbConnection live) {
        if (live is SqlConnection sql) {
            SqlConnection.ClearPool(sql);
        }
    }

    private sealed class Unsubscribe : IDisposable {
        private readonly Action _off;
        public Unsubscribe(Action off) => _off = off;
        public void Dispose() => _off();
    }

    /// <summary>Databases this login can actually open. A database it cannot reach is
    /// a folder that errors when clicked, so it is not offered.</summary>
    public Task<IReadOnlyList<MetadataNode>> DatabasesAsync(
        DbConnection live, CancellationToken cancellationToken) =>
        Ado.NodesAsync(live,
            "SELECT name FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name",
            "database", cancellationToken);

    public Task<IReadOnlyList<MetadataNode>> SchemasAsync(
        DbConnection live, CancellationToken cancellationToken) {
        return Ado.NodesAsync(live,
            // The ones SQL Server ships with are noise in a tree somebody is looking
            // for their own tables in.
            @"SELECT s.name FROM sys.schemas s
              WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest', 'db_owner',
                                   'db_accessadmin', 'db_securityadmin', 'db_ddladmin',
                                   'db_backupoperator', 'db_datareader', 'db_datawriter',
                                   'db_denydatareader', 'db_denydatawriter')
              ORDER BY s.name",
            "schema", cancellationToken);
    }

    /// <summary>Every object in one schema in a single pass. The tree groups them into
    /// Tables, Views and Programmability itself — three round trips to fill three
    /// folders that are always opened together is three times the latency.</summary>
    public async Task<IReadOnlyList<MetadataNode>> ObjectsAsync(
        DbConnection live, string schema, CancellationToken cancellationToken) {
        using var command = Ado.Command(live,
            @"SELECT o.name,
                     CASE o.type WHEN 'U' THEN 'table' WHEN 'V' THEN 'view'
                                 WHEN 'P' THEN 'procedure' ELSE 'function' END AS kind
              FROM sys.objects o
              JOIN sys.schemas s ON s.schema_id = o.schema_id
              WHERE s.name = @schema AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
              ORDER BY o.name", ("@schema", schema));
        var nodes = new List<MetadataNode>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            nodes.Add(new MetadataNode { Name = reader.GetString(0), Kind = reader.GetString(1) });
        }
        return nodes;
    }

    public async Task<ObjectDetail> DetailAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) {
        return new ObjectDetail {
            Columns = await ColumnsAsync(live, schema, obj, cancellationToken).ConfigureAwait(false),
            Keys = await KeysAsync(live, schema, obj, cancellationToken).ConfigureAwait(false),
            Indexes = await IndexesAsync(live, schema, obj, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Primary and unique keys, then foreign keys.
    /// <para>
    /// Two queries composing their labels in C#, rather than one <c>UNION ALL</c> of
    /// concatenated strings. That version failed on any database whose collation
    /// differs from the server's — <c>name</c> is <c>sysname</c> in the database's
    /// collation, <c>type_desc</c> is <c>nvarchar</c> in the server's, and
    /// concatenating or union-ing the two is a collation conflict SQL Server refuses
    /// outright ("Cannot resolve collation conflict … in add operator"). Sprinkling
    /// <c>COLLATE DATABASE_DEFAULT</c> would have fixed that one query; not building
    /// strings in SQL at all removes the whole class.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> KeysAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) {
        var keys = new List<string>();
        await ReadAsync(live,
            @"SELECT kc.name, kc.type_desc
              FROM sys.key_constraints kc
              JOIN sys.objects o ON o.object_id = kc.parent_object_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
              WHERE s.name = @schema AND o.name = @object
              ORDER BY kc.name",
            schema, obj, cancellationToken,
            reader => keys.Add($"{Text(reader, 0)} ({Text(reader, 1)})")).ConfigureAwait(false);

        await ReadAsync(live,
            // The referenced names come back null when this login cannot see the other
            // end of the key; that is a row worth labelling, not one worth throwing on.
            @"SELECT fk.name,
                     OBJECT_SCHEMA_NAME(fk.referenced_object_id),
                     OBJECT_NAME(fk.referenced_object_id)
              FROM sys.foreign_keys fk
              JOIN sys.objects o ON o.object_id = fk.parent_object_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
              WHERE s.name = @schema AND o.name = @object
              ORDER BY fk.name",
            schema, obj, cancellationToken,
            reader => keys.Add(
                $"{Text(reader, 0)} → {Text(reader, 1) ?? "?"}.{Text(reader, 2) ?? "?"}"))
            .ConfigureAwait(false);
        return keys;
    }

    private static async Task<IReadOnlyList<string>> IndexesAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) {
        var indexes = new List<string>();
        await ReadAsync(live,
            @"SELECT i.name, i.is_unique
              FROM sys.indexes i
              JOIN sys.objects o ON o.object_id = i.object_id
              JOIN sys.schemas s ON s.schema_id = o.schema_id
              WHERE s.name = @schema AND o.name = @object AND i.name IS NOT NULL
              ORDER BY i.name",
            schema, obj, cancellationToken,
            reader => indexes.Add(Text(reader, 0) + (reader.GetBoolean(1) ? " (unique)" : string.Empty)))
            .ConfigureAwait(false);
        return indexes;
    }

    /// <summary>
    /// Scripts an object the way SSMS's "Script as" does.
    /// <para>
    /// <c>create</c> asks the server for a view's or procedure's stored definition; a
    /// table has none, so one is generated from its columns. Everything else —
    /// <c>drop</c>, <c>select</c>, <c>insert</c>, <c>update</c>, <c>delete</c>,
    /// <c>execute</c> — is generated here from the same column list the tree already
    /// fetched, and is meant to be edited rather than run as it stands: the
    /// placeholders say so out loud, which is exactly what SSMS emits and why nobody
    /// runs one by accident.
    /// </para>
    /// </summary>
    public async Task<string> ScriptAsync(
        DbConnection live, string schema, string obj, string kind, string variant,
        CancellationToken cancellationToken) {
        var name = Quote(schema) + "." + Quote(obj);
        var isTable = string.Equals(kind, "table", StringComparison.OrdinalIgnoreCase);

        switch ((variant ?? "create").ToLowerInvariant()) {
            case "drop":
                return $"DROP {Noun(kind)} {name};{Environment.NewLine}";

            case "execute":
                return $"EXEC {name};{Environment.NewLine}";

            case "select": {
                    var columns = await ColumnsAsync(live, schema, obj, cancellationToken).ConfigureAwait(false);
                    var list = columns.Count == 0
                        ? "*"
                        : string.Join("," + Environment.NewLine + "       ", columns.Select(c => Quote(c.Name)));
                    return $"SELECT TOP 1000 {list}{Environment.NewLine}FROM {name};{Environment.NewLine}";
                }

            case "insert": {
                    var columns = await WritableAsync(live, schema, obj, cancellationToken).ConfigureAwait(false);
                    var names = string.Join(", ", columns.Select(c => Quote(c.Name)));
                    var values = string.Join(", ", columns.Select(Placeholder));
                    return $"INSERT INTO {name} ({names}){Environment.NewLine}VALUES ({values});{Environment.NewLine}";
                }

            case "update": {
                    var columns = await WritableAsync(live, schema, obj, cancellationToken).ConfigureAwait(false);
                    var sets = string.Join("," + Environment.NewLine + "    ",
                        columns.Select(c => $"{Quote(c.Name)} = {Placeholder(c)}"));
                    return $"UPDATE {name}{Environment.NewLine}SET {sets}{Environment.NewLine}"
                        + $"WHERE <search condition,,>;{Environment.NewLine}";
                }

            case "delete":
                return $"DELETE FROM {name}{Environment.NewLine}WHERE <search condition,,>;{Environment.NewLine}";

            default:
                break;
        }

        if (!isTable) {
            using var command = Ado.Command(live,
                "SELECT OBJECT_DEFINITION(OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@object)))",
                ("@schema", schema), ("@object", obj));
            var definition = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return definition as string
                ?? "-- No definition is available. It may be encrypted, or this login cannot see it.";
        }

        var all = await ColumnsAsync(live, schema, obj, cancellationToken).ConfigureAwait(false);
        var script = new StringBuilder()
            .Append("CREATE TABLE ").Append(name).AppendLine(" (");
        for (var i = 0; i < all.Count; i++) {
            var column = all[i];
            script.Append("    ").Append(Quote(column.Name)).Append(' ').Append(column.Type)
                .Append(column.Identity ? " IDENTITY" : string.Empty)
                .Append(column.Nullable ? " NULL" : " NOT NULL")
                .AppendLine(i == all.Count - 1 ? string.Empty : ",");
        }
        var keys = all.Where(c => c.PrimaryKey).Select(c => Quote(c.Name)).ToList();
        if (keys.Count > 0) {
            script.Append("    , PRIMARY KEY (").Append(string.Join(", ", keys)).AppendLine(")");
        }
        return script.AppendLine(");").ToString();
    }

    /// <summary>The columns an INSERT or UPDATE may name — identity columns are the
    /// server's to fill, and listing them produces a statement that always fails.</summary>
    private static async Task<IReadOnlyList<ColumnDetail>> WritableAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) =>
        (await ColumnsAsync(live, schema, obj, cancellationToken).ConfigureAwait(false))
            .Where(c => !c.Identity).ToList();

    /// <summary>SSMS's placeholder shape, kept because people recognise it and because
    /// a script full of these cannot be run by accident.</summary>
    private static string Placeholder(ColumnDetail column) => $"<{column.Name}, {column.Type},>";

    private static string Noun(string kind) => (kind ?? string.Empty).ToLowerInvariant() switch {
        "view" => "VIEW",
        "procedure" => "PROCEDURE",
        "function" => "FUNCTION",
        _ => "TABLE",
    };

    /// <summary>
    /// Every table and view in a database with its column names, in one pass.
    /// <para>
    /// One query rather than the tree's object-at-a-time walk: completion cannot
    /// wait for a round trip per table, and a catalog join is cheap even on a large
    /// schema. Procedures and functions are left out — they have no columns to
    /// complete and would only pad the payload.
    /// </para>
    /// </summary>
    public async Task<CompletionSchema> CompletionsAsync(
        DbConnection live, CancellationToken cancellationToken) {
        using var command = Ado.Command(live,
            @"SELECT s.name, o.name, CASE o.type WHEN 'V' THEN 'view' ELSE 'table' END, c.name
              FROM sys.objects o
              JOIN sys.schemas s ON s.schema_id = o.schema_id
              LEFT JOIN sys.columns c ON c.object_id = o.object_id
              WHERE o.type IN ('U', 'V')
                AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
              ORDER BY s.name, o.name, c.column_id");

        var objects = new List<CompletionObject>();
        var columns = new List<string>();
        string schema = null, name = null, kind = null;
        var rows = 0;
        var truncated = false;

        void Flush() {
            if (name != null) {
                objects.Add(new CompletionObject {
                    Schema = schema,
                    Name = name,
                    Kind = kind,
                    Columns = columns.ToList(),
                });
            }
        }

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            if (++rows > _maxCompletionRows) {
                // Stop on a whole object rather than mid-way through one: half a
                // table's columns is worse than none, because it looks complete.
                truncated = true;
                break;
            }
            var rowSchema = Text(reader, 0);
            var rowName = Text(reader, 1);
            if (rowName != name || rowSchema != schema) {
                Flush();
                schema = rowSchema;
                name = rowName;
                kind = Text(reader, 2);
                columns = new List<string>();
            }
            if (Text(reader, 3) is { } column) {
                columns.Add(column);
            }
        }
        Flush();

        return new CompletionSchema {
            Database = live.Database,
            Objects = objects,
            Truncated = truncated,
        };
    }

    /// <summary>How much of a schema is worth sending for completion. A warehouse
    /// with more columns than this completes against the first of them and says so —
    /// which is better than a payload nobody can afford to fetch.</summary>
    private const int _maxCompletionRows = 20_000;

    // --- the queries --------------------------------------------------------

    private static async Task<IReadOnlyList<ColumnDetail>> ColumnsAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) {
        using var command = Ado.Command(live,
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
              ORDER BY c.column_id", ("@schema", schema), ("@object", obj));

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

    /// <summary>Runs a schema/object query and hands each row to <paramref name="row"/>.</summary>
    private static async Task ReadAsync(
        DbConnection live, string sql, string schema, string obj, CancellationToken cancellationToken,
        Action<DbDataReader> row) {
        using var command = Ado.Command(live, sql, ("@schema", schema), ("@object", obj));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            row(reader);
        }
    }

    private static string Text(DbDataReader reader, int ordinal) => Ado.Text(reader, ordinal);

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

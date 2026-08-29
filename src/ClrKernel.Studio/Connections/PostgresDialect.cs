using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using ClrKernel.Database;
using ClrKernel.Database.Provider.Postgres;
using Npgsql;

namespace ClrKernel.Studio;

/// <summary>
/// What PostgreSQL can say about itself, at the levels the tree walks.
/// <para>
/// The information_schema is used where it answers, and <c>pg_catalog</c> where it
/// does not — indexes and identity columns have no portable view. Two differences
/// from SQL Server shape everything here: a database is a separate connection rather
/// than a <c>USE</c> away, which is why <see cref="Open"/> takes it; and the system
/// schemas are named rather than flagged, so they are excluded by name.
/// </para>
/// </summary>
public sealed class PostgresDialect : IConnectionDialect {
    public string Type => PostgresConnectionConfig.TypeName;

    public DbConnection Open(RawConnectionNode node, SecretStore secrets, string database) {
        var live = (NpgsqlConnection)Postgres
            .FromNode(database == null ? node : node.With("database", database), secrets)
            .Create();
        var builder = new NpgsqlConnectionStringBuilder(live.ConnectionString);
        // Npgsql's name for what SQL Server calls LoadBalanceTimeout. Left alone when
        // it is already set: a pasted connection string is the author's, not ours.
        if (builder.ConnectionIdleLifetime == 300 && !builder.ContainsKey("Connection Idle Lifetime")) {
            builder.ConnectionIdleLifetime = IConnectionDialect.PoolLifetimeSeconds;
        }
        live.ConnectionString = builder.ConnectionString;
        return live;
    }

    /// <summary>RAISE NOTICE and friends arrive here rather than in the reader.</summary>
    public IDisposable OnInfoMessage(DbConnection live, Action<string> message) {
        if (live is not NpgsqlConnection postgres) {
            return null;
        }
        void Handler(object _, NpgsqlNoticeEventArgs e) => message(e.Notice.MessageText);
        postgres.Notice += Handler;
        return new Unsubscribe(() => postgres.Notice -= Handler);
    }

    /// <summary>
    /// PostgreSQL's detail and hint, which are the useful half of a failure —
    /// "column x does not exist" alone leaves out the suggestion beside it.
    /// </summary>
    public IEnumerable<string> ExtraErrors(DbException error) {
        if (error is not PostgresException postgres) {
            yield break;
        }
        if (!string.IsNullOrEmpty(postgres.Detail)) {
            yield return postgres.Detail;
        }
        if (!string.IsNullOrEmpty(postgres.Hint)) {
            yield return postgres.Hint;
        }
    }

    public void ClearPool(DbConnection live) {
        if (live is NpgsqlConnection postgres) {
            NpgsqlConnection.ClearPool(postgres);
        }
    }

    private sealed class Unsubscribe : IDisposable {
        private readonly Action _off;
        public Unsubscribe(Action off) => _off = off;
        public void Dispose() => _off();
    }

    /// <summary>
    /// Databases this login may connect to. <c>datallowconn</c> excludes the template
    /// databases, which are folders that error when clicked.
    /// </summary>
    public Task<IReadOnlyList<MetadataNode>> DatabasesAsync(
        DbConnection live, CancellationToken cancellationToken) =>
        Ado.NodesAsync(live,
            @"SELECT datname FROM pg_database
              WHERE datallowconn AND NOT datistemplate
                AND has_database_privilege(datname, 'CONNECT')
              ORDER BY datname",
            "database", cancellationToken);

    public Task<IReadOnlyList<MetadataNode>> SchemasAsync(
        DbConnection live, CancellationToken cancellationToken) =>
        Ado.NodesAsync(live,
            // pg_toast_* and pg_temp_* are per-session plumbing; nobody is looking for
            // their tables in there.
            @"SELECT nspname FROM pg_namespace
              WHERE nspname NOT IN ('pg_catalog', 'information_schema')
                AND nspname NOT LIKE 'pg_toast%' AND nspname NOT LIKE 'pg_temp%'
                AND has_schema_privilege(nspname, 'USAGE')
              ORDER BY nspname",
            "schema", cancellationToken);

    /// <summary>Tables, views and routines in one pass, for the same reason SQL Server
    /// does it in one: three folders are always opened together.</summary>
    public async Task<IReadOnlyList<MetadataNode>> ObjectsAsync(
        DbConnection live, string schema, CancellationToken cancellationToken) {
        var nodes = new List<MetadataNode>();
        nodes.AddRange(await Ado.NodesAsync(live,
            @"SELECT c.relname,
                     CASE c.relkind WHEN 'v' THEN 'view' WHEN 'm' THEN 'view' ELSE 'table' END
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE n.nspname = @schema AND c.relkind IN ('r', 'p', 'v', 'm')
              ORDER BY c.relname",
            "table", cancellationToken, ("@schema", schema)).ConfigureAwait(false));
        nodes.AddRange(await Ado.NodesAsync(live,
            // prokind: 'f' is a function, 'p' a procedure, 'a' an aggregate and 'w' a
            // window function. The last two have no body worth scripting.
            @"SELECT p.proname, CASE p.prokind WHEN 'p' THEN 'procedure' ELSE 'function' END
              FROM pg_proc p
              JOIN pg_namespace n ON n.oid = p.pronamespace
              WHERE n.nspname = @schema AND p.prokind IN ('f', 'p')
              ORDER BY p.proname",
            "function", cancellationToken, ("@schema", schema)).ConfigureAwait(false));
        return nodes;
    }

    public async Task<ObjectDetail> DetailAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) =>
        new ObjectDetail {
            Columns = await ColumnsAsync(live, schema, obj, cancellationToken).ConfigureAwait(false),
            Keys = await KeysAsync(live, schema, obj, cancellationToken).ConfigureAwait(false),
            Indexes = await IndexesAsync(live, schema, obj, cancellationToken).ConfigureAwait(false),
        };

    public async Task<CompletionSchema> CompletionsAsync(
        DbConnection live, CancellationToken cancellationToken) {
        using var command = Ado.Command(live,
            @"SELECT n.nspname, c.relname,
                     CASE c.relkind WHEN 'v' THEN 'view' WHEN 'm' THEN 'view' ELSE 'table' END,
                     a.attname
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
              LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
              WHERE c.relkind IN ('r', 'p', 'v', 'm')
                AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                AND n.nspname NOT LIKE 'pg_toast%' AND n.nspname NOT LIKE 'pg_temp%'
              ORDER BY n.nspname, c.relname, a.attnum");

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
                // On a whole object, not mid-way through one: half a table's columns
                // looks complete and is not.
                truncated = true;
                break;
            }
            var rowSchema = Ado.Text(reader, 0);
            var rowName = Ado.Text(reader, 1);
            if (rowName != name || rowSchema != schema) {
                Flush();
                schema = rowSchema;
                name = rowName;
                kind = Ado.Text(reader, 2);
                columns = new List<string>();
            }
            if (Ado.Text(reader, 3) is { } column) {
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

    /// <summary>
    /// The same "Script as" variants SQL Server offers, in PostgreSQL's spelling —
    /// <c>LIMIT</c> rather than <c>TOP</c>, and <c>CALL</c> rather than <c>EXEC</c>.
    /// The generated ones carry placeholders for the same reason: a script full of
    /// them cannot be run by accident.
    /// </summary>
    public async Task<string> ScriptAsync(
        DbConnection live, string schema, string obj, string kind, string variant,
        CancellationToken cancellationToken) {
        var name = Quote(schema) + "." + Quote(obj);
        switch ((variant ?? "create").ToLowerInvariant()) {
            case "drop":
                return $"DROP {Noun(kind)} {name};{Environment.NewLine}";

            case "execute":
                return $"CALL {name}();{Environment.NewLine}";

            case "select": {
                    var columns = await ColumnsAsync(live, schema, obj, cancellationToken)
                        .ConfigureAwait(false);
                    var list = columns.Count == 0
                        ? "*"
                        : string.Join("," + Environment.NewLine + "       ",
                            columns.Select(c => Quote(c.Name)));
                    return $"SELECT {list}{Environment.NewLine}FROM {name}{Environment.NewLine}"
                        + $"LIMIT 1000;{Environment.NewLine}";
                }

            case "insert": {
                    var columns = await WritableAsync(live, schema, obj, cancellationToken)
                        .ConfigureAwait(false);
                    var names = string.Join(", ", columns.Select(c => Quote(c.Name)));
                    var values = string.Join(", ", columns.Select(Placeholder));
                    return $"INSERT INTO {name} ({names}){Environment.NewLine}VALUES ({values});{Environment.NewLine}";
                }

            case "update": {
                    var columns = await WritableAsync(live, schema, obj, cancellationToken)
                        .ConfigureAwait(false);
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

        if (!string.Equals(kind, "table", StringComparison.OrdinalIgnoreCase)) {
            // pg_get_viewdef and pg_get_functiondef between them cover everything the
            // tree offers to script; which one applies is decided by what the name
            // resolves to, not by the kind the client happened to send.
            using var definition = Ado.Command(live,
                @"SELECT CASE
                           WHEN c.oid IS NOT NULL THEN pg_get_viewdef(c.oid, true)
                           WHEN p.oid IS NOT NULL THEN pg_get_functiondef(p.oid)
                         END
                  FROM (SELECT 1) one
                  LEFT JOIN pg_class c ON c.relname = @object
                       AND c.relnamespace = to_regnamespace(@schema) AND c.relkind IN ('v', 'm')
                  LEFT JOIN pg_proc p ON p.proname = @object
                       AND p.pronamespace = to_regnamespace(@schema)
                  LIMIT 1",
                ("@schema", schema), ("@object", obj));
            var text = await definition.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return text as string
                ?? "-- No definition is available, or this login cannot see it.";
        }

        var all = await ColumnsAsync(live, schema, obj, cancellationToken).ConfigureAwait(false);
        var script = new StringBuilder().Append("CREATE TABLE ").Append(name).AppendLine(" (");
        for (var i = 0; i < all.Count; i++) {
            var column = all[i];
            script.Append("    ").Append(Quote(column.Name)).Append(' ').Append(column.Type)
                .Append(column.Nullable ? " NULL" : " NOT NULL")
                .AppendLine(i == all.Count - 1 ? string.Empty : ",");
        }
        var keys = all.Where(c => c.PrimaryKey).Select(c => Quote(c.Name)).ToList();
        if (keys.Count > 0) {
            script.Append("    , PRIMARY KEY (").Append(string.Join(", ", keys)).AppendLine(")");
        }
        return script.AppendLine(");").ToString();
    }

    private const int _maxCompletionRows = 20_000;

    private static async Task<IReadOnlyList<ColumnDetail>> ColumnsAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) {
        using var command = Ado.Command(live,
            // format_type is what psql prints — "numeric(10,2)", "character varying(50)" —
            // so the column list and the generated CREATE TABLE agree without a second
            // table of type rules here.
            @"SELECT a.attname,
                     format_type(a.atttypid, a.atttypmod),
                     NOT a.attnotnull,
                     a.attidentity <> '' OR COALESCE(pg_get_expr(d.adbin, d.adrelid), '') LIKE 'nextval%',
                     COALESCE(k.is_key, false)
              FROM pg_attribute a
              JOIN pg_class c ON c.oid = a.attrelid
              JOIN pg_namespace n ON n.oid = c.relnamespace
              LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
              LEFT JOIN (
                  SELECT conrelid, unnest(conkey) AS attnum, true AS is_key
                  FROM pg_constraint WHERE contype = 'p'
              ) k ON k.conrelid = a.attrelid AND k.attnum = a.attnum
              WHERE n.nspname = @schema AND c.relname = @object
                AND a.attnum > 0 AND NOT a.attisdropped
              ORDER BY a.attnum",
            ("@schema", schema), ("@object", obj));

        var columns = new List<ColumnDetail>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            columns.Add(new ColumnDetail {
                Name = Ado.Text(reader, 0),
                Type = Ado.Text(reader, 1),
                Nullable = Ado.Flag(reader, 2),
                Identity = Ado.Flag(reader, 3),
                PrimaryKey = Ado.Flag(reader, 4),
            });
        }
        return columns;
    }

    private static async Task<IReadOnlyList<string>> KeysAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) {
        using var command = Ado.Command(live,
            @"SELECT con.conname, con.contype,
                     CASE WHEN con.contype = 'f'
                          THEN fn.nspname || '.' || fc.relname END
              FROM pg_constraint con
              JOIN pg_class c ON c.oid = con.conrelid
              JOIN pg_namespace n ON n.oid = c.relnamespace
              LEFT JOIN pg_class fc ON fc.oid = con.confrelid
              LEFT JOIN pg_namespace fn ON fn.oid = fc.relnamespace
              WHERE n.nspname = @schema AND c.relname = @object AND con.contype IN ('p', 'u', 'f')
              ORDER BY con.contype, con.conname",
            ("@schema", schema), ("@object", obj));

        var keys = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            var name = Ado.Text(reader, 0);
            keys.Add(Ado.Text(reader, 1) switch {
                "f" => $"{name} → {Ado.Text(reader, 2) ?? "?"}",
                "u" => $"{name} (UNIQUE)",
                _ => $"{name} (PRIMARY KEY)",
            });
        }
        return keys;
    }

    private static async Task<IReadOnlyList<string>> IndexesAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) {
        using var command = Ado.Command(live,
            @"SELECT ic.relname, i.indisunique
              FROM pg_index i
              JOIN pg_class c ON c.oid = i.indrelid
              JOIN pg_class ic ON ic.oid = i.indexrelid
              JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE n.nspname = @schema AND c.relname = @object
              ORDER BY ic.relname",
            ("@schema", schema), ("@object", obj));

        var indexes = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            indexes.Add(Ado.Text(reader, 0) + (Ado.Flag(reader, 1) ? " (unique)" : string.Empty));
        }
        return indexes;
    }

    /// <summary>The columns an INSERT or UPDATE may name — a generated one is the
    /// server's to fill, and naming it produces a statement that always fails.</summary>
    private static async Task<IReadOnlyList<ColumnDetail>> WritableAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) =>
        (await ColumnsAsync(live, schema, obj, cancellationToken).ConfigureAwait(false))
            .Where(c => !c.Identity).ToList();

    private static string Placeholder(ColumnDetail column) => $"<{column.Name}, {column.Type},>";

    private static string Noun(string kind) => (kind ?? string.Empty).ToLowerInvariant() switch {
        "view" => "VIEW",
        "procedure" => "PROCEDURE",
        "function" => "FUNCTION",
        _ => "TABLE",
    };

    /// <summary>PostgreSQL folds unquoted identifiers to lower case, so everything the
    /// tree emits is quoted — otherwise a table created as "Orders" cannot be selected
    /// from by the script generated for it.</summary>
    private static string Quote(string identifier) =>
        "\"" + (identifier ?? string.Empty).Replace("\"", "\"\"") + "\"";
}

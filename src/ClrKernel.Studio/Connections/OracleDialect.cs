using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using ClrKernel.Database;
using Oracle.ManagedDataAccess.Client;
using OracleProvider = ClrKernel.Database.Provider.Oracle.Oracle;

namespace ClrKernel.Studio;

/// <summary>
/// What Oracle can say about itself.
/// <para>
/// Two things differ from the others and both show in the tree. A connection reaches
/// one service rather than a list of databases, so the top level is that one entry
/// rather than an empty folder — the tree needs something to open. And a schema
/// <i>is</i> a user, so the schema level lists users that own something rather than a
/// namespace table; listing every account instead would bury the two schemas anybody
/// is looking for under a hundred Oracle-maintained ones.
/// </para>
/// <para>
/// The <c>ALL_</c> views rather than <c>DBA_</c>: they show what this login can
/// actually reach, which is the same rule the other dialects follow, and a
/// least-privilege login has no business reading <c>DBA_</c> anything.
/// </para>
/// </summary>
public sealed class OracleDialect : IConnectionDialect {
    public string Type => "Oracle";

    /// <summary>
    /// The database argument is ignored: an Oracle connection names one service, and
    /// the only value the tree can pass back is the one <see cref="DatabasesAsync"/>
    /// already reported for this connection.
    /// </summary>
    public DbConnection Open(RawConnectionNode node, SecretStore secrets, string database) =>
        OracleProvider.FromNode(node, secrets).Create();

    public async Task<IReadOnlyList<MetadataNode>> DatabasesAsync(
        DbConnection live, CancellationToken cancellationToken) {
        using var command = Ado.Command(live,
            "SELECT SYS_CONTEXT('USERENV', 'DB_NAME') FROM dual");
        var name = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return new[] { new MetadataNode { Name = name as string ?? "database", Kind = "database" } };
    }

    public Task<IReadOnlyList<MetadataNode>> SchemasAsync(
        DbConnection live, CancellationToken cancellationToken) =>
        Ado.NodesAsync(live,
            // Owners of something this login can see. ALL_OBJECTS is already filtered
            // to what is reachable, so the join does the privilege check for free.
            @"SELECT DISTINCT owner FROM all_objects
              WHERE object_type IN ('TABLE', 'VIEW', 'PROCEDURE', 'FUNCTION', 'PACKAGE')
                AND owner NOT IN (
                    'SYS', 'SYSTEM', 'OUTLN', 'XDB', 'CTXSYS', 'MDSYS', 'ORDSYS',
                    'ORDDATA', 'OLAPSYS', 'WMSYS', 'LBACSYS', 'DVSYS', 'DBSNMP',
                    'APPQOSSYS', 'AUDSYS', 'GSMADMIN_INTERNAL', 'OJVMSYS', 'DBSFWUSER',
                    'REMOTE_SCHEDULER_AGENT', 'SYSBACKUP', 'SYSDG', 'SYSKM', 'SYSRAC',
                    'SYS$UMF', 'PDBADMIN')
              ORDER BY owner",
            "schema", cancellationToken);

    public Task<IReadOnlyList<MetadataNode>> ObjectsAsync(
        DbConnection live, string schema, CancellationToken cancellationToken) =>
        Ado.NodesAsync(live,
            @"SELECT object_name,
                     CASE object_type
                         WHEN 'TABLE' THEN 'table'
                         WHEN 'VIEW' THEN 'view'
                         WHEN 'PROCEDURE' THEN 'procedure'
                         ELSE 'function' END
              FROM all_objects
              WHERE owner = :schema
                AND object_type IN ('TABLE', 'VIEW', 'PROCEDURE', 'FUNCTION')
              ORDER BY object_name",
            "table", cancellationToken, (":schema", schema));

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
            @"SELECT c.owner, c.table_name, LOWER(o.object_type), c.column_name
              FROM all_tab_columns c
              JOIN all_objects o
                ON o.owner = c.owner AND o.object_name = c.table_name
               AND o.object_type IN ('TABLE', 'VIEW')
              WHERE c.owner NOT IN (
                    'SYS', 'SYSTEM', 'OUTLN', 'XDB', 'CTXSYS', 'MDSYS', 'ORDSYS',
                    'ORDDATA', 'OLAPSYS', 'WMSYS', 'LBACSYS', 'DVSYS', 'DBSNMP',
                    'APPQOSSYS', 'AUDSYS', 'GSMADMIN_INTERNAL', 'OJVMSYS', 'DBSFWUSER')
              ORDER BY c.owner, c.table_name, c.column_id");

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
    /// Oracle's spelling: <c>FETCH FIRST</c> rather than <c>TOP</c> or <c>LIMIT</c>,
    /// no trailing semicolon on a single statement (SQL*Plus wants one, a driver does
    /// not), and <c>BEGIN … END;</c> around a procedure call.
    /// </summary>
    public async Task<string> ScriptAsync(
        DbConnection live, string schema, string obj, string kind, string variant,
        CancellationToken cancellationToken) {
        var name = Quote(schema) + "." + Quote(obj);
        switch ((variant ?? "create").ToLowerInvariant()) {
            case "drop":
                return $"DROP {Noun(kind)} {name}{Environment.NewLine}";

            case "execute":
                return $"BEGIN {name}; END;{Environment.NewLine}";

            case "select": {
                    var columns = await ColumnsAsync(live, schema, obj, cancellationToken)
                        .ConfigureAwait(false);
                    var list = columns.Count == 0
                        ? "*"
                        : string.Join("," + Environment.NewLine + "       ",
                            columns.Select(c => Quote(c.Name)));
                    return $"SELECT {list}{Environment.NewLine}FROM {name}{Environment.NewLine}"
                        + $"FETCH FIRST 1000 ROWS ONLY{Environment.NewLine}";
                }

            case "insert": {
                    var columns = await WritableAsync(live, schema, obj, cancellationToken)
                        .ConfigureAwait(false);
                    var names = string.Join(", ", columns.Select(c => Quote(c.Name)));
                    var values = string.Join(", ", columns.Select(Placeholder));
                    return $"INSERT INTO {name} ({names}){Environment.NewLine}VALUES ({values}){Environment.NewLine}";
                }

            case "update": {
                    var columns = await WritableAsync(live, schema, obj, cancellationToken)
                        .ConfigureAwait(false);
                    var sets = string.Join("," + Environment.NewLine + "    ",
                        columns.Select(c => $"{Quote(c.Name)} = {Placeholder(c)}"));
                    return $"UPDATE {name}{Environment.NewLine}SET {sets}{Environment.NewLine}"
                        + $"WHERE <search condition,,>{Environment.NewLine}";
                }

            case "delete":
                return $"DELETE FROM {name}{Environment.NewLine}WHERE <search condition,,>{Environment.NewLine}";

            default:
                break;
        }

        if (!string.Equals(kind, "table", StringComparison.OrdinalIgnoreCase)) {
            // ALL_SOURCE rather than DBMS_METADATA.GET_DDL: the latter needs privileges
            // a read-only login is unlikely to have, and its failure is an ORA- error
            // rather than an empty answer.
            using var command = Ado.Command(live,
                @"SELECT text FROM all_source
                  WHERE owner = :schema AND name = :object ORDER BY line",
                (":schema", schema), (":object", obj));
            var body = new StringBuilder();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
                body.Append(Ado.Text(reader, 0));
            }
            if (body.Length > 0) {
                return "CREATE OR REPLACE " + body.ToString().TrimStart();
            }
            using var view = Ado.Command(live,
                "SELECT text FROM all_views WHERE owner = :schema AND view_name = :object",
                (":schema", schema), (":object", obj));
            // ALL_VIEWS.TEXT is a LONG, and the managed driver fetches nothing of one
            // unless told how much to take — the symptom is an empty definition rather
            // than an error, which is exactly the sort of thing that ships.
            if (view is OracleCommand oracle) {
                oracle.InitialLONGFetchSize = -1;
            }
            var definition = await view.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return definition is string text
                ? $"CREATE OR REPLACE VIEW {name} AS{Environment.NewLine}{text}{Environment.NewLine}"
                : "-- No definition is available, or this login cannot see it.";
        }

        var all = await ColumnsAsync(live, schema, obj, cancellationToken).ConfigureAwait(false);
        var script = new StringBuilder().Append("CREATE TABLE ").Append(name).AppendLine(" (");
        for (var i = 0; i < all.Count; i++) {
            var column = all[i];
            script.Append("    ").Append(Quote(column.Name)).Append(' ').Append(column.Type)
                .Append(column.Nullable ? string.Empty : " NOT NULL")
                .AppendLine(i == all.Count - 1 ? string.Empty : ",");
        }
        var keys = all.Where(c => c.PrimaryKey).Select(c => Quote(c.Name)).ToList();
        if (keys.Count > 0) {
            script.Append("    , PRIMARY KEY (").Append(string.Join(", ", keys)).AppendLine(")");
        }
        return script.AppendLine(")").ToString();
    }

    public IEnumerable<string> ExtraErrors(DbException error) =>
        error is OracleException oracle
            ? oracle.Errors.Cast<OracleError>().Select(e => e.Message)
            : Array.Empty<string>();

    public void ClearPool(DbConnection live) {
        if (live is OracleConnection oracle) {
            OracleConnection.ClearPool(oracle);
        }
    }

    private const int _maxCompletionRows = 20_000;

    private static async Task<IReadOnlyList<ColumnDetail>> ColumnsAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) {
        using var command = Ado.Command(live,
            @"SELECT c.column_name, c.data_type, c.data_length, c.data_precision, c.data_scale,
                     c.nullable, c.identity_column,
                     CASE WHEN k.column_name IS NULL THEN 0 ELSE 1 END
              FROM all_tab_columns c
              LEFT JOIN (
                  SELECT cc.owner, cc.table_name, cc.column_name
                  FROM all_constraints con
                  JOIN all_cons_columns cc
                    ON cc.owner = con.owner AND cc.constraint_name = con.constraint_name
                  WHERE con.constraint_type = 'P'
              ) k ON k.owner = c.owner AND k.table_name = c.table_name
                 AND k.column_name = c.column_name
              WHERE c.owner = :schema AND c.table_name = :object
              ORDER BY c.column_id",
            (":schema", schema), (":object", obj));

        var columns = new List<ColumnDetail>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            columns.Add(new ColumnDetail {
                Name = Ado.Text(reader, 0),
                Type = TypeText(reader),
                // Oracle spells it 'Y'/'N' rather than as a boolean.
                Nullable = string.Equals(Ado.Text(reader, 5), "Y", StringComparison.OrdinalIgnoreCase),
                Identity = string.Equals(Ado.Text(reader, 6), "YES", StringComparison.OrdinalIgnoreCase),
                PrimaryKey = Ado.Flag(reader, 7),
            });
        }
        return columns;
    }

    /// <summary>The type as a script would write it, so the column list and the
    /// generated CREATE TABLE agree.</summary>
    private static string TypeText(DbDataReader reader) {
        var name = Ado.Text(reader, 1) ?? string.Empty;
        switch (name.ToUpperInvariant()) {
            case "NUMBER":
                if (reader.IsDBNull(3)) {
                    return "NUMBER";
                }
                var scale = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
                var precision = Convert.ToInt32(reader.GetValue(3));
                return scale == 0 ? $"NUMBER({precision})" : $"NUMBER({precision},{scale})";
            case "VARCHAR2":
            case "NVARCHAR2":
            case "CHAR":
            case "NCHAR":
            case "RAW":
                return reader.IsDBNull(2)
                    ? name
                    : $"{name}({Convert.ToInt32(reader.GetValue(2))})";
            default:
                return name;
        }
    }

    private static async Task<IReadOnlyList<string>> KeysAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) {
        using var command = Ado.Command(live,
            @"SELECT con.constraint_name, con.constraint_type,
                     r.owner || '.' || r.table_name
              FROM all_constraints con
              LEFT JOIN all_constraints rc
                ON rc.owner = con.r_owner AND rc.constraint_name = con.r_constraint_name
              LEFT JOIN all_tables r
                ON r.owner = rc.owner AND r.table_name = rc.table_name
              WHERE con.owner = :schema AND con.table_name = :object
                AND con.constraint_type IN ('P', 'U', 'R')
              ORDER BY con.constraint_type, con.constraint_name",
            (":schema", schema), (":object", obj));

        var keys = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            var name = Ado.Text(reader, 0);
            keys.Add(Ado.Text(reader, 1) switch {
                "R" => $"{name} → {Ado.Text(reader, 2) ?? "?"}",
                "U" => $"{name} (UNIQUE)",
                _ => $"{name} (PRIMARY KEY)",
            });
        }
        return keys;
    }

    private static async Task<IReadOnlyList<string>> IndexesAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) {
        using var command = Ado.Command(live,
            @"SELECT index_name, uniqueness FROM all_indexes
              WHERE table_owner = :schema AND table_name = :object
              ORDER BY index_name",
            (":schema", schema), (":object", obj));

        var indexes = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            indexes.Add(Ado.Text(reader, 0)
                + (string.Equals(Ado.Text(reader, 1), "UNIQUE", StringComparison.OrdinalIgnoreCase)
                    ? " (unique)" : string.Empty));
        }
        return indexes;
    }

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

    /// <summary>Oracle folds unquoted identifiers to upper case, so everything the tree
    /// emits is quoted — the catalog reports the stored name and a script that unquotes
    /// it cannot find a table created as "orders".</summary>
    private static string Quote(string identifier) =>
        "\"" + (identifier ?? string.Empty).Replace("\"", "\"\"") + "\"";
}

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using ClrKernel.Database;
using OdbcProvider = ClrKernel.Database.Provider.Odbc.Odbc;

namespace ClrKernel.Studio;

/// <summary>
/// What can be said about a database reached through ODBC, which is: whatever its
/// driver is willing to say.
/// <para>
/// ODBC is not a database. Behind a DSN could be anything, so this asks
/// <see cref="DbConnection.GetSchema(string)"/> — the driver's own answer, in ADO.NET's
/// shape — rather than a catalog query in a dialect it cannot know. The tree is
/// therefore shallower here on purpose: schemas and objects, with columns and a
/// generated SELECT, and no keys, indexes or stored definitions. Those have no
/// portable source, and a folder that opens onto an empty list reads as "this
/// database has none" rather than "nobody can tell".
/// </para>
/// <para>
/// Everything here is best-effort by design: a driver that does not implement a
/// collection throws, and an empty list is the honest answer rather than a failed
/// request. A connection whose driver says nothing still runs queries.
/// </para>
/// </summary>
public sealed class OdbcDialect : IConnectionDialect {
    public string Type => "Odbc";

    /// <summary>
    /// The database is ignored. ODBC's catalog concept is the driver's, and rewriting
    /// a connection string somebody pasted to point at another one is guesswork —
    /// what it names is what it opens.
    /// </summary>
    public DbConnection Open(RawConnectionNode node, SecretStore secrets, string database) =>
        OdbcProvider.FromNode(node, secrets).Create();

    /// <summary>The one thing this connection reaches, so the tree has something to
    /// open. There is no portable way to ask an ODBC driver what else exists.</summary>
    public Task<IReadOnlyList<MetadataNode>> DatabasesAsync(
        DbConnection live, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MetadataNode>>(new[] {
            new MetadataNode {
                Name = string.IsNullOrWhiteSpace(live.Database) ? live.DataSource ?? "database" : live.Database,
                Kind = "database",
            },
        });

    public Task<IReadOnlyList<MetadataNode>> SchemasAsync(
        DbConnection live, CancellationToken cancellationToken) {
        var schemas = Rows(live, "Tables")
            .Select(row => Field(row, "TABLE_SCHEM") ?? Field(row, "TABLE_SCHEMA"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Select(s => new MetadataNode { Name = s, Kind = "schema" })
            .ToList();
        // A driver with no schema concept at all — plenty have none — would otherwise
        // give a database that opens onto nothing.
        if (schemas.Count == 0) {
            schemas.Add(new MetadataNode { Name = _noSchema, Kind = "schema" });
        }
        return Task.FromResult<IReadOnlyList<MetadataNode>>(schemas);
    }

    public Task<IReadOnlyList<MetadataNode>> ObjectsAsync(
        DbConnection live, string schema, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MetadataNode>>(
            Rows(live, "Tables")
                .Where(row => InSchema(row, schema))
                .Select(row => new MetadataNode {
                    Name = Field(row, "TABLE_NAME"),
                    Kind = (Field(row, "TABLE_TYPE") ?? string.Empty)
                        .Contains("VIEW", StringComparison.OrdinalIgnoreCase) ? "view" : "table",
                })
                .Where(node => !string.IsNullOrWhiteSpace(node.Name))
                .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());

    /// <summary>Columns only. Keys and indexes have no portable source, and an empty
    /// list would claim the table has none.</summary>
    public Task<ObjectDetail> DetailAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken) =>
        Task.FromResult(new ObjectDetail {
            Columns = ColumnsOf(live, schema, obj),
        });

    public Task<CompletionSchema> CompletionsAsync(
        DbConnection live, CancellationToken cancellationToken) {
        var columns = Rows(live, "Columns")
            .GroupBy(
                row => (
                    Schema: Field(row, "TABLE_SCHEM") ?? Field(row, "TABLE_SCHEMA"),
                    Name: Field(row, "TABLE_NAME")),
                new SchemaObjectComparer());
        var objects = columns
            .Where(group => !string.IsNullOrWhiteSpace(group.Key.Name))
            .Take(_maxCompletionObjects)
            .Select(group => new CompletionObject {
                Schema = group.Key.Schema,
                Name = group.Key.Name,
                Kind = "table",
                Columns = group
                    .OrderBy(row => Ordinal(row))
                    .Select(row => Field(row, "COLUMN_NAME"))
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList(),
            })
            .ToList();
        return Task.FromResult(new CompletionSchema {
            Database = live.Database,
            Objects = objects,
            Truncated = objects.Count == _maxCompletionObjects,
        });
    }

    /// <summary>
    /// A SELECT, and the write statements as templates. No CREATE: that would mean
    /// inventing DDL in a dialect this connection has not told us, and a script that
    /// does not run on the database it was generated for is worse than none.
    /// </summary>
    public Task<string> ScriptAsync(
        DbConnection live, string schema, string obj, string kind, string variant,
        CancellationToken cancellationToken) {
        var name = schema == _noSchema || string.IsNullOrWhiteSpace(schema)
            ? Quote(obj)
            : Quote(schema) + "." + Quote(obj);
        var columns = ColumnsOf(live, schema, obj);

        switch ((variant ?? "select").ToLowerInvariant()) {
            case "insert": {
                    var names = string.Join(", ", columns.Select(c => Quote(c.Name)));
                    var values = string.Join(", ", columns.Select(Placeholder));
                    return Task.FromResult(
                        $"INSERT INTO {name} ({names}){Environment.NewLine}VALUES ({values}){Environment.NewLine}");
                }

            case "update": {
                    var sets = string.Join("," + Environment.NewLine + "    ",
                        columns.Select(c => $"{Quote(c.Name)} = {Placeholder(c)}"));
                    return Task.FromResult(
                        $"UPDATE {name}{Environment.NewLine}SET {sets}{Environment.NewLine}"
                        + $"WHERE <search condition,,>{Environment.NewLine}");
                }

            case "delete":
                return Task.FromResult(
                    $"DELETE FROM {name}{Environment.NewLine}WHERE <search condition,,>{Environment.NewLine}");

            case "drop":
                return Task.FromResult($"DROP TABLE {name}{Environment.NewLine}");

            default: {
                    var list = columns.Count == 0
                        ? "*"
                        : string.Join("," + Environment.NewLine + "       ",
                            columns.Select(c => Quote(c.Name)));
                    return Task.FromResult(
                        $"SELECT {list}{Environment.NewLine}FROM {name}{Environment.NewLine}");
                }
        }
    }

    public IEnumerable<string> ExtraErrors(DbException error) =>
        error is OdbcException odbc
            ? odbc.Errors.Cast<OdbcError>().Select(e => e.Message)
            : Array.Empty<string>();

    /// <summary>What the tree calls a schema when the driver has no such concept.</summary>
    private const string _noSchema = "(default)";

    /// <summary>Bounded because this reads every column of every table in one go and a
    /// driver has no way to stop half way.</summary>
    private const int _maxCompletionObjects = 2_000;

    private IReadOnlyList<ColumnDetail> ColumnsOf(DbConnection live, string schema, string obj) =>
        Rows(live, "Columns")
            .Where(row => InSchema(row, schema)
                && string.Equals(Field(row, "TABLE_NAME"), obj, StringComparison.OrdinalIgnoreCase))
            .OrderBy(Ordinal)
            .Select(row => new ColumnDetail {
                Name = Field(row, "COLUMN_NAME"),
                Type = Field(row, "TYPE_NAME"),
                // "1" is SQL_NULLABLE; a driver that leaves it out gets the safer answer.
                Nullable = Field(row, "NULLABLE") != "0",
            })
            .Where(column => !string.IsNullOrWhiteSpace(column.Name))
            .ToList();

    /// <summary>
    /// One of ADO.NET's schema collections, or nothing. A driver that does not
    /// implement one throws, and an empty tree branch is a better answer than a failed
    /// request — this is the whole reason ODBC is browsable at all.
    /// </summary>
    private static IEnumerable<DataRow> Rows(DbConnection live, string collection) {
        try {
            return live.GetSchema(collection).Rows.Cast<DataRow>().ToList();
        } catch (Exception e) when (e is OdbcException or NotSupportedException or InvalidOperationException) {
            return Array.Empty<DataRow>();
        }
    }

    private static bool InSchema(DataRow row, string schema) {
        var rowSchema = Field(row, "TABLE_SCHEM") ?? Field(row, "TABLE_SCHEMA");
        return string.IsNullOrWhiteSpace(rowSchema)
            ? schema == _noSchema || string.IsNullOrWhiteSpace(schema)
            : string.Equals(rowSchema, schema, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A column of a schema row, or null — collections differ between drivers
    /// and a missing column is a thing to skip rather than to throw on.</summary>
    private static string Field(DataRow row, string column) =>
        row.Table.Columns.Contains(column) && row[column] != DBNull.Value
            ? row[column]?.ToString()
            : null;

    private static int Ordinal(DataRow row) =>
        int.TryParse(Field(row, "ORDINAL_POSITION"), out var position) ? position : 0;

    private static string Placeholder(ColumnDetail column) => $"<{column.Name}, {column.Type},>";

    /// <summary>
    /// Double quotes, which is the SQL standard's delimiter and what most drivers
    /// accept. It is a guess — ODBC cannot tell us — so the identifier is left alone
    /// when it needs no quoting at all, which is the common case and the safe one.
    /// </summary>
    private static string Quote(string identifier) {
        var text = identifier ?? string.Empty;
        return text.All(c => char.IsLetterOrDigit(c) || c == '_')
            ? text
            : "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private sealed class SchemaObjectComparer : IEqualityComparer<(string Schema, string Name)> {
        public bool Equals((string Schema, string Name) x, (string Schema, string Name) y) =>
            string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Schema, string Name) value) =>
            HashCode.Combine(
                value.Schema?.ToUpperInvariant(), value.Name?.ToUpperInvariant());
    }
}

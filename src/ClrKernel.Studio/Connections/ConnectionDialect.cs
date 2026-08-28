using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Secrets;
using ClrKernel.Database;

namespace ClrKernel.Studio;

/// <summary>
/// What this server needs to know about one kind of database to open it and show
/// what is in it.
/// <para>
/// The split is deliberate: turning saved settings into a connection string belongs
/// to the provider package — that knowledge already lives beside the notebook API
/// that uses it, and a second copy here would drift. What belongs here is the
/// catalog: the queries behind the object tree, which are this server's business and
/// which the provider packages must not learn about.
/// </para>
/// <para>
/// <see cref="Open"/> takes the database rather than a method taking an already-open
/// connection and a name. SQL Server could <c>USE</c>; PostgreSQL cannot — a
/// different database is a different connection — so the database has to be part of
/// building the connection, or the interface only fits one provider.
/// </para>
/// </summary>
public interface IConnectionDialect {
    /// <summary>The <c>$type</c> this speaks for.</summary>
    string Type { get; }

    /// <summary>
    /// Builds a connection from a resolved node — <b>not</b> opened, so the caller can
    /// await the open and have a cancellation token mean something.
    /// <paramref name="database"/> overrides whatever the node names, or is null to
    /// take the node's own.
    /// </summary>
    DbConnection Open(RawConnectionNode node, SecretStore secrets, string database);

    /// <summary>The databases on this server, as the tree's top level.</summary>
    Task<IReadOnlyList<MetadataNode>> DatabasesAsync(DbConnection live, CancellationToken cancellationToken);

    /// <summary>The schemas in the connected database.</summary>
    Task<IReadOnlyList<MetadataNode>> SchemasAsync(DbConnection live, CancellationToken cancellationToken);

    /// <summary>Tables, views and routines in one schema, in one pass.</summary>
    Task<IReadOnlyList<MetadataNode>> ObjectsAsync(
        DbConnection live, string schema, CancellationToken cancellationToken);

    /// <summary>One object's columns, keys and indexes.</summary>
    Task<ObjectDetail> DetailAsync(
        DbConnection live, string schema, string obj, CancellationToken cancellationToken);

    /// <summary>Everything the editor completes against, in one pass.</summary>
    Task<CompletionSchema> CompletionsAsync(DbConnection live, CancellationToken cancellationToken);

    /// <summary>A statement for an object — "Script as" in the tree.</summary>
    Task<string> ScriptAsync(
        DbConnection live, string schema, string obj, string kind, string variant,
        CancellationToken cancellationToken);

    // --- what only some providers have ------------------------------------
    // Defaults rather than four more required members: a provider without any of
    // these still works, it just says a little less.

    /// <summary>
    /// Subscribes to the driver's informational messages — SQL Server's <c>PRINT</c>,
    /// PostgreSQL's <c>RAISE NOTICE</c> — which arrive on an event rather than in the
    /// reader. Returns something to dispose to unsubscribe, or null.
    /// </summary>
    IDisposable OnInfoMessage(DbConnection live, Action<string> message) => null;

    /// <summary>
    /// The errors after the first, where the driver reports several for one failure.
    /// The first is the exception's own message and is added by the caller; repeating
    /// it here printed every failure twice.
    /// </summary>
    IEnumerable<string> ExtraErrors(DbException error) => Array.Empty<string>();

    /// <summary>Drops this connection's pooled sockets — what Disconnect means.</summary>
    void ClearPool(DbConnection live) { }

    /// <summary>
    /// How long a pooled connection may live before it is retired rather than reused.
    /// Long enough that query-look-query never pays to reconnect; short enough that a
    /// browser tab left open overnight is not still holding a socket in the morning.
    /// Applied by each dialect when it builds its connection string, because the
    /// keyword that means it differs per driver.
    /// </summary>
    public const int PoolLifetimeSeconds = 300;
}

/// <summary>
/// The dialects this build carries, by <c>$type</c>.
/// <para>
/// This is what "queryable" means: a connection type in here can be opened, browsed
/// and queried by this server, and one that is not can still be saved and named by a
/// notebook — the kernel opens that one. So the list is exactly the set of provider
/// packages the server references, and adding a database means adding a dialect and
/// a reference, nothing else.
/// </para>
/// </summary>
public static class ConnectionDialects {
    private static readonly IReadOnlyList<IConnectionDialect> _all = new IConnectionDialect[] {
        new SqlServerDialect(),
        new PostgresDialect(),
    };

    public static IConnectionDialect For(string type) =>
        _all.FirstOrDefault(d => string.Equals(d.Type, type, StringComparison.OrdinalIgnoreCase));

    public static bool Supports(string type) => For(type) != null;

    /// <summary>The types, for the catalog's ordering and for diagnostics.</summary>
    public static IReadOnlyList<string> Types => _all.Select(d => d.Type).ToList();
}

/// <summary>Small ADO helpers the dialects share, so four copies of "read a column
/// as text" do not disagree about nulls.</summary>
internal static class Ado {
    public static DbCommand Command(DbConnection live, string sql, params (string Name, object Value)[] parameters) {
        var command = live.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    /// <summary>A column as text, or null. Every catalog query here reads names, and
    /// a name that came back null is a row to label rather than one to throw on.</summary>
    public static string Text(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal)?.ToString();

    public static bool Flag(DbDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && Convert.ToBoolean(reader.GetValue(ordinal));

    public static async Task<IReadOnlyList<MetadataNode>> NodesAsync(
        DbConnection live, string sql, string kind, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters) {
        using var command = Command(live, sql, parameters);
        var nodes = new List<MetadataNode>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            nodes.Add(new MetadataNode {
                Name = Text(reader, 0),
                Kind = reader.FieldCount > 1 ? Text(reader, 1) : kind,
            });
        }
        return nodes;
    }
}

using System;
using ClrKernel.Core.Secrets;
using Npgsql;

namespace ClrKernel.Database.Provider.Postgres;

/// <summary>
/// PostgreSQL provider entry point. Returns the same fluent <see cref="DataSource"/> as
/// the other providers, so C# cells query PostgreSQL the same way:
/// <code>
/// var warehouse = Postgres.Connect("db.local", 5432, "analytics", "reader", "pg:reader");
/// var rows = warehouse.Query("select * from orders").Results();
/// </code>
/// Passwords are never inline — a secret reference resolves from ClrKernel's secret
/// store (OS credential manager, or the <c>CLRKERNEL_SECRET_&lt;REF&gt;</c> env var).
/// </summary>
public static class Postgres {
    /// <summary>
    /// Connects to a database. The password for <paramref name="user"/> resolves from
    /// the secret store under <paramref name="secretRef"/>.
    /// </summary>
    public static DataSource Connect(
        string server, int port, string database, string user, string secretRef,
        SecretStore secrets = null) {
        if (string.IsNullOrWhiteSpace(server)) {
            throw new ArgumentException("server is required.", nameof(server));
        }
        if (string.IsNullOrWhiteSpace(database)) {
            throw new ArgumentException("database is required.", nameof(database));
        }
        var builder = new NpgsqlConnectionStringBuilder {
            Host = server,
            Port = port <= 0 ? 5432 : port,
            Database = database,
            Username = user,
        };
        if (!string.IsNullOrEmpty(secretRef)) {
            builder.Password = (secrets ?? new SecretStore()).Resolve(secretRef);
        }
        return Build($"{server}:{builder.Port}/{database}", builder.ConnectionString);
    }

    /// <summary>Connects from a full Npgsql connection string (advanced escape hatch).</summary>
    public static DataSource FromConnectionString(string connectionString, string name = "postgres") {
        if (string.IsNullOrWhiteSpace(connectionString)) {
            throw new ArgumentException("connectionString is required.", nameof(connectionString));
        }
        return Build(name, connectionString);
    }

    /// <summary>
    /// Connects from a <c>$type: "Postgres"</c> entry in a connection config file
    /// (properties: <c>server</c>, <c>port</c>, <c>database</c>, <c>user</c>,
    /// <c>password</c> — the password as a <c>{ "secret": "&lt;ref&gt;" }</c>).
    /// </summary>
    public static DataSource FromConfig(string name, SecretStore secrets = null) =>
        Map(ConnectionConfig.Load(name, secrets).EnsureType(PostgresConnectionConfig.TypeName));

    /// <summary>
    /// The same, from settings a caller already holds rather than from a file — how a
    /// server opens a connection it saved. Same mapping, so the two cannot drift.
    /// </summary>
    public static DataSource FromNode(RawConnectionNode node, SecretStore secrets = null) =>
        Map(ConnectionConfig.From(node, secrets).EnsureType(PostgresConnectionConfig.TypeName));

    private static DataSource Map(ConnectionConfig config) =>
        Build(config.Name, PostgresConnectionConfig.ToConnectionString(config));

    private static DataSource Build(string name, string connectionString) =>
        new DataSource(name, () => new NpgsqlConnection(connectionString));
}

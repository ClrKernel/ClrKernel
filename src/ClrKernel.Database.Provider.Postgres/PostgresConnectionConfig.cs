using System;
using Npgsql;

namespace ClrKernel.Database.Provider.Postgres;

/// <summary>
/// Turns a <c>"$type": "Postgres"</c> node into a connection string. One copy of
/// which key means what, shared by the file path (<see cref="Postgres.FromConfig"/>)
/// and the in-memory one (<see cref="Postgres.FromNode"/>).
/// </summary>
public static class PostgresConnectionConfig {
    /// <summary>The <c>$type</c> discriminator for PostgreSQL connection nodes.</summary>
    public const string TypeName = "Postgres";

    /// <summary>
    /// The connection string for a resolved config — passwords already substituted for
    /// their references by <see cref="ConnectionConfig"/>.
    /// </summary>
    public static string ToConnectionString(ConnectionConfig config) {
        if (config == null) {
            throw new ArgumentNullException(nameof(config));
        }
        // A connection string somebody supplied is theirs: the remaining keys are
        // applied on top of it rather than instead of it, so `database` can point one
        // saved string at a second database without a second entry.
        var builder = config.Get("connectionString") is { Length: > 0 } raw
            ? new NpgsqlConnectionStringBuilder(raw)
            : new NpgsqlConnectionStringBuilder();
        if ((config.Get("server") ?? config.Get("host")) is { Length: > 0 } host) {
            builder.Host = host;
        }
        if (config.GetInt("port", 0) is > 0 and var port) {
            builder.Port = port;
        }
        if (config.Get("database") is { Length: > 0 } database) {
            builder.Database = database;
        }
        if ((config.Get("user") ?? config.Get("username")) is { Length: > 0 } user) {
            builder.Username = user;
        }
        if (config.Get("password") is { Length: > 0 } password) {
            builder.Password = password;
        }
        if (config.Get("sslMode") is { Length: > 0 } sslMode
            && Enum.TryParse<SslMode>(sslMode, ignoreCase: true, out var parsed)) {
            builder.SslMode = parsed;
        }
        foreach (var property in config.Properties) {
            if (!IsReserved(property.Key) && !string.IsNullOrEmpty(property.Value)) {
                // Npgsql throws on a keyword it does not know, which is the right
                // answer: a typo'd option that silently did nothing would be worse.
                builder[property.Key] = property.Value;
            }
        }
        return builder.ConnectionString;
    }

    private static bool IsReserved(string key) =>
        key.Equals("connectionString", StringComparison.OrdinalIgnoreCase)
        || key.Equals("server", StringComparison.OrdinalIgnoreCase)
        || key.Equals("host", StringComparison.OrdinalIgnoreCase)
        || key.Equals("port", StringComparison.OrdinalIgnoreCase)
        || key.Equals("database", StringComparison.OrdinalIgnoreCase)
        || key.Equals("user", StringComparison.OrdinalIgnoreCase)
        || key.Equals("username", StringComparison.OrdinalIgnoreCase)
        || key.Equals("password", StringComparison.OrdinalIgnoreCase)
        || key.Equals("sslMode", StringComparison.OrdinalIgnoreCase)
        || key.Equals("name", StringComparison.OrdinalIgnoreCase);
}

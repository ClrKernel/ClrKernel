using System;
using System.Collections.Generic;
using System.Linq;
using ClrKernel.Core.Secrets;

namespace ClrKernel.Database.Provider.Jdbc;

/// <summary>
/// EXPERIMENTAL JDBC provider entry point. Loads a Java JDBC driver (via IKVM) and
/// returns the same fluent <see cref="DataSource"/> as the other providers:
/// <code>
/// var db = Jdbc.Connect(
///     "jdbc:postgresql://host:5432/app", "org.postgresql.Driver",
///     driverAssemblyPath: "/path/to/postgresql.dll", user: "app", secretRef: "jdbc:app");
/// var rows = db.Query("select * from public.orders").Results();
/// </code>
/// The JDBC bridge does not implement command parameters — use parameter-less SQL
/// (<c>db.Query(sql)</c>). Requires IKVM and a driver assembly you supply; validate on
/// Windows before relying on it.
/// </summary>
public static class Jdbc {
    /// <summary>
    /// Connects using a JDBC driver in an IKVM-compiled assembly. The password (if any)
    /// resolves from the secret store under <paramref name="secretRef"/>.
    /// </summary>
    public static DataSource Connect(
        string jdbcUrl, string driverClass, string driverAssemblyPath,
        IEnumerable<KeyValuePair<string, string>> properties = null,
        string user = null, string secretRef = null, SecretStore secrets = null, string name = null) {
        if (string.IsNullOrWhiteSpace(jdbcUrl)) {
            throw new ArgumentException("jdbcUrl is required.", nameof(jdbcUrl));
        }
        if (string.IsNullOrWhiteSpace(driverClass)) {
            throw new ArgumentException("driverClass is required.", nameof(driverClass));
        }
        if (string.IsNullOrWhiteSpace(driverAssemblyPath)) {
            throw new ArgumentException("driverAssemblyPath is required.", nameof(driverAssemblyPath));
        }
        IkvmConfiguration.EnsureConfigured();
        JdbcProviderFactory.FromAssemblyPath(driverAssemblyPath, driverClass);
        var connectionString = BuildConnectionString(jdbcUrl, driverClass, properties, user, secretRef, secrets);
        return new DataSource(name ?? jdbcUrl, () => new JdbcConnection { ConnectionString = connectionString });
    }

    /// <summary>Connects using a JDBC driver in a <c>.jar</c> file (loaded via IKVM).</summary>
    public static DataSource ConnectJar(
        string jdbcUrl, string driverClass, string driverJarPath,
        IEnumerable<KeyValuePair<string, string>> properties = null,
        string user = null, string secretRef = null, SecretStore secrets = null, string name = null) {
        if (string.IsNullOrWhiteSpace(driverJarPath)) {
            throw new ArgumentException("driverJarPath is required.", nameof(driverJarPath));
        }
        IkvmConfiguration.EnsureConfigured();
        JdbcProviderFactory.FromJarPath(driverJarPath, driverClass);
        var connectionString = BuildConnectionString(jdbcUrl, driverClass, properties, user, secretRef, secrets);
        return new DataSource(name ?? jdbcUrl, () => new JdbcConnection { ConnectionString = connectionString });
    }

    /// <summary>
    /// Connects from a <c>$type: "Jdbc"</c> entry in a connection config file
    /// (properties: <c>jdbcUrl</c>, <c>driverClass</c>, one of
    /// <c>driverAssemblyPath</c> / <c>driverJarPath</c>, optional <c>user</c> and
    /// <c>password</c> as a <c>{ "secret": "&lt;ref&gt;" }</c>; any other key is
    /// passed to the driver as a property).
    /// <para>
    /// The descriptor has documented this as a follow-up since it was written —
    /// JDBC was reachable from C# and from nowhere else. A dialect cell names a
    /// connection, and a connection is a config node, so this is what makes
    /// <c>#!ansisql</c> over JDBC a thing you can write down.
    /// </para>
    /// </summary>
    public static DataSource FromConfig(string name, SecretStore secrets = null) {
        var config = ConnectionConfig.Load(name, secrets).EnsureType("Jdbc");
        var url = config.Require("jdbcUrl");
        var driverClass = config.Require("driverClass");
        var user = config.Get("user");
        var password = config.Get("password");
        var properties = config.Properties
            .Where(pair => !_reserved.Contains(pair.Key))
            .ToList();

        // The password is already resolved by ConnectionConfig.Load, so it is
        // passed as a property rather than as a secret reference to resolve twice.
        if (!string.IsNullOrEmpty(password)) {
            properties.Add(new KeyValuePair<string, string>("password", password));
        }

        var assemblyPath = config.Get("driverAssemblyPath");
        var jarPath = config.Get("driverJarPath");
        if (!string.IsNullOrWhiteSpace(assemblyPath)) {
            return Connect(url, driverClass, assemblyPath, properties, user, null, secrets, config.Name);
        }
        if (!string.IsNullOrWhiteSpace(jarPath)) {
            return ConnectJar(url, driverClass, jarPath, properties, user, null, secrets, config.Name);
        }
        throw new ConnectionConfigException(
            $"JDBC connection '{name}' needs a driverAssemblyPath or a driverJarPath.");
    }

    private static readonly HashSet<string> _reserved = new HashSet<string>(
        new[] { "jdbcUrl", "driverClass", "driverAssemblyPath", "driverJarPath", "user", "password" },
        StringComparer.OrdinalIgnoreCase);

    internal static string BuildConnectionString(
        string jdbcUrl, string driverClass, IEnumerable<KeyValuePair<string, string>> properties,
        string user, string secretRef, SecretStore secrets) {
        var props = new java.util.Properties();
        if (!string.IsNullOrEmpty(user)) {
            props.setProperty("user", user);
        }
        if (!string.IsNullOrEmpty(secretRef)) {
            secrets ??= new SecretStore();
            props.setProperty("password", secrets.Resolve(secretRef));
        }
        if (properties != null) {
            foreach (var pair in properties) {
                props.setProperty(pair.Key, pair.Value);
            }
        }
        return JdbcConnectionStringBuilder.CreateConnectionString(driverClass, jdbcUrl, props);
    }
}

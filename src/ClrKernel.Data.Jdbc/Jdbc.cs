using System;
using System.Collections.Generic;
using ClrKernel.Core.Secrets;

namespace ClrKernel.Data.Jdbc;

/// <summary>
/// EXPERIMENTAL JDBC provider entry point. Loads a Java JDBC driver (via IKVM) and
/// returns the same fluent <see cref="Database"/> as the other providers:
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
    public static Database Connect(
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
        return new Database(name ?? jdbcUrl, () => new JdbcConnection { ConnectionString = connectionString });
    }

    /// <summary>Connects using a JDBC driver in a <c>.jar</c> file (loaded via IKVM).</summary>
    public static Database ConnectJar(
        string jdbcUrl, string driverClass, string driverJarPath,
        IEnumerable<KeyValuePair<string, string>> properties = null,
        string user = null, string secretRef = null, SecretStore secrets = null, string name = null) {
        if (string.IsNullOrWhiteSpace(driverJarPath)) {
            throw new ArgumentException("driverJarPath is required.", nameof(driverJarPath));
        }
        IkvmConfiguration.EnsureConfigured();
        JdbcProviderFactory.FromJarPath(driverJarPath, driverClass);
        var connectionString = BuildConnectionString(jdbcUrl, driverClass, properties, user, secretRef, secrets);
        return new Database(name ?? jdbcUrl, () => new JdbcConnection { ConnectionString = connectionString });
    }

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

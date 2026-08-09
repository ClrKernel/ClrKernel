using System;
using System.Collections.Generic;
using System.Data.Odbc;
using ClrKernel.Data.Secrets;

namespace ClrKernel.Data.Odbc;

/// <summary>
/// ODBC provider entry point. Returns the same fluent <see cref="Database"/> as the
/// other providers, so any ODBC data source queries the same way:
/// <code>
/// var db = Odbc.FromConnectionString("Driver={PostgreSQL Unicode};Server=host;Database=app;");
/// var rows = db.Query("select * from public.orders").Results();
/// </code>
/// The ODBC driver for your database must be installed on the machine. Passwords are
/// never inline in ClrKernel code — use <see cref="Connect"/> with a secret reference,
/// or a DSN that already carries credentials.
/// </summary>
public static class Odbc {
    /// <summary>Connects from a full ODBC connection string.</summary>
    public static Database FromConnectionString(string connectionString, string name = "odbc") {
        if (string.IsNullOrWhiteSpace(connectionString)) {
            throw new ArgumentException("connectionString is required.", nameof(connectionString));
        }
        return Build(name, connectionString);
    }

    /// <summary>Connects to a DSN, optionally with a user whose password resolves from the secret store.</summary>
    public static Database FromDsn(string dsn, string user = null, string secretRef = null, SecretStore secrets = null) {
        if (string.IsNullOrWhiteSpace(dsn)) {
            throw new ArgumentException("dsn is required.", nameof(dsn));
        }
        var builder = new OdbcConnectionStringBuilder { Dsn = dsn };
        ApplyCredentials(builder, user, secretRef, secrets);
        return Build(dsn, builder.ConnectionString);
    }

    /// <summary>
    /// Connects with an explicit driver plus extra connection-string keywords (e.g.
    /// <c>Server</c>, <c>Database</c>). A user's password resolves from the secret store.
    /// </summary>
    public static Database Connect(
        string driver, IEnumerable<KeyValuePair<string, string>> properties = null,
        string user = null, string secretRef = null, SecretStore secrets = null, string name = null) {
        if (string.IsNullOrWhiteSpace(driver)) {
            throw new ArgumentException("driver is required.", nameof(driver));
        }
        var builder = new OdbcConnectionStringBuilder { Driver = driver };
        if (properties != null) {
            foreach (var pair in properties) {
                builder[pair.Key] = pair.Value;
            }
        }
        ApplyCredentials(builder, user, secretRef, secrets);
        return Build(name ?? driver, builder.ConnectionString);
    }

    /// <summary>
    /// Connects from an <c>$type: "Odbc"</c> entry in a connection config file
    /// (properties: <c>connectionString</c>, or <c>driver</c>/<c>dsn</c> plus keywords;
    /// <c>password</c> as a <c>{ "secret": "&lt;ref&gt;" }</c>).
    /// </summary>
    public static Database FromConfig(string name, SecretStore secrets = null) {
        var config = ConnectionConfig.Load(name, secrets).EnsureType("Odbc");
        if (config.Get("connectionString") is { Length: > 0 } cs) {
            return Build(config.Name, cs);
        }
        var builder = new OdbcConnectionStringBuilder();
        if (config.Get("driver") is { Length: > 0 } driver) {
            builder.Driver = driver;
        }
        if (config.Get("dsn") is { Length: > 0 } dsn) {
            builder.Dsn = dsn;
        }
        foreach (var pair in config.Properties) {
            if (!IsReserved(pair.Key)) {
                builder[pair.Key] = pair.Value;
            }
        }
        if (config.Get("user") is { Length: > 0 } user) {
            builder["Uid"] = user;
        }
        if (config.Get("password") is { Length: > 0 } password) {
            builder["Pwd"] = password;
        }
        return Build(config.Name, builder.ConnectionString);
    }

    private static void ApplyCredentials(OdbcConnectionStringBuilder builder, string user, string secretRef, SecretStore secrets) {
        if (!string.IsNullOrEmpty(user)) {
            builder["Uid"] = user;
        }
        if (!string.IsNullOrEmpty(secretRef)) {
            secrets ??= new SecretStore();
            builder["Pwd"] = secrets.Resolve(secretRef);
        }
    }

    private static bool IsReserved(string key) =>
        key.Equals("driver", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("dsn", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("connectionString", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("user", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("password", StringComparison.OrdinalIgnoreCase);

    private static Database Build(string name, string connectionString) =>
        new Database(name, () => new OdbcConnection(connectionString));
}

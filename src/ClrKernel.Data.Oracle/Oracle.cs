using System;
using ClrKernel.Data.Secrets;
using OracleClient = Oracle.ManagedDataAccess.Client;

namespace ClrKernel.Data.Oracle;

/// <summary>
/// Oracle provider entry point. Returns the same fluent <see cref="Database"/> as the
/// other providers, so C# cells query Oracle the same way:
/// <code>
/// var erp = Oracle.Connect("orahost", 1521, "ORCL", "scott", "oracle:erp");
/// var rows = erp.Query("select * from emp").Results();   // grid + dynamic rows
/// </code>
/// Passwords are never inline — a secret reference resolves from ClrKernel's secret
/// store (OS credential manager, or the <c>CLRKERNEL_SECRET_&lt;REF&gt;</c> env var).
/// </summary>
public static class Oracle {
    /// <summary>
    /// Connects to an Oracle service. The password for <paramref name="userId"/> resolves
    /// from the secret store under <paramref name="secretRef"/>.
    /// </summary>
    public static Database Connect(
        string server, int port, string serviceName, string userId, string secretRef, SecretStore secrets = null) {
        if (string.IsNullOrWhiteSpace(server)) {
            throw new ArgumentException("server is required.", nameof(server));
        }
        if (string.IsNullOrWhiteSpace(serviceName)) {
            throw new ArgumentException("serviceName is required.", nameof(serviceName));
        }
        if (string.IsNullOrWhiteSpace(secretRef)) {
            throw new ArgumentException("secretRef is required (the secret-store key holding the password).", nameof(secretRef));
        }
        secrets ??= new SecretStore();
        var password = secrets.Resolve(secretRef);
        var dataSource =
            $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={server})(PORT={port}))(CONNECT_DATA=(SERVICE_NAME={serviceName})))";
        var builder = new OracleClient.OracleConnectionStringBuilder {
            DataSource = dataSource,
            UserID = userId,
            Password = password,
        };
        return Build($"{server}:{port}/{serviceName}", builder.ConnectionString);
    }

    /// <summary>Connects from a full Oracle connection string (advanced escape hatch).</summary>
    public static Database FromConnectionString(string connectionString, string name = "oracle") {
        if (string.IsNullOrWhiteSpace(connectionString)) {
            throw new ArgumentException("connectionString is required.", nameof(connectionString));
        }
        return Build(name, connectionString);
    }

    /// <summary>
    /// Connects from a <c>$type: "Oracle"</c> entry in a connection config file
    /// (properties: <c>server</c>, <c>port</c>, <c>serviceName</c>, <c>userId</c>,
    /// <c>password</c> — the password as a <c>{ "secret": "&lt;ref&gt;" }</c>).
    /// </summary>
    public static Database FromConfig(string name, SecretStore secrets = null) {
        var config = ConnectionConfig.Load(name, secrets).EnsureType("Oracle");
        if (config.Get("connectionString") is { Length: > 0 } cs) {
            return Build(config.Name, cs);
        }
        var dataSource =
            $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={config.Require("server")})(PORT={config.GetInt("port", 1521)}))" +
            $"(CONNECT_DATA=(SERVICE_NAME={config.Require("serviceName")})))";
        var builder = new OracleClient.OracleConnectionStringBuilder {
            DataSource = dataSource,
            UserID = config.Get("userId") ?? config.Get("user"),
            Password = config.Get("password"),
        };
        return Build(config.Name, builder.ConnectionString);
    }

    private static Database Build(string name, string connectionString) =>
        new Database(name, () => new OracleClient.OracleConnection(connectionString));
}

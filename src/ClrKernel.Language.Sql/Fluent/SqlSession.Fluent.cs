using System;
using ClrKernel.Database.Provider.SqlServer;

namespace ClrKernel.Language.Sql;

/// <summary>
/// Fluent, ad-hoc connection factory on the session (exposed as <c>Sql</c>): build
/// a <see cref="SqlDatabase"/> inline without registering a named <c>#!sql-connect</c>
/// connection first.
/// <code>
/// var dw = Sql.Connection("database.example.com", "AdventureWorksDW2025");   // Integrated Security
/// var orders = dw.Query("select * from dbo.Orders").Results();
/// </code>
/// </summary>
public sealed partial class SqlSession {
    /// <summary>An ad-hoc connection using Integrated Security (Entra "Default" off Windows).</summary>
    public SqlDatabase Connection(string server, string database = null) =>
        new SqlDatabase(
            new SqlConnectionSpec {
                Name = Label(server, database),
                Server = server,
                Database = database,
                Auth = SqlAuthMode.Integrated,
            },
            _secrets);

    /// <summary>An ad-hoc connection with a SQL login; the password resolves from the
    /// secret store under <paramref name="secretRef"/> (never inline).</summary>
    public SqlDatabase Connection(string server, string database, string user, string secretRef) =>
        new SqlDatabase(
            new SqlConnectionSpec {
                Name = Label(server, database),
                Server = server,
                Database = database,
                Auth = SqlAuthMode.SqlPassword,
                User = user,
                SecretRef = secretRef,
            },
            _secrets);

    /// <summary>An ad-hoc connection using Microsoft Entra (Azure AD) default auth.</summary>
    public SqlDatabase AzureConnection(string server, string database = null) =>
        new SqlDatabase(
            new SqlConnectionSpec {
                Name = Label(server, database),
                Server = server,
                Database = database,
                Auth = SqlAuthMode.AzureAdDefault,
            },
            _secrets);

    /// <summary>An ad-hoc connection from a full connection string (advanced escape hatch).</summary>
    public SqlDatabase ConnectionString(string connectionString) {
        if (string.IsNullOrWhiteSpace(connectionString)) {
            throw new ArgumentException("connectionString is required.", nameof(connectionString));
        }
        return new SqlDatabase(
            new SqlConnectionSpec {
                Name = "custom",
                Auth = SqlAuthMode.RawConnectionString,
                RawConnectionString = connectionString,
            },
            _secrets);
    }

    /// <summary>A fluent handle for an already-registered <c>#!sql-connect</c> connection.</summary>
    public SqlDatabase Database(string registeredConnectionName) =>
        new SqlDatabase(_registry.Resolve(registeredConnectionName), _secrets);

    private static string Label(string server, string database) =>
        string.IsNullOrEmpty(database) ? server : $"{server}/{database}";
}

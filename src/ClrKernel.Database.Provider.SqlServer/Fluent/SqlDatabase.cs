using System;
using System.Data.Common;
using ClrKernel.Core.Secrets;
using Microsoft.Data.SqlClient;

namespace ClrKernel.Database.Provider.SqlServer;

/// <summary>
/// A fluent handle to a SQL Server database — the entry point for the ergonomic
/// query API. Create one with <c>Sql.Connection(server, database)</c> and chain
/// <see cref="Query(string, object)"/>, <see cref="Table(string)"/>,
/// <see cref="DataSource.Execute(string, object)"/>, or
/// <see cref="DataSource.Scalar{T}(string, object)"/>:
/// <code>
/// var dw = Sql.Connection("database.example.com", "AdventureWorksDW2025");
/// var orders = dw.Query("select * from dbo.Orders").Results();   // grid + rows
/// </code>
/// Each call opens and closes its own connection unless run inside
/// <see cref="DataSource.Transaction"/>. Auth defaults to Integrated Security; passwords
/// (when used) resolve from the secret store, never inline.
/// <para>
/// The command/results/transaction machinery is inherited from <see cref="DataSource"/>;
/// what SQL Server adds is the connection spec, SqlClient-typed <see cref="Open"/> (which
/// <c>SqlBulkCopy</c> requires), and the bulk-copy surface on <see cref="SqlTable"/>.
/// </para>
/// </summary>
public sealed class SqlDatabase : DataSource {
    private readonly SqlConnectionSpec _spec;

    internal SqlDatabase(SqlConnectionSpec spec, SecretStore secrets)
        : base(Validated(spec).Name, ConnectionFactory(spec, secrets ?? new SecretStore())) {
        _spec = spec;
    }

    /// <summary>The connection's descriptive name (e.g. <c>server/database</c>).</summary>
    /// <remarks>Read from the spec on each access rather than captured at construction,
    /// so a renamed spec still reports its current name — the pre-P4b behaviour.</remarks>
    public override string Name => _spec.Name;

    internal SqlConnectionSpec Spec => _spec;

    /// <summary>Opens a live connection (caller owns/disposes it).</summary>
    /// <remarks>Narrowed to <see cref="SqlConnection"/>: <c>SqlBulkCopy</c> and the
    /// create-if-missing path need the concrete type, not <see cref="DbConnection"/>.</remarks>
    public override SqlConnection Open() => (SqlConnection)base.Open();

    /// <summary>A lazy query; call <c>.Results()</c> / <c>.Results&lt;T&gt;()</c> to run it.</summary>
    public override SqlQuery Query(string sql, object parameters = null) => new SqlQuery(this, sql, parameters);

    /// <summary>A reference to a table (usable as a query source or a bulk-copy target).</summary>
    public override SqlTable Table(string name) => new SqlTable(this, name);

    private static SqlConnectionSpec Validated(SqlConnectionSpec spec) =>
        spec ?? throw new ArgumentNullException(nameof(spec));

    // Deferred: the secret is resolved when a connection is actually opened, not when
    // the handle is built, so constructing a SqlDatabase never touches the credential store.
    private static Func<DbConnection> ConnectionFactory(SqlConnectionSpec spec, SecretStore secrets) => () => {
        string connectionString;
        try {
            connectionString = spec.BuildConnectionString(secrets);
        } catch (SecretNotFoundException e) {
            throw new SqlCellException(e.Message, e);
        }
        return new SqlConnection(connectionString);
    };
}

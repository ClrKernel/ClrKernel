using System;
using System.Data;
using System.Data.Common;

namespace ClrKernel.Database;

/// <summary>
/// A provider-agnostic, fluent handle to a database. Created by a provider
/// (<c>Oracle.Connect(...)</c>, <c>Odbc.FromConnectionString(...)</c>, …) from a
/// connection factory, then used the same way regardless of engine:
/// <code>
/// var db = Oracle.Connect("host", 1521, "ORCL", "scott", "oracle:scott");
/// var rows = db.Query("select * from emp").Results();   // grid + dynamic rows
/// </code>
/// Each call opens and closes its own connection unless run inside
/// <see cref="Transaction"/>.
/// <para>
/// A provider may derive from this to add engine-specific surface — see
/// <c>ClrKernel.Database.Provider.SqlServer.SqlDatabase</c>, which narrows
/// <see cref="Open"/>, <see cref="Query"/> and <see cref="Table"/> to SqlClient types
/// via covariant overrides. The members below are <c>virtual</c> for that reason; the
/// non-virtual ones (<see cref="Execute"/>, <see cref="Scalar{T}"/>,
/// <see cref="Transaction"/>) are shared as-is because they route through the virtual ones.
/// </para>
/// </summary>
public class DataSource {
    private readonly Func<DbConnection> _connectionFactory;

    public DataSource(string name, Func<DbConnection> connectionFactory) {
        Name = name ?? string.Empty;
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>A descriptive name for the connection (e.g. <c>host/service</c>).</summary>
    public virtual string Name { get; }

    /// <summary>Default command timeout (seconds) applied when a call doesn't set one.</summary>
    public int? DefaultCommandTimeout { get; set; }

    /// <summary>Opens a live connection (caller owns/disposes it).</summary>
    public virtual DbConnection Open() {
        var connection = _connectionFactory();
        connection.Open();
        return connection;
    }

    /// <summary>A lazy query; call <c>.Results()</c> / <c>.Results&lt;T&gt;()</c> to run it.</summary>
    public virtual DataSourceQuery Query(string sql, object parameters = null) => new DataSourceQuery(this, sql, parameters);

    /// <summary>A reference to a table (query source and generic insert target).</summary>
    public virtual DataSourceTable Table(string name) => new DataSourceTable(this, name);

    /// <summary>Runs a non-query statement and returns rows affected.</summary>
    public int Execute(string sql, object parameters = null) {
        using var connection = Open();
        using var command = CreateCommand(connection, null, sql, parameters, DefaultCommandTimeout);
        return command.ExecuteNonQuery();
    }

    /// <summary>Runs a query and returns the first column of the first row as <typeparamref name="T"/>.</summary>
    public T Scalar<T>(string sql, object parameters = null) {
        using var connection = Open();
        using var command = CreateCommand(connection, null, sql, parameters, DefaultCommandTimeout);
        return ValueConverter.To<T>(command.ExecuteScalar());
    }

    /// <summary>Begins a transaction; queries/commands on the returned handle share it
    /// until <c>Commit()</c> / <c>Rollback()</c> (dispose without committing rolls back).</summary>
    public DataSourceTransaction Transaction() {
        var connection = Open();
        var transaction = connection.BeginTransaction();
        return new DataSourceTransaction(this, connection, transaction);
    }

    internal int? EffectiveTimeout(int? explicitTimeout) => explicitTimeout ?? DefaultCommandTimeout;

    internal static DbCommand CreateCommand(
        DbConnection connection, DbTransaction transaction, string sql, object parameters, int? timeout) {
        if (string.IsNullOrWhiteSpace(sql)) {
            throw new ArgumentException("sql is required.", nameof(sql));
        }
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;
        if (transaction != null) {
            command.Transaction = transaction;
        }
        if (timeout is int t) {
            command.CommandTimeout = t;
        }
        ParameterBinder.Bind(command, parameters);
        return command;
    }
}

/// <summary>
/// A <see cref="DataSource"/>-backed transaction. Queries and commands run on its open
/// connection and share the transaction; <see cref="Commit"/> / <see cref="Rollback"/>
/// finish it. Disposing without committing rolls back.
/// </summary>
public sealed class DataSourceTransaction : IDisposable {
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private bool _finished;

    internal DataSourceTransaction(DataSource database, DbConnection connection, DbTransaction transaction) {
        DataSource = database;
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>The database this transaction runs against.</summary>
    public DataSource DataSource { get; }

    /// <summary>Runs a non-query in the transaction and returns rows affected.</summary>
    public int Execute(string sql, object parameters = null) {
        using var command = ClrKernel.Database.DataSource.CreateCommand(_connection, _transaction, sql, parameters, null);
        return command.ExecuteNonQuery();
    }

    /// <summary>Runs a scalar query in the transaction.</summary>
    public T Scalar<T>(string sql, object parameters = null) {
        using var command = ClrKernel.Database.DataSource.CreateCommand(_connection, _transaction, sql, parameters, null);
        return ValueConverter.To<T>(command.ExecuteScalar());
    }

    /// <summary>Runs a query in the transaction and materializes the rows.</summary>
    public DataResults Query(string sql, object parameters = null, int limit = 1000) {
        using var command = ClrKernel.Database.DataSource.CreateCommand(_connection, _transaction, sql, parameters, null);
        using var reader = command.ExecuteReader();
        var table = new DataTable();
        table.Load(reader);
        return new DataResults(table, limit);
    }

    /// <summary>Commits the transaction.</summary>
    public void Commit() {
        _transaction.Commit();
        _finished = true;
    }

    /// <summary>Rolls the transaction back.</summary>
    public void Rollback() {
        _transaction.Rollback();
        _finished = true;
    }

    public void Dispose() {
        try {
            if (!_finished) {
                _transaction.Rollback();
            }
        } catch {
            // best effort — connection is being torn down anyway
        }
        _transaction.Dispose();
        _connection.Dispose();
    }
}

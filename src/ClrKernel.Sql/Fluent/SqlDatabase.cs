using System;
using System.Data;
using ClrKernel.Core.Secrets;
using ClrKernel.Data;
using Microsoft.Data.SqlClient;

namespace ClrKernel.Sql;

/// <summary>
/// A fluent handle to a SQL Server database — the entry point for the ergonomic
/// query API. Create one with <c>Sql.Connection(server, database)</c> and chain
/// <see cref="Query(string, object)"/>, <see cref="Table(string)"/>,
/// <see cref="Execute(string, object)"/>, or <see cref="Scalar{T}(string, object)"/>:
/// <code>
/// var dw = Sql.Connection("database.example.com", "AdventureWorksDW2025");
/// var orders = dw.Query("select * from dbo.Orders").Results();   // grid + rows
/// </code>
/// Each call opens and closes its own connection unless run inside
/// <see cref="Transaction"/>. Auth defaults to Integrated Security; passwords (when
/// used) resolve from the secret store, never inline.
/// </summary>
public sealed class SqlDatabase {
    private readonly SqlConnectionSpec _spec;
    private readonly SecretStore _secrets;

    internal SqlDatabase(SqlConnectionSpec spec, SecretStore secrets) {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _secrets = secrets ?? new SecretStore();
    }

    /// <summary>The connection's descriptive name (e.g. <c>server/database</c>).</summary>
    public string Name => _spec.Name;

    /// <summary>Default command timeout (seconds) applied when a call doesn't set one.</summary>
    public int? DefaultCommandTimeout { get; set; }

    internal SqlConnectionSpec Spec => _spec;
    internal SecretStore Secrets => _secrets;

    /// <summary>Opens a live connection (caller owns/disposes it).</summary>
    public SqlConnection Open() {
        string connectionString;
        try {
            connectionString = _spec.BuildConnectionString(_secrets);
        } catch (SecretNotFoundException e) {
            throw new SqlCellException(e.Message, e);
        }
        var connection = new SqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>A lazy query; call <c>.Results()</c> / <c>.Results&lt;T&gt;()</c> to run it.</summary>
    public SqlQuery Query(string sql, object parameters = null) => new SqlQuery(this, sql, parameters);

    /// <summary>A reference to a table (usable as a query source or a bulk-copy target).</summary>
    public SqlTable Table(string name) => new SqlTable(this, name);

    /// <summary>Runs a non-query statement and returns rows affected.</summary>
    public int Execute(string sql, object parameters = null) {
        using var connection = Open();
        using var command = CreateCommand(connection, null, sql, parameters, null);
        return command.ExecuteNonQuery();
    }

    /// <summary>Runs a query and returns the first column of the first row as <typeparamref name="T"/>.</summary>
    public T Scalar<T>(string sql, object parameters = null) {
        using var connection = Open();
        using var command = CreateCommand(connection, null, sql, parameters, null);
        return ValueConverter.To<T>(command.ExecuteScalar());
    }

    /// <summary>Begins a transaction; queries/commands on the returned handle share it
    /// until you <c>Commit()</c> / <c>Rollback()</c> (or dispose, which rolls back).</summary>
    public SqlDatabaseTransaction Transaction() {
        var connection = Open();
        var transaction = connection.BeginTransaction();
        return new SqlDatabaseTransaction(this, connection, transaction);
    }

    internal int? EffectiveTimeout(int? explicitTimeout) => explicitTimeout ?? DefaultCommandTimeout;

    internal static SqlCommand CreateCommand(
        SqlConnection connection, SqlTransaction transaction, string sql, object parameters, int? timeout) {
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
/// A <see cref="SqlDatabase"/>-backed transaction. Queries and commands run on its
/// open connection and share the transaction; call <see cref="Commit"/> or
/// <see cref="Rollback"/> to finish. Disposing without committing rolls back.
/// </summary>
public sealed class SqlDatabaseTransaction : IDisposable {
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private bool _finished;

    internal SqlDatabaseTransaction(SqlDatabase database, SqlConnection connection, SqlTransaction transaction) {
        Database = database;
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>The database this transaction runs against.</summary>
    public SqlDatabase Database { get; }

    /// <summary>Runs a non-query in the transaction and returns rows affected.</summary>
    public int Execute(string sql, object parameters = null) {
        using var command = SqlDatabase.CreateCommand(_connection, _transaction, sql, parameters, null);
        return command.ExecuteNonQuery();
    }

    /// <summary>Runs a scalar query in the transaction.</summary>
    public T Scalar<T>(string sql, object parameters = null) {
        using var command = SqlDatabase.CreateCommand(_connection, _transaction, sql, parameters, null);
        return ValueConverter.To<T>(command.ExecuteScalar());
    }

    /// <summary>Runs a query in the transaction and materializes the rows.</summary>
    public DataResults Query(string sql, object parameters = null, int limit = 1000) {
        using var command = SqlDatabase.CreateCommand(_connection, _transaction, sql, parameters, null);
        using var reader = command.ExecuteReader();
        var table = new DataTable();
        table.Load(reader);
        return new DataResults(table);
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

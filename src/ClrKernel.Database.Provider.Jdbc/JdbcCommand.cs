using System;
using System.Data;
using System.Data.Common;

namespace ClrKernel.Database.Provider.Jdbc;

// Ported from Integrator.Databases.Jdbc. Executes statements over a java.sql.Statement.
// NOTE: parameters/PreparedStatement are not implemented — pass fully-formed SQL
// (ClrKernel.Database.Database.Query(sql) with no parameter object).
public class JdbcCommand : DbCommand {
    private java.sql.Statement _statement;
    private readonly JdbcConnection _connection;

    public JdbcCommand(JdbcConnection connection, java.sql.Statement statement) {
        _connection = connection;
        _statement = statement;
    }

    public override string CommandText { get; set; }
    public override CommandType CommandType { get; set; } = CommandType.Text;
    protected override DbConnection DbConnection { get => _connection; set => throw new NotImplementedException(); }
    public override int CommandTimeout { get => _statement.getQueryTimeout(); set => _statement.setQueryTimeout(value); }
    protected override DbTransaction DbTransaction { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public override UpdateRowSource UpdatedRowSource { get => UpdateRowSource.None; set { } }
    public override bool DesignTimeVisible { get; set; }
    protected override DbParameterCollection DbParameterCollection => throw new NotImplementedException();

    // JDBC bridge does not implement parameters yet (would need PreparedStatement).
    protected override DbParameter CreateDbParameter() =>
        throw new NotSupportedException("The JDBC provider does not support command parameters; inline values into the SQL.");
    public override void Prepare() => throw new NotImplementedException();

    public override void Cancel() => _statement.cancel();
    public override int ExecuteNonQuery() => _statement.executeUpdate(CommandText);

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) {
        if (string.IsNullOrWhiteSpace(CommandText)) {
            throw new ArgumentException("CommandText must be set before executing the command.");
        }
        if (behavior.HasFlag(CommandBehavior.SchemaOnly)
            || behavior.HasFlag(CommandBehavior.KeyInfo)
            || behavior.HasFlag(CommandBehavior.SequentialAccess)) {
            throw new NotImplementedException("Unsupported behavior: SchemaOnly, KeyInfo, SequentialAccess.");
        }
        if (behavior.HasFlag(CommandBehavior.SingleRow)) {
            _statement.setMaxRows(1);
        }
        var sqlCommand = CommandType switch {
            CommandType.Text => CommandText,
            CommandType.TableDirect => $"select * from {CommandText}",
            _ => CommandText,
        };
        return new JdbcDataReader(_statement.executeQuery(sqlCommand));
    }

    public override object ExecuteScalar() {
        using var reader = ExecuteReader(CommandBehavior.Default);
        return reader.Read() ? reader.GetValue(0) : null;
    }

    protected override void Dispose(bool disposing) {
        if (_statement == null) {
            return;
        }
        if (disposing) {
            _statement.close();
        }
        _statement = null;
        base.Dispose(disposing);
    }
}

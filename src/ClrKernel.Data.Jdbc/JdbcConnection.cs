using System;
using System.Data;
using System.Data.Common;

namespace ClrKernel.Data.Jdbc;

// Ported from Integrator.Databases.Jdbc. Wraps a java.sql.Connection as an ADO.NET
// DbConnection so the provider-agnostic ClrKernel.Data.Database can drive it.
public class JdbcConnection : DbConnection {
    private java.sql.Connection _connection;

    public JdbcConnection() { }

    public override string ConnectionString { get; set; }
    public override int ConnectionTimeout => _connection.getNetworkTimeout() / 1000;

    public override ConnectionState State =>
        _connection != null && !_connection.isClosed() ? ConnectionState.Open : ConnectionState.Closed;

    public override string Database => _connection.getCatalog();
    public override void ChangeDatabase(string databaseName) => _connection.setCatalog(databaseName);
    public override string DataSource => throw new NotImplementedException();
    public override string ServerVersion => throw new NotImplementedException();
    protected override DbTransaction BeginDbTransaction(IsolationLevel il) => throw new NotImplementedException();

    public override void Open() {
        if (_connection != null) {
            return;
        }
        var cs = new JdbcConnectionStringBuilder(ConnectionString);
        var factory = JdbcProviderFactory.FindByDriver(cs.JdbcDriver);
        _connection = factory.GetJdbcConnection(cs.JdbcUrl, cs.GetProperties());
    }

    public override void Close() {
        _connection?.close();
        _connection = null;
    }

    protected override DbCommand CreateDbCommand() =>
        State == ConnectionState.Open
            ? new JdbcCommand(this, _connection.createStatement())
            : throw new InvalidOperationException("Connection is closed.");

    protected override void Dispose(bool disposing) {
        if (disposing) {
            Close();
        }
        base.Dispose(disposing);
    }
}

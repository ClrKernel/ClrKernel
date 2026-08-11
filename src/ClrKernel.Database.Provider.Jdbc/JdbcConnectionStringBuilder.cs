using System;
using System.Data.Common;

namespace ClrKernel.Database.Provider.Jdbc;

// Ported from Integrator.Databases.Jdbc. Encodes the JDBC driver class, URL, and
// properties into an ADO.NET connection string used by JdbcConnection.
public class JdbcConnectionStringBuilder : DbConnectionStringBuilder {
    public JdbcConnectionStringBuilder() { }
    public JdbcConnectionStringBuilder(string connectionString) { ConnectionString = connectionString; }

    private const string _jdbcDriver = "JdbcDriver";
    private const string _jdbcUrl = "JdbcUrl";
    private const string _user = "user";
    private const string _password = "password";

    public string JdbcDriver { get => ContainsKey(_jdbcDriver) ? (string)this[_jdbcDriver] : null; set => this[_jdbcDriver] = value; }
    public string JdbcUrl { get => ContainsKey(_jdbcUrl) ? (string)this[_jdbcUrl] : null; set => this[_jdbcUrl] = value; }
    public string User { get => ContainsKey(_user) ? (string)this[_user] : null; set => this[_user] = value; }
    public string Password { get => ContainsKey(_password) ? (string)this[_password] : null; set => this[_password] = value; }

    public java.util.Properties GetProperties() {
        var result = new java.util.Properties();
        foreach (string key in Keys) {
            if (key == _jdbcDriver || key == _jdbcUrl) {
                continue;
            }
            switch (this[key]) {
                case null: break;
                case string value: result.setProperty(key, value); break;
                case object value: throw new Exception($"JdbcConnectionStringBuilder: values must be strings. Key '{key}' had type '{value.GetType()}'.");
            }
        }
        return result;
    }

    public static string CreateConnectionString(string jdbcDriver, string jdbcUrl, java.util.Properties properties = null) {
        var cs = new JdbcConnectionStringBuilder { JdbcDriver = jdbcDriver, JdbcUrl = jdbcUrl };
        object[] propertyNames = properties?.stringPropertyNames()?.toArray() ?? Array.Empty<object>();
        foreach (string propertyName in propertyNames) {
            cs[propertyName] = properties?.getProperty(propertyName);
        }
        return cs.ConnectionString;
    }
}

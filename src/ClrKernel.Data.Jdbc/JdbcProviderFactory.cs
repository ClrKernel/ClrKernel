using System;
using System.Collections.Generic;
using System.Data.Common;

namespace ClrKernel.Data.Jdbc;

// Ported from Integrator.Databases.Jdbc. Loads a Java JDBC driver (from an IKVM-compiled
// assembly or a jar) and hands out JDBC connections wrapped as ADO.NET connections.
public class JdbcProviderFactory : DbProviderFactory {
    private static readonly Dictionary<string, JdbcProviderFactory> _factories = new();

    public static JdbcProviderFactory FindByDriver(string driverClass) =>
        _factories.TryGetValue(driverClass, out var driverFactory) ? driverFactory : null;

    public static JdbcProviderFactory FromAssemblyPath(string assemblyPath, string driverClass) =>
        new JdbcProviderFactory(() => {
            var assembly = System.Reflection.Assembly.LoadFrom(assemblyPath);
            var driverType = assembly.GetType(driverClass, true);
            return (java.sql.Driver)Activator.CreateInstance(driverType!)!;
        }, driverClass);

    public static JdbcProviderFactory FromAssembly(System.Reflection.Assembly assembly, string driverClass) =>
        new JdbcProviderFactory(() => {
            var driverType = assembly.GetType(driverClass, true);
            return (java.sql.Driver)Activator.CreateInstance(driverType!)!;
        }, driverClass);

    public static JdbcProviderFactory FromJarPath(string jarPath, string driverClass) =>
        FromJarUrl(new java.io.File(jarPath).toURI().toString(), driverClass);

    public static JdbcProviderFactory FromJarUrl(string jarUrl, string driverClass) =>
        FromClassLoader(new java.net.URLClassLoader(new[] { new java.net.URL(jarUrl) }), driverClass);

    public static JdbcProviderFactory FromClassLoader(java.lang.ClassLoader classLoader, string driverClass) {
        var cls = java.lang.Class.forName(driverClass, true, classLoader);
        return FromDriver((java.sql.Driver)cls.newInstance(), driverClass);
    }

    public static JdbcProviderFactory FromDriver(java.sql.Driver driver, string driverClass = null) =>
        new JdbcProviderFactory(() => driver, driverClass ?? driver.GetType().FullName!);

    public JdbcProviderFactory(Func<java.sql.Driver> loadDriver, string driverClass) {
        _driver = new Lazy<java.sql.Driver>(loadDriver);
        _driverClass = driverClass;
        _factories[driverClass] = this;
    }

    private readonly string _driverClass;
    private readonly Lazy<java.sql.Driver> _driver;

    public java.sql.Driver JdbcDriver => _driver.Value;

    public bool AcceptsUrl(string url) => JdbcDriver.acceptsURL(url);

    public java.sql.Connection GetJdbcConnection(string url, java.util.Properties properties = null) =>
        JdbcDriver.connect(url, properties ?? new java.util.Properties());

    public System.Data.IDbConnection GetDbConnection(string jdbcUrl, java.util.Properties properties = null) =>
        new JdbcConnection { ConnectionString = JdbcConnectionStringBuilder.CreateConnectionString(_driverClass, jdbcUrl, properties) };

    public override DbConnectionStringBuilder CreateConnectionStringBuilder() =>
        new JdbcConnectionStringBuilder { JdbcDriver = _driverClass };

    public override DbDataSource CreateDataSource(string connectionString) => new JdbcDataSource(connectionString);
}

public class JdbcDataSource : DbDataSource {
    private readonly string _connectionString;
    public JdbcDataSource(string connectionString) { _connectionString = connectionString; }
    public override string ConnectionString => _connectionString;
    protected override DbConnection CreateDbConnection() => new JdbcConnection { ConnectionString = _connectionString };
}

using System.Collections.Generic;
using ClrKernel.Data.Secrets;

namespace ClrKernel.Data.Jdbc;

/// <summary>
/// EXPERIMENTAL Progress OpenEdge helper over the JDBC provider (DataDirect driver).
/// Mirrors the lib-notebooks OpenEdge connection:
/// <code>
/// var db = OpenEdge.Connect("host", "sports2000", "user", "openedge:app",
///     driverAssemblyPath: "OpenEdge.JdbcDriver.dll");
/// var rows = db.Query("select * from public.Customer").Results();
/// </code>
/// You supply the DataDirect OpenEdge JDBC driver assembly (IKVM-compiled from the
/// vendor jar). Validate on Windows before relying on it.
/// </summary>
public static class OpenEdge {
    /// <summary>The DataDirect OpenEdge JDBC driver class.</summary>
    public const string JdbcDriverClass = "com.ddtek.jdbc.openedge.OpenEdgeDriver";

    /// <summary>
    /// Connects to an OpenEdge database over JDBC. The password resolves from the secret
    /// store under <paramref name="secretRef"/>.
    /// </summary>
    public static Database Connect(
        string server, string database, string user, string secretRef, string driverAssemblyPath,
        SecretStore secrets = null, IEnumerable<KeyValuePair<string, string>> properties = null) {
        var url = $"jdbc:datadirect:openedge://{server};databaseName={database};";
        return Jdbc.Connect(
            url, JdbcDriverClass, driverAssemblyPath, properties, user, secretRef, secrets,
            name: $"{server}/{database}");
    }
}

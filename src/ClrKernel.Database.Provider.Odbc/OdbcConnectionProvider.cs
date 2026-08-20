using ClrKernel.Core.Primitives;

namespace ClrKernel.Database.Provider.Odbc;

/// <summary>The ODBC connection type's self-description — the schema behind
/// <c>"$type": "Odbc"</c> nodes (read by <c>Odbc.FromConfig</c>). Every key
/// beyond the reserved ones passes through verbatim as a connection-string
/// keyword, which is how PostgreSQL and friends are reached today.</summary>
public static class OdbcConnectionProvider {
    public static ConnectionProviderDescriptor Descriptor { get; } = new() {
        Type = "Odbc",
        DisplayName = "ODBC",
        Description = "Any ODBC driver (opt-in: #r \"nuget: ClrKernel.Database.Provider.Odbc\").",
        AllowExtraSettings = true,
        Settings = new ConnectionSetting[] {
            new() { Name = "connectionString", DisplayName = "Connection string", OneOfGroup = "source" },
            new() { Name = "driver", DisplayName = "Driver", OneOfGroup = "source" },
            new() { Name = "dsn", DisplayName = "DSN", OneOfGroup = "source" },
            new() { Name = "user", DisplayName = "User" },
            new() { Name = "password", DisplayName = "Password", Kind = ConnectionSettingKind.SecretRef },
        },
    };
}

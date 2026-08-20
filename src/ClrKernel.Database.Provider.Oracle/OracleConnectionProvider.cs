using ClrKernel.Core.Primitives;

namespace ClrKernel.Database.Provider.Oracle;

/// <summary>The Oracle connection type's self-description — the schema behind
/// <c>"$type": "Oracle"</c> nodes (read by <c>Oracle.FromConfig</c>). Used from C#
/// cells; no <c>#!</c> directive form.</summary>
public static class OracleConnectionProvider {
    public static ConnectionProviderDescriptor Descriptor { get; } = new() {
        Type = "Oracle",
        DisplayName = "Oracle",
        Description = "Oracle Database via ODP.NET (opt-in: #r \"nuget: ClrKernel.Database.Provider.Oracle\").",
        Settings = new ConnectionSetting[] {
            new() { Name = "connectionString", DisplayName = "Connection string", OneOfGroup = "target" },
            new() { Name = "server", DisplayName = "Server", OneOfGroup = "target" },
            new() { Name = "port", DisplayName = "Port", Kind = ConnectionSettingKind.Int, Default = "1521" },
            new() { Name = "serviceName", DisplayName = "Service name" },
            new() { Name = "userId", Aliases = new[] { "user" }, DisplayName = "User" },
            new() { Name = "password", DisplayName = "Password", Kind = ConnectionSettingKind.SecretRef },
        },
    };
}

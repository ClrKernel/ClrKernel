using ClrKernel.Core.Primitives;

namespace ClrKernel.Database.Provider.Jdbc;

/// <summary>The JDBC bridge's self-description. There is no <c>$type</c> /
/// connections.json backing yet (a noted follow-up) — the descriptor documents the
/// settings <c>Jdbc.Connect</c> / <c>Jdbc.ConnectJar</c> take, so generated UI can
/// describe the provider even though it is configured in code today.</summary>
public static class JdbcConnectionProvider {
    public static ConnectionProviderDescriptor Descriptor { get; } = new() {
        Type = "Jdbc",
        DisplayName = "JDBC",
        Description = "Any JDBC driver via IKVM (opt-in: #r \"nuget: ClrKernel.Database.Provider.Jdbc\"; Windows x64).",
        AllowExtraSettings = true, // driver properties bag
        Settings = new ConnectionSetting[] {
            new() { Name = "jdbcUrl", DisplayName = "JDBC URL", Required = true },
            new() { Name = "driverClass", DisplayName = "Driver class", Required = true },
            new() { Name = "driverAssemblyPath", DisplayName = "Driver assembly (.dll)", Kind = ConnectionSettingKind.FilePath, OneOfGroup = "driver" },
            new() { Name = "driverJarPath", DisplayName = "Driver jar", Kind = ConnectionSettingKind.FilePath, OneOfGroup = "driver" },
            new() { Name = "user", DisplayName = "User" },
            new() { Name = "password", DisplayName = "Password", Kind = ConnectionSettingKind.SecretRef },
        },
    };
}

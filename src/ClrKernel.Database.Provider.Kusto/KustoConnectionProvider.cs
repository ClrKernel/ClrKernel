using ClrKernel.Core.Primitives;

namespace ClrKernel.Database.Provider.Kusto;

/// <summary>The Kusto connection type's self-description — Azure Data Explorer, a
/// Fabric Eventhouse / KQL database, Log Analytics — behind <c>#!kql-connect</c>.</summary>
public static class KustoConnectionProvider {
    public const string TypeName = "Kusto";

    public static ConnectionProviderDescriptor Descriptor { get; } = new() {
        Type = TypeName,
        DisplayName = "Kusto (Azure Data Explorer)",
        Description = "Azure Data Explorer, a Fabric Eventhouse / KQL database, or Log Analytics, via Entra sign-in.",
        LanguageIds = new[] { "kql" },
        ConnectSelector = "#!kql-connect",
        Settings = new ConnectionSetting[] {
            new() { Name = "name", DisplayName = "Connection name", Required = true, DirectiveFlag = "--name" },
            new() { Name = "cluster", Aliases = new[] { "server", "url" }, DisplayName = "Cluster URL", Required = true, DirectiveFlag = "--cluster",
                Description = "https://<cluster>.<region>.kusto.windows.net, or a Fabric Eventhouse query URI." },
            new() { Name = "database", DisplayName = "Database", Required = true, DirectiveFlag = "--database" },
            new() { Name = "auth", DisplayName = "Authentication", Kind = ConnectionSettingKind.Enum,
                EnumValues = new[] { "entra", "interactive", "clientsecret" }, Default = "entra",
                CredentialValues = new[] { "clientsecret" }, DirectiveFlag = "--auth" },
            new() { Name = "tenant", Aliases = new[] { "tenantId" }, DisplayName = "Tenant id", DirectiveFlag = "--tenant" },
            new() { Name = "clientId", Aliases = new[] { "appId" }, DisplayName = "Client (application) id", DirectiveFlag = "--client-id" },
            new() { Name = "secret", DisplayName = "Client secret", Kind = ConnectionSettingKind.SecretRef, DirectiveFlag = "--secret",
                Description = "A secret reference — never the secret itself." },
        },
    };
}

using ClrKernel.Core.Primitives;

namespace ClrKernel.Database.Provider.AnalysisServices;

/// <summary>The Analysis Services connection type's self-description — on-prem SSAS,
/// Azure AS, and Fabric / Power BI semantic models, behind <c>"$type":
/// "AnalysisServices"</c> nodes and the <c>#!dax-connect</c> wizard.</summary>
public static class SsasConnectionProvider {
    public static ConnectionProviderDescriptor Descriptor { get; } = new() {
        Type = SsasConnectionConfig.TypeName,
        DisplayName = "Analysis Services",
        Description = "SSAS, Azure Analysis Services, or a Fabric / Power BI semantic model (XMLA).",
        LanguageIds = new[] { "dax" },
        ConnectSelector = "#!dax-connect",
        Settings = new ConnectionSetting[] {
            new() { Name = "name", DisplayName = "Cube name", Required = true, DirectiveFlag = "--name" },
            new() { Name = "server", Aliases = new[] { "host" }, DisplayName = "Server / XMLA endpoint", OneOfGroup = "target", DirectiveFlag = "--server" },
            new() { Name = "workspace", DisplayName = "Fabric / Power BI workspace", OneOfGroup = "target", DirectiveFlag = "--workspace",
                Requires = new[] { "model" },
                Description = "With a model, connects to the workspace's XMLA endpoint (--fabric)." },
            new() { Name = "connectionString", DisplayName = "Connection string", OneOfGroup = "target", DirectiveFlag = "--connection-string" },
            new() { Name = "database", Aliases = new[] { "catalog" }, DisplayName = "Database (server / Azure AS)", DirectiveFlag = "--database" },
            new() { Name = "model", Aliases = new[] { "dataset" }, DisplayName = "Semantic model (Fabric / Power BI)", DirectiveFlag = "--model",
                Description = "With a workspace, selects the Fabric / Power BI semantic model." },
            new() { Name = "auth", DisplayName = "Authentication", Kind = ConnectionSettingKind.Enum,
                EnumValues = new[] { "integrated", "sql", "user", "aad", "entra" },
                CredentialValues = new[] { "sql", "user" }, DirectiveFlag = "--auth" },
            new() { Name = "user", Aliases = new[] { "username" }, DisplayName = "User", DirectiveFlag = "--user" },
            new() { Name = "password", DisplayName = "Password", Kind = ConnectionSettingKind.SecretRef, DirectiveFlag = "--secret",
                Description = "A secret reference — never the password itself." },
            new() { Name = "integrated", DisplayName = "Signed-in Windows identity", Kind = ConnectionSettingKind.Bool,
                Default = "false", DirectiveFlag = "--integrated",
                Description = "Hand the XMLA endpoint the signed-in Windows identity instead of a fetched token (Windows only)." },
            // Rebuilt on load from the server URL; a delegate cannot be serialized.
            new() { Name = "tokenProvider", DisplayName = "Entra token provider", RuntimeOnly = true },
        },
    };
}

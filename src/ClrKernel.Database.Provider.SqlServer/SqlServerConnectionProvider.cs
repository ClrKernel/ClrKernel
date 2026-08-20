using ClrKernel.Core.Primitives;

namespace ClrKernel.Database.Provider.SqlServer;

/// <summary>The SQL Server connection type's self-description — the schema behind
/// <c>"$type": "SqlServer"</c> nodes and the <c>#!sql-connect</c> wizard.</summary>
public static class SqlServerConnectionProvider {
    public static ConnectionProviderDescriptor Descriptor { get; } = new() {
        Type = SqlConnectionConfig.TypeName,
        DisplayName = "SQL Server",
        Description = "SQL Server / Azure SQL over ADO.NET.",
        LanguageIds = new[] { "sql" },
        ConnectSelector = "#!sql-connect",
        AllowExtraSettings = true, // --option k=v → connection-string keywords
        Settings = new ConnectionSetting[] {
            new() { Name = "name", DisplayName = "Connection name", Required = true, DirectiveFlag = "--name" },
            new() { Name = "server", Aliases = new[] { "host" }, DisplayName = "Server", OneOfGroup = "target", DirectiveFlag = "--server" },
            new() { Name = "connectionString", DisplayName = "Connection string", OneOfGroup = "target", DirectiveFlag = "--connection-string" },
            new() { Name = "database", DisplayName = "Database", DirectiveFlag = "--database" },
            new() { Name = "auth", DisplayName = "Authentication", Kind = ConnectionSettingKind.Enum,
                EnumValues = new[] { "sql", "integrated", "entra", "entra-password", "entra-interactive" },
                Default = "integrated", DirectiveFlag = "--auth" },
            new() { Name = "user", Aliases = new[] { "username" }, DisplayName = "User", DirectiveFlag = "--user" },
            new() { Name = "password", DisplayName = "Password", Kind = ConnectionSettingKind.SecretRef, DirectiveFlag = "--secret",
                Description = "A secret reference — the password lives in the OS credential store, never in config." },
            new() { Name = "encrypt", DisplayName = "Encrypt", Kind = ConnectionSettingKind.Bool, Default = "true", DirectiveFlag = "--encrypt" },
            new() { Name = "trustServerCertificate", Aliases = new[] { "trustCert" }, DisplayName = "Trust server certificate",
                Kind = ConnectionSettingKind.Bool, Default = "false", DirectiveFlag = "--trust-cert" },
        },
    };
}

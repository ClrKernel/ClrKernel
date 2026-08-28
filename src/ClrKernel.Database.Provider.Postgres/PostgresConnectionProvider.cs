using ClrKernel.Core.Primitives;

namespace ClrKernel.Database.Provider.Postgres;

/// <summary>The PostgreSQL connection type's self-description — the schema behind
/// <c>"$type": "Postgres"</c> nodes and the form that saves one.</summary>
public static class PostgresConnectionProvider {
    public static ConnectionProviderDescriptor Descriptor { get; } = new() {
        Type = PostgresConnectionConfig.TypeName,
        DisplayName = "PostgreSQL",
        Description = "PostgreSQL over Npgsql.",
        LanguageIds = new[] { "sql" },
        // Anything else becomes a connection-string keyword, the way the ODBC and SQL
        // Server providers already work — Npgsql has many and enumerating them here
        // would be a second, staler copy of its documentation.
        AllowExtraSettings = true,
        Settings = new ConnectionSetting[] {
            new() { Name = "name", DisplayName = "Connection name", Required = true },
            new() { Name = "server", Aliases = new[] { "host" }, DisplayName = "Host", OneOfGroup = "target" },
            new() { Name = "connectionString", DisplayName = "Connection string", OneOfGroup = "target" },
            new() { Name = "port", DisplayName = "Port", Kind = ConnectionSettingKind.Int, Default = "5432" },
            new() { Name = "database", DisplayName = "Database" },
            new() { Name = "user", Aliases = new[] { "username" }, DisplayName = "User" },
            new() { Name = "password", DisplayName = "Password", Kind = ConnectionSettingKind.SecretRef,
                Description = "A secret reference — the password lives in the credential store, never in config." },
            // Npgsql's own name for the setting, so what is typed here matches what
            // its documentation calls it.
            new() { Name = "sslMode", DisplayName = "SSL mode", Kind = ConnectionSettingKind.Enum,
                EnumValues = new[] { "Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull" },
                Default = "Prefer" },
        },
    };
}

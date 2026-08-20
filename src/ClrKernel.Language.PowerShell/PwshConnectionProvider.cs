using ClrKernel.Core.Primitives;

namespace ClrKernel.Language.PowerShell;

/// <summary>The PSRemoting target's self-description — <c>"$type": "PSRemoting"</c>
/// nodes, created with <c>#!pwsh-connect</c>. (PowerShell cells also accept shared
/// <c>"$type": "Ssh"</c> nodes — that provider is described by the shell language.)</summary>
public static class PwshConnectionProvider {
    public static ConnectionProviderDescriptor Descriptor { get; } = new() {
        Type = PwshConnectionConfig.TypeName,
        DisplayName = "PowerShell Remoting",
        Description = "A remote runspace over SSH or WinRM.",
        LanguageIds = new[] { "powershell" },
        ConnectSelector = "#!pwsh-connect",
        Settings = new ConnectionSetting[] {
            new() { Name = "name", DisplayName = "Connection name", Required = true, DirectiveFlag = "--name" },
            new() { Name = "host", Aliases = new[] { "server" }, DisplayName = "Host", Required = true, DirectiveFlag = "--host" },
            new() { Name = "user", Aliases = new[] { "username" }, DisplayName = "User", DirectiveFlag = "--user" },
            new() { Name = "port", DisplayName = "Port", Kind = ConnectionSettingKind.Int, DirectiveFlag = "--port" },
            new() { Name = "transport", DisplayName = "Transport", Kind = ConnectionSettingKind.Enum,
                EnumValues = new[] { "ssh", "winrm" }, Default = "ssh",
                Description = "On the connect directive this is the --ssh / --winrm flag pair." },
            new() { Name = "identity", Aliases = new[] { "identityFile" }, DisplayName = "Identity file (ssh)",
                Kind = ConnectionSettingKind.FilePath, DirectiveFlag = "--identity" },
            new() { Name = "password", DisplayName = "Password (winrm)", Kind = ConnectionSettingKind.SecretRef, DirectiveFlag = "--secret",
                Description = "A secret reference resolved from the credential store at connect time." },
            new() { Name = "useSsl", DisplayName = "HTTPS (winrm)", Kind = ConnectionSettingKind.Bool, Default = "false", DirectiveFlag = "--use-ssl" },
        },
    };
}

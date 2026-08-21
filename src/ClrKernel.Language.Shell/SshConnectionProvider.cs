using ClrKernel.Core.Primitives;

namespace ClrKernel.Language.Shell;

/// <summary>The SSH target's self-description — <c>"$type": "Ssh"</c> nodes, shared
/// by the shell AND PowerShell languages (one host definition serves both), created
/// with <c>#!shell-connect</c>. Key-based auth only: no secrets by design.</summary>
public static class SshConnectionProvider {
    public static ConnectionProviderDescriptor Descriptor { get; } = new() {
        Type = ShellConnectionConfig.TypeName,
        DisplayName = "SSH",
        Description = "A remote host over SSH (keys, agent, and ~/.ssh/config apply — no passwords).",
        LanguageIds = new[] { "shellscript", "powershell" },
        ConnectSelector = "#!shell-connect",
        Settings = new ConnectionSetting[] {
            new() { Name = "name", DisplayName = "Connection name", Required = true, DirectiveFlag = "--name" },
            new() { Name = "host", Aliases = new[] { "server" }, DisplayName = "Host", Required = true, DirectiveFlag = "--host" },
            new() { Name = "user", Aliases = new[] { "username" }, DisplayName = "User", DirectiveFlag = "--user" },
            new() { Name = "port", DisplayName = "Port", Kind = ConnectionSettingKind.Int, Default = "22", DirectiveFlag = "--port" },
            new() { Name = "identity", Aliases = new[] { "identityFile" }, DisplayName = "Identity file",
                Kind = ConnectionSettingKind.FilePath, DirectiveFlag = "--identity" },
            new() { Name = "remoteShell", Aliases = new[] { "shell" }, DisplayName = "Remote shell",
                Kind = ConnectionSettingKind.Enum, EnumValues = new[] { "bash", "zsh", "sh" }, DirectiveFlag = "--remote-shell" },
        },
    };
}

using System;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using ClrKernel.Core.Scripting;
using ClrKernel.Core.Secrets;
using ClrKernel.Database;

namespace ClrKernel.Language.PowerShell;

/// <summary>How a remote PowerShell runspace is reached.</summary>
public enum PwshTransport {
    /// <summary>PowerShell-over-SSH (cross-platform; needs PowerShell on the remote and
    /// the ssh subsystem enabled). Key-based auth — keys, agent, ~/.ssh/config apply.</summary>
    Ssh,
    /// <summary>Classic WinRM PSRemoting. Credentials are user + a secret <em>reference</em>
    /// resolved from the OS credential store at connect time — never stored.</summary>
    WinRm,
}

/// <summary>A named PSRemoting target for <c>#!pwsh --connection</c> cells.</summary>
public sealed class PwshConnectionSpec {
    private const string _shellUri = "http://schemas.microsoft.com/powershell/Microsoft.PowerShell";

    public string Name { get; set; }
    public string Host { get; set; }
    public string User { get; set; }
    public int Port { get; set; }
    public PwshTransport Transport { get; set; } = PwshTransport.Ssh;
    public string IdentityFile { get; set; }
    public string SecretRef { get; set; }
    public bool UseSsl { get; set; }

    public string Describe() =>
        (string.IsNullOrEmpty(User) ? "" : User + "@") + Host +
        (Port > 0 ? ":" + Port : "") + " (" + (Transport == PwshTransport.Ssh ? "ssh" : "winrm") + ")";

    public RunspaceConnectionInfo CreateConnectionInfo(SecretStore secrets) {
        if (string.IsNullOrWhiteSpace(Host)) {
            throw new PowerShellCellException($"PSRemoting connection '{Name}' has no host.");
        }
        if (Transport == PwshTransport.Ssh) {
            // Explicit connecting timeout: the default lets a dead host hang a cell.
            return new SSHConnectionInfo(User, Host, IdentityFile, Port > 0 ? Port : 22, "powershell", 20000);
        }
        var credential = BuildCredential(secrets);
        var scheme = UseSsl ? "https" : "http";
        var port = Port > 0 ? Port : (UseSsl ? 5986 : 5985);
        return new WSManConnectionInfo(scheme, Host, port, "/wsman", _shellUri, credential);
    }

    // Null credential = the current identity (domain SSO on Windows).
    private PSCredential BuildCredential(SecretStore secrets) {
        if (string.IsNullOrEmpty(User)) {
            return null;
        }
        if (string.IsNullOrEmpty(SecretRef)) {
            throw new PowerShellCellException(
                $"PSRemoting connection '{Name}' names user '{User}' but no --secret reference. " +
                "Store the password in the credential store and reference it — it is never written anywhere.");
        }
        if (!secrets.TryResolve(SecretRef, out var password)) {
            throw new PowerShellCellException(
                $"Secret '{SecretRef}' for PSRemoting connection '{Name}' was not found in the " +
                "credential store (or CLRKERNEL_SECRET_* environment).");
        }
        var secure = new SecureString();
        foreach (var ch in password) {
            secure.AppendChar(ch);
        }
        secure.MakeReadOnly();
        return new PSCredential(User, secure);
    }
}

/// <summary>Maps a <see cref="PwshConnectionSpec"/> from <c>connections.json</c>. Reads its
/// own <c>"$type": "PSRemoting"</c> nodes and shares <c>"$type": "Ssh"</c> targets with the
/// shell language, so one host definition serves both.</summary>
public static class PwshConnectionConfig {
    public const string TypeName = "PSRemoting";
    public const string SshTypeName = "Ssh";

    public static PwshConnectionSpec FromNode(RawConnectionNode node) {
        if (node == null) {
            throw new ArgumentNullException(nameof(node));
        }
        var spec = new PwshConnectionSpec {
            Name = node.Name,
            Host = node.Get("host") ?? node.Get("server"),
            User = node.Get("user") ?? node.Get("username"),
            Port = int.TryParse(node.Get("port"), out var port) ? port : 0,
            IdentityFile = node.Get("identity") ?? node.Get("identityFile"),
            UseSsl = string.Equals(node.Get("useSsl"), "true", StringComparison.OrdinalIgnoreCase),
            Transport = node.IsType(SshTypeName) || string.Equals(node.Get("transport"), "ssh", StringComparison.OrdinalIgnoreCase)
                ? PwshTransport.Ssh
                : PwshTransport.WinRm,
        };
        var secretRef = node.SecretRef("password");
        if (!string.IsNullOrEmpty(secretRef)) {
            spec.SecretRef = secretRef;
        }
        return spec;
    }
}

/// <summary>Parses <c>#!pwsh-connect</c> and the per-cell <c>--connection</c> flag.</summary>
public static class PwshDirectives {
    /// <summary>The declarative shape of <c>#!pwsh-connect</c>.</summary>
    public static readonly DirectiveDefinition ConnectDefinition = new() {
        Selector = "#!pwsh-connect",
        Description = "Registers a named PSRemoting target for #!pwsh cells.",
        Parameters = new DirectiveParameter[] {
            new() { Name = "--name", Aliases = new[] { "-n" }, Required = true, Description = "Connection name." },
            new() { Name = "--host", Aliases = new[] { "--server", "--computer" }, Required = true, Description = "Remote host." },
            new() { Name = "--user", Aliases = new[] { "--username", "-u" }, Description = "User name." },
            new() { Name = "--port", Aliases = new[] { "-p" }, Description = "Port (22 for ssh, 5985/5986 for winrm)." },
            new() { Name = "--ssh", Kind = DirectiveParameterKind.Flag, Description = "PowerShell-over-SSH transport (default)." },
            new() { Name = "--winrm", Aliases = new[] { "--wsman" }, Kind = DirectiveParameterKind.Flag, Description = "WinRM transport." },
            new() { Name = "--identity", Aliases = new[] { "-i" }, Description = "SSH identity file." },
            new() { Name = "--secret", Aliases = new[] { "--secret-ref" }, Description = "Secret reference for the WinRM password." },
            new() { Name = "--use-ssl", Aliases = new[] { "--ssl" }, Kind = DirectiveParameterKind.Flag, Description = "HTTPS for WinRM." },
            new() { Name = "--password", Kind = DirectiveParameterKind.Forbidden,
                ForbiddenMessage = "Passwords must not be placed in notebook cells. For WinRM, store the password " +
                    "in the credential store and pass a --secret reference instead." },
        },
    };

    /// <summary>
    /// Parses a <c>#!pwsh-connect</c> line. Flags: <c>--name</c>, <c>--host</c>,
    /// <c>--user</c>, <c>--port</c>, <c>--ssh</c> (default) / <c>--winrm</c>,
    /// <c>--identity</c> (ssh key), <c>--secret</c> (winrm password reference),
    /// <c>--use-ssl</c>. A committed <c>--password</c> is rejected on purpose.
    /// </summary>
    public static PwshConnectionSpec ParseConnect(string line) {
        var args = DirectiveParser.Parse(ConnectDefinition, line);
        var spec = new PwshConnectionSpec {
            Name = args.Get("--name"),
            Host = args.Get("--host"),
            User = args.Get("--user"),
            IdentityFile = args.Get("--identity"),
            SecretRef = args.Get("--secret"),
            UseSsl = args.Has("--use-ssl"),
        };
        if (args.Has("--port")) {
            spec.Port = int.TryParse(args.Get("--port"), out var port)
                ? port
                : throw new FormatException("--port expects a number.");
        }
        switch (args.LastOf("--ssh", "--winrm")) {
            case "--ssh": spec.Transport = PwshTransport.Ssh; break;
            case "--winrm": spec.Transport = PwshTransport.WinRm; break;
        }
        return spec;
    }

    /// <summary>The <c>--connection &lt;name&gt;</c> from a selector line, or null (local runspace).</summary>
    public static string SelectorConnection(string firstLine) =>
        DirectiveParser.FindValue(firstLine, "--connection");
}

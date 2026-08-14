using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
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
    /// <summary>
    /// Parses a <c>#!pwsh-connect</c> line. Flags: <c>--name</c>, <c>--host</c>,
    /// <c>--user</c>, <c>--port</c>, <c>--ssh</c> (default) / <c>--winrm</c>,
    /// <c>--identity</c> (ssh key), <c>--secret</c> (winrm password reference),
    /// <c>--use-ssl</c>. A committed <c>--password</c> is rejected on purpose.
    /// </summary>
    public static PwshConnectionSpec ParseConnect(string line) {
        var tokens = Tokenize(StripSelector(line, "#!pwsh-connect"));
        var spec = new PwshConnectionSpec();
        for (var i = 0; i < tokens.Count; i++) {
            var t = tokens[i];
            string Next() => i + 1 < tokens.Count ? tokens[++i] : throw new FormatException($"Missing value for {t}.");
            switch (t.ToLowerInvariant()) {
                case "--name": case "-n": spec.Name = Next(); break;
                case "--host": case "--server": case "--computer": spec.Host = Next(); break;
                case "--user": case "--username": case "-u": spec.User = Next(); break;
                case "--port":
                case "-p":
                    spec.Port = int.TryParse(Next(), out var port)
                        ? port
                        : throw new FormatException("--port expects a number.");
                    break;
                case "--ssh": spec.Transport = PwshTransport.Ssh; break;
                case "--winrm": case "--wsman": spec.Transport = PwshTransport.WinRm; break;
                case "--identity": case "-i": spec.IdentityFile = Next(); break;
                case "--secret": case "--secret-ref": spec.SecretRef = Next(); break;
                case "--use-ssl": case "--ssl": spec.UseSsl = true; break;
                case "--password":
                    throw new FormatException(
                        "Passwords must not be placed in notebook cells. For WinRM, store the password " +
                        "in the credential store and pass a --secret reference instead.");
                default:
                    throw new FormatException($"Unknown #!pwsh-connect flag '{t}'.");
            }
        }
        if (string.IsNullOrWhiteSpace(spec.Name)) {
            throw new FormatException("#!pwsh-connect requires --name.");
        }
        if (string.IsNullOrWhiteSpace(spec.Host)) {
            throw new FormatException("#!pwsh-connect requires --host.");
        }
        return spec;
    }

    private static readonly Regex _connectionFlag = new(
        @"--connection\s+(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The <c>--connection &lt;name&gt;</c> from a selector line, or null (local runspace).</summary>
    public static string SelectorConnection(string firstLine) {
        var match = _connectionFlag.Match(firstLine ?? string.Empty);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string StripSelector(string line, string selector) {
        var trimmed = (line ?? string.Empty).Trim();
        return trimmed.StartsWith(selector, StringComparison.OrdinalIgnoreCase)
            ? trimmed.Substring(selector.Length)
            : trimmed;
    }

    private static List<string> Tokenize(string text) {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        foreach (var ch in text ?? string.Empty) {
            if (quote != '\0') {
                if (ch == quote) {
                    quote = '\0';
                } else {
                    current.Append(ch);
                }
            } else if (ch == '"' || ch == '\'') {
                quote = ch;
            } else if (char.IsWhiteSpace(ch)) {
                if (current.Length > 0) {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            } else {
                current.Append(ch);
            }
        }
        if (current.Length > 0) {
            tokens.Add(current.ToString());
        }
        return tokens;
    }
}

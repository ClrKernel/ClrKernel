using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClrKernel.Core.Secrets;
/// <summary>
/// Picks the right OS-native credential store for the current platform:
/// macOS Keychain, Windows Credential Manager, or the Linux Secret Service
/// (libsecret / <c>secret-tool</c>). Returns null when no native store is
/// available (e.g. libsecret not installed), so <see cref="SecretStore"/> can
/// fall back to environment variables.
/// </summary>
public static class OsSecretProvider {
    public const string ServiceName = "ClrKernel";

    public static ISecretProvider TryCreate() {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
            return new KeychainSecretProvider();
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            return new WindowsCredentialSecretProvider();
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && SecretToolAvailable()) {
            return new LibSecretSecretProvider();
        }
        return null;
    }

    private static bool SecretToolAvailable() {
        try {
            var psi = new ProcessStartInfo("secret-tool", "--version") {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p == null) {
                return false;
            }
            p.WaitForExit(3000);
            return p.HasExited && p.ExitCode == 0;
        } catch {
            return false;
        }
    }
}

/// <summary>macOS Keychain via the <c>security</c> CLI (generic passwords).</summary>
internal sealed class KeychainSecretProvider : ISecretProvider {
    public string Name => "keychain";
    public bool CanStore => true;

    public bool TryGet(string key, out string secret) {
        var r = ProcessRunner.Run("security",
            new[] { "find-generic-password", "-a", key, "-s", OsSecretProvider.ServiceName, "-w" });
        if (r.ExitCode == 0) {
            secret = r.StandardOutput.TrimEnd('\n', '\r');
            return true;
        }
        secret = null;
        return false;
    }

    public void Set(string key, string secret) {
        // -U replaces an existing item. Passing the secret via argv is a known,
        // transient local-visibility trade-off of the `security` CLI.
        var r = ProcessRunner.Run("security",
            new[] { "add-generic-password", "-a", key, "-s", OsSecretProvider.ServiceName, "-w", secret ?? string.Empty, "-U" });
        if (r.ExitCode != 0) {
            throw new InvalidOperationException($"Keychain store failed: {r.StandardError.Trim()}");
        }
    }

    public void Delete(string key) {
        ProcessRunner.Run("security",
            new[] { "delete-generic-password", "-a", key, "-s", OsSecretProvider.ServiceName });
    }
}

/// <summary>Linux Secret Service via <c>secret-tool</c> (secret read from stdin).</summary>
internal sealed class LibSecretSecretProvider : ISecretProvider {
    public string Name => "libsecret";
    public bool CanStore => true;

    public bool TryGet(string key, out string secret) {
        var r = ProcessRunner.Run("secret-tool",
            new[] { "lookup", "service", OsSecretProvider.ServiceName, "account", key });
        if (r.ExitCode == 0 && !string.IsNullOrEmpty(r.StandardOutput)) {
            secret = r.StandardOutput.TrimEnd('\n', '\r');
            return true;
        }
        secret = null;
        return false;
    }

    public void Set(string key, string secret) {
        // secret-tool reads the secret from stdin, so it never appears on argv.
        var r = ProcessRunner.Run("secret-tool",
            new[] { "store", "--label", OsSecretProvider.ServiceName + " " + key,
                    "service", OsSecretProvider.ServiceName, "account", key },
            stdin: secret ?? string.Empty);
        if (r.ExitCode != 0) {
            throw new InvalidOperationException($"libsecret store failed: {r.StandardError.Trim()}");
        }
    }

    public void Delete(string key) {
        ProcessRunner.Run("secret-tool",
            new[] { "clear", "service", OsSecretProvider.ServiceName, "account", key });
    }
}

internal readonly struct ProcessResult {
    public ProcessResult(int exitCode, string stdout, string stderr) {
        ExitCode = exitCode;
        StandardOutput = stdout;
        StandardError = stderr;
    }
    public int ExitCode { get; }
    public string StandardOutput { get; }
    public string StandardError { get; }
}

internal static class ProcessRunner {
    public static ProcessResult Run(string fileName, string[] args, string stdin = null) {
        var psi = new ProcessStartInfo(fileName) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        if (stdin != null) {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        var outText = p.StandardOutput.ReadToEnd();
        var errText = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, outText, errText);
    }
}

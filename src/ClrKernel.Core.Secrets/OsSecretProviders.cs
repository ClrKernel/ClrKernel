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
    /// <param name="prefix">The configuration prefix, used as the store's service name.</param>
    public static ISecretProvider TryCreate(string prefix = null) {
        var serviceName = SecretPrefix.OrDefault(prefix);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
            return new KeychainSecretProvider(serviceName);
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            return new WindowsCredentialSecretProvider(serviceName);
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && SecretToolAvailable(serviceName)) {
            return new LibSecretSecretProvider(serviceName);
        }
        return null;
    }

    /// <summary>
    /// Whether the Secret Service actually answers — not merely whether
    /// <c>secret-tool</c> is installed.
    /// <para>
    /// The difference matters because the binary exits 0 for <c>--version</c> on a
    /// machine with no session bus and no keyring daemon, where every store and
    /// lookup then fails with "Cannot autolaunch D-Bus". A provider that says it
    /// <see cref="ISecretProvider.CanStore"/> and cannot is worse than an absent
    /// one: <see cref="SecretStore.Store"/> picks it, throws, and the connection
    /// editor offers a password field that can never work.
    /// </para>
    /// <para>
    /// The probe is a lookup for a key nobody stores. A live service answers "not
    /// found" — exit 1, nothing on stderr. A missing one writes the reason to
    /// stderr, which is the whole discriminator, since both exit 1.
    /// </para>
    /// </summary>
    private static bool SecretToolAvailable(string serviceName) {
        try {
            var psi = new ProcessStartInfo("secret-tool") {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] {
                "lookup", "service", serviceName, "account", "__probe__",
            }) {
                psi.ArgumentList.Add(arg);
            }
            using var p = Process.Start(psi);
            if (p == null) {
                return false;
            }
            // Read before waiting: a full stderr pipe would deadlock the wait.
            var complaint = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            if (!p.HasExited) {
                // A prompt with nobody to answer it, most likely. Killing it matters:
                // the process would otherwise outlive this call still holding the bus.
                try {
                    p.Kill(entireProcessTree: true);
                } catch {
                    // Already gone between the check and the kill.
                }
                return false;
            }
            return complaint.Trim().Length == 0;
        } catch {
            return false;
        }
    }
}

/// <summary>macOS Keychain via the <c>security</c> CLI (generic passwords).</summary>
internal sealed class KeychainSecretProvider : ISecretProvider {
    private readonly string _serviceName;

    /// <param name="prefix">The configuration prefix, used as the keychain service name.</param>
    public KeychainSecretProvider(string prefix = null) {
        _serviceName = SecretPrefix.OrDefault(prefix);
    }

    public string Name => "keychain";
    public bool CanStore => true;

    /// <summary>The keychain service every item here is filed under.</summary>
    public string ServiceName => _serviceName;

    public bool TryGet(string key, out string secret) {
        var r = ProcessRunner.Run("security",
            new[] { "find-generic-password", "-a", key, "-s", _serviceName, "-w" });
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
            new[] { "add-generic-password", "-a", key, "-s", _serviceName, "-w", secret ?? string.Empty, "-U" });
        if (r.ExitCode != 0) {
            throw new InvalidOperationException($"Keychain store failed: {r.StandardError.Trim()}");
        }
    }

    public void Delete(string key) {
        ProcessRunner.Run("security",
            new[] { "delete-generic-password", "-a", key, "-s", _serviceName });
    }
}

/// <summary>Linux Secret Service via <c>secret-tool</c> (secret read from stdin).</summary>
internal sealed class LibSecretSecretProvider : ISecretProvider {
    private readonly string _serviceName;

    /// <param name="prefix">The configuration prefix, used as the <c>service</c> attribute.</param>
    public LibSecretSecretProvider(string prefix = null) {
        _serviceName = SecretPrefix.OrDefault(prefix);
    }

    public string Name => "libsecret";
    public bool CanStore => true;

    /// <summary>The <c>service</c> attribute every item here is stored with.</summary>
    public string ServiceName => _serviceName;

    public bool TryGet(string key, out string secret) {
        var r = ProcessRunner.Run("secret-tool",
            new[] { "lookup", "service", _serviceName, "account", key });
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
            new[] { "store", "--label", _serviceName + " " + key,
                    "service", _serviceName, "account", key },
            stdin: secret ?? string.Empty);
        if (r.ExitCode != 0) {
            throw new InvalidOperationException($"libsecret store failed: {r.StandardError.Trim()}");
        }
    }

    public void Delete(string key) {
        ProcessRunner.Run("secret-tool",
            new[] { "clear", "service", _serviceName, "account", key });
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

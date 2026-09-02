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

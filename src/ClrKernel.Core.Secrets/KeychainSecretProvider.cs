using System;

namespace ClrKernel.Core.Secrets;
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

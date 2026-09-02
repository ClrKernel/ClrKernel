using System;

namespace ClrKernel.Core.Secrets;
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

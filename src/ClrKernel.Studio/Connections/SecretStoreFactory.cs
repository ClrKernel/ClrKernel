using System;
using System.IO;
using ClrKernel.Core.Secrets;

namespace ClrKernel.Studio;

/// <summary>
/// The secret store this server runs with, chosen rather than assumed.
///
/// <para>
/// The kernel's own default chain is right for a laptop: try the machine's
/// credential store, fall back to a file if one was configured, then the
/// environment. A server is not a laptop — it may have no keyring at all, or one
/// that is present but locked, and "it silently used the environment instead" is a
/// bad way to find that out. So the choice is a setting with three values, and a
/// server that cannot honour it says so at startup rather than quietly doing
/// something else.
/// </para>
/// <para>
/// No in-memory cache in front of these, unlike the default chain: a cache passed
/// to <c>ForProviders</c> is an ordinary provider, and being first and writable it
/// would swallow every write before the real store ever saw one. Reading through is
/// also what a server wants — the file provider goes out of its way to do it, so a
/// password changed by the web app is not served stale to a kernel.
/// </para>
/// </summary>
public static class SecretStoreFactory {
    /// <summary>The default file, beside the run history rather than in a worktree.</summary>
    public static string DefaultFile(JobsOptions options) =>
        Path.Combine(options.DataDir, "secrets.json");

    /// <summary>
    /// Builds the store, and reports what it chose. <paramref name="note"/> is
    /// called with a line for the startup log when the answer is worth knowing.
    /// </summary>
    public static SecretStore Create(JobsOptions options, Action<string> note = null) {
        var kind = (options?.SecretStore ?? "auto").Trim().ToLowerInvariant();
        switch (kind) {
            case "os": {
                    var os = OsSecretProvider.TryCreate(null);
                    if (os == null) {
                        // Asked for and not available: the environment still answers
                        // reads, so the server runs — it just cannot save anything, and
                        // that is the half worth saying out loud.
                        note?.Invoke(
                            "secretStore=os, but this machine has no credential store. Passwords "
                            + "cannot be saved here; set CLRKERNEL_SECRET_* variables, or use "
                            + "secretStore=file.");
                        return SecretStore.ForProviders(Env());
                    }
                    return SecretStore.ForProviders(os, Env());
                }
            case "file": {
                    var path = string.IsNullOrWhiteSpace(options.SecretsFile)
                        ? DefaultFile(options)
                        : Path.GetFullPath(options.SecretsFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    note?.Invoke(
                        $"Secrets are kept in {path}. It is unencrypted — as protected as the disk "
                        + "it sits on — and must not be inside a git worktree.");
                    return SecretStore.ForProviders(new FileSecretProvider(path), Env());
                }
            case "auto":
                // What every version before this did, and still the right default:
                // the machine's store where there is one, the file when a path was
                // configured, the environment last.
                return new SecretStore();
            default:
                throw new ArgumentException(
                    $"secretStore must be 'auto', 'os' or 'file', not '{options.SecretStore}'.");
        }
    }

    private static EnvironmentSecretProvider Env() => new(null);
}

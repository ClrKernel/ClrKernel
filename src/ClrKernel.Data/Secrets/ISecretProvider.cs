using System;

namespace ClrKernel.Data.Secrets;
/// <summary>
/// A source of named secrets (SQL passwords, tokens). Kept deliberately small
/// so an enterprise PAM / password service — HashiCorp Vault, Azure Key Vault,
/// CyberArk — can be dropped in later by implementing this interface and
/// registering it with <see cref="SecretStore"/>. Keys are opaque names such
/// as <c>"sql:analytics"</c>; the secret value is never written to a notebook
/// or any committed file.
/// </summary>
public interface ISecretProvider {
    /// <summary>A short identifier for diagnostics (e.g. "keychain", "env").</summary>
    string Name { get; }

    /// <summary>Whether this provider can store secrets (false for read-only
    /// sources like environment variables or a remote PAM in read mode).</summary>
    bool CanStore { get; }

    /// <summary>Reads a secret by key. Returns false (secret = null) if absent.</summary>
    bool TryGet(string key, out string secret);

    /// <summary>Stores (or replaces) a secret. Throws if <see cref="CanStore"/> is false.</summary>
    void Set(string key, string secret);

    /// <summary>Removes a secret. No-op if it does not exist.</summary>
    void Delete(string key);
}

/// <summary>Thrown when a referenced secret cannot be found in any provider.</summary>
public sealed class SecretNotFoundException : Exception {
    public SecretNotFoundException(string message) : base(message) { }
}

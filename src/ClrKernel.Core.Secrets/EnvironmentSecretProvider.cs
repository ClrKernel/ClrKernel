using System;

namespace ClrKernel.Core.Secrets;
/// <summary>
/// Resolves secrets from environment variables — the standard way to inject
/// credentials into headless / CI runs without an OS keychain and without
/// committing anything. A key like <c>"sql:analytics"</c> maps to the env var
/// <c>CLRKERNEL_SECRET_SQL_ANALYTICS</c> (upper-cased, non-alphanumerics → '_').
/// An env var whose name exactly matches the key is also honored, so a spec can
/// point <c>--secret MY_DB_PASSWORD</c> straight at an existing variable.
/// Read-only: it never writes secrets back to the environment.
/// </summary>
public sealed class EnvironmentSecretProvider : ISecretProvider {
    /// <summary>The variable prefix a default-constructed provider uses.</summary>
    public const string DefaultVariablePrefix = "CLRKERNEL_SECRET_";

    private readonly string _prefix;

    /// <param name="prefix">The configuration prefix, not the variable prefix
    /// itself: "Acme" yields <c>ACME_SECRET_*</c>.</param>
    public EnvironmentSecretProvider(string prefix = null) {
        _prefix = SecretPrefix.OrDefault(prefix);
        VariablePrefix = VariablePrefixFor(_prefix);
    }

    /// <summary>The configuration prefix this provider was built with.</summary>
    public string Prefix => _prefix;

    /// <summary>What every derived variable name starts with (e.g. <c>CLRKERNEL_SECRET_</c>).</summary>
    public string VariablePrefix { get; }

    public string Name => "env";
    public bool CanStore => false;

    public bool TryGet(string key, out string secret) {
        secret = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrEmpty(secret)) {
            return true;
        }
        secret = Environment.GetEnvironmentVariable(EnvName(key));
        return !string.IsNullOrEmpty(secret);
    }

    public void Set(string key, string secret) =>
        throw new NotSupportedException("The environment secret provider is read-only.");

    public void Delete(string key) =>
        throw new NotSupportedException("The environment secret provider is read-only.");

    /// <summary>The variable name <paramref name="key"/> resolves from — the name
    /// to quote at somebody who has to set one.</summary>
    public string EnvName(string key) => EnvName(key, _prefix);

    /// <summary>The same, for a caller holding a prefix rather than a provider.</summary>
    public static string EnvName(string key, string prefix) {
        if (key == null) {
            throw new ArgumentNullException(nameof(key));
        }
        return VariablePrefixFor(SecretPrefix.OrDefault(prefix)) + SecretPrefix.ToEnvironmentName(key);
    }

    /// <summary>The variable prefix a given configuration prefix produces.</summary>
    public static string VariablePrefixFor(string prefix) =>
        SecretPrefix.ToEnvironmentToken(prefix) + "_SECRET_";
}

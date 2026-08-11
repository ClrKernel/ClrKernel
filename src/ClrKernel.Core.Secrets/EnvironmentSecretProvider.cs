using System;
using System.Text;

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
    public const string Prefix = "CLRKERNEL_SECRET_";

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

    public static string EnvName(string key) {
        var sb = new StringBuilder(Prefix, Prefix.Length + key.Length);
        foreach (var c in key) {
            sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }
        return sb.ToString();
    }
}

using System;
using System.Collections.Generic;
using ClrKernel.Data;

namespace ClrKernel.Sql;

/// <summary>
/// Maps a <see cref="SqlConnectionSpec"/> to and from a <c>connections.json</c>
/// <c>"$type": "SqlServer"</c> node. Writing keeps the file secret-free (the password
/// is a <c>{ "secret": "&lt;ref&gt;" }</c> reference); reading keeps the reference on the
/// spec so it resolves lazily at execution time — the same secret store the
/// <c>#!sql-connect</c> button already uses.
/// </summary>
public static class SqlConnectionConfig {
    /// <summary>The <c>$type</c> discriminator for SQL Server connection nodes.</summary>
    public const string TypeName = "SqlServer";

    /// <summary>The non-secret properties (plus a password secret reference when the auth
    /// mode needs one) to write for <paramref name="spec"/>.</summary>
    public static IReadOnlyList<ConfigProperty> ToProperties(SqlConnectionSpec spec) {
        if (spec == null) {
            throw new ArgumentNullException(nameof(spec));
        }
        var props = new List<ConfigProperty>();
        if (!string.IsNullOrWhiteSpace(spec.RawConnectionString)) {
            props.Add(ConfigProperty.Plain("connectionString", spec.RawConnectionString));
        }
        if (!string.IsNullOrWhiteSpace(spec.Server)) {
            props.Add(ConfigProperty.Plain("server", spec.Server));
        }
        if (!string.IsNullOrWhiteSpace(spec.Database)) {
            props.Add(ConfigProperty.Plain("database", spec.Database));
        }
        props.Add(ConfigProperty.Plain("auth", AuthToString(spec.Auth)));
        if (!string.IsNullOrWhiteSpace(spec.User)) {
            props.Add(ConfigProperty.Plain("user", spec.User));
        }
        // Encrypt defaults to true; only record the non-default to keep files tidy.
        if (!spec.Encrypt) {
            props.Add(ConfigProperty.Plain("encrypt", "false"));
        }
        if (spec.TrustServerCertificate) {
            props.Add(ConfigProperty.Plain("trustServerCertificate", "true"));
        }
        if (spec.NeedsSecret) {
            props.Add(ConfigProperty.Secret("password", spec.EffectiveSecretRef));
        }
        return props;
    }

    /// <summary>Builds a <see cref="SqlConnectionSpec"/> from a raw (unresolved) SqlServer node.</summary>
    public static SqlConnectionSpec FromNode(RawConnectionNode node) {
        if (node == null) {
            throw new ArgumentNullException(nameof(node));
        }
        var spec = new SqlConnectionSpec {
            Name = node.Name,
            Server = node.Get("server") ?? node.Get("host"),
            Database = node.Get("database"),
            User = node.Get("user") ?? node.Get("username"),
            Auth = AuthFromString(node.Get("auth")),
            RawConnectionString = node.Get("connectionString"),
            Encrypt = ParseBool(node.Get("encrypt"), fallback: true),
            TrustServerCertificate =
                ParseBool(node.Get("trustServerCertificate") ?? node.Get("trustCert"), fallback: false),
        };
        // Keep the reference (never the password); BuildConnectionString resolves it later.
        var secretRef = node.SecretRef("password");
        if (!string.IsNullOrEmpty(secretRef)) {
            spec.SecretRef = secretRef;
        }
        if (!string.IsNullOrWhiteSpace(spec.RawConnectionString) && node.Get("auth") == null) {
            spec.Auth = SqlAuthMode.RawConnectionString;
        }
        return spec;
    }

    /// <summary>Canonical config string for an auth mode (accepted by <c>#!sql-connect --auth</c>).</summary>
    public static string AuthToString(SqlAuthMode auth) => auth switch {
        SqlAuthMode.SqlPassword => "sql",
        SqlAuthMode.Integrated => "integrated",
        SqlAuthMode.AzureAdDefault => "entra",
        SqlAuthMode.AzureAdPassword => "entra-password",
        SqlAuthMode.AzureAdInteractive => "entra-interactive",
        SqlAuthMode.RawConnectionString => "raw",
        _ => "integrated",
    };

    private static SqlAuthMode AuthFromString(string value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch {
        "sql" or "sqlpassword" or "password" => SqlAuthMode.SqlPassword,
        "integrated" or "windows" or "trusted" => SqlAuthMode.Integrated,
        "aad" or "entra" or "aad-default" or "entra-default" => SqlAuthMode.AzureAdDefault,
        "aad-password" or "entra-password" => SqlAuthMode.AzureAdPassword,
        "aad-interactive" or "entra-interactive" or "interactive" => SqlAuthMode.AzureAdInteractive,
        "raw" => SqlAuthMode.RawConnectionString,
        _ => SqlAuthMode.Integrated,
    };

    private static bool ParseBool(string value, bool fallback) => (value ?? string.Empty).Trim().ToLowerInvariant() switch {
        "true" or "yes" or "1" or "on" => true,
        "false" or "no" or "0" or "off" => false,
        _ => fallback,
    };
}

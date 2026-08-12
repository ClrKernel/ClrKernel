using System;
using ClrKernel.Database.Entra;

namespace ClrKernel.Database.Provider.AnalysisServices;

/// <summary>
/// Maps an <see cref="SsasConnectionSpec"/> to and from a <c>connections.json</c>
/// <c>"$type": "AnalysisServices"</c> node — the DAX counterpart to
/// <c>SqlConnectionConfig</c>'s <c>"SqlServer"</c> nodes, so both kinds of connection live in
/// one file and are told apart by their discriminator.
/// </summary>
public static class SsasConnectionConfig {
    /// <summary>The <c>$type</c> discriminator for Analysis Services / Fabric nodes.</summary>
    public const string TypeName = "AnalysisServices";

    /// <summary>The non-secret properties to write for <paramref name="spec"/>.</summary>
    public static System.Collections.Generic.IReadOnlyList<ConfigProperty> ToProperties(SsasConnectionSpec spec) {
        if (spec == null) {
            throw new ArgumentNullException(nameof(spec));
        }
        var props = new System.Collections.Generic.List<ConfigProperty>();
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
        // A reference, never the password — the same invariant the SQL nodes keep.
        if (spec.Auth == SsasAuthMode.UserPassword && !string.IsNullOrWhiteSpace(spec.SecretRef)) {
            props.Add(ConfigProperty.Secret("password", spec.SecretRef));
        }
        return props;
    }

    /// <summary>
    /// Builds a spec from a raw (unresolved) AnalysisServices node.
    /// </summary>
    /// <remarks>
    /// An Entra connection gets its <see cref="SsasConnectionSpec.TokenProvider"/> rebuilt here.
    /// A stored spec cannot carry a token provider — it's a delegate — and a loaded
    /// <see cref="SsasAuthMode.AzureAd"/> spec without one attaches no token and fails with
    /// ADOMD's "Authentication failed for all authenticators", which says nothing about the cause.
    /// The scope follows the endpoint: Power BI for a <c>powerbi://</c> workspace, Azure Analysis
    /// Services otherwise.
    /// </remarks>
    public static SsasConnectionSpec FromNode(RawConnectionNode node) {
        if (node == null) {
            throw new ArgumentNullException(nameof(node));
        }
        var spec = new SsasConnectionSpec {
            Server = node.Get("server") ?? node.Get("host"),
            Database = node.Get("database") ?? node.Get("model") ?? node.Get("catalog"),
            User = node.Get("user") ?? node.Get("username"),
            Auth = AuthFromString(node.Get("auth")),
            RawConnectionString = node.Get("connectionString"),
            SecretRef = node.SecretRef("password"),
        };
        if (!string.IsNullOrWhiteSpace(spec.RawConnectionString) && node.Get("auth") == null) {
            spec.Auth = SsasAuthMode.ConnectionString;
        }
        if (spec.Auth == SsasAuthMode.AzureAd) {
            var credential = EntraAuth.DefaultWithInteractiveFallback();
            var scope = IsFabric(spec.Server) ? EntraScopes.PowerBi : EntraScopes.AzureAnalysisServices;
            spec.TokenProvider = () => EntraAuth.Token(credential, scope);
        }
        return spec;
    }

    private static bool IsFabric(string server) =>
        (server ?? string.Empty).TrimStart().StartsWith("powerbi://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Canonical config string for an auth mode.</summary>
    public static string AuthToString(SsasAuthMode auth) => auth switch {
        SsasAuthMode.UserPassword => "user",
        SsasAuthMode.AzureAd => "entra",
        SsasAuthMode.ConnectionString => "raw",
        _ => "integrated",
    };

    private static SsasAuthMode AuthFromString(string value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch {
        "user" or "userpassword" or "password" or "basic" => SsasAuthMode.UserPassword,
        "aad" or "entra" or "azuread" or "token" => SsasAuthMode.AzureAd,
        "raw" or "connectionstring" => SsasAuthMode.ConnectionString,
        _ => SsasAuthMode.Integrated,
    };
}

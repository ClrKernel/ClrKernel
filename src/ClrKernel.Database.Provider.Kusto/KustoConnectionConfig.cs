using System;
using System.Collections.Generic;

namespace ClrKernel.Database.Provider.Kusto;

/// <summary>
/// Maps a <see cref="KustoConnectionSpec"/> to and from a <c>connections.json</c>
/// <c>"$type": "Kusto"</c> node, beside the SQL Server and Analysis Services nodes
/// in the same file. The client secret is written as a reference, never a value.
/// </summary>
public static class KustoConnectionConfig {
    public const string TypeName = KustoConnectionProvider.TypeName;

    public static IReadOnlyList<ConfigProperty> ToProperties(KustoConnectionSpec spec) {
        if (spec == null) {
            throw new ArgumentNullException(nameof(spec));
        }
        var props = new List<ConfigProperty> {
            ConfigProperty.Plain("cluster", spec.Cluster),
            ConfigProperty.Plain("database", spec.Database),
            ConfigProperty.Plain("auth", AuthToString(spec.Auth)),
        };
        if (!string.IsNullOrWhiteSpace(spec.TenantId)) {
            props.Add(ConfigProperty.Plain("tenant", spec.TenantId));
        }
        if (!string.IsNullOrWhiteSpace(spec.ClientId)) {
            props.Add(ConfigProperty.Plain("clientId", spec.ClientId));
        }
        if (spec.Auth == KustoAuthMode.ClientSecret && !string.IsNullOrWhiteSpace(spec.SecretRef)) {
            props.Add(ConfigProperty.Secret("secret", spec.SecretRef));
        }
        return props;
    }

    /// <summary>A secret-free spec from a raw node; the caller resolves <see cref="KustoConnectionSpec.SecretRef"/>.</summary>
    public static KustoConnectionSpec FromNode(RawConnectionNode node) {
        if (node == null) {
            throw new ArgumentNullException(nameof(node));
        }
        return new KustoConnectionSpec {
            Cluster = node.Get("cluster") ?? node.Get("server") ?? node.Get("url"),
            Database = node.Get("database"),
            Auth = AuthFromString(node.Get("auth")),
            TenantId = node.Get("tenant") ?? node.Get("tenantId"),
            ClientId = node.Get("clientId") ?? node.Get("appId"),
            SecretRef = node.SecretRef("secret"),
        };
    }

    public static string AuthToString(KustoAuthMode auth) => auth switch {
        KustoAuthMode.Interactive => "interactive",
        KustoAuthMode.ClientSecret => "clientsecret",
        _ => "entra",
    };

    private static KustoAuthMode AuthFromString(string value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch {
        "interactive" or "browser" => KustoAuthMode.Interactive,
        "clientsecret" or "client-secret" or "serviceprincipal" or "spn" => KustoAuthMode.ClientSecret,
        _ => KustoAuthMode.Entra,
    };
}

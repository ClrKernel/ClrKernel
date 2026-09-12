using System;
using System.Collections.Generic;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Kql;

/// <summary>The KQL session's databases, through the provider-neutral catalog contract the editors use.</summary>
public sealed class KqlConnectionCatalog : IConnectionCatalog, IConfigBackedConnections {
    private readonly KqlSession _session;

    internal KqlConnectionCatalog(KqlSession session) {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public string DefaultName => _session.DefaultName;

    public IReadOnlyList<ConnectionInfo> List() {
        var result = new List<ConnectionInfo>();
        foreach (var (name, spec) in _session.All) {
            result.Add(new ConnectionInfo {
                Name = name,
                Describe = spec.Describe(),
                Server = spec.Cluster,
                Database = spec.Database,
                Auth = spec.Auth.ToString(),
                User = spec.ClientId,
                IsDefault = string.Equals(name, DefaultName, StringComparison.OrdinalIgnoreCase),
            });
        }
        return result;
    }

    public string Add(string directive, string secret = null) {
        if (!string.IsNullOrEmpty(secret)) {
            var reference = KqlDirectives.SecretRefOf(directive);
            if (!string.IsNullOrWhiteSpace(reference)) {
                _session.StoreSecret(reference, secret);
            }
        }
        return _session.Connect(directive ?? string.Empty);
    }

    public bool Remove(string name) => _session.Remove(name ?? string.Empty);

    public void SetDefault(string name) => _session.SetDefault(name ?? string.Empty);

    public ConnectionConfigStatus Status(string directory) {
        var path = _session.FindConfigFile(Blank(directory));
        return new ConnectionConfigStatus {
            Found = path != null,
            Path = path,
            Names = path != null ? _session.ConfigConnectionNames(path) : Array.Empty<string>(),
        };
    }

    public IReadOnlyList<string> LoadFromConfig(string directory) => _session.LoadFromConfig(Blank(directory));

    public string SaveToConfig(string name, string filePath) =>
        _session.SaveConnectionToConfig(name ?? string.Empty, filePath ?? string.Empty);

    private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

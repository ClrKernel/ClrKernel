using System;
using System.Collections.Generic;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Sql;

/// <summary>
/// The SQL session's connections, exposed through the provider-neutral catalog contract so the
/// JSON-RPC host can serve the connection UI without referencing this package.
/// <para>
/// A thin projection over <see cref="SqlSession"/>, which remains the thing that owns the
/// registry, the secret store and the <c>connections.json</c> integration. Nothing here holds
/// state of its own.
/// </para>
/// </summary>
public sealed class SqlConnectionCatalog : IConnectionCatalog, IConfigBackedConnections {
    private readonly SqlSession _session;

    internal SqlConnectionCatalog(SqlSession session) {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public string DefaultName => _session.Connections.DefaultName;

    public IReadOnlyList<ConnectionInfo> List() {
        var result = new List<ConnectionInfo>();
        foreach (var spec in _session.Connections.All) {
            result.Add(new ConnectionInfo {
                Name = spec.Name,
                Describe = spec.Describe(),
                Server = spec.Server,
                Database = spec.Database,
                Auth = spec.Auth.ToString(),
                User = spec.User,
                NeedsSecret = spec.NeedsSecret,
                SecretRef = spec.EffectiveSecretRef,
                IsDefault = string.Equals(spec.Name, DefaultName, StringComparison.OrdinalIgnoreCase),
            });
        }
        return result;
    }

    public string Add(string directive, string secret = null) {
        var spec = _session.Connect(directive ?? string.Empty).Spec;
        if (!string.IsNullOrEmpty(secret)) {
            _session.StoreSecret(spec.EffectiveSecretRef, secret);
        }
        return spec.Name;
    }

    public bool Remove(string name) => _session.Connections.Remove(name ?? string.Empty);

    public void SetDefault(string name) => _session.Connections.SetDefault(name ?? string.Empty);

    public ConnectionConfigStatus Status(string directory) {
        var path = _session.FindConfigFile(Blank(directory));
        return new ConnectionConfigStatus {
            Found = path != null,
            Path = path,
            Names = path != null ? _session.ConfigConnectionNames(path) : Array.Empty<string>(),
        };
    }

    public IReadOnlyList<string> LoadFromConfig(string directory) =>
        _session.LoadFromConfig(Blank(directory));

    public string SaveToConfig(string name, string filePath) =>
        _session.SaveConnectionToConfig(name ?? string.Empty, filePath ?? string.Empty);

    private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

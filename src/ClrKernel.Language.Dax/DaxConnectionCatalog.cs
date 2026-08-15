using System;
using System.Collections.Generic;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Dax;

/// <summary>
/// The DAX session's cubes, exposed through the provider-neutral catalog contract.
/// <para>
/// Implements <see cref="IConfigBackedConnections"/> too: cubes save to the same
/// <c>connections.json</c> the SQL connections use, under <c>"$type": "AnalysisServices"</c>.
/// Gaining that took no change to the JSON-RPC host — it type-checks for the capability — which
/// is what the two interfaces were separated for.
/// </para>
/// <para>
/// <c>SetDefault</c> and <c>Remove</c> were already on <c>SsasConnectionRegistry</c> — DAX
/// only ever lacked the RPCs, not the capability. Routing through the catalog gives it both for
/// free.
/// </para>
/// </summary>
public sealed class DaxConnectionCatalog : IConnectionCatalog, IConfigBackedConnections {
    private readonly SsasSession _session;

    internal DaxConnectionCatalog(SsasSession session) {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public string DefaultName => _session.Cubes.DefaultName;

    public IReadOnlyList<ConnectionInfo> List() {
        var result = new List<ConnectionInfo>();
        foreach (var (name, spec) in _session.Cubes.All) {
            result.Add(new ConnectionInfo {
                Name = name,
                Describe = spec.Describe(),
                Server = spec.Server,
                Database = spec.Database,
                Auth = spec.Auth.ToString(),
                IsDefault = string.Equals(name, DefaultName, StringComparison.OrdinalIgnoreCase),
            });
        }
        return result;
    }

    public string Add(string directive, string secret = null) {
        // Store first: the directive resolves its --secret reference while registering, so a
        // password supplied by the UI has to be in the store before that happens.
        if (!string.IsNullOrEmpty(secret)) {
            var reference = DaxDirectives.SecretRefOf(directive);
            if (!string.IsNullOrWhiteSpace(reference)) {
                _session.StoreSecret(reference, secret);
            }
        }
        return _session.Connect(directive ?? string.Empty);
    }

    public bool Remove(string name) => _session.Cubes.Remove(name ?? string.Empty);

    public void SetDefault(string name) => _session.Cubes.SetDefault(name ?? string.Empty);

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

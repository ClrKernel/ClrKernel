using System;
using System.Collections.Generic;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Dax;

/// <summary>
/// The DAX session's cubes, exposed through the provider-neutral catalog contract.
/// <para>
/// Implements <see cref="IConnectionCatalog"/> but not <see cref="IConfigBackedConnections"/>:
/// cubes have no <c>connections.json</c> support yet. The host discovers that by type check, so
/// the config methods simply aren't offered for DAX rather than being present and failing.
/// </para>
/// <para>
/// <c>SetDefault</c> and <c>Remove</c> were already on <see cref="SsasConnectionRegistry"/> — DAX
/// only ever lacked the RPCs, not the capability. Routing through the catalog gives it both for
/// free.
/// </para>
/// </summary>
public sealed class DaxConnectionCatalog : IConnectionCatalog {
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

    /// <param name="secret">Ignored — a cube's credentials come from the directive or Entra,
    /// never from a stored password.</param>
    public string Add(string directive, string secret = null) => _session.Connect(directive ?? string.Empty);

    public bool Remove(string name) => _session.Cubes.Remove(name ?? string.Empty);

    public void SetDefault(string name) => _session.Cubes.SetDefault(name ?? string.Empty);
}

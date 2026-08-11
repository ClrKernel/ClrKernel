using System;
using System.Collections.Generic;
using System.Linq;

namespace ClrKernel.Database.Provider.AnalysisServices;
/// <summary>
/// The set of named cube (Tabular model) connections available to <c>#!dax</c>
/// cells in a session. A cell targets a named cube or the default.
/// </summary>
public sealed class SsasConnectionRegistry {
    private readonly Dictionary<string, SsasConnectionSpec> _cubes =
        new Dictionary<string, SsasConnectionSpec>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string DefaultName { get; private set; }
    public bool IsEmpty => _cubes.Count == 0;
    public IReadOnlyCollection<string> Names => _names.Values.ToList();

    /// <summary>The registered cubes as (name, spec) pairs, in registration order.</summary>
    public IEnumerable<(string Name, SsasConnectionSpec Spec)> All =>
        _names.Values.Select(n => (n, _cubes[n]));

    public bool Remove(string name) {
        var removed = _cubes.Remove(name);
        _names.Remove(name);
        if (removed && string.Equals(DefaultName, name, StringComparison.OrdinalIgnoreCase)) {
            DefaultName = _names.Values.FirstOrDefault();
        }
        return removed;
    }

    public void SetDefault(string name) {
        if (!_cubes.ContainsKey(name)) {
            throw new KeyNotFoundException($"No cube named '{name}'.");
        }
        DefaultName = _names[name];
    }

    public void Register(string name, SsasConnectionSpec spec, bool asDefault = false) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("A cube connection needs a name.", nameof(name));
        }
        _cubes[name] = spec ?? throw new ArgumentNullException(nameof(spec));
        _names[name] = name;
        if (asDefault || DefaultName == null) {
            DefaultName = name;
        }
    }

    public bool TryGet(string name, out SsasConnectionSpec spec) => _cubes.TryGetValue(name, out spec);

    /// <summary>Resolves a cube by name, or the default when no name is given.</summary>
    public SsasConnectionSpec Resolve(string requestedName) {
        if (!string.IsNullOrWhiteSpace(requestedName)) {
            if (_cubes.TryGetValue(requestedName, out var spec)) {
                return spec;
            }
            throw new InvalidOperationException(
                $"No cube connection named '{requestedName}'. " +
                (IsEmpty ? "Add one with #!dax-connect." : $"Known cubes: {string.Join(", ", _names.Values)}."));
        }
        if (DefaultName != null && _cubes.TryGetValue(DefaultName, out var def)) {
            return def;
        }
        throw new InvalidOperationException(
            "No cube connection is configured. Add one with a #!dax-connect cell.");
    }
}

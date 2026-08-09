using System;
using System.Collections.Generic;
using System.Linq;

namespace ClrKernel.Sql;
/// <summary>
/// The set of named SQL connections available in a session. Multiple
/// connections can be registered in one notebook; a cell picks one by name or
/// uses the default. Holds only secret-free <see cref="SqlConnectionSpec"/>s.
/// </summary>
public sealed class SqlConnectionRegistry {
    private readonly Dictionary<string, SqlConnectionSpec> _connections =
        new Dictionary<string, SqlConnectionSpec>(StringComparer.OrdinalIgnoreCase);

    public string DefaultName { get; private set; }

    public IReadOnlyCollection<SqlConnectionSpec> All => _connections.Values.ToList();

    public bool IsEmpty => _connections.Count == 0;

    public void Register(SqlConnectionSpec spec, bool asDefault = false) {
        if (spec == null) {
            throw new ArgumentNullException(nameof(spec));
        }
        if (string.IsNullOrWhiteSpace(spec.Name)) {
            throw new ArgumentException("Connection needs a name.", nameof(spec));
        }
        _connections[spec.Name] = spec;
        // First connection becomes the default automatically.
        if (asDefault || DefaultName == null) {
            DefaultName = spec.Name;
        }
    }

    public bool TryGet(string name, out SqlConnectionSpec spec) =>
        _connections.TryGetValue(name, out spec);

    public bool Remove(string name) {
        var removed = _connections.Remove(name);
        if (removed && string.Equals(DefaultName, name, StringComparison.OrdinalIgnoreCase)) {
            DefaultName = _connections.Keys.FirstOrDefault();
        }
        return removed;
    }

    public void SetDefault(string name) {
        if (!_connections.ContainsKey(name)) {
            throw new KeyNotFoundException($"No connection named '{name}'.");
        }
        DefaultName = name;
    }

    /// <summary>
    /// Resolves the spec for a cell: the named connection, or the default when
    /// no name is given. Throws a message that guides the user to define one.
    /// </summary>
    public SqlConnectionSpec Resolve(string requestedName) {
        if (!string.IsNullOrWhiteSpace(requestedName)) {
            if (_connections.TryGetValue(requestedName, out var spec)) {
                return spec;
            }
            throw new InvalidOperationException(
                $"No SQL connection named '{requestedName}'. " +
                (IsEmpty
                    ? "Add one with #!sql-connect or the connection button."
                    : $"Known connections: {string.Join(", ", _connections.Keys)}."));
        }
        if (DefaultName != null && _connections.TryGetValue(DefaultName, out var def)) {
            return def;
        }
        throw new InvalidOperationException(
            "No SQL connection is configured. Add one with a #!sql-connect cell " +
            "or the connection button next to the language picker.");
    }
}

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using ClrKernel.Core.Secrets;
using ClrKernel.Database.Provider.Kusto;

namespace ClrKernel.Language.Kql;

/// <summary>
/// The named Kusto databases of one notebook session, and the runner for
/// <c>#!kql</c> cells: a cell's KQL executes against the chosen (or default)
/// database and the result renders as the interactive grid.
/// </summary>
public sealed partial class KqlSession : IDisposable {
    private readonly Dictionary<string, KustoConnectionSpec> _specs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KustoDatabase> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();
    private readonly SecretStore _secrets;

    public KqlSession(SecretStore secrets = null) {
        _secrets = secrets ?? new SecretStore();
    }

    public string DefaultName { get; private set; }

    public IEnumerable<(string Name, KustoConnectionSpec Spec)> All => _order.Select(n => (n, _specs[n]));

    public string StoreSecret(string secretRef, string secret) => _secrets.Store(secretRef, secret);

    /// <summary>Registers a database from a <c>#!kql-connect</c> line; returns its name.</summary>
    public string Connect(string directiveLine) {
        var directive = KqlDirectives.ParseConnect(directiveLine, _secrets);
        Register(directive.Name, directive.Spec, directive.IsDefault);
        return directive.Name;
    }

    public void Register(string name, KustoConnectionSpec spec, bool asDefault = false) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("A Kusto connection needs a name.", nameof(name));
        }
        if (_open.Remove(name, out var stale)) {
            stale.Dispose();
        }
        _specs[name] = spec ?? throw new ArgumentNullException(nameof(spec));
        if (!_order.Contains(name, StringComparer.OrdinalIgnoreCase)) {
            _order.Add(name);
        }
        if (asDefault || DefaultName == null) {
            DefaultName = name;
        }
    }

    public bool Remove(string name) {
        if (_open.Remove(name, out var open)) {
            open.Dispose();
        }
        var removed = _specs.Remove(name);
        _order.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (removed && string.Equals(DefaultName, name, StringComparison.OrdinalIgnoreCase)) {
            DefaultName = _order.FirstOrDefault();
        }
        return removed;
    }

    public void SetDefault(string name) {
        if (!_specs.ContainsKey(name)) {
            throw new KeyNotFoundException($"No Kusto connection named '{name}'.");
        }
        DefaultName = _order.First(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The database a cell names, or the default.</summary>
    public KustoDatabase Resolve(string requestedName) {
        if (_specs.Count == 0) {
            LoadFromConfig(); // a headless or Jupyter run may not have loaded the config yet
        }
        var name = string.IsNullOrWhiteSpace(requestedName) ? DefaultName : requestedName;
        if (name == null) {
            throw new InvalidOperationException("No Kusto connection is configured. Add one with a #!kql-connect cell.");
        }
        if (!_specs.TryGetValue(name, out var spec)) {
            throw new InvalidOperationException(
                $"No Kusto connection named '{name}'. " +
                (_specs.Count == 0 ? "Add one with #!kql-connect." : $"Known: {string.Join(", ", _order)}."));
        }
        if (!_open.TryGetValue(name, out var db)) {
            _open[name] = db = KustoDb.FromSpec(spec);
        }
        return db;
    }

    /// <summary>Runs a <c>#!kql</c> cell: a management command when it starts with a dot, a query otherwise.</summary>
    public DataTable Execute(string cellBody) {
        var request = KqlDirectives.ParseCell(cellBody);
        var db = Resolve(request.ConnectionName);
        var kql = StripDirectiveLines(request.Kql);
        return kql.StartsWith(".", StringComparison.Ordinal) ? db.Execute(kql) : db.Query(kql);
    }

    // Removes leading #!kql lines; `// connections x` is a KQL comment and may stay.
    private static string StripDirectiveLines(string body) {
        var lines = (body ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        var stillLeading = true;
        foreach (var line in lines) {
            var trimmed = line.Trim();
            if (stillLeading) {
                if (trimmed.Length == 0 || trimmed.StartsWith("#!kql", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                stillLeading = false;
            }
            kept.Add(line);
        }
        return string.Join("\n", kept).Trim();
    }

    private readonly Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> _schemas = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tables and their columns of a database a cell has already used, for
    /// completion — one <c>.show database schema</c>, cached for the session.
    /// Only for an open connection: a completion request must never be the
    /// thing that starts a sign-in, and a browser opening on a keystroke is
    /// exactly that. Empty, never an error, when the cluster cannot be reached.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Schema(string requestedName) {
        var name = string.IsNullOrWhiteSpace(requestedName) ? DefaultName : requestedName;
        if (name == null || !_open.TryGetValue(name, out var db)) {
            return new Dictionary<string, IReadOnlyList<string>>();
        }
        if (_schemas.TryGetValue(name, out var cached)) {
            return cached;
        }
        var schema = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        try {
            var table = db.Execute(".show database schema");
            var byTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Data.DataRow row in table.Rows) {
                var tableName = row["TableName"]?.ToString();
                if (string.IsNullOrEmpty(tableName)) {
                    continue;
                }
                if (!byTable.TryGetValue(tableName, out var columns)) {
                    byTable[tableName] = columns = new List<string>();
                }
                var column = row["ColumnName"]?.ToString();
                if (!string.IsNullOrEmpty(column)) {
                    columns.Add(column);
                }
            }
            foreach (var (t, cols) in byTable) {
                schema[t] = cols;
            }
            _schemas[name] = schema;
        } catch {
            // ponytail: no schema is a quieter completion list, not a broken editor.
        }
        return schema;
    }

    public void Dispose() {
        foreach (var db in _open.Values) {
            db.Dispose();
        }
        _open.Clear();
    }
}

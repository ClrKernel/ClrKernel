using System.Collections.Generic;
using ClrKernel.Database;
using ClrKernel.Database.Provider.Kusto;

namespace ClrKernel.Language.Kql;

/// <summary>
/// <c>connections.json</c> for the KQL session: the same file the SQL and DAX
/// connections use, entries told apart by <c>"$type": "Kusto"</c>.
/// </summary>
public sealed partial class KqlSession {
    public string FindConfigFile(string startDirectory = null) => ConnectionConfig.FindFile(startDirectory);

    public IReadOnlyList<string> ConfigConnectionNames(string filePath) => ConnectionConfig.ListNames(filePath);

    /// <summary>Registers every Kusto entry from the nearest config file(s); returns the names.</summary>
    public IReadOnlyList<string> LoadFromConfig(string startDirectory = null) {
        var loaded = new List<string>();
        foreach (var file in ConnectionConfig.FindFiles(startDirectory)) {
            foreach (var node in ConnectionConfig.LoadAllRaw(file)) {
                if (!node.IsType(KustoConnectionConfig.TypeName)) {
                    continue;
                }
                var spec = KustoConnectionConfig.FromNode(node);
                // The file holds a reference; the secret comes from the store now, and
                // a missing one does not stop the notebook opening — the connection
                // says so when it is used.
                if (spec.Auth == KustoAuthMode.ClientSecret && !string.IsNullOrWhiteSpace(spec.SecretRef)
                    && _secrets.TryResolve(spec.SecretRef, out var secret)) {
                    spec.ClientSecret = secret;
                }
                Register(node.Name, spec);
                if (!loaded.Contains(node.Name)) {
                    loaded.Add(node.Name);
                }
            }
        }
        return loaded;
    }

    /// <summary>Writes the registered connection into <paramref name="filePath"/>, secret-free.</summary>
    public string SaveConnectionToConfig(string name, string filePath) {
        if (!_specs.TryGetValue(name, out var spec)) {
            throw new KeyNotFoundException($"No Kusto connection named '{name}'.");
        }
        ConnectionConfig.Upsert(filePath, name, KustoConnectionConfig.TypeName, KustoConnectionConfig.ToProperties(spec));
        return filePath;
    }
}

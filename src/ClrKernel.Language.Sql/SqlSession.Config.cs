using System;
using System.Collections.Generic;
using ClrKernel.Database;
using ClrKernel.Database.Provider.SqlServer;

namespace ClrKernel.Language.Sql;

/// <summary>
/// <c>connections.json</c> integration for the SQL session: discover a config file,
/// auto-load its <c>SqlServer</c> entries into the registry at session start, and save
/// a registered connection back to a chosen file (secret-free).
/// </summary>
public sealed partial class SqlSession {
    /// <summary>The nearest <c>connections.json</c> at or above <paramref name="startDirectory"/>,
    /// or null if none is found. Lets the UI show whether a config file already exists.</summary>
    public string FindConfigFile(string startDirectory = null) =>
        ConnectionConfig.FindFile(startDirectory);

    /// <summary>The connection names defined in a specific config file (empty if missing).</summary>
    public IReadOnlyList<string> ConfigConnectionNames(string filePath) =>
        ConnectionConfig.ListNames(filePath);

    /// <summary>
    /// Registers every <c>SqlServer</c> entry from the nearest config file at or above
    /// <paramref name="startDirectory"/> into the session, so saved connections are
    /// available without re-adding them. Returns the names loaded. A same-named spec
    /// already in the session is replaced; the default connection is left untouched
    /// unless none is set yet.
    /// </summary>
    public IReadOnlyList<string> LoadFromConfig(string startDirectory = null) {
        var file = ConnectionConfig.FindFile(startDirectory);
        if (file == null) {
            return Array.Empty<string>();
        }
        var loaded = new List<string>();
        foreach (var node in ConnectionConfig.LoadAllRaw(file)) {
            if (!node.IsType(SqlConnectionConfig.TypeName)) {
                continue;
            }
            _registry.Register(SqlConnectionConfig.FromNode(node));
            loaded.Add(node.Name);
        }
        return loaded;
    }

    /// <summary>
    /// Writes the registered connection <paramref name="name"/> into
    /// <paramref name="filePath"/> as a secret-free <c>SqlServer</c> node (creating or
    /// merging the file, preserving other entries). Returns the file written.
    /// </summary>
    public string SaveConnectionToConfig(string name, string filePath) {
        var spec = _registry.Resolve(name);
        ConnectionConfig.Upsert(filePath, spec.Name, SqlConnectionConfig.TypeName, SqlConnectionConfig.ToProperties(spec));
        return filePath;
    }
}

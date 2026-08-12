using System;
using System.Collections.Generic;
using ClrKernel.Database;
using ClrKernel.Database.Provider.AnalysisServices;

namespace ClrKernel.Language.Dax;

/// <summary>
/// <c>connections.json</c> integration for the DAX session: discover a config file, load its
/// <c>AnalysisServices</c> entries into the cube registry, and save a registered cube back to a
/// chosen file (secret-free).
/// <para>
/// The same file the SQL connections use — entries are told apart by their <c>$type</c>, so one
/// notebook folder can carry both.
/// </para>
/// </summary>
public sealed partial class SsasSession {
    /// <summary>The nearest <c>connections.json</c> at or above the directory, or null.</summary>
    public string FindConfigFile(string startDirectory = null) =>
        ConnectionConfig.FindFile(startDirectory);

    /// <summary>The connection names defined in a config file (empty if missing).</summary>
    public IReadOnlyList<string> ConfigConnectionNames(string filePath) =>
        ConnectionConfig.ListNames(filePath);

    /// <summary>
    /// Registers every <c>AnalysisServices</c> entry from the nearest config file, so saved cubes
    /// are available without re-adding them. Returns the names loaded. An entry of another
    /// <c>$type</c> — a SQL Server connection, say — is skipped rather than misread.
    /// </summary>
    public IReadOnlyList<string> LoadFromConfig(string startDirectory = null) {
        var file = ConnectionConfig.FindFile(startDirectory);
        if (file == null) {
            return Array.Empty<string>();
        }
        var loaded = new List<string>();
        foreach (var node in ConnectionConfig.LoadAllRaw(file)) {
            if (!node.IsType(SsasConnectionConfig.TypeName)) {
                continue;
            }
            var spec = SsasConnectionConfig.FromNode(node);
            // A password stored as a reference is resolved now, the same way a --secret on the
            // directive is: the file holds the reference, never the password.
            if (spec.Auth == SsasAuthMode.UserPassword && !string.IsNullOrWhiteSpace(spec.SecretRef)
                && _secrets.TryResolve(spec.SecretRef, out var password)) {
                spec.Password = password;
            }
            // A secret that isn't there yet must not stop a notebook opening — the cube registers
            // without a password and says so when it is used.
            _registry.Register(node.Name, spec);
            loaded.Add(node.Name);
        }
        return loaded;
    }

    /// <summary>Writes the registered cube <paramref name="name"/> into <paramref name="filePath"/>.</summary>
    public string SaveConnectionToConfig(string name, string filePath) {
        var spec = _registry.Resolve(name);
        ConnectionConfig.Upsert(filePath, name, SsasConnectionConfig.TypeName, SsasConnectionConfig.ToProperties(spec));
        return filePath;
    }
}

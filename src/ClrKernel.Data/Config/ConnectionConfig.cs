using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClrKernel.Data.Secrets;

namespace ClrKernel.Data;

/// <summary>
/// A connection definition loaded from a JSON config file — a secret-free way to keep
/// connection settings out of notebooks and source. Providers read what they need via
/// <see cref="Get"/> / <see cref="GetInt"/>; passwords are stored as secret references
/// and resolved from the secret store, never written in the file.
/// </summary>
/// <remarks>
/// File format (searched from the working directory up the folder tree —
/// <c>clrkernel.connections[.env].json</c> then <c>connections[.env].json</c>):
/// <code>
/// {
///   "warehouse": {
///     "$type": "SqlServer",
///     "server": "dw.db.local",
///     "database": "datawarehouse",
///     "user": "svc",
///     "password": { "secret": "sql:warehouse" }
///   }
/// }
/// </code>
/// A node value of the string <c>"inherit"</c> continues the search in the next file up
/// the tree. A property value <c>{ "secret": "&lt;ref&gt;" }</c> resolves from the secret
/// store.
/// </remarks>
public sealed class ConnectionConfig {
    private readonly IReadOnlyDictionary<string, string> _properties;

    internal ConnectionConfig(string name, string type, string sourceFile, IReadOnlyDictionary<string, string> properties) {
        Name = name;
        Type = type;
        SourceFile = sourceFile;
        _properties = properties;
    }

    /// <summary>The config node name.</summary>
    public string Name { get; }

    /// <summary>The <c>$type</c> discriminator (e.g. <c>SqlServer</c>, <c>Oracle</c>, <c>Odbc</c>).</summary>
    public string Type { get; }

    /// <summary>The file the definition was read from.</summary>
    public string SourceFile { get; }

    /// <summary>All resolved properties (secrets applied), keyed case-insensitively.</summary>
    public IReadOnlyDictionary<string, string> Properties => _properties;

    /// <summary>A property value (secrets already resolved), or <paramref name="fallback"/>.</summary>
    public string Get(string key, string fallback = null) =>
        _properties.TryGetValue(key, out var value) ? value : fallback;

    /// <summary>A required property value; throws if missing.</summary>
    public string Require(string key) =>
        _properties.TryGetValue(key, out var value)
            ? value
            : throw new ConnectionConfigException($"Config '{Name}' is missing required property '{key}'.");

    /// <summary>An integer property value, or <paramref name="fallback"/>.</summary>
    public int GetInt(string key, int fallback = 0) =>
        _properties.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : fallback;

    /// <summary>Ensures the config's <c>$type</c> is one of <paramref name="allowed"/> (case-insensitive).</summary>
    public ConnectionConfig EnsureType(params string[] allowed) {
        if (allowed is { Length: > 0 } && !allowed.Contains(Type, StringComparer.OrdinalIgnoreCase)) {
            throw new ConnectionConfigException(
                $"Config '{Name}' has $type '{Type}', expected one of: {string.Join(", ", allowed)}.");
        }
        return this;
    }

    // --- loading -----------------------------------------------------------

    /// <summary>
    /// Loads the named connection definition, searching config files from
    /// <paramref name="startDirectory"/> (default: current directory) up the folder tree.
    /// </summary>
    public static ConnectionConfig Load(
        string name, SecretStore secrets = null, string env = null, string startDirectory = null, int maxParents = 10) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("name is required.", nameof(name));
        }
        secrets ??= new SecretStore();
        var files = CandidateFiles(env, startDirectory ?? Directory.GetCurrentDirectory(), maxParents).ToList();
        if (files.Count == 0) {
            throw new ConnectionConfigException(
                $"No connection config file found (looked for {string.Join(" / ", FileNames(env))} up to {maxParents} parents of '{startDirectory ?? Directory.GetCurrentDirectory()}').");
        }

        foreach (var file in files) {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(name, out var node)) {
                continue;
            }
            // "inherit" → keep searching further up the tree.
            if (node.ValueKind == JsonValueKind.String &&
                string.Equals(node.GetString(), "inherit", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            if (node.ValueKind != JsonValueKind.Object) {
                throw new ConnectionConfigException($"Config '{name}' in '{file}' must be a JSON object.");
            }
            return Materialize(name, file, node, secrets);
        }

        throw new ConnectionConfigException(
            $"Connection '{name}' not found in: {string.Join(", ", files)}.");
    }

    private static ConnectionConfig Materialize(string name, string file, JsonElement node, SecretStore secrets) {
        string type = null;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in node.EnumerateObject()) {
            if (property.NameEquals("$type")) {
                type = property.Value.GetString();
                continue;
            }
            properties[property.Name] = ResolveValue(property.Value, secrets, name, property.Name);
        }
        return new ConnectionConfig(name, type, file, properties);
    }

    private static string ResolveValue(JsonElement value, SecretStore secrets, string node, string key) {
        switch (value.ValueKind) {
            case JsonValueKind.Object when value.TryGetProperty("secret", out var secretRef):
                var reference = secretRef.GetString();
                if (string.IsNullOrEmpty(reference)) {
                    throw new ConnectionConfigException($"Config '{node}' property '{key}' has an empty secret reference.");
                }
                return secrets.Resolve(reference);
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Number:
                return value.GetRawText();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Null:
                return null;
            default:
                return value.GetRawText();
        }
    }

    private static IEnumerable<string> FileNames(string env) {
        var suffix = string.IsNullOrEmpty(env) ? string.Empty : "." + env;
        yield return $"clrkernel.connections{suffix}.json";
        yield return $"connections{suffix}.json";
    }

    private static IEnumerable<string> CandidateFiles(string env, string startDirectory, int maxParents) {
        var dir = new DirectoryInfo(startDirectory);
        var depth = 0;
        while (dir != null && depth++ <= maxParents) {
            foreach (var fileName in FileNames(env)) {
                var path = Path.Combine(dir.FullName, fileName);
                if (File.Exists(path)) {
                    yield return path;
                }
            }
            dir = dir.Parent;
        }
    }
}

/// <summary>Raised when a connection config file or node can't be loaded/validated.</summary>
public sealed class ConnectionConfigException : Exception {
    public ConnectionConfigException(string message) : base(message) { }
}

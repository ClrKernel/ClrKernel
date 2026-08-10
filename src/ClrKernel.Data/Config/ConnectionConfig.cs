using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    // --- discovery / raw read / write (no secret resolution) ---------------

    /// <summary>The nearest config file at or above <paramref name="startDirectory"/>, or null
    /// if none exists. Used to tell the UI whether a <c>connections.json</c> was found.</summary>
    public static string FindFile(string startDirectory = null, string env = null, int maxParents = 10) =>
        CandidateFiles(env, startDirectory ?? Directory.GetCurrentDirectory(), maxParents).FirstOrDefault();

    /// <summary>The connection names defined in a specific config file (empty if missing/blank).</summary>
    public static IReadOnlyList<string> ListNames(string filePath) {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
            return Array.Empty<string>();
        }
        var text = File.ReadAllText(filePath);
        if (string.IsNullOrWhiteSpace(text)) {
            return Array.Empty<string>();
        }
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) {
            return Array.Empty<string>();
        }
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
    }

    /// <summary>Reads every object node in a config file as a <see cref="RawConnectionNode"/>,
    /// without resolving secrets. String <c>"inherit"</c> nodes are skipped.</summary>
    public static IReadOnlyList<RawConnectionNode> LoadAllRaw(string filePath) {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
            return Array.Empty<RawConnectionNode>();
        }
        var text = File.ReadAllText(filePath);
        if (string.IsNullOrWhiteSpace(text)) {
            return Array.Empty<RawConnectionNode>();
        }
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) {
            return Array.Empty<RawConnectionNode>();
        }
        var nodes = new List<RawConnectionNode>();
        foreach (var property in doc.RootElement.EnumerateObject()) {
            if (property.Value.ValueKind != JsonValueKind.Object) {
                continue; // "inherit" markers and anything non-object
            }
            nodes.Add(MaterializeRaw(property.Name, filePath, property.Value));
        }
        return nodes;
    }

    private static RawConnectionNode MaterializeRaw(string name, string file, JsonElement node) {
        string type = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var secretRefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in node.EnumerateObject()) {
            if (property.NameEquals("$type")) {
                type = property.Value.GetString();
                continue;
            }
            if (property.Value.ValueKind == JsonValueKind.Object &&
                property.Value.TryGetProperty("secret", out var secretRef)) {
                secretRefs[property.Name] = secretRef.GetString();
                continue;
            }
            values[property.Name] = RawScalar(property.Value);
        }
        return new RawConnectionNode(name, type, file, values, secretRefs);
    }

    private static string RawScalar(JsonElement value) => value.ValueKind switch {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => null,
        _ => value.GetRawText(),
    };

    /// <summary>Creates or replaces a named connection node in <paramref name="filePath"/>,
    /// preserving every other node and the file's formatting intent (2-space indent).
    /// Secrets are written as <c>{ "secret": "&lt;ref&gt;" }</c> — never the password.</summary>
    public static void Upsert(string filePath, string name, string type, IEnumerable<ConfigProperty> properties) {
        if (string.IsNullOrWhiteSpace(filePath)) {
            throw new ArgumentException("filePath is required.", nameof(filePath));
        }
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("name is required.", nameof(name));
        }

        JsonObject root;
        if (File.Exists(filePath)) {
            var text = File.ReadAllText(filePath);
            root = string.IsNullOrWhiteSpace(text)
                ? new JsonObject()
                : JsonNode.Parse(text) as JsonObject
                    ?? throw new ConnectionConfigException($"'{filePath}' is not a JSON object.");
        } else {
            root = new JsonObject();
        }

        var entry = new JsonObject { ["$type"] = type };
        foreach (var property in properties) {
            if (property?.Value == null) {
                continue;
            }
            entry[property.Key] = property.IsSecret
                ? new JsonObject { ["secret"] = property.Value }
                : JsonValue.Create(property.Value);
        }
        root[name] = entry; // replaces an existing node in place, or appends a new one

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json + Environment.NewLine);
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

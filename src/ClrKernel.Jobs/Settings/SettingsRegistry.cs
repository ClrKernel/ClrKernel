using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClrKernel.Jobs;

/// <summary>One configurable (or displayed) value inside a settings section.</summary>
public sealed class SettingField {
    /// <summary>The settings.json key, e.g. <c>maxParallelism</c>.</summary>
    public string Name { get; init; }
    public string Label { get; init; }
    /// <summary>string | int | bool | secret. Secrets never expose their value — only whether one is set.</summary>
    public string Type { get; init; }
    /// <summary>Omitted for secrets.</summary>
    public object Value { get; set; }
    /// <summary>For secrets: whether a value exists, without echoing it.</summary>
    public bool? IsSet { get; set; }
    /// <summary>Which layer supplied the value (--flag, ENV name, settings.json, default).</summary>
    public string Source { get; set; }
    /// <summary>
    /// Whether the web UI may change this. Execution- and auth-affecting fields
    /// (api key, kernel path, store, connection string, roots, urls) are never
    /// web-writable: with no key configured, any client that can reach the port
    /// could otherwise reconfigure what the server executes or lock the owner out.
    /// </summary>
    public bool WebWritable { get; init; }
    public bool RestartRequired { get; init; }
    public string Help { get; init; }
}

/// <summary>A group of settings one feature owns, rendered generically by the UI.</summary>
public sealed class SettingsSection {
    public string Key { get; init; }
    public string Title { get; init; }
    public string Description { get; init; }
    /// <summary>When set, the UI renders a link (e.g. the channels editor) instead of fields.</summary>
    public string LinkTo { get; init; }
    public List<SettingField> Fields { get; init; } = new();
}

/// <summary>
/// The pluggable settings surface: features register sections once and the UI
/// renders whatever is here. Writes persist to <c>settings.json</c> in the data dir
/// — the lowest layer of the existing precedence — so a value pinned by a CLI flag
/// or environment variable stays authoritative and renders locked instead.
/// </summary>
public sealed class SettingsRegistry {
    private readonly JobsOptions _options;
    private readonly List<SettingsSection> _sections = new();
    private readonly object _writeLock = new();

    public SettingsRegistry(JobsOptions options) {
        _options = options;
    }

    public void Add(SettingsSection section) => _sections.Add(section);

    public IReadOnlyList<SettingsSection> Sections => _sections;

    public SettingsSection Find(string key) =>
        _sections.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Validates and persists one section's web-writable values into settings.json.
    /// Returns null on success, otherwise the reason the write was refused.
    /// </summary>
    public string Write(string sectionKey, Dictionary<string, JsonElement> values) {
        var section = Find(sectionKey);
        if (section == null) {
            return $"No settings section '{sectionKey}'.";
        }
        if (values is not { Count: > 0 }) {
            return "No values to save.";
        }

        var writes = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var (name, raw) in values) {
            var field = section.Fields.FirstOrDefault(f => f.Name == name);
            if (field == null) {
                return $"'{sectionKey}' has no setting named '{name}'.";
            }
            if (!field.WebWritable) {
                return $"'{name}' cannot be changed from the web UI — set it with a flag, " +
                    "environment variable, or settings.json on the host.";
            }
            // CLI/env stay authoritative; writing a shadowed value would silently do nothing.
            var source = _options.SourceOf(name);
            if (source is not ("default" or "settings.json")) {
                return $"'{name}' is pinned by {source}; change it there.";
            }

            switch (field.Type) {
                case "int":
                    if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetInt32(out var i) || i < 1) {
                        return $"'{name}' must be a positive whole number.";
                    }
                    writes[name] = i;
                    break;
                case "bool":
                    if (raw.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
                        return $"'{name}' must be true or false.";
                    }
                    writes[name] = raw.GetBoolean();
                    break;
                default:
                    if (raw.ValueKind != JsonValueKind.String) {
                        return $"'{name}' must be a string.";
                    }
                    writes[name] = raw.GetString();
                    break;
            }
        }

        lock (_writeLock) {
            var path = Path.Combine(_options.DataDir, "settings.json");
            var root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();
            foreach (var (name, node) in writes) {
                root[name] = node;
            }
            Directory.CreateDirectory(_options.DataDir);
            var staging = path + ".tmp";
            File.WriteAllText(staging, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(staging, path, overwrite: true);
        }
        return null;
    }

    /// <summary>The built-in sections. Later features (git) add their own here.</summary>
    public static SettingsRegistry CreateDefault(JobsOptions options) {
        var registry = new SettingsRegistry(options);

        registry.Add(new SettingsSection {
            Key = "general",
            Title = "General",
            Description = "Where the server looks and how much it runs at once.",
            Fields = {
                new SettingField {
                    Name = "maxParallelism", Label = "Max parallel runs", Type = "int",
                    Value = options.MaxParallelism, Source = options.SourceOf("maxParallelism"),
                    WebWritable = true, RestartRequired = true,
                    Help = "How many notebook runs may execute at the same time.",
                },
                new SettingField {
                    Name = "notebooksRoot", Label = "Notebooks root", Type = "string",
                    Value = options.NotebooksRoot, Source = options.SourceOf("notebooksRoot"),
                    WebWritable = false, RestartRequired = true,
                },
                new SettingField {
                    Name = "dataDir", Label = "Data directory", Type = "string",
                    Value = options.DataDir, Source = options.SourceOf("dataDir"),
                    WebWritable = false, RestartRequired = true,
                },
                new SettingField {
                    Name = "store", Label = "Run-history store", Type = "string",
                    Value = options.Store, Source = options.SourceOf("store"),
                    WebWritable = false, RestartRequired = true,
                },
                new SettingField {
                    Name = "urls", Label = "Listen address", Type = "string",
                    Value = options.Urls ?? "http://localhost:5000", Source = options.SourceOf("urls"),
                    WebWritable = false, RestartRequired = true,
                },
            },
        });

        registry.Add(new SettingsSection {
            Key = "security",
            Title = "Security",
            Description = "Authentication and what the server executes. Not editable from the " +
                "browser by design — change these on the host.",
            Fields = {
                new SettingField {
                    Name = "apiKey", Label = "API key", Type = "secret",
                    IsSet = options.ApiKey != null, Source = options.SourceOf("apiKey"),
                    WebWritable = false, RestartRequired = true,
                    Help = "When set, /api/* requires it in the X-Api-Key header. " +
                        "Set with --api-key or CLRKERNEL_JOBS_APIKEY.",
                },
                new SettingField {
                    Name = "clrkernelPath", Label = "Kernel executable", Type = "string",
                    Value = options.ClrKernelPath ?? "(PATH, then ~/.dotnet/tools)",
                    Source = options.SourceOf("clrkernelPath"),
                    WebWritable = false, RestartRequired = true,
                },
                new SettingField {
                    Name = "connectionString", Label = "Store connection string", Type = "secret",
                    IsSet = options.ConnectionString != null, Source = options.SourceOf("connectionString"),
                    WebWritable = false, RestartRequired = true,
                },
            },
        });

        registry.Add(new SettingsSection {
            Key = "notifications",
            Title = "Notifications",
            Description = "Webhook and email channels live in notifications.yaml beside your " +
                "notebooks and have their own editor.",
            LinkTo = "/channels",
        });

        return registry;
    }
}

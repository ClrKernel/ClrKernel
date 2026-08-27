using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClrKernel.Studio;

/// <summary>
/// The shape of a <c>*.jobs.yaml</c>, as a JSON Schema the editor can complete and
/// validate against, and as the key sets the server validates with.
/// <para>
/// One definition for both. The editor's schema and the server's checker
/// disagreeing is the failure this shape exists to prevent: a file the editor
/// called fine and the push refused, or the reverse. <c>JobsSchemaTest</c> also
/// reflects over <see cref="JobsFile"/> and friends, so adding a property to the
/// model without describing it here fails a test rather than silently producing a
/// key the editor calls unknown.
/// </para>
/// <para>
/// Hand-written rather than generated. Generating from reflection gets the names
/// right and everything a person reads wrong — no descriptions, no cron example,
/// no sense of which fields matter — and the completion list is most of the point.
/// </para>
/// </summary>
public static class JobsSchema {
    public sealed record Field(string Name, string Type, string Description, string Example = null);

    /// <summary>Top level of the file.</summary>
    public static readonly IReadOnlyList<Field> Root = new[] {
        new Field("notebook", "string",
            "Notebook every job in this file runs, relative to the file. A job may override it.",
            "./daily.nb.md"),
        new Field("defaults", "object", "Values every job in this file inherits unless it sets its own."),
        new Field("jobs", "array", "The jobs. At least one."),
    };

    /// <summary>One job, and the same shape as <c>defaults</c> (which has no name).</summary>
    public static readonly IReadOnlyList<Field> Entry = new[] {
        new Field("name", "string", "Unique within the environment. This is what run history records.", "daily"),
        new Field("notebook", "string", "Notebook to run, relative to this file.", "./daily.nb.md"),
        new Field("cron", "string",
            "Five-field cron, UTC. Omit for a job that only ever runs on demand.", "0 6 * * *"),
        new Field("enabled", "boolean", "Default true. A disabled job is never scheduled and blocks nothing."),
        new Field("timeoutSeconds", "integer", "Kill the run after this long."),
        new Field("retryCount", "integer", "Attempts after a failure. Default 0."),
        new Field("parameters", "object", "Values injected into the notebook's parameters cell."),
        new Field("dependsOn", "array", "Job names in the same environment that must have succeeded first."),
        new Field("notify", "object", "Which channels hear about this job's outcomes."),
    };

    /// <summary>The <c>notify</c> block: channel names by outcome.</summary>
    public static readonly IReadOnlyList<Field> Notify = new[] {
        new Field("onFailure", "array", "Channel names notified when a run fails."),
        new Field("onSuccess", "array", "Channel names notified when a run succeeds."),
    };

    public static IReadOnlyCollection<string> RootKeys { get; } = Root.Select(f => f.Name).ToHashSet();
    public static IReadOnlyCollection<string> EntryKeys { get; } = Entry.Select(f => f.Name).ToHashSet();
    public static IReadOnlyCollection<string> NotifyKeys { get; } = Notify.Select(f => f.Name).ToHashSet();

    /// <summary>The schema document, as the editor consumes it.</summary>
    public static string Json { get; } = Build();

    private static string Build() {
        // `additionalProperties: false` everywhere, deliberately. The YAML
        // deserializer ignores keys it does not know, which is how `scedule:` gets
        // to be a job that never runs — so the schema is stricter than the parser
        // on purpose, and the server's checker matches it.
        var schema = new JsonObject {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "ClrKernel Studio jobs file",
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("jobs"),
            ["properties"] = Properties(Root, entryRef: "#/definitions/entry"),
            ["definitions"] = new JsonObject {
                ["entry"] = new JsonObject {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = Properties(Entry, entryRef: null),
                },
                ["notify"] = new JsonObject {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = Properties(Notify, entryRef: null),
                },
            },
        };
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Properties(IReadOnlyList<Field> fields, string entryRef) {
        var properties = new JsonObject();
        foreach (var field in fields) {
            var node = new JsonObject { ["description"] = field.Description };
            switch (field.Name) {
                case "jobs":
                    node["type"] = "array";
                    node["minItems"] = 1;
                    node["items"] = new JsonObject { ["$ref"] = entryRef };
                    break;
                case "defaults":
                    node["$ref"] = entryRef;
                    // A $ref beside anything else is ignored by most validators, so the
                    // description has to live on the ref'd object or nowhere. Nowhere.
                    node.Remove("description");
                    break;
                case "notify":
                    node["$ref"] = "#/definitions/notify";
                    node.Remove("description");
                    break;
                case "dependsOn":
                case "onFailure":
                case "onSuccess":
                    node["type"] = "array";
                    node["items"] = new JsonObject { ["type"] = "string" };
                    break;
                case "parameters":
                    node["type"] = "object";
                    break;
                case "timeoutSeconds":
                case "retryCount":
                    node["type"] = "integer";
                    node["minimum"] = 0;
                    break;
                default:
                    node["type"] = field.Type;
                    break;
            }
            if (field.Example != null) {
                node["examples"] = new JsonArray(field.Example);
            }
            properties[field.Name] = node;
        }
        return properties;
    }
}

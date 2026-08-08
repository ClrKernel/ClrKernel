using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClrKernel.Runner;

/// <summary>
/// Builds and writes an executed notebook in nbformat v4 (the .ipynb schema),
/// so <c>clrkernel run -o out.ipynb</c> produces a file any Jupyter tool can open
/// — same shape papermill / dotnet-repl emit.
/// </summary>
public static class IpynbWriter {
    public static JsonObject MarkdownCell(string source) => new() {
        ["cell_type"] = "markdown",
        ["metadata"] = new JsonObject(),
        ["source"] = source,
    };

    public static JsonObject CodeCell(string source, int? executionCount, JsonArray outputs, IEnumerable<string> tags = null) {
        var metadata = new JsonObject();
        var tagList = tags?.ToList();
        if (tagList is { Count: > 0 }) {
            metadata["tags"] = new JsonArray(tagList.Select(t => (JsonNode)t).ToArray());
        }
        return new JsonObject {
            ["cell_type"] = "code",
            ["execution_count"] = executionCount is int n ? n : null,
            ["metadata"] = metadata,
            ["outputs"] = outputs ?? new JsonArray(),
            ["source"] = source,
        };
    }

    public static JsonObject StreamOutput(string name, string text) => new() {
        ["output_type"] = "stream",
        ["name"] = name,
        ["text"] = text,
    };

    public static JsonObject ExecuteResultOutput(int executionCount, IReadOnlyDictionary<string, object> data) => new() {
        ["output_type"] = "execute_result",
        ["execution_count"] = executionCount,
        ["data"] = ToDataBundle(data),
        ["metadata"] = new JsonObject(),
    };

    public static JsonObject DisplayDataOutput(IReadOnlyDictionary<string, object> data) => new() {
        ["output_type"] = "display_data",
        ["data"] = ToDataBundle(data),
        ["metadata"] = new JsonObject(),
    };

    public static JsonObject ErrorOutput(string ename, string evalue, IEnumerable<string> traceback) => new() {
        ["output_type"] = "error",
        ["ename"] = ename,
        ["evalue"] = evalue,
        ["traceback"] = new JsonArray((traceback ?? Enumerable.Empty<string>()).Select(t => (JsonNode)t).ToArray()),
    };

    private static JsonObject ToDataBundle(IReadOnlyDictionary<string, object> data) {
        var bundle = new JsonObject();
        if (data != null) {
            foreach (var kv in data) {
                bundle[kv.Key] = kv.Value?.ToString() ?? string.Empty;
            }
        }
        return bundle;
    }

    /// <summary>Writes the notebook to <paramref name="path"/>.</summary>
    public static void Write(string path, IEnumerable<JsonObject> cells) {
        var cellList = cells.ToList();
        for (int i = 0; i < cellList.Count; i++) {
            // nbformat 4.5 requires a unique cell id (^[a-zA-Z0-9-_]+$, <=64 chars).
            cellList[i]["id"] = $"cell{i + 1}";
        }

        var root = new JsonObject {
            ["cells"] = new JsonArray(cellList.Cast<JsonNode>().ToArray()),
            ["metadata"] = new JsonObject {
                ["kernelspec"] = new JsonObject {
                    ["name"] = "clrkernel",
                    ["display_name"] = "ClrKernel (C#)",
                    ["language"] = "csharp",
                },
                ["language_info"] = new JsonObject {
                    ["name"] = "csharp",
                    ["file_extension"] = ".cs",
                    ["mimetype"] = "text/x-csharp",
                    ["pygments_lexer"] = "csharp",
                },
            },
            ["nbformat"] = 4,
            ["nbformat_minor"] = 5,
        };

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

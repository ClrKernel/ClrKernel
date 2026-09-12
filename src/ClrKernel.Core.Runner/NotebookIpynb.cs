using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClrKernel.Core.Runner;

/// <summary>
/// Writes cells as an <c>.ipynb</c> (nbformat 4.5) with no outputs — the editing
/// form, the reverse of <see cref="NotebookConverter.Cells"/> for that extension. A
/// code cell in a language other than C# leads with its <c>#!tag</c> selector, which
/// is how a polyglot <c>.ipynb</c> says what a cell is and what the reader takes back
/// off. Outputs a file had are not carried: the cells API never sees them, and a
/// notebook edited here is source.
/// </summary>
public static class NotebookIpynb {
    private static readonly HashSet<string> _csharpTags = new(System.StringComparer.OrdinalIgnoreCase) { "csharp", "c#", "cs" };

    private static readonly JsonSerializerOptions _json = new() {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(IEnumerable<MarkdownCell> cells) {
        var list = (cells ?? Enumerable.Empty<MarkdownCell>()).ToList();
        var array = new JsonArray();
        for (var i = 0; i < list.Count; i++) {
            var cell = list[i];
            var source = (cell.Source ?? string.Empty).TrimEnd();
            var code = cell.Kind == CellKind.Code;
            if (code && !string.IsNullOrEmpty(cell.Tag) && !_csharpTags.Contains(cell.Tag)) {
                source = "#!" + cell.Tag + "\n" + source;
            }
            var node = new JsonObject {
                ["cell_type"] = code ? "code" : "markdown",
                ["id"] = $"cell{i + 1}",
                ["metadata"] = new JsonObject(),
                ["source"] = new JsonArray(Lines(source).Select(l => (JsonNode)l).ToArray()),
            };
            if (code) {
                node["execution_count"] = null;
                node["outputs"] = new JsonArray();
            }
            array.Add(node);
        }
        var root = new JsonObject {
            ["cells"] = array,
            ["metadata"] = new JsonObject {
                ["kernelspec"] = new JsonObject {
                    ["name"] = "clrkernel",
                    ["display_name"] = "ClrKernel (C#)",
                    ["language"] = "csharp",
                },
                ["language_info"] = new JsonObject { ["name"] = "csharp" },
            },
            ["nbformat"] = 4,
            ["nbformat_minor"] = 5,
        };
        return root.ToJsonString(_json) + "\n";
    }

    // nbformat's multi-line source: each line keeps its newline except the last.
    private static IEnumerable<string> Lines(string source) {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++) {
            yield return i < lines.Length - 1 ? lines[i] + "\n" : lines[i];
        }
    }
}

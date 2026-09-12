using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClrKernel.Core.Runner;

/// <summary>
/// Writes cells as a Polyglot <c>.dib</c>: a <c>#!meta</c> header, then one
/// <c>#!&lt;tag&gt;</c> section per cell. The reverse of
/// <see cref="NotebookConverter.Cells"/> for that extension, so Studio can open a
/// <c>.dib</c> as cells and save it back as the file it was — a notebook being
/// migrated is still a notebook while the migration waits.
/// </summary>
public static class NotebookDib {
    /// <summary>What Polyglot writes at the top; ClrKernel drops it on read.</summary>
    private const string _meta =
        "#!meta\n\n{\"kernelInfo\":{\"defaultKernelName\":\"csharp\",\"items\":[{\"aliases\":[],\"name\":\"csharp\"}]}}\n\n";

    private static readonly HashSet<string> _csharpTags = new(System.StringComparer.OrdinalIgnoreCase) { "csharp", "c#", "cs" };

    /// <summary>The cells as a <c>.dib</c>, LF-terminated (translate afterwards if the file was CRLF).</summary>
    public static string Serialize(IEnumerable<MarkdownCell> cells) {
        var text = new StringBuilder(_meta);
        foreach (var cell in cells ?? Enumerable.Empty<MarkdownCell>()) {
            var tag = cell.Kind == CellKind.Markdown ? "markdown"
                : string.IsNullOrEmpty(cell.Tag) || _csharpTags.Contains(cell.Tag) ? "csharp"
                : cell.Tag;
            text.Append("#!").Append(tag).Append("\n\n").Append((cell.Source ?? string.Empty).TrimEnd()).Append("\n\n");
        }
        return text.ToString();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClrKernel.Core.Runner;

/// <summary>Kind of a notebook cell.</summary>
public enum CellKind {
    Markdown,
    Code,
}

/// <summary>An input cell: markdown prose or an executable C# code block.</summary>
public sealed class NotebookCell {
    public CellKind Kind { get; init; }
    public string Source { get; init; } = string.Empty;

    public NotebookCell() { }

    public NotebookCell(CellKind kind, string source) {
        Kind = kind;
        Source = source;
    }
}

/// <summary>
/// Parses a notebook into an ordered list of markdown and code cells — the full
/// structure (prose included), unlike <see cref="NotebookImporter.ExtractCSharpBlocks"/>
/// which returns only the C#. Used by the runner to write a faithful executed
/// .ipynb. Supports .nb.md / .md (executable markdown), .ipynb, .dib, and .csx.
/// </summary>
public static class NotebookDocument {
    private static readonly Regex _markdownFence = new(
        @"^(?<fence>`{3,}|~{3,})\s*(?<lang>[^\s`~]*)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex _dibSection = new(
        @"^#!(?<kind>csharp|c#|fsharp|f#|pwsh|powershell|html|http|javascript|js|markdown|md|meta|mermaid|value|sql|kql)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> _csharpLangs =
        new(StringComparer.OrdinalIgnoreCase) { "csharp", "cs", "c#" };

    private static readonly HashSet<string> _pwshLangs =
        new(StringComparer.OrdinalIgnoreCase) { "pwsh", "powershell", "ps1" };

    // Non-C# fences/sections become code cells carrying a selector so the engine
    // routes each to its handler.
    private const string _httpSelector = "#!http";
    private const string _mermaidSelector = "#!mermaid";
    private const string _pwshSelector = "#!pwsh";

    public static IReadOnlyList<NotebookCell> Parse(string path) {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var content = File.ReadAllText(path);
        return extension switch {
            ".ipynb" => ParseIpynb(content),
            ".dib" => ParseDib(content),
            ".md" or ".markdown" => ParseMarkdown(content),
            ".csx" or ".cs" => new[] { new NotebookCell(CellKind.Code, content.Trim()) },
            _ => throw new NotSupportedException(
                $"Unsupported notebook type '{extension}' (supported: .nb.md, .md, .ipynb, .dib, .csx)."),
        };
    }

    /// <summary>
    /// Splits executable markdown: csharp/cs/c# fenced blocks become code cells;
    /// everything else (prose, and fences in other languages) stays markdown.
    /// </summary>
    public static IReadOnlyList<NotebookCell> ParseMarkdown(string content) {
        var cells = new List<NotebookCell>();
        var markdown = new List<string>();
        List<string> code = null;
        string closingFence = null;
        var codeIsCSharp = false;
        var codeIsHttp = false;
        var codeIsMermaid = false;
        var codeIsPwsh = false;

        void FlushMarkdown() {
            var text = string.Join("\n", markdown).Trim();
            if (text.Length > 0) {
                cells.Add(new NotebookCell(CellKind.Markdown, text));
            }
            markdown.Clear();
        }

        foreach (var line in content.Replace("\r\n", "\n").Split('\n')) {
            if (code == null) {
                var match = _markdownFence.Match(line);
                if (match.Success) {
                    code = new List<string>();
                    closingFence = new string(match.Groups["fence"].Value[0], match.Groups["fence"].Value.Length);
                    codeIsCSharp = _csharpLangs.Contains(match.Groups["lang"].Value);
                    codeIsHttp = match.Groups["lang"].Value.Equals("http", StringComparison.OrdinalIgnoreCase);
                    codeIsMermaid = match.Groups["lang"].Value.Equals("mermaid", StringComparison.OrdinalIgnoreCase);
                    codeIsPwsh = _pwshLangs.Contains(match.Groups["lang"].Value);
                    if (!codeIsCSharp && !codeIsHttp && !codeIsMermaid && !codeIsPwsh) {
                        // Keep other-language fences verbatim inside the markdown cell.
                        markdown.Add(line);
                    }
                } else {
                    markdown.Add(line);
                }
            } else if (line.TrimEnd() == closingFence || line.StartsWith(closingFence, StringComparison.Ordinal)) {
                if (codeIsCSharp || codeIsHttp || codeIsMermaid || codeIsPwsh) {
                    var text = string.Join("\n", code).Trim();
                    if (text.Length > 0) {
                        FlushMarkdown();
                        cells.Add(new NotebookCell(CellKind.Code, codeIsHttp ? _httpSelector + "\n" + text
                            : codeIsMermaid ? _mermaidSelector + "\n" + text
                            : codeIsPwsh ? _pwshSelector + "\n" + text
                            : text));
                    }
                } else {
                    markdown.Add(line);
                }
                code = null;
            } else {
                if (codeIsCSharp || codeIsHttp || codeIsMermaid || codeIsPwsh) {
                    code.Add(line);
                } else {
                    markdown.Add(line);
                }
            }
        }
        FlushMarkdown();

        return cells;
    }

    /// <summary>Splits a .dib document into markdown and csharp cells by #! section markers.</summary>
    public static IReadOnlyList<NotebookCell> ParseDib(string content) {
        var cells = new List<NotebookCell>();
        var current = new List<string>();
        var kind = CellKind.Code; // leading content defaults to C#
        var httpSection = false;
        var mermaidSection = false;
        var pwshSection = false;

        void Flush() {
            var text = string.Join("\n", current).Trim();
            if (text.Length > 0) {
                cells.Add(new NotebookCell(kind, httpSection ? _httpSelector + "\n" + text
                    : mermaidSection ? _mermaidSelector + "\n" + text
                    : pwshSection ? _pwshSelector + "\n" + text
                    : text));
            }
            current.Clear();
        }

        foreach (var line in content.Replace("\r\n", "\n").Split('\n')) {
            var match = _dibSection.Match(line);
            if (match.Success) {
                Flush();
                var section = match.Groups["kind"].Value.ToLowerInvariant();
                kind = section is "csharp" or "c#" or "http" or "mermaid" or "pwsh" or "powershell" ? CellKind.Code
                    : section is "markdown" or "md" ? CellKind.Markdown
                    : CellKind.Markdown; // other kernels: keep as prose, not executed
                httpSection = section == "http";
                mermaidSection = section == "mermaid";
                pwshSection = section is "pwsh" or "powershell";
            } else {
                current.Add(line);
            }
        }
        Flush();

        return cells;
    }

    /// <summary>Reads .ipynb cells (source only; existing outputs are dropped — we re-execute).</summary>
    public static IReadOnlyList<NotebookCell> ParseIpynb(string content) {
        var cells = new List<NotebookCell>();
        var notebook = JsonNode.Parse(content);
        foreach (var cell in notebook?["cells"]?.AsArray() ?? new JsonArray()) {
            var type = cell?["cell_type"]?.GetValue<string>();
            var source = cell?["source"] switch {
                JsonArray lines => string.Concat(lines.Select(l => l?.GetValue<string>() ?? "")),
                JsonNode scalar => scalar.GetValue<string>(),
                _ => "",
            };
            if (type == "code") {
                if (source.Trim().Length > 0) {
                    cells.Add(new NotebookCell(CellKind.Code, source.TrimEnd()));
                }
            } else if (type == "markdown") {
                cells.Add(new NotebookCell(CellKind.Markdown, source.TrimEnd()));
            }
        }
        return cells;
    }
}

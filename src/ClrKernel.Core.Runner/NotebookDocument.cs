using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Core.Runner;

/// <summary>Kind of a notebook cell.</summary>
public enum CellKind {
    Markdown,
    Code,
}

/// <summary>An input cell: markdown prose or an executable code block.</summary>
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
/// structure (prose included), unlike <c>NotebookImporter.ExtractCSharpBlocks</c>
/// which returns only the C#. Used by the runner to write a faithful executed
/// .ipynb. Supports .nb.md / .md (executable markdown), .ipynb, .dib, and .csx.
/// <para>
/// Which language tags and .dib sections execute is decided by the
/// <see cref="LanguageDescriptor"/> list a caller passes — the kernel passes its
/// registry, a remote client (Jobs) passes what the kernel's initialize reply
/// declared. C# is always executable; with no descriptors every other tagged block
/// stays markdown, so a process with no languages registered degrades safely.
/// </para>
/// </summary>
public static class NotebookDocument {
    private static readonly Regex _taggedBlock = new(
        @"^(?<delim>`{3,}|~{3,})\s*(?<lang>[^\s`~]*)\s*$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> _csharpLangs =
        new(StringComparer.OrdinalIgnoreCase) { "csharp", "cs", "c#" };

    // .dib section names recognized as cell boundaries even when no language
    // descriptor claims them — prose, other kernels' cells, and the well-known
    // language names. A name a descriptor claims becomes code; these stay text.
    private static readonly HashSet<string> _dibProseSections =
        new(StringComparer.OrdinalIgnoreCase) {
            "fsharp", "f#", "html", "javascript", "js", "markdown", "md", "meta", "value", "kql",
            "pwsh", "powershell", "http", "mermaid", "sql",
        };

    public static IReadOnlyList<NotebookCell> Parse(
        string path, IReadOnlyList<LanguageDescriptor> languages = null) {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var content = File.ReadAllText(path);
        return extension switch {
            ".ipynb" => ParseIpynb(content),
            ".dib" => ParseDib(content, languages),
            ".md" or ".markdown" => ParseMarkdown(content, languages),
            ".csx" or ".cs" => new[] { new NotebookCell(CellKind.Code, content.Trim()) },
            _ => throw new NotSupportedException(
                $"Unsupported notebook type '{extension}' (supported: .nb.md, .md, .ipynb, .dib, .csx)."),
        };
    }

    /// <summary>
    /// Splits executable markdown: csharp/cs/c# blocks and blocks tagged
    /// with a registered language's tag become code cells; everything else
    /// (prose, and blocks of unknown languages) stays markdown.
    /// </summary>
    public static IReadOnlyList<NotebookCell> ParseMarkdown(
        string content, IReadOnlyList<LanguageDescriptor> languages = null) {
        var byTag = LanguageDescriptor.ByTag(languages);
        var cells = new List<NotebookCell>();
        var markdown = new List<string>();
        List<string> code = null;
        string closingDelimiter = null;
        var codeIsCSharp = false;
        LanguageDescriptor codeLanguage = null;
        string codeTag = null;

        void FlushMarkdown() {
            var text = string.Join("\n", markdown).Trim();
            if (text.Length > 0) {
                cells.Add(new NotebookCell(CellKind.Markdown, text));
            }
            markdown.Clear();
        }

        foreach (var line in content.Replace("\r\n", "\n").Split('\n')) {
            if (code == null) {
                var match = _taggedBlock.Match(line);
                if (match.Success) {
                    code = new List<string>();
                    closingDelimiter = new string(match.Groups["delim"].Value[0], match.Groups["delim"].Value.Length);
                    codeTag = match.Groups["lang"].Value;
                    codeIsCSharp = _csharpLangs.Contains(codeTag);
                    codeLanguage = codeIsCSharp ? null : byTag.GetValueOrDefault(codeTag);
                    if (!codeIsCSharp && codeLanguage == null) {
                        // Keep unknown-language blocks verbatim inside the markdown cell.
                        markdown.Add(line);
                    }
                } else {
                    markdown.Add(line);
                }
            } else if (line.TrimEnd() == closingDelimiter || line.StartsWith(closingDelimiter, StringComparison.Ordinal)) {
                if (codeIsCSharp || codeLanguage != null) {
                    var text = string.Join("\n", code).Trim();
                    if (text.Length > 0) {
                        FlushMarkdown();
                        cells.Add(new NotebookCell(CellKind.Code,
                            codeLanguage == null ? text : codeLanguage.BlockForTag(codeTag, text)));
                    }
                } else {
                    markdown.Add(line);
                }
                code = null;
            } else {
                if (codeIsCSharp || codeLanguage != null) {
                    code.Add(line);
                } else {
                    markdown.Add(line);
                }
            }
        }
        FlushMarkdown();

        return cells;
    }

    /// <summary>Splits a .dib document into cells by #! section markers. C# and
    /// registered-language sections execute; other kernels' sections stay prose.</summary>
    public static IReadOnlyList<NotebookCell> ParseDib(
        string content, IReadOnlyList<LanguageDescriptor> languages = null) {
        var byTag = LanguageDescriptor.ByTag(languages);
        var cells = new List<NotebookCell>();
        var current = new List<string>();
        var kind = CellKind.Code; // leading content defaults to C#
        LanguageDescriptor sectionLanguage = null;
        string sectionTag = null;

        void Flush() {
            var text = string.Join("\n", current).Trim();
            if (text.Length > 0) {
                cells.Add(new NotebookCell(kind,
                    sectionLanguage == null ? text : sectionLanguage.BlockForTag(sectionTag, text)));
            }
            current.Clear();
        }

        foreach (var line in content.Replace("\r\n", "\n").Split('\n')) {
            var section = DibSectionName(line);
            if (section != null &&
                (_csharpLangs.Contains(section) || _dibProseSections.Contains(section) || byTag.ContainsKey(section))) {
                Flush();
                sectionLanguage = byTag.GetValueOrDefault(section);
                sectionTag = section;
                kind = _csharpLangs.Contains(section) || sectionLanguage != null ? CellKind.Code : CellKind.Markdown;
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

    // A bare "#!name" line is a .dib section marker; a line with arguments
    // ("#!sql-connect --name x") is a directive that belongs to its cell.
    private static string DibSectionName(string line) {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("#!", StringComparison.Ordinal) || trimmed.Length <= 2) {
            return null;
        }
        var name = trimmed.Substring(2);
        return name.Any(char.IsWhiteSpace) ? null : name;
    }
}

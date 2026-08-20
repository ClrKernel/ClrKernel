using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Core.Runner;

/// <summary>
/// One cell of a <c>.nb.md</c> document as written: the body verbatim, plus the
/// code-block tag it carried. Unlike <see cref="NotebookCell"/> — which is the
/// <em>execution</em> view, with a language selector injected and prose trimmed —
/// this is the <em>editing</em> view, and it round-trips.
/// </summary>
public sealed class MarkdownCell {
    public CellKind Kind { get; init; }

    /// <summary>The code-block tag as written (<c>csharp</c>, <c>zsh</c>, <c>tsql</c>),
    /// or null for a markdown cell. Preserved rather than recomputed: one language
    /// claims several tags, and rewriting <c>zsh</c> to <c>bash</c> would change
    /// which shell the cell runs in.</summary>
    public string Tag { get; init; }

    /// <summary>The cell body: the block's contents without its delimiters, or the
    /// prose itself. No selector line is injected here — that happens at execution.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The delimiter run this block was written with (<c>```</c>, <c>~~~~</c>),
    /// so an unusual one survives a round trip. Ignored for markdown cells.</summary>
    public string Delimiter { get; init; } = "```";

    /// <summary>False when the document ended before the block was closed; the
    /// serializer then leaves it open rather than inventing a closing line.</summary>
    public bool Closed { get; init; } = true;

    /// <summary>Blank lines between this cell and the next, as written. One is the
    /// usual spacing; some notebooks butt code blocks straight together, and
    /// re-spacing them would rewrite a file the user only opened.</summary>
    public int BlankLinesAfter { get; set; } = 1;

    public static MarkdownCell Markdown(string source) =>
        new() { Kind = CellKind.Markdown, Source = source ?? string.Empty };

    public static MarkdownCell Code(string tag, string source) =>
        new() { Kind = CellKind.Code, Tag = tag, Source = source ?? string.Empty };
}

/// <summary>
/// Reads and writes executable markdown (<c>.nb.md</c>) as cells — the one C#
/// implementation, so the Jobs web editor never needs its own copy of the format.
/// <para>
/// The canonical form matches the VS Code extension's serializer
/// (<c>editors/vscode/src/markdownSerializer.ts</c>): cells separated by a blank
/// line, code blocks fenced with their tag, trailing whitespace stripped from each
/// cell, one newline at end of file. <c>NotebookMarkdownTest</c> asserts every
/// <c>samples/*.nb.md</c> survives Parse→Serialize byte for byte — which matters
/// because every save in the web UI is a git commit, and a commit that rewrites
/// even one blank line invalidates a notebook's promotion evidence.
/// </para>
/// </summary>
public static class NotebookMarkdown {
    private static readonly Regex _blockOpen = new(
        @"^(?<delim>`{3,}|~{3,})\s*(?<lang>[^\s`~]*)\s*$", RegexOptions.Compiled);

    private static readonly HashSet<string> _csharpTags =
        new(StringComparer.OrdinalIgnoreCase) { "csharp", "cs", "c#" };

    /// <summary>
    /// Splits a document into cells. A block whose tag is C# or is claimed by a
    /// registered language becomes a code cell; everything else — prose, and blocks
    /// of unknown languages, delimiters included — stays markdown.
    /// </summary>
    public static IReadOnlyList<MarkdownCell> Parse(
        string content, IReadOnlyList<LanguageDescriptor> languages = null) {
        var byTag = LanguageDescriptor.ByTag(languages);
        var cells = new List<MarkdownCell>();
        var markdown = new List<string>();
        List<string> body = null;
        string closingDelimiter = null;
        string openDelimiter = null;
        string tag = null;
        var isCode = false;

        // Turns the lines accumulated since the last cell into a markdown cell (when
        // there is prose in them) and records the blank-line spacing on either side,
        // so the exact gaps come back out on serialize.
        void FlushMarkdown() {
            var lead = 0;
            while (lead < markdown.Count && markdown[lead].Trim().Length == 0) {
                lead++;
            }
            var end = markdown.Count;
            while (end > lead && markdown[end - 1].Trim().Length == 0) {
                end--;
            }
            if (cells.Count > 0) {
                // Blank lines before this prose (or the whole gap, when there is none)
                // belong to the cell that came before.
                cells[^1].BlankLinesAfter = lead < end ? lead : markdown.Count;
            }
            if (lead < end) {
                cells.Add(new MarkdownCell {
                    Kind = CellKind.Markdown,
                    Source = string.Join("\n", markdown.GetRange(lead, end - lead)),
                    BlankLinesAfter = markdown.Count - end,
                });
            }
            markdown.Clear();
        }

        foreach (var raw in (content ?? string.Empty).Replace("\r\n", "\n").Split('\n')) {
            if (body == null) {
                var match = _blockOpen.Match(raw);
                if (!match.Success) {
                    markdown.Add(raw);
                    continue;
                }
                tag = match.Groups["lang"].Value;
                isCode = _csharpTags.Contains(tag) || byTag.ContainsKey(tag);
                openDelimiter = match.Groups["delim"].Value;
                closingDelimiter = new string(openDelimiter[0], openDelimiter.Length);
                body = new List<string>();
                if (!isCode) {
                    // An unknown language: the block is prose, delimiters and all.
                    markdown.Add(raw);
                }
            } else if (raw.TrimEnd() == closingDelimiter || raw.StartsWith(closingDelimiter, StringComparison.Ordinal)) {
                if (isCode) {
                    FlushMarkdown();
                    cells.Add(new MarkdownCell {
                        Kind = CellKind.Code,
                        Tag = tag,
                        Source = string.Join("\n", body).Trim('\n'),
                        Delimiter = openDelimiter,
                    });
                } else {
                    markdown.AddRange(body);
                    markdown.Add(raw);
                }
                body = null;
            } else {
                (isCode ? body : markdown).Add(raw);
            }
        }
        if (body != null) {
            // Unterminated: keep the content rather than dropping it.
            if (isCode) {
                FlushMarkdown();
                cells.Add(new MarkdownCell {
                    Kind = CellKind.Code,
                    Tag = tag,
                    Source = string.Join("\n", body).Trim('\n'),
                    Delimiter = openDelimiter,
                    Closed = false,
                });
            } else {
                markdown.AddRange(body);
            }
        }
        FlushMarkdown();

        return cells;
    }

    /// <summary>Writes cells back to executable markdown, preserving each cell's
    /// spacing. A new cell defaults to one blank line, matching the usual layout.</summary>
    public static string Serialize(IEnumerable<MarkdownCell> cells) {
        var list = (cells ?? Enumerable.Empty<MarkdownCell>()).ToList();
        var text = new StringBuilder();
        for (var i = 0; i < list.Count; i++) {
            var cell = list[i];
            var source = (cell.Source ?? string.Empty).TrimEnd();
            if (cell.Kind == CellKind.Markdown) {
                text.Append(source);
            } else {
                var delimiter = string.IsNullOrEmpty(cell.Delimiter) ? "```" : cell.Delimiter;
                text.Append(delimiter).Append(cell.Tag ?? "csharp").Append('\n').Append(source);
                if (cell.Closed) {
                    text.Append('\n').Append(delimiter);
                }
            }
            // End this cell's last line, then its blank lines. The final cell just
            // gets the newline every text file ends with.
            text.Append('\n');
            if (i < list.Count - 1) {
                text.Append('\n', Math.Max(cell.BlankLinesAfter, 0));
            }
        }
        return text.ToString();
    }

    /// <summary>
    /// The tag to write for a cell whose language was just chosen — the language's
    /// own name when it claims it (<c>sql</c>, <c>powershell</c>), else its first
    /// tag. Only for new cells and picker changes: a tag already in the document is
    /// preserved as written.
    /// </summary>
    public static string TagFor(LanguageDescriptor language) {
        if (language == null) {
            return "csharp";
        }
        return language.LanguageTags.FirstOrDefault(t => string.Equals(t, language.Id, StringComparison.OrdinalIgnoreCase))
            ?? language.LanguageTags.FirstOrDefault()
            ?? "csharp";
    }

    /// <summary>The language a tag belongs to, or null for C# and unknown tags.</summary>
    public static LanguageDescriptor LanguageForTag(
        string tag, IReadOnlyList<LanguageDescriptor> languages) =>
        tag != null && !_csharpTags.Contains(tag)
            ? LanguageDescriptor.ByTag(languages).GetValueOrDefault(tag)
            : null;

    /// <summary>
    /// The code to execute for a cell: the language's selector is prepended so the
    /// engine routes it, unless the body already carries one. Mirrors the VS Code
    /// controller — the selector is a run-time concern and never written to disk.
    /// </summary>
    public static string ExecutableSource(MarkdownCell cell, IReadOnlyList<LanguageDescriptor> languages) {
        var language = LanguageForTag(cell?.Tag, languages);
        var source = cell?.Source ?? string.Empty;
        return language == null ? source : language.BlockForTag(cell.Tag, source);
    }
}

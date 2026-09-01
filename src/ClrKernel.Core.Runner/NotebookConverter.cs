using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Core.Runner;

/// <summary>
/// Turns a <c>.dib</c>, <c>.ipynb</c> or <c>.csx</c> into executable markdown.
///
/// <para>
/// Both halves already existed and did not meet: <see cref="NotebookDocument"/> reads
/// every format the runner accepts, and <see cref="NotebookMarkdown"/> writes the one
/// this project is about. What was missing between them is the *tag* — a
/// <c>.dib</c> section knows its language from a <c>#!</c> marker and an
/// <c>.ipynb</c> cell from a selector line inside its own source, and a fenced block
/// needs it on the fence.
/// </para>
///
/// <para>
/// **Outputs are dropped**, deliberately and without asking. A notebook whose stored
/// results travel with it is the format being converted away from; keeping them would
/// produce a <c>.nb.md</c> that no longer diffs like source, which is the entire
/// reason to convert. Re-run it to get results back.
/// </para>
/// </summary>
public static class NotebookConverter {
    /// <summary>What <see cref="ToMarkdown"/> will accept, for a caller's error message.</summary>
    public static readonly IReadOnlyList<string> Convertible = new[] { ".dib", ".ipynb", ".csx" };

    /// <summary>
    /// Cells for a document of this extension. Throws <see cref="NotSupportedException"/>
    /// for anything else — including <c>.nb.md</c>, which is already the destination and
    /// would only be rewritten.
    /// </summary>
    public static IReadOnlyList<MarkdownCell> Cells(
        string content, string extension, IReadOnlyList<LanguageDescriptor> languages = null) {
        return (extension ?? string.Empty).ToLowerInvariant() switch {
            ".dib" => FromDib(content, languages),
            ".ipynb" => FromIpynb(content, languages),
            // A script is one code cell. No sections, nothing to infer.
            ".csx" => new[] { MarkdownCell.Code("csharp", (content ?? string.Empty).Trim()) },
            // Named separately because `Path.GetExtension("notes.nb.md")` is ".md",
            // so the general message would answer a question about a .nb.md by
            // talking about some other extension.
            ".md" => throw new NotSupportedException(
                "That is already executable markdown — there is nothing to convert."),
            _ => throw new NotSupportedException(
                $"Cannot convert '{extension}' (convertible: {string.Join(", ", Convertible)})."),
        };
    }

    /// <summary>The converted document, ready to write to a <c>.nb.md</c>.</summary>
    public static string ToMarkdown(
        string content, string extension, IReadOnlyList<LanguageDescriptor> languages = null) =>
        NotebookMarkdown.Serialize(Cells(content, extension, languages));

    private static IReadOnlyList<MarkdownCell> FromDib(
        string content, IReadOnlyList<LanguageDescriptor> languages) =>
        NotebookDocument.DibSections(content, languages)
            .Select(s => s.Kind == CellKind.Markdown
                ? MarkdownCell.Markdown(s.Text)
                // The section's own marker is the tag, so `#!zsh` stays zsh rather
                // than becoming whichever tag its language happens to list first.
                : MarkdownCell.Code(TagOf(s.Tag, s.Language), s.Text))
            .ToList();

    private static IReadOnlyList<MarkdownCell> FromIpynb(
        string content, IReadOnlyList<LanguageDescriptor> languages) =>
        NotebookDocument.ParseIpynb(content)
            .Select(cell => {
                if (cell.Kind == CellKind.Markdown) {
                    return MarkdownCell.Markdown(cell.Source);
                }
                // A `.ipynb` code cell carries no language of its own here, but one
                // written by a polyglot kernel — or by `clrkernel run -o`, which
                // records what it executed — starts with the selector line. Read it
                // and take it off: on a fence it would be a duplicate at best and
                // executed twice at worst.
                var (tag, body) = SplitSelector(cell.Source, languages);
                return MarkdownCell.Code(tag ?? "csharp", body);
            })
            .ToList();

    /// <summary>A leading <c>#!tag</c> line and the rest, when the tag is a language
    /// this kernel knows. Anything else is left alone — an unknown <c>#!</c> line may
    /// be a magic the cell means to run.</summary>
    private static (string Tag, string Body) SplitSelector(
        string source, IReadOnlyList<LanguageDescriptor> languages) {
        var text = (source ?? string.Empty).Replace("\r\n", "\n");
        var newline = text.IndexOf('\n');
        var first = (newline < 0 ? text : text[..newline]).Trim();
        if (!first.StartsWith("#!", StringComparison.Ordinal)) {
            return (null, text.Trim());
        }
        var tag = first[2..].Trim();
        var byTag = LanguageDescriptor.ByTag(languages);
        if (tag.Length == 0 || !byTag.TryGetValue(tag, out var language)) {
            return (null, text.Trim());
        }
        return (TagOf(tag, language), newline < 0 ? string.Empty : text[(newline + 1)..].Trim());
    }

    /// <summary>The tag as the source wrote it when the language claims it, so
    /// <c>zsh</c> and <c>bash</c> stay distinct; otherwise the language's own name.</summary>
    private static string TagOf(string written, LanguageDescriptor language) {
        if (string.IsNullOrWhiteSpace(written)) {
            return NotebookMarkdown.TagFor(language);
        }
        return language?.LanguageTags?.Any(
            t => string.Equals(t, written, StringComparison.OrdinalIgnoreCase)) == true
            ? written
            : NotebookMarkdown.TagFor(language);
    }

    /// <summary>The <c>.nb.md</c> beside an input path, for a caller that gave no output.</summary>
    public static string DefaultOutput(string input) {
        var directory = Path.GetDirectoryName(input) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(input);
        return Path.Combine(directory, stem + ".nb.md");
    }
}

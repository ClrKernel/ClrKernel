using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
// global:: because this project's own namespace ends in FSharp and would shadow the compiler's.
using global::FSharp.Compiler.CodeAnalysis;
using global::FSharp.Compiler.Diagnostics;
using global::FSharp.Compiler.EditorServices;
using global::FSharp.Compiler.Symbols;
using global::FSharp.Compiler.Text;
using global::FSharp.Compiler.Tokenization;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace ClrKernel.Language.FSharp;

/// <summary>
/// F# editor features from the notebook's own fsi session, so completion and
/// hover know the bindings earlier cells made — <c>ParseAndCheckInteraction</c>
/// type-checks the cell against the session's state without running it.
/// </summary>
public sealed class FSharpCellLanguageServices : ICellLanguageServices {
    private readonly FSharpSession _session;

    public FSharpCellLanguageServices(FSharpSession session) {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task<CompletionResult> CompleteAsync(string code, int offset, LanguageServiceContext context) {
        var text = code ?? string.Empty;
        offset = Math.Max(0, Math.Min(offset, text.Length));
        var (lineNumber, lineText, column) = Locate(text, offset);
        var lineToCursor = lineText.Substring(0, column);
        if (lineToCursor.TrimStart().StartsWith("#!", StringComparison.Ordinal)) {
            var magic = DirectiveCompletion.Complete(
                new[] { ShareDirective.Definition }, lineToCursor, offset - column);
            return Task.FromResult(Convert(magic));
        }

        var (parse, check) = _session.Check(text);
        var partial = QuickParse.GetPartialLongNameEx(lineText, Math.Max(0, column - 1));
        var declarations = check.GetDeclarationListInfo(
            FSharpOption<FSharpParseFileResults>.Some(parse), lineNumber, lineText, partial, null, null, null);
        var result = new CompletionResult {
            ReplaceStart = offset - partial.PartialIdent.Length,
            ReplaceLength = partial.PartialIdent.Length,
        };
        foreach (var item in declarations.Items) {
            result.Items.Add(new CompletionEntry {
                Label = item.NameInList,
                InsertText = item.NameInCode,
                Kind = KindOf(item.Glyph),
                Detail = item.Kind.ToString(),
            });
        }
        return Task.FromResult(result);
    }

    public Task<HoverResult> HoverAsync(string code, int offset) {
        var text = code ?? string.Empty;
        if (offset < 0 || offset > text.Length) {
            return Task.FromResult<HoverResult>(null);
        }
        var (lineNumber, lineText, column) = Locate(text, offset);
        var start = column;
        while (start > 0 && IsIdentifierChar(lineText[start - 1])) {
            start--;
        }
        var end = column;
        while (end < lineText.Length && IsIdentifierChar(lineText[end])) {
            end++;
        }
        if (end <= start) {
            return Task.FromResult<HoverResult>(null);
        }
        var partial = QuickParse.GetPartialLongNameEx(lineText, end - 1);
        var names = ListModule.OfSeq(partial.QualifyingIdents.Append(partial.PartialIdent));
        var (_, check) = _session.Check(text);
        var tip = check.GetToolTip(lineNumber, end, lineText, names, FSharpTokenTag.Identifier, null);
        var markdown = Render(tip);
        if (string.IsNullOrWhiteSpace(markdown)) {
            return Task.FromResult<HoverResult>(null);
        }
        return Task.FromResult(new HoverResult {
            Markdown = markdown,
            Start = offset - (column - start),
            Length = end - start,
        });
    }

    public Task<SignatureHelpResult> SignatureHelpAsync(string code, int offset) {
        var text = code ?? string.Empty;
        if (offset < 0 || offset > text.Length) {
            return Task.FromResult<SignatureHelpResult>(null);
        }
        var (lineNumber, lineText, column) = Locate(text, offset);
        var (_, check) = _session.Check(text);
        var group = check.GetMethods(lineNumber, column, lineText, null);
        if (group == null || group.Methods.Length == 0) {
            return Task.FromResult<SignatureHelpResult>(null);
        }
        var result = new SignatureHelpResult();
        foreach (var method in group.Methods) {
            var entry = new SignatureEntry {
                Label = group.MethodName + "(" + string.Join(", ", method.Parameters.Select(p => Text(p.Display))) + ")",
                Documentation = Render(method.Description),
            };
            foreach (var parameter in method.Parameters) {
                entry.Parameters.Add(new SignatureParameter { Label = Text(parameter.Display) });
            }
            result.Signatures.Add(entry);
        }
        return Task.FromResult(result);
    }

    /// <summary>The compiler's own errors and warnings for the cell, against the session's state.</summary>
    public IReadOnlyList<DiagnosticResult> Diagnose(string text) {
        var diagnostics = new List<DiagnosticResult>(DirectiveCompletion.Check(new[] { ShareDirective.Definition }, text));
        if (string.IsNullOrWhiteSpace(text)) {
            return diagnostics;
        }
        var (_, check) = _session.Check(text);
        foreach (var d in check.Diagnostics) {
            if (d.Severity != FSharpDiagnosticSeverity.Error && d.Severity != FSharpDiagnosticSeverity.Warning) {
                continue;
            }
            diagnostics.Add(new DiagnosticResult {
                Line = d.StartLine,
                Column = d.StartColumn + 1,
                EndLine = d.EndLine,
                EndColumn = d.EndColumn + 1,
                Code = d.ErrorNumber,
                Message = d.Message,
                Severity = d.Severity == FSharpDiagnosticSeverity.Error ? 1 : 2,
            });
        }
        return diagnostics;
    }

    // 1-based line, the line's text, and the 0-based column of the offset in it.
    private static (int Line, string Text, int Column) Locate(string text, int offset) {
        var normalized = text.Replace("\r\n", "\n");
        var lineStart = normalized.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        if (offset == 0) {
            lineStart = 0;
        }
        var lineEnd = normalized.IndexOf('\n', offset);
        if (lineEnd < 0) {
            lineEnd = normalized.Length;
        }
        var lineNumber = normalized.Substring(0, lineStart).Count(c => c == '\n') + 1;
        return (lineNumber, normalized.Substring(lineStart, lineEnd - lineStart), offset - lineStart);
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '\'';

    private static CompletionResult Convert(DirectiveCompletionList magic) {
        var result = new CompletionResult { ReplaceStart = magic.ReplaceStart, ReplaceLength = magic.ReplaceLength };
        foreach (var item in magic.Items) {
            result.Items.Add(new CompletionEntry { Label = item.Label, InsertText = item.Label, Kind = item.Kind, Detail = item.Detail });
        }
        return result;
    }

    private static string KindOf(FSharpGlyph glyph) => glyph.Tag switch {
        FSharpGlyph.Tags.Class or FSharpGlyph.Tags.Struct or FSharpGlyph.Tags.Type or FSharpGlyph.Tags.Typedef => "class",
        FSharpGlyph.Tags.Interface => "interface",
        FSharpGlyph.Tags.Method or FSharpGlyph.Tags.OverridenMethod or FSharpGlyph.Tags.ExtensionMethod => "method",
        FSharpGlyph.Tags.Property => "property",
        FSharpGlyph.Tags.Field => "field",
        FSharpGlyph.Tags.Module or FSharpGlyph.Tags.NameSpace => "module",
        FSharpGlyph.Tags.Enum or FSharpGlyph.Tags.EnumMember or FSharpGlyph.Tags.Union or FSharpGlyph.Tags.Constant => "enum",
        FSharpGlyph.Tags.Variable => "variable",
        _ => "keyword",
    };

    private static string Text(IEnumerable<TaggedText> parts) => string.Concat(parts.Select(p => p.Text));

    private static string Render(ToolTipText tip) {
        var markdown = new StringBuilder();
        foreach (var element in tip.Item) {
            if (element is not ToolTipElement.Group group) {
                continue;
            }
            foreach (var data in group.elements) {
                var signature = Text(data.MainDescription).Trim();
                if (signature.Length > 0) {
                    markdown.Append("```fsharp\n").Append(signature).Append("\n```\n");
                }
                if (data.XmlDoc is FSharpXmlDoc.FromXmlText xml) {
                    var summary = string.Join("\n", xml.Item.UnprocessedLines.Select(l => l.Trim()))
                        .Replace("<summary>", "").Replace("</summary>", "").Trim();
                    if (summary.Length > 0) {
                        markdown.Append('\n').Append(summary).Append('\n');
                    }
                }
            }
        }
        return markdown.ToString().Trim();
    }
}

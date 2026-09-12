using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Kql;

/// <summary>
/// KQL editor features. Magic lines and the <c>// connections</c> comment complete
/// from the directive tables and the session's connection names; after a <c>|</c>
/// the operators; elsewhere the session's table and column names (one
/// <c>.show database schema</c>, cached), then functions and keywords. Hover is
/// one line of help for an operator or function.
/// </summary>
public sealed class KqlCellLanguageServices : ICellLanguageServices {
    private readonly KqlSession _session;

    public KqlCellLanguageServices(KqlSession session) {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task<CompletionResult> CompleteAsync(string code, int offset, LanguageServiceContext context) {
        var text = (code ?? string.Empty).Replace("\r\n", "\n");
        offset = Math.Max(0, Math.Min(offset, text.Length));
        var lineStart = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        var lineToCursor = text.Substring(lineStart, offset - lineStart);
        var left = lineToCursor.TrimStart();
        var names = _session.All.Select(c => c.Name).ToList();

        if (left.StartsWith("#!", StringComparison.Ordinal)) {
            var magic = DirectiveCompletion.Complete(
                KqlDirectives.AllDefinitions, lineToCursor, lineStart,
                role => role == "connection" ? names : Enumerable.Empty<string>());
            var fromMagic = new CompletionResult { ReplaceStart = magic.ReplaceStart, ReplaceLength = magic.ReplaceLength };
            foreach (var item in magic.Items) {
                fromMagic.Items.Add(new CompletionEntry { Label = item.Label, InsertText = item.Label, Kind = item.Kind, Detail = item.Detail });
            }
            return Task.FromResult(fromMagic);
        }

        var (wordStart, word) = WordBefore(text, offset);
        var result = new CompletionResult { ReplaceStart = wordStart, ReplaceLength = offset - wordStart };

        if (left.StartsWith("//", StringComparison.Ordinal)) {
            // `// connections <name>` — the one comment that means something.
            var rest = left.Substring(2).TrimStart();
            if (rest.Length == 0 || "connections".StartsWith(rest, StringComparison.OrdinalIgnoreCase) && !rest.Contains(' ')) {
                result.Items.Add(new CompletionEntry { Label = "connections", InsertText = "connections ", Kind = "keyword", Detail = "Run this cell against a named connection." });
            } else if (rest.StartsWith("connection", StringComparison.OrdinalIgnoreCase)) {
                foreach (var name in names.Where(n => n.StartsWith(word, StringComparison.OrdinalIgnoreCase))) {
                    result.Items.Add(new CompletionEntry { Label = name, InsertText = name, Kind = "connection", Detail = "Kusto connection" });
                }
            }
            return Task.FromResult(result);
        }

        var beforeWord = lineToCursor.Substring(0, lineToCursor.Length - word.Length).TrimEnd();
        if (beforeWord.EndsWith("|", StringComparison.Ordinal)) {
            foreach (var (op, help) in KqlLanguage.Operators) {
                result.Items.Add(new CompletionEntry { Label = op, InsertText = op, Kind = "keyword", Detail = help });
            }
            return Task.FromResult(Filtered(result, word));
        }

        var connection = KqlDirectives.ParseCell(text).ConnectionName;
        foreach (var (table, columns) in _session.Schema(connection)) {
            result.Items.Add(new CompletionEntry { Label = table, InsertText = QuoteIfNeeded(table), Kind = "table", Detail = "table" });
            foreach (var column in columns) {
                result.Items.Add(new CompletionEntry { Label = column, InsertText = QuoteIfNeeded(column), Kind = "column", Detail = table });
            }
        }
        foreach (var (signature, help) in KqlLanguage.Functions) {
            result.Items.Add(new CompletionEntry { Label = signature, InsertText = KqlLanguage.NameOf(signature) + "(", Kind = "function", Detail = help });
        }
        foreach (var keyword in KqlLanguage.Keywords) {
            result.Items.Add(new CompletionEntry { Label = keyword, InsertText = keyword, Kind = "keyword" });
        }
        return Task.FromResult(Filtered(result, word));
    }

    public Task<HoverResult> HoverAsync(string code, int offset) {
        var text = (code ?? string.Empty).Replace("\r\n", "\n");
        if (offset < 0 || offset > text.Length) {
            return Task.FromResult<HoverResult>(null);
        }
        var start = offset;
        while (start > 0 && IsWordChar(text[start - 1])) {
            start--;
        }
        var end = offset;
        while (end < text.Length && IsWordChar(text[end])) {
            end++;
        }
        var markdown = end > start ? KqlLanguage.Describe(text.Substring(start, end - start)) : null;
        return Task.FromResult(markdown == null ? null : new HoverResult { Markdown = markdown, Start = start, Length = end - start });
    }

    public Task<SignatureHelpResult> SignatureHelpAsync(string code, int offset) => Task.FromResult<SignatureHelpResult>(null);

    /// <summary>KQL itself is checked by the cluster; its directive lines are checked here.</summary>
    public IReadOnlyList<DiagnosticResult> Diagnose(string text) => DirectiveCompletion.Check(KqlDirectives.AllDefinitions, text);

    private static (int Start, string Word) WordBefore(string text, int offset) {
        var start = offset;
        while (start > 0 && IsWordChar(text[start - 1])) {
            start--;
        }
        return (start, text.Substring(start, offset - start));
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '!';

    private static CompletionResult Filtered(CompletionResult result, string word) {
        if (word.Length == 0) {
            return result;
        }
        var kept = result.Items.Where(i => i.Label.StartsWith(word, StringComparison.OrdinalIgnoreCase)).ToList();
        result.Items.Clear();
        result.Items.AddRange(kept);
        return result;
    }

    private static string QuoteIfNeeded(string name) =>
        name.All(c => char.IsLetterOrDigit(c) || c == '_') ? name : "['" + name.Replace("'", "\\'") + "']";
}

using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Python;

/// <summary>
/// Python editor features, answered by the notebook's own interpreter.
///
/// <para>
/// From the live namespace rather than from static analysis, which for a notebook
/// is the more useful half: once a cell has run, <c>df</c> <em>is</em> a DataFrame
/// and <c>dir()</c> knows every method it has — including ones no type checker
/// could infer from the source. The same reason <c>#!pwsh</c> completes from its
/// runspace. The trade is the honest one: a name typed but never run is not there
/// yet, exactly as in Jupyter.
/// </para>
/// <para>
/// Nothing here provisions an interpreter. On a machine with none, these return
/// null rather than starting a 60 MB download because someone hovered a word.
/// </para>
/// </summary>
public sealed class PythonCellLanguageServices : ICellLanguageServices {
    private readonly PythonCellLanguage _language;

    public PythonCellLanguageServices(PythonCellLanguage language) {
        _language = language;
    }

    public async Task<CompletionResult> CompleteAsync(string code, int offset, LanguageServiceContext context) {
        if (await _language.Session.ServiceAsync("complete", code, offset).ConfigureAwait(false)
            is not { } reply) {
            return null;
        }
        var result = new CompletionResult {
            ReplaceStart = reply.GetProperty("start").GetInt32(),
            ReplaceLength = reply.GetProperty("length").GetInt32(),
        };
        foreach (var item in reply.GetProperty("items").EnumerateArray()) {
            var label = Text(item, "label");
            result.Items.Add(new CompletionEntry {
                Label = label,
                InsertText = label,
                Kind = Text(item, "kind"),
                Detail = Text(item, "detail"),
            });
        }
        return result;
    }

    public async Task<HoverResult> HoverAsync(string code, int offset) {
        if (await _language.Session.ServiceAsync("hover", code, offset).ConfigureAwait(false)
            is not { } reply) {
            return null;
        }
        return new HoverResult {
            Markdown = Text(reply, "markdown"),
            Start = reply.GetProperty("start").GetInt32(),
            Length = reply.GetProperty("length").GetInt32(),
        };
    }

    public async Task<SignatureHelpResult> SignatureHelpAsync(string code, int offset) {
        if (await _language.Session.ServiceAsync("signature", code, offset).ConfigureAwait(false)
            is not { } reply) {
            return null;
        }
        var result = new SignatureHelpResult {
            ActiveSignature = reply.GetProperty("active").GetInt32(),
            ActiveParameter = reply.GetProperty("activeParameter").GetInt32(),
        };
        foreach (var signature in reply.GetProperty("signatures").EnumerateArray()) {
            var entry = new SignatureEntry { Label = Text(signature, "label") };
            foreach (var parameter in signature.GetProperty("parameters").EnumerateArray()) {
                entry.Parameters.Add(new SignatureParameter { Label = Text(parameter, "label") });
            }
            result.Signatures.Add(entry);
        }
        return result;
    }

    /// <summary>
    /// Not syntax-checked ahead of execution, like PowerShell cells. The check
    /// wants the interpreter's parser, and this call is synchronous while the
    /// interpreter is a process — a keystroke is the wrong moment to block on one.
    /// </summary>
    public IReadOnlyList<DiagnosticResult> Diagnose(string text) => new List<DiagnosticResult>();

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

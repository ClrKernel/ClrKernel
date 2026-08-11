using System.Collections.Generic;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.PowerShell;

/// <summary>
/// PowerShell editor features. All three come from the live runspace, so they
/// reflect the session's actual state — modules imported and variables assigned
/// by earlier cells — rather than a static keyword list. Runspace calls are
/// synchronous, so they run on the thread pool to keep the LSP loop responsive.
/// </summary>
public sealed class PowerShellCellLanguageServices : ICellLanguageServices {
    private readonly PowerShellCellLanguage _language;

    public PowerShellCellLanguageServices(PowerShellCellLanguage language) {
        _language = language;
    }

    public async Task<CompletionResult> CompleteAsync(string code, int offset, LanguageServiceContext context) {
        var completion = await Task.Run(() => _language.Session.Complete(code, offset)).ConfigureAwait(false);
        var result = new CompletionResult {
            ReplaceStart = completion.ReplaceStart,
            ReplaceLength = completion.ReplaceLength,
        };
        foreach (var item in completion.Items) {
            result.Items.Add(new CompletionEntry {
                Label = item.Label,
                InsertText = item.InsertText,
                Kind = item.Kind,
                Detail = item.Detail,
            });
        }
        return result;
    }

    public async Task<HoverResult> HoverAsync(string code, int offset) {
        var hover = await Task.Run(() => _language.Session.Hover(code, offset)).ConfigureAwait(false);
        if (hover == null || string.IsNullOrEmpty(hover.Markdown)) {
            return null;
        }
        return new HoverResult {
            Markdown = hover.Markdown,
            Start = hover.Start,
            Length = hover.Length,
        };
    }

    public async Task<SignatureHelpResult> SignatureHelpAsync(string code, int offset) {
        var help = await Task.Run(() => _language.Session.SignatureHelp(code, offset)).ConfigureAwait(false);
        if (help == null || help.Signatures.Count == 0) {
            return null;
        }
        var result = new SignatureHelpResult {
            ActiveSignature = help.ActiveSignature,
            ActiveParameter = help.ActiveParameter,
        };
        foreach (var signature in help.Signatures) {
            var entry = new SignatureEntry { Label = signature.Label };
            foreach (var parameter in signature.Parameters) {
                entry.Parameters.Add(new SignatureParameter { Label = parameter.Label });
            }
            result.Signatures.Add(entry);
        }
        return result;
    }

    /// <summary>PowerShell cells are not syntax-checked ahead of execution.</summary>
    public IReadOnlyList<DiagnosticResult> Diagnose(string text) => new List<DiagnosticResult>();
}

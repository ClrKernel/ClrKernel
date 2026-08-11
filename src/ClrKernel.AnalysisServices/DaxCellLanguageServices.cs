using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;

namespace ClrKernel.AnalysisServices;

/// <summary>
/// DAX editor features. Completion offers the session's registered cube names
/// alongside DAX keywords and functions.
/// </summary>
public sealed class DaxCellLanguageServices : ICellLanguageServices {
    private readonly SsasSession _session;

    public DaxCellLanguageServices(SsasSession session) {
        _session = session;
    }

    public Task<CompletionResult> CompleteAsync(string code, int offset, LanguageServiceContext context) {
        var completion = DaxLanguage.Complete(code, offset, new DaxCompletionContext {
            CubeNames = _session.Cubes.Names.ToList(),
        });

        var result = new CompletionResult {
            ReplaceStart = completion.ReplaceStart,
            ReplaceLength = completion.ReplaceLength,
        };
        foreach (var item in completion.Items) {
            result.Items.Add(new CompletionEntry {
                Label = item.Label,
                InsertText = item.InsertText,
                // A cube reads as a connection to the editor, same as SQL's.
                Kind = item.Kind == "cube" ? "connection" : item.Kind,
                Detail = item.Detail,
            });
        }
        return Task.FromResult(result);
    }

    public Task<HoverResult> HoverAsync(string code, int offset) {
        var hover = DaxLanguage.Hover(code, offset);
        if (hover == null || string.IsNullOrEmpty(hover.Markdown)) {
            return Task.FromResult<HoverResult>(null);
        }
        return Task.FromResult(new HoverResult {
            Markdown = hover.Markdown,
            Start = hover.Start,
            Length = hover.Length,
        });
    }

    /// <summary>DAX has no signature help.</summary>
    public Task<SignatureHelpResult> SignatureHelpAsync(string code, int offset) =>
        Task.FromResult<SignatureHelpResult>(null);

    /// <summary>DAX is not syntax-checked offline.</summary>
    public IReadOnlyList<DiagnosticResult> Diagnose(string text) => new List<DiagnosticResult>();
}

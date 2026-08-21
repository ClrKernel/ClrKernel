using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Sql;

/// <summary>
/// T-SQL editor features. Completion is session-aware: it offers the session's
/// connection names and pipeline step names, including <c>-- step</c> names
/// declared in the other SQL cells the editor has open.
/// </summary>
public sealed class SqlCellLanguageServices : ICellLanguageServices {
    private readonly SqlSession _session;

    private static readonly Regex _stepDeclaration =
        new Regex(@"(?im)^\s*--\s*step\s+([A-Za-z0-9_-]+)");

    public SqlCellLanguageServices(SqlSession session) {
        _session = session;
    }

    public Task<CompletionResult> CompleteAsync(string code, int offset, LanguageServiceContext context) {
        var steps = new HashSet<string>(_session.Pipeline.All.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var document in context?.OpenDocuments ?? Array.Empty<string>()) {
            foreach (Match m in _stepDeclaration.Matches(document ?? string.Empty)) {
                steps.Add(m.Groups[1].Value);
            }
        }

        var completion = SqlLanguage.Complete(code, offset, new SqlCompletionContext {
            ConnectionNames = _session.Connections.All.Select(c => c.Name).ToList(),
            StepNames = steps.ToList(),
        });

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
        return Task.FromResult(result);
    }

    public Task<HoverResult> HoverAsync(string code, int offset) {
        var hover = SqlLanguage.Hover(code, offset);
        if (hover == null || string.IsNullOrEmpty(hover.Markdown)) {
            return Task.FromResult<HoverResult>(null);
        }
        return Task.FromResult(new HoverResult {
            Markdown = hover.Markdown,
            Start = hover.Start,
            Length = hover.Length,
        });
    }

    /// <summary>T-SQL has no signature help.</summary>
    public Task<SignatureHelpResult> SignatureHelpAsync(string code, int offset) =>
        Task.FromResult<SignatureHelpResult>(null);

    public IReadOnlyList<DiagnosticResult> Diagnose(string text) =>
        TSqlSyntax.Check(text ?? string.Empty)
            .Select(d => new DiagnosticResult {
                Line = d.Line,
                Column = d.Column,
                EndLine = d.EndLine,
                EndColumn = d.EndColumn,
                Code = d.Number,
                Message = d.Message,
            })
            .Concat(DirectiveCompletion.Check(SqlDirectives.AllDefinitions, text))
            .ToList();
}

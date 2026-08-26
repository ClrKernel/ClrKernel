using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Sql;

/// <summary>
/// One dialect's editor features. Completion is session-aware — it offers the
/// session's connection names and pipeline step names, including <c>-- step</c>
/// names declared in the other SQL cells the editor has open — and
/// dialect-aware: the keywords, functions and types come from the dialect that
/// owns the cell, so an Oracle cell is never offered <c>NVARCHAR</c> and a T-SQL
/// one is never offered <c>NVL</c>.
/// </summary>
public sealed class SqlCellLanguageServices : ICellLanguageServices {
    private readonly SqlSession _session;
    private readonly SqlDialectLanguage _dialect;

    private static readonly Regex _stepDeclaration =
        new Regex(@"(?im)^\s*--\s*step\s+([A-Za-z0-9_-]+)");

    public SqlCellLanguageServices(SqlSession session, SqlDialectLanguage dialect = null) {
        _session = session;
        _dialect = dialect;
    }

    private SqlVocabulary Vocabulary => _dialect?.Vocabulary ?? SqlVocabulary.TSql;

    private IReadOnlyList<DirectiveDefinition> Directives =>
        _dialect?.Directives ?? SqlDirectives.AllDefinitions;

    public Task<CompletionResult> CompleteAsync(string code, int offset, LanguageServiceContext context) {
        var steps = new HashSet<string>(_session.Pipeline.All.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var document in context?.OpenDocuments ?? Array.Empty<string>()) {
            foreach (Match m in _stepDeclaration.Matches(document ?? string.Empty)) {
                steps.Add(m.Groups[1].Value);
            }
        }

        var completion = SqlLanguage.Complete(code, offset, new SqlCompletionContext {
            ConnectionNames = ConnectionNames(),
            StepNames = steps.ToList(),
        }, Vocabulary);

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

    /// <summary>
    /// Every connection the notebook can name, not only the SQL Server ones.
    /// <para>
    /// A dialect cell completes against connections it may not be able to run on —
    /// and should. Hiding the incompatible ones would turn "you cannot run Oracle
    /// SQL on that" into "there is no such connection", which sends the reader
    /// looking for a typo instead of at the two words that actually disagree.
    /// </para>
    /// </summary>
    private IReadOnlyList<string> ConnectionNames() {
        var names = _session.Connections.All.Select(c => c.Name).ToList();
        foreach (var name in SqlTarget.ProviderTypesInConfig().Keys) {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                names.Add(name);
            }
        }
        return names;
    }

    public Task<HoverResult> HoverAsync(string code, int offset) {
        var hover = SqlLanguage.Hover(code, offset, Vocabulary);
        if (hover == null || string.IsNullOrEmpty(hover.Markdown)) {
            return Task.FromResult<HoverResult>(null);
        }
        return Task.FromResult(new HoverResult {
            Markdown = hover.Markdown,
            Start = hover.Start,
            Length = hover.Length,
        });
    }

    /// <summary>No dialect here has signature help.</summary>
    public Task<SignatureHelpResult> SignatureHelpAsync(string code, int offset) =>
        Task.FromResult<SignatureHelpResult>(null);

    /// <summary>
    /// Squiggles. The parser is T-SQL's own, so only T-SQL gets syntax errors —
    /// there is no Oracle parser here, and running the T-SQL one over Oracle would
    /// reject valid statements with confident-sounding messages. Every dialect
    /// gets its directive lines checked, which is the part that is about ClrKernel
    /// rather than about SQL.
    /// </summary>
    public IReadOnlyList<DiagnosticResult> Diagnose(string text) {
        var directives = DirectiveCompletion.Check(Directives, text)
            .Concat(IncompatibleConnection(text))
            .ToList();
        if (_dialect != null && _dialect is not SqlCellLanguage) {
            return directives;
        }
        return TSqlSyntax.Check(WithoutSelectorLines(text))
            .Select(d => new DiagnosticResult {
                Line = d.Line,
                Column = d.Column,
                EndLine = d.EndLine,
                EndColumn = d.EndColumn,
                Code = d.Number,
                Message = d.Message,
            })
            .Concat(directives)
            .ToList();
    }

    /// <summary>
    /// The cell's text with its <c>#!</c> lines blanked out.
    /// <para>
    /// A selector line is ClrKernel's, not T-SQL's, and handing one to the parser
    /// gets "Incorrect syntax near '#'" — a squiggle on the one line in the cell
    /// that is definitely correct. Blanked rather than removed so every other
    /// diagnostic keeps the line number it was found on.
    /// </para>
    /// </summary>
    private static string WithoutSelectorLines(string text) {
        var lines = Lines(text);
        for (var i = 0; i < lines.Length; i++) {
            if (lines[i].TrimStart().StartsWith("#!", StringComparison.Ordinal)) {
                lines[i] = string.Empty;
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The dialect/provider mismatch, said while you are writing rather than when
    /// you run it: a T-SQL cell pointed at an Oracle connection is going to fail,
    /// and it will fail as a parse error from a driver, which names neither of the
    /// two things that actually disagree.
    /// <para>
    /// A <b>warning</b>, not an error. The cell is valid in its own dialect and the
    /// connection is a real connection; what is wrong is the pairing, and it stops
    /// being wrong the moment either one is changed — including by somebody else,
    /// in connections.json, without touching this notebook.
    /// </para>
    /// <para>
    /// A name that resolves to nothing says nothing here. Half a name is a name
    /// being typed, and a warning that flashes while you type is a warning people
    /// learn to stop reading.
    /// </para>
    /// </summary>
    private IEnumerable<DiagnosticResult> IncompatibleConnection(string text) {
        if (_dialect == null || string.IsNullOrWhiteSpace(text)) {
            yield break;
        }
        var (name, line) = ConnectionNamedIn(text);
        if (name == null) {
            yield break;
        }

        string providerType = null;
        if (_session.Connections.TryGet(name, out _)) {
            providerType = "SqlServer";
        } else if (SqlTarget.ProviderTypesInConfig().TryGetValue(name, out var configured)) {
            providerType = configured;
        }
        if (providerType == null || _dialect.Supports(providerType)) {
            yield break;
        }

        yield return new DiagnosticResult {
            Line = line,
            Column = 0,
            EndLine = line,
            EndColumn = Lines(text).ElementAtOrDefault(line)?.Length ?? 0,
            Severity = 2,
            Message =
                $"'{name}' is a {providerType} connection, and {_dialect.DisplayName} runs on " +
                $"{string.Join(", ", _dialect.SupportedProviders)}. This cell will not run as written.",
        };
    }

    /// <summary>
    /// The connection a cell names and the line it names it on — from the selector
    /// line's flag or from a <c>-- connections</c> comment. Null when the cell says
    /// nothing, which means the session default and is not this check's business.
    /// </summary>
    private static (string Name, int Line) ConnectionNamedIn(string text) {
        var lines = Lines(text);
        for (var i = 0; i < lines.Length; i++) {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) {
                continue;
            }
            if (trimmed.StartsWith("#!", StringComparison.Ordinal)) {
                if (SqlDirectives.SelectorConnection(trimmed) is { Length: > 0 } named) {
                    return (named, i);
                }
                continue;
            }
            if (!trimmed.StartsWith("--", StringComparison.Ordinal)) {
                break; // the first real statement line ends the directive block
            }
            var request = SqlDirectives.ParseCell(trimmed);
            if (!string.IsNullOrEmpty(request.ConnectionName)) {
                return (request.ConnectionName, i);
            }
        }
        return (null, 0);
    }

    private static string[] Lines(string text) => (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
}

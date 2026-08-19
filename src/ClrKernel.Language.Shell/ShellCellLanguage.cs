using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Shell;

/// <summary>
/// <c>#!bash</c> / <c>#!zsh</c> / <c>#!sh</c> cells: each cell runs in the named
/// shell (<c>#!shell</c> means bash) with the working directory and exported
/// environment persisting across cells. Output — ANSI colour included — comes
/// back as the <see cref="DisplayConsoleText"/> concept; the registered
/// formatters render the escapes as HTML and strip them from plain text.
/// </summary>
public sealed class ShellCellLanguage : ICellLanguage {
    private ShellSession _session;

    /// <summary>Matches the VS Code cell languageId for shell scripts.</summary>
    public string Id => "shellscript";

    public string DisplayName => "Shell";

    public IReadOnlyList<string> Selectors { get; } = new[] { "#!bash", "#!zsh", "#!sh", "#!shell", "#!shell-connect" };

    public IReadOnlyList<string> LanguageTags { get; } = new[] { "bash", "zsh", "sh", "shell" };

    public IReadOnlyList<DirectiveDefinition> Directives { get; } = new[] { ShellDirectives.ConnectDefinition };

    public ICellLanguageServices Services => null;

    /// <summary>Nothing to connect to.</summary>
    public IConnectionCatalog Connections => null;

    public ScriptContribution ScriptContribution => null;

    /// <summary>The session (cwd/env persistence), created on first use.</summary>
    public ShellSession Session => _session ??= new ShellSession();

    public async Task<object> ExecuteAsync(CellInvocation cell, ICellExecutionContext context) {
        if (cell.Selector.Equals("#!shell-connect", StringComparison.OrdinalIgnoreCase)) {
            var spec = Session.Connect(cell.FirstLine);
            return new DisplayBadge("ssh " + spec.Name, spec.Describe());
        }

        var shell = ShellFor(cell.Selector);
        var connection = ShellDirectives.SelectorConnection(cell.FirstLine);
        var result = connection != null
            ? await Session.ExecuteRemoteAsync(shell, cell.Body, connection).ConfigureAwait(false)
            : await Session.ExecuteAsync(shell, cell.Body, context.WorkingDirectory).ConfigureAwait(false);

        if (result.ExitCode != 0) {
            // The output is still worth seeing (it usually says why): display it,
            // AND carry its tail in the exception — a host that isn't showing
            // display output (tests, logs) must still see the reason.
            if (!string.IsNullOrEmpty(result.Output)) {
                new DisplayConsoleText(result.Output).Display();
            }
            throw new ShellCellException(
                $"{shell} exited with code {result.ExitCode}" + (connection != null ? $" on '{connection}'" : "") +
                (string.IsNullOrEmpty(result.Output) ? "." : ": " + Tail(result.Output, 400)));
        }

        return string.IsNullOrEmpty(result.Output) ? null : new DisplayConsoleText(result.Output);
    }

    private static string Tail(string text, int max) =>
        text.Length <= max ? text : "…" + text.Substring(text.Length - max);

    private static string ShellFor(string selector) {
        var name = (selector ?? string.Empty).TrimStart('#', '!');
        return name.Equals("shell", StringComparison.OrdinalIgnoreCase) || name.Length == 0 ? "bash" : name;
    }
}

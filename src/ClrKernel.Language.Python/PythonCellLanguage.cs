using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Python;

/// <summary>
/// <c>#!python</c> cells, run in one resident interpreter per notebook so state
/// carries from cell to cell.
///
/// <para>
/// Output streams into a single display cell as it arrives rather than appearing
/// in a lump when the cell finishes — a loop that prints and sleeps looks like a
/// loop that prints and sleeps.
/// </para>
/// </summary>
public sealed class PythonCellLanguage : ICellLanguage, IDisposable {
    private PythonSession _session;

    public string Id => "python";

    public string DisplayName => "Python";

    public IReadOnlyList<string> LanguageTags { get; } = new[] { "python", "py" };

    public IReadOnlyList<DirectiveDefinition> Directives { get; } = new[] {
        PythonDirectives.CellDefinition("#!python"),
        PythonDirectives.CellDefinition("#!py"),
        PythonDirectives.ResetDefinition,
    };

    /// <summary>No completion or hover yet: that wants the interpreter's own
    /// introspection, and running cells is the part worth having first.</summary>
    public ICellLanguageServices Services => null;

    /// <summary>Nothing to connect to — a Python cell reaches a database through
    /// its own libraries.</summary>
    public IConnectionCatalog Connections => null;

    public ScriptContribution ScriptContribution => null;

    /// <summary>The interpreter, started on first use.</summary>
    public PythonSession Session => _session ??= new PythonSession();

    public async Task<object> ExecuteAsync(CellInvocation cell, ICellExecutionContext context) {
        if (cell.Selector.Equals("#!python-reset", StringComparison.OrdinalIgnoreCase)) {
            Session.Restart();
            return new DisplayBadge("python", "interpreter restarted — every name it held is gone");
        }

        // Straight to Console, which is how a C# cell streams: the engine's
        // ConsoleProxy is capturing Console.Out for the duration of the cell and
        // forwards each line to the host as it appears. Writing here means a Python
        // cell and a C# cell stream by the same route and look the same doing it.
        //
        // The first attempt built a display cell and updated it per chunk. That
        // duplicated every line under `clrkernel run`, where an update is recorded
        // as another output rather than replacing the first — and it was more code
        // than Console.Write.
        Session.OnOutput = chunk => Console.Write(chunk);
        try {
            var result = await Session.ExecuteAsync(cell.Body, context?.WorkingDirectory)
                .ConfigureAwait(false);
            if (result.Failed) {
                // Whatever the cell printed first has already streamed; a traceback
                // usually only makes sense next to it.
                throw new PythonCellException(result.Error.TrimEnd());
            }
            // Nothing to return: `exec` has no value, and the output is already out.
            return null;
        } finally {
            Session.OnOutput = null;
        }
    }

    public void Dispose() => _session?.Dispose();
}

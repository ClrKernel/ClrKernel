using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Not <c>python</c>, for the reason <c>csharp-script</c> is not <c>csharp</c>:
    /// Pylance would attach to every cell and cannot know that names come from
    /// earlier cells, or that packages live in the kernel's own target directory —
    /// so a notebook would carry an unresolved-import squiggle on every import and
    /// an undefined-name squiggle on everything it had already bound.
    /// </summary>
    public string EditorLanguageId => "clr-python";

    /// <summary>Highlight it as Python; only the identity differs.</summary>
    public string GrammarId => "python";

    /// <summary>The default would truncate "python" to "PYTH".</summary>
    public string Monogram => "PY";

    public IReadOnlyList<DirectiveDefinition> Directives { get; } = new[] {
        PythonDirectives.CellDefinition("#!python"),
        PythonDirectives.CellDefinition("#!py"),
        PythonDirectives.ResetDefinition,
        PythonDirectives.InstallDefinition,
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
        if (cell.Selector.Equals("#!python-install", StringComparison.OrdinalIgnoreCase)) {
            return await InstallAsync(cell, context).ConfigureAwait(false);
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
            // What the cell ended on, plus anything it drew. All but the last are
            // shown as they are found; the last is returned, so a cell ending on a
            // DataFrame behaves exactly like a C# cell ending on a value.
            foreach (var display in result.Displays.Take(Math.Max(0, result.Displays.Count - 1))) {
                display.Display();
            }
            return result.Displays.Count > 0 ? result.Displays[^1] : null;
        } finally {
            Session.OnOutput = null;
        }
    }

    /// <summary>
    /// <c>#!python-install pandas</c>, or bare with a <c>requirements.txt</c> beside
    /// the notebook.
    /// </summary>
    private async Task<object> InstallAsync(CellInvocation cell, ICellExecutionContext context) {
        var working = context?.WorkingDirectory;
        // Both shapes: `#!python-install pandas matplotlib`, and the selector alone
        // over a requirements-style list, one package per line.
        var afterSelector = (cell.FirstLine ?? string.Empty)[cell.Selector.Length..];
        var named = (afterSelector + "\n" + (cell.Body ?? string.Empty))
            .Split('\n')
            .Select(l => l.Split('#')[0].Trim())
            .Where(l => l.Length > 0)
            .SelectMany(l => l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        var arguments = named.Length > 0 ? named : PythonEnvironment.Requirements(working);
        if (arguments == null || arguments.Count == 0) {
            throw new PythonCellException(
                "Nothing to install: name packages after #!python-install, or put a "
                + $"{PythonEnvironment.RequirementsFile} beside the notebook.");
        }

        // Starting the session first is what makes the packages directory known, and
        // means an install into a notebook that has not run a cell yet still lands
        // where that notebook's cells will look.
        await Session.ExecuteAsync(string.Empty, working).ConfigureAwait(false);
        var report = await PythonEnvironment
            .InstallAsync(Session.Executable, Session.Packages, arguments, line => Console.WriteLine(line))
            .ConfigureAwait(false);
        await Session.RefreshPackagesAsync(working).ConfigureAwait(false);

        Console.WriteLine(report.TrimEnd());
        return new DisplayBadge("python", $"installed into {Session.Packages}");
    }

    public void Dispose() => _session?.Dispose();
}

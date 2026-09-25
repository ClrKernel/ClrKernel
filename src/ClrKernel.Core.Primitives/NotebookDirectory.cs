using System.IO;
using System.Threading;

namespace ClrKernel.Core.Primitives;

/// <summary>
/// The folder of the notebook whose request is being served, for code that finds
/// files beside a notebook (<c>connections.json</c>) and has only the process's
/// current directory to start from.
///
/// <para>
/// One <c>lsp</c> process serves every open notebook, and its current directory is
/// the first notebook's folder. A second notebook in another folder could not see
/// its own <c>connections.json</c>: every lookup that defaulted to
/// <c>Directory.GetCurrentDirectory()</c> looked beside the wrong file. Ambient
/// rather than a parameter, because the lookups sit behind provider entry points
/// (<c>Odbc.Connect("name")</c>) whose signatures are the notebook API.
/// </para>
/// </summary>
public static class NotebookDirectory {
    private static readonly AsyncLocal<string> _current = new();

    /// <summary>Set by whoever knows which notebook a request belongs to; flows
    /// through the awaits below it and no further.</summary>
    public static string Current {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>The notebook's folder when one is known, else the process's.</summary>
    public static string OrCurrentDirectory => _current.Value ?? Directory.GetCurrentDirectory();
}

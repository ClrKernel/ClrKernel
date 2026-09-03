using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Python;

/// <summary>The declarative shape of the <c>#!python</c> cell directives — the same
/// table routing, completion and the front ends all read, so they cannot drift.</summary>
public static class PythonDirectives {
    public static DirectiveDefinition CellDefinition(string selector) => new() {
        Selector = selector,
        Description = "Runs the cell in this notebook's Python interpreter.",
    };

    /// <summary>
    /// Installs packages this notebook's cells can import. Into a directory of its
    /// own, not the interpreter, so two notebooks cannot fight over a version.
    /// </summary>
    public static readonly DirectiveDefinition InstallDefinition = new() {
        Selector = "#!python-install",
        Description =
            "Installs Python packages for this notebook. Names them in the cell, or "
            + "reads requirements.txt beside the notebook when given none.",
    };

    /// <summary>Drops the interpreter, so the next cell starts with nothing defined.</summary>
    public static readonly DirectiveDefinition ResetDefinition = new() {
        Selector = "#!python-reset",
        Description = "Restarts the Python interpreter, clearing every name it holds.",
    };
}

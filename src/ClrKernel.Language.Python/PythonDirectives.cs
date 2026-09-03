using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Python;

/// <summary>The declarative shape of the <c>#!python</c> cell directives — the same
/// table routing, completion and the front ends all read, so they cannot drift.</summary>
public static class PythonDirectives {
    public static DirectiveDefinition CellDefinition(string selector) => new() {
        Selector = selector,
        Description = "Runs the cell in this notebook's Python interpreter.",
    };

    /// <summary>Drops the interpreter, so the next cell starts with nothing defined.</summary>
    public static readonly DirectiveDefinition ResetDefinition = new() {
        Selector = "#!python-reset",
        Description = "Restarts the Python interpreter, clearing every name it holds.",
    };
}

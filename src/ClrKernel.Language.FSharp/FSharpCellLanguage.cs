using System.Collections.Generic;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.FSharp;

/// <summary>
/// <c>#!fsharp</c> cells: F# Interactive, one session per notebook. A cell's
/// trailing expression is displayed the way a C# cell's is; <c>printfn</c> output
/// streams as console text.
/// </summary>
public sealed class FSharpCellLanguage : ICellLanguage {
    private readonly FSharpSession _session = new FSharpSession();

    public FSharpSession Session => _session;

    public string Id => "fsharp";

    public string DisplayName => "F#";

    public string Monogram => "F#";

    public IReadOnlyList<DirectiveDefinition> Directives { get; } = new[] {
        new DirectiveDefinition { Selector = "#!fsharp", Description = "Runs the cell as F# in the notebook's F# Interactive session." },
        new DirectiveDefinition { Selector = "#!fs", Description = "Runs the cell as F# (alias of #!fsharp)." },
    };

    public IReadOnlyList<string> LanguageTags { get; } = new[] { "fsharp", "fs", "f#" };

    public ICellLanguageServices Services => null;

    public IConnectionCatalog Connections => null;

    /// <summary>Nothing for C# cells to call: the F# session is not reachable from C#.</summary>
    public ScriptContribution ScriptContribution => null;

    public Task<object> ExecuteAsync(CellInvocation cell, ICellExecutionContext context) =>
        Task.FromResult(_session.Execute(cell.Body));
}

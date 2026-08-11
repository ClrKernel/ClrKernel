using System.Collections.Generic;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Mermaid;

/// <summary>
/// <c>#!mermaid</c> cells: render the body as a self-contained diagram. Nothing
/// flows into the C# script state.
/// </summary>
public sealed class MermaidCellLanguage : ICellLanguage {
    public string Id => "mermaid";

    public IReadOnlyList<string> Selectors { get; } = new[] { "#!mermaid" };

    public ICellLanguageServices Services => null;

    /// <summary>Nothing to connect to.</summary>
    public IConnectionCatalog Connections => null;

    public ScriptContribution ScriptContribution { get; } = new ScriptContribution(
        references: new[] { typeof(MermaidRenderer).Assembly },
        imports: new[] { "ClrKernel.Language.Mermaid" });   // DisplayMermaid() helper

    public Task<object> ExecuteAsync(CellInvocation cell, ICellExecutionContext context) =>
        Task.FromResult<object>(MermaidRenderer.Render(cell.Body));
}

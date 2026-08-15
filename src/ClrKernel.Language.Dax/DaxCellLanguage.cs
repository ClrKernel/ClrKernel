using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using ClrKernel.Database.Provider.AnalysisServices;

namespace ClrKernel.Language.Dax;

/// <summary>
/// DAX cell magics: <c>#!dax-connect</c> registers named cubes (on-prem SSAS,
/// Azure AS, or a Fabric / Power BI semantic model) and <c>#!dax</c> queries one.
/// Both share the session's cube registry with the C# <c>AnalysisServices</c> API.
/// </summary>
public sealed class DaxCellLanguage : ICellLanguage {
    private readonly SsasSession _session = new SsasSession();

    /// <summary>The session's cube connections.</summary>
    public SsasSession Session => _session;

    public string Id => "dax";

    public IReadOnlyList<string> Selectors { get; } = new[] { "#!dax", "#!dax-connect" };

    public ICellLanguageServices Services => _services ??= new DaxCellLanguageServices(_session);

    private ICellLanguageServices _services;

    // Only the provider is cell-facing — `AnalysisServices` and everything it returns. Nothing in
    // Language.Dax is called from a C# cell, so this names one assembly and one
    // namespace. Both are resolved when a cell compiles, so a stale string here builds
    // clean and fails only at run time; SsasTest executes `AnalysisServices.Connect(...)` in a cell
    // and is what catches it.
    /// <summary>The session's cubes, for the editor's connection UI.</summary>
    public IConnectionCatalog Connections => _connections ??= new DaxConnectionCatalog(_session);

    private IConnectionCatalog _connections;

    public ScriptContribution ScriptContribution { get; } = new ScriptContribution(
        references: new[] { typeof(AnalysisServices).Assembly },
        imports: new[] { "ClrKernel.Database.Provider.AnalysisServices" });   // AnalysisServices.Connect / ProcessPartitions

    public Task<object> ExecuteAsync(CellInvocation cell, ICellExecutionContext context) {
        if (string.Equals(cell.Selector, "#!dax-connect", StringComparison.OrdinalIgnoreCase)) {
            var names = new List<string>();
            foreach (var line in cell.Text.Split('\n')) {
                if (line.TrimStart().StartsWith("#!dax-connect", StringComparison.OrdinalIgnoreCase)) {
                    names.Add(_session.Connect(line.Trim()));
                }
            }
            var summary = $"Connected cube(s): {string.Join(", ", names)} (default: {_session.Cubes.DefaultName})";
            return Task.FromResult<object>(new DisplayData(summary));
        }

        // #!dax [cube]: re-express an inline cube name as the leading comment the
        // executor understands.
        var inline = DaxDirectives.SelectorConnection(cell.FirstLine);
        var cellText = string.IsNullOrEmpty(inline)
            ? cell.Body
            : "-- connections " + inline + "\n" + cell.Body;
        return Task.FromResult<object>(_session.Execute(cellText));
    }
}

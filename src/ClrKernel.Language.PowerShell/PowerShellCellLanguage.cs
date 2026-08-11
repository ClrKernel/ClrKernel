using System.Collections.Generic;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.PowerShell;

/// <summary>
/// <c>#!pwsh</c> / <c>#!powershell</c> cells: run in a persistent runspace, so
/// state and completions share one session. Output comes back as display data;
/// nothing flows into the C# script state.
/// </summary>
public sealed class PowerShellCellLanguage : ICellLanguage {
    private PowerShellSession _session;

    public string Id => "powershell";

    public IReadOnlyList<string> Selectors { get; } = new[] { "#!pwsh", "#!powershell" };

    public ICellLanguageServices Services => _services ??= new PowerShellCellLanguageServices(this);

    private ICellLanguageServices _services;

    /// <summary>Nothing to connect to.</summary>
    public IConnectionCatalog Connections => null;

    public ScriptContribution ScriptContribution => null;

    /// <summary>The runspace, created on first use (also by completion queries).</summary>
    public PowerShellSession Session => _session ??= new PowerShellSession();

    public Task<object> ExecuteAsync(CellInvocation cell, ICellExecutionContext context) =>
        Task.FromResult<object>(Session.Execute(cell.Body));
}

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

    public string DisplayName => "PowerShell";

    /// <summary>Its id is too long to cut to four and still read — "PS" is
    /// what people write.</summary>
    public string Monogram => "PS";

    public IReadOnlyList<string> LanguageTags { get; } = new[] { "pwsh", "powershell", "ps1" };

    // The cell directives are the language's routing tokens; #!pwsh leads, so it
    // is the default for a bare cell.
    public IReadOnlyList<DirectiveDefinition> Directives { get; } = new[] {
        PwshDirectives.CellDefinition("#!pwsh"),
        PwshDirectives.CellDefinition("#!powershell"),
        PwshDirectives.ConnectDefinition,
    };

    public ICellLanguageServices Services => _services ??= new PowerShellCellLanguageServices(this);

    private ICellLanguageServices _services;

    /// <summary>Nothing to connect to.</summary>
    public IConnectionCatalog Connections => null;

    public ScriptContribution ScriptContribution => null;

    /// <summary>The runspace, created on first use (also by completion queries).</summary>
    public PowerShellSession Session => _session ??= new PowerShellSession();

    public Task<object> ExecuteAsync(CellInvocation cell, ICellExecutionContext context) {
        if (cell.Selector.Equals("#!pwsh-connect", System.StringComparison.OrdinalIgnoreCase)) {
            var spec = Session.Connect(cell.FirstLine);
            return Task.FromResult<object>(new ClrKernel.Core.Primitives.DisplayBadge("psremote " + spec.Name, spec.Describe()));
        }
        var connection = PwshDirectives.SelectorConnection(cell.FirstLine);
        return Task.FromResult<object>(Session.Execute(cell.Body, connection));
    }
}

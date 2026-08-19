using System.Collections.Generic;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Http;

/// <summary>
/// <c>#!http</c> cells: run the body as a .http document. Session state (file
/// variables, named responses) persists across cells so requests chain like one
/// growing file. Response cards are emitted as display data; nothing flows into
/// the C# script state.
/// </summary>
public sealed class HttpCellLanguage : ICellLanguage {
    private HttpSession _session;

    public string Id => "http";

    public string DisplayName => "HTTP";

    public IReadOnlyList<string> Selectors { get; } = new[] { "#!http" };

    public IReadOnlyList<string> LanguageTags { get; } = new[] { "http" };

    public ICellLanguageServices Services => null;

    /// <summary>Nothing to connect to.</summary>
    public IConnectionCatalog Connections => null;

    public ScriptContribution ScriptContribution => null;

    /// <summary>The session's HTTP state, created on first use.</summary>
    public HttpSession Session(string workingDirectory) => _session ??= new HttpSession(workingDirectory);

    public async Task<object> ExecuteAsync(CellInvocation cell, ICellExecutionContext context) {
        await Session(context.WorkingDirectory).ExecuteAsync(cell.Body).ConfigureAwait(false);
        return null;
    }
}

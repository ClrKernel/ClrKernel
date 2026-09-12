using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using ClrKernel.Database.Provider.Kusto;

namespace ClrKernel.Language.Kql;

/// <summary>
/// KQL cell magics: <c>#!kql-connect</c> registers named Kusto databases and
/// <c>#!kql</c> queries one. The same databases are reachable from C# through
/// <c>KustoDb.Connect(...)</c>.
/// </summary>
public sealed class KqlCellLanguage : ICellLanguage {
    private readonly KqlSession _session = new KqlSession();
    private IConnectionCatalog _connections;

    public KqlSession Session => _session;

    public string Id => "kql";

    public string DisplayName => "KQL";

    public IReadOnlyList<string> LanguageTags { get; } = new[] { "kql", "kusto" };

    public IReadOnlyList<DirectiveDefinition> Directives => KqlDirectives.AllDefinitions;

    public ICellLanguageServices Services => _services ??= new KqlCellLanguageServices(_session);

    private ICellLanguageServices _services;

    public IConnectionCatalog Connections => _connections ??= new KqlConnectionCatalog(_session);

    public IReadOnlyList<string> SupportedProviders { get; } = new[] { KustoConnectionProvider.TypeName };

    // Only the provider is cell-facing: `Kusto.Connect(...)` and what it returns.
    public ScriptContribution ScriptContribution { get; } = new ScriptContribution(
        references: new[] { typeof(KustoDb).Assembly },
        imports: new[] { "ClrKernel.Database.Provider.Kusto" });

    public Task<object> ExecuteAsync(CellInvocation cell, ICellExecutionContext context) {
        if (string.Equals(cell.Selector, "#!kql-connect", StringComparison.OrdinalIgnoreCase)) {
            var names = new List<string>();
            foreach (var line in cell.Text.Split('\n')) {
                if (line.TrimStart().StartsWith("#!kql-connect", StringComparison.OrdinalIgnoreCase)) {
                    names.Add(_session.Connect(line.Trim()));
                }
            }
            return Task.FromResult<object>(new DisplayData(
                $"Connected Kusto database(s): {string.Join(", ", names)} (default: {_session.DefaultName})"));
        }
        var inline = KqlDirectives.SelectorConnection(cell.FirstLine);
        var body = string.IsNullOrEmpty(inline) ? cell.Body : "// connections " + inline + "\n" + cell.Body;
        return Task.FromResult<object>(_session.Execute(body));
    }
}

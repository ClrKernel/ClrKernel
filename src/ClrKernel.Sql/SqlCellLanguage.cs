using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Sql;

/// <summary>
/// The T-SQL cell magics: <c>#!sql</c> plus the verbs <c>#!sql-connect</c>,
/// <c>#!sql-bulk</c>, <c>#!sql-merge</c>, <c>#!sql-run</c> and
/// <c>#!sql-deploy</c>. All share one <see cref="SqlSession"/>, so connections
/// registered by a magic are the same ones the C# <c>Sql</c> API sees.
/// <para>
/// Every verb is a distinct selector rather than an argument of <c>#!sql</c>, so
/// dispatch order is decided structurally by <see cref="CellLanguageRegistry"/>
/// (longest selector first) instead of by the order of tests in this file.
/// </para>
/// </summary>
public sealed class SqlCellLanguage : ICellLanguage {
    private readonly SqlSession _session = new SqlSession();

    /// <summary>
    /// The instance backing the cell-visible <c>Sql</c> global. Set on
    /// construction; the last engine created wins, matching
    /// <c>InteractiveScriptEngine.Current</c>.
    /// </summary>
    public static SqlCellLanguage Current { get; private set; }

    public SqlCellLanguage() {
        Current = this;
    }

    /// <summary>The session's connections, ETL and pipeline state.</summary>
    public SqlSession Session => _session;

    public string Id => "sql";

    public IReadOnlyList<string> Selectors { get; } = new[] {
        "#!sql", "#!sql-connect", "#!sql-bulk", "#!sql-merge", "#!sql-run", "#!sql-deploy",
    };

    public ICellLanguageServices Services => _services ??= new SqlCellLanguageServices(_session);

    private ICellLanguageServices _services;

    public ScriptContribution ScriptContribution { get; } = new ScriptContribution(
        references: new[] { typeof(SqlSession).Assembly },
        imports: new[] {
            "ClrKernel.Sql",       // SqlSession, MergeSpec (Sql.BulkCopy / Sql.Merge)
            "ClrKernel.Sql.Etl",   // BulkCopyOptions, MergeSpec, DataTableBuilder
        },
        usingStatics: new[] { "using static ClrKernel.Sql.SqlGlobals;" });

    public async Task<object> ExecuteAsync(CellInvocation cell, ICellExecutionContext context) {
        switch (cell.Selector.ToLowerInvariant()) {
            case "#!sql-connect":
                return await ConnectAsync(cell, context).ConfigureAwait(false);
            case "#!sql-bulk":
                return _session.ExecuteBulk(cell.FirstLine);
            case "#!sql-merge":
                return _session.ExecuteMerge(cell.FirstLine);
            case "#!sql-run":
                return _session.ExecuteRun(cell.FirstLine);
            case "#!sql-deploy":
                return _session.ExecuteDeploy(cell.FirstLine);
            default:
                // #!sql [connection]: re-express an inline connection name as the
                // leading SQL comment the executor understands.
                var inline = SqlDirectives.SelectorConnection(cell.FirstLine);
                var cellText = string.IsNullOrEmpty(inline)
                    ? cell.Body
                    : "-- connections " + inline + "\n" + cell.Body;
                return _session.Execute(cellText);
        }
    }

    // One #!sql-connect cell may hold several directive lines; each registers a
    // connection and may bind a C# variable for it.
    private async Task<object> ConnectAsync(CellInvocation cell, ICellExecutionContext context) {
        var names = new List<string>();
        var bound = new List<string>();

        foreach (var line in cell.Text.Split('\n')) {
            if (!line.TrimStart().StartsWith("#!sql-connect", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            var directive = _session.Connect(line.Trim());
            names.Add(directive.Spec.Name);

            // Bind a C# variable so #!csharp cells can use the connection
            // directly, e.g. `dw.Query("...").Results()`.
            if (!string.IsNullOrEmpty(directive.Variable)) {
                await context.RunScriptAsync(
                    $"var {directive.Variable} = Sql.Database({CSharpStringLiteral(directive.Spec.Name)});")
                    .ConfigureAwait(false);
                bound.Add(directive.Variable);
            }
        }

        var label = names.Count == 1 ? $"Connected: {names[0]}" : $"Connected: {string.Join(", ", names)}";
        if (bound.Count > 0) {
            label += bound.Count == 1
                ? $" → C# variable `{bound[0]}`"
                : $" → C# variables {string.Join(", ", bound.ConvertAll(b => "`" + b + "`"))}";
        }
        return new DisplayData($"{label} (default: {_session.Connections.DefaultName})");
    }

    private static string CSharpStringLiteral(string value) =>
        "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// <summary>
/// The cell-visible <c>Sql</c> global, imported into every C# cell via
/// <c>using static</c>. Kept here rather than in the engine so
/// <c>Core.Scripting</c> needs no reference to this package.
/// </summary>
public static class SqlGlobals {
    /// <summary>
    /// The session's SQL connections and ETL API, for use from C# cells:
    /// <c>Sql.BulkCopy("warehouse", "dbo.Orders", rows)</c>,
    /// <c>Sql.Merge("warehouse", new MergeSpec { ... })</c>,
    /// <c>Sql.OpenConnection("analytics")</c>. Shares the same connections and
    /// secret store as <c>#!sql</c> / <c>#!sql-connect</c> cells.
    /// </summary>
    public static SqlSession Sql => SqlCellLanguage.Current?.Session;
}

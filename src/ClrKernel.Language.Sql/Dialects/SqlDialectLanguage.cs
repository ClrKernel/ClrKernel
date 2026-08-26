using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Language.Sql;

/// <summary>
/// What every SQL dialect is: a name, a set of words, a list of connection types
/// its statements can be carried by, and one shared session.
/// <para>
/// The split that makes this work is between the <b>dialect</b> — a property of
/// the cell, written into the file, deciding syntax and completion — and the
/// <b>provider</b>, a property of the connection, deciding how the statement is
/// transported. <see cref="SupportedProviders"/> is the join between them, and it
/// is a compatibility declaration rather than part of the language's identity:
/// pointing a cell at a different connection must never change what language the
/// cell is written in.
/// </para>
/// <para>
/// The session is <b>shared across dialects</b>, and passed in rather than made
/// here. A connection belongs to the notebook, not to the dialect that happened
/// to declare it: <c>#!sql-connect --name warehouse</c> then <c>#!ansisql
/// warehouse</c> has to mean one connection, or a name would mean a different
/// thing in every cell depending on how it was written.
/// </para>
/// </summary>
public abstract class SqlDialectLanguage : ICellLanguage {
    protected SqlDialectLanguage(SqlSession session) {
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>The connections, ETL and pipeline state shared by every dialect.</summary>
    public SqlSession Session { get; }

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    /// <summary>The words this dialect knows.</summary>
    public abstract SqlVocabulary Vocabulary { get; }

    /// <summary>Each dialect names itself in four characters or fewer.</summary>
    public abstract string Monogram { get; }

    public abstract IReadOnlyList<string> SupportedProviders { get; }

    public abstract IReadOnlyList<DirectiveDefinition> Directives { get; }

    public abstract IReadOnlyList<string> LanguageTags { get; }

    /// <summary>Every dialect clusters under one heading in a picker.</summary>
    public string Category => "SQL";

    /// <summary>
    /// An editor id of its own, prefixed so it can never collide with one an
    /// editor or another extension already has.
    /// <para>
    /// The T-SQL dialect is why. Its kernel id is <c>sql</c>, which is a VS Code
    /// built-in: a cell called <c>sql</c> wears the built-in's name in every menu
    /// — "SQL", never "T-SQL", however the kernel describes itself — and every
    /// SQL extension the user has installed attaches to it. Taking an id of our
    /// own is the same move <c>csharp-script</c> makes, for the same two reasons.
    /// </para>
    /// </summary>
    public string EditorLanguageId => "clr-" + Id;

    /// <summary>
    /// One highlighter for all of them.
    /// <para>
    /// The dialects differ by a few dozen words, and a tokenizer does not read
    /// words — it reads strings, comments, numbers and identifiers, which are the
    /// same in all three. What has to be dialect-correct is <em>completion</em>,
    /// and completion here is served per cell by the kernel, which knows exactly
    /// which cell asked. Three near-identical tokenizers would buy the client a
    /// maintenance burden and the reader nothing.
    /// </para>
    /// </summary>
    public string GrammarId => "sql";

    /// <summary>Only the dialect that owns the C# API contributes to the session;
    /// the others would be contributing the same assemblies a second time.</summary>
    public virtual ScriptContribution ScriptContribution => null;

    public virtual IConnectionCatalog Connections => null;

    public ICellLanguageServices Services =>
        _services ??= new SqlCellLanguageServices(Session, this);

    private ICellLanguageServices _services;

    /// <summary>
    /// Runs a query cell in this dialect. Dialects with verbs of their own
    /// (<c>#!sql-bulk</c> and friends) override and fall back to this.
    /// </summary>
    public virtual Task<object> ExecuteAsync(CellInvocation cell, ICellExecutionContext context) =>
        Task.FromResult<object>(ExecuteQuery(cell));

    /// <summary>The <c>#!&lt;dialect&gt; [connection]</c> path, shared by all of them:
    /// re-express an inline connection name as the leading comment the executor reads,
    /// then run it in this dialect.</summary>
    protected object ExecuteQuery(CellInvocation cell) {
        var inline = SqlDirectives.SelectorConnection(cell.FirstLine);
        var cellText = string.IsNullOrEmpty(inline)
            ? cell.Body
            : "-- connections " + inline + "\n" + cell.Body;
        return Session.Execute(cellText, this);
    }

    /// <summary>
    /// Whether a connection of this <c>$type</c> can carry this dialect's statements.
    /// An unknown type answers no: a provider nobody declared support for is not a
    /// provider this dialect has been shown to work on.
    /// </summary>
    public bool Supports(string providerType) =>
        providerType != null &&
        SupportedProviders.Contains(providerType, StringComparer.OrdinalIgnoreCase);

    /// <summary>The cell-level directive every dialect has: run this, optionally naming
    /// the connection. Built here so a new dialect declares a name and nothing else.</summary>
    protected static DirectiveDefinition QueryDirective(string selector, string displayName) => new() {
        Selector = selector,
        Description = $"Runs the cell as {displayName} on a registered connection.",
        Parameters = new DirectiveParameter[] {
            new() {
                Name = "--connections", Aliases = new[] { "--connection", "-c" },
                ValueRole = "connection",
                Description = "Connection to run on (default connection when omitted).",
            },
        },
    };
}

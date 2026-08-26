using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ClrKernel.Core.Scripting;

/// <summary>
/// A cell language plugged into the engine. Implementations live in the
/// <c>ClrKernel.Language.*</c> packages and are registered by the composition
/// root (the CLI), so <c>Core.Scripting</c> never references them.
/// </summary>
public interface ICellLanguage {
    /// <summary>
    /// Stable identifier, matching the notebook cell languageId where one exists
    /// ("sql", "dax", "http", "mermaid", "powershell").
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Every <c>#!</c> directive this language answers — <c>#!sql</c> and its verbs
    /// <c>#!sql-connect</c>, <c>#!sql-bulk</c>, … — declared once, as the same
    /// tables its parsers bind against, so routing, completion, diagnostics and
    /// front ends can never drift from what actually parses.
    /// <para>
    /// A directive's name IS the language's routing token: the <b>first</b>
    /// directive is the default one, the one serializers prepend to a bare cell.
    /// Order beyond that does not matter for dispatch — see
    /// <see cref="CellLanguageRegistry"/>.
    /// </para>
    /// </summary>
    IReadOnlyList<DirectiveDefinition> Directives { get; }

    /// <summary>
    /// The routing tokens, derived from <see cref="Directives"/>: a selector is
    /// simply a directive's name. A cell is routed here when its first non-blank
    /// line starts with one of these followed by whitespace or end-of-line.
    /// </summary>
    IReadOnlyList<string> Selectors => Directives.Select(d => d.Selector).ToList();

    /// <summary>The default routing token, prepended to a bare cell of this language.</summary>
    string DefaultSelector => Directives.Count > 0 ? Directives[0].Selector : null;

    /// <summary>Human-readable name for pickers and generated UI ("SQL", "PowerShell").</summary>
    string DisplayName => Id;

    /// <summary>
    /// What a picker clusters this language under — "SQL" keeps the dialects
    /// together instead of scattering them between C# and HTTP. Null for a
    /// language that stands on its own.
    /// </summary>
    string Category => null;

    /// <summary>
    /// The <c>connections.json</c> <c>$type</c> values this language's cells can
    /// execute against, in preference order.
    /// <para>
    /// A <b>compatibility declaration, not an identity</b>: which provider carries
    /// a statement is a property of the connection, and changing connection must
    /// not change what language the cell is written in. Open strings rather than
    /// an enum, so a third party shipping a PostgreSQL dialect needs no change
    /// here. Empty means the language is not provider-bound at all (HTTP, Mermaid,
    /// Markdown), which is not the same as "runs on anything".
    /// </para>
    /// </summary>
    IReadOnlyList<string> SupportedProviders => Array.Empty<string>();

    /// <summary>
    /// The id an <em>editor</em> knows this language by, when that differs from
    /// <see cref="Id"/>. Three SQL dialects are three languages to the kernel and
    /// one tokenizer to Monaco; this is what lets a client pick a highlighter
    /// without a table of language names in it.
    /// </summary>
    string EditorLanguageId => Id;

    /// <summary>
    /// The code-block tags this language claims in <c>.nb.md</c> / <c>.dib</c>
    /// documents (<c>sql</c>, <c>tsql</c>; <c>bash</c>, <c>zsh</c>…). Parsers and
    /// serializers consult these instead of hard-coding tag tables. Empty when the
    /// language has no tagged-block form.
    /// </summary>
    IReadOnlyList<string> LanguageTags => Array.Empty<string>();

    /// <summary>
    /// What this language adds to the C# scripting session — assemblies, imported
    /// namespaces, and <c>using static</c> lines — so its API is reachable from
    /// <c>#!csharp</c> cells. Null when it adds nothing.
    /// </summary>
    ScriptContribution ScriptContribution { get; }

    /// <summary>
    /// Editor language features for this language, or null when it has none
    /// (a cell language is not obliged to provide completion or diagnostics).
    /// </summary>
    ICellLanguageServices Services { get; }

    /// <summary>
    /// This language's named connections, or null when it has none — HTTP, Mermaid and
    /// PowerShell do not connect to anything. Lets an editor serve a connection UI for any
    /// language without the host referencing that language's package.
    /// </summary>
    IConnectionCatalog Connections { get; }

    /// <summary>Runs a cell that matched one of <see cref="Selectors"/>.</summary>
    Task<object> ExecuteAsync(CellInvocation cell, ICellExecutionContext context);
}

/// <summary>
/// Assemblies and namespaces a package wants available inside C# cells.
/// <para>
/// These are resolved at <b>script-compile</b> time, and the imports are plain
/// strings: a wrong or missing one produces a runtime CS0234 in a cell, never a
/// build error. Any package contributing here should have a test that executes a
/// cell through the engine.
/// </para>
/// </summary>
public sealed class ScriptContribution {
    public ScriptContribution(
        IReadOnlyList<System.Reflection.Assembly> references = null,
        IReadOnlyList<string> imports = null,
        IReadOnlyList<string> usingStatics = null) {
        References = references ?? Array.Empty<System.Reflection.Assembly>();
        Imports = imports ?? Array.Empty<string>();
        UsingStatics = usingStatics ?? Array.Empty<string>();
    }

    /// <summary>Assemblies referenced by the script compilation.</summary>
    public IReadOnlyList<System.Reflection.Assembly> References { get; }

    /// <summary>Namespaces imported into every cell.</summary>
    public IReadOnlyList<string> Imports { get; }

    /// <summary>Full <c>using static ...;</c> lines prepended to the session.</summary>
    public IReadOnlyList<string> UsingStatics { get; }
}

/// <summary>A matched cell, pre-split so languages don't each re-parse it.</summary>
public sealed class CellInvocation {
    public CellInvocation(string selector, string firstLine, string body, string text) {
        Selector = selector;
        FirstLine = firstLine;
        Body = body;
        Text = text;
    }

    /// <summary>The selector that matched, e.g. <c>#!sql-connect</c>.</summary>
    public string Selector { get; }

    /// <summary>The whole first non-blank line, including any arguments.</summary>
    public string FirstLine { get; }

    /// <summary>Everything after the first non-blank line.</summary>
    public string Body { get; }

    /// <summary>The original cell text, for languages that scan every line.</summary>
    public string Text { get; }
}

/// <summary>What a cell language may ask of the running session.</summary>
public interface ICellExecutionContext {
    /// <summary>The notebook's working directory.</summary>
    string WorkingDirectory { get; }

    /// <summary>
    /// Compiles and runs a C# fragment in the session's script state and records
    /// it as a submission, so language services replaying the session see it.
    /// Used by <c>#!sql-connect</c> to bind a connection to a C# variable.
    /// </summary>
    Task RunScriptAsync(string code);
}

/// <summary>
/// The ordered set of cell languages available to an engine.
/// <para>
/// Matching is <b>longest selector first</b>, and a selector only matches when
/// it is followed by whitespace or end-of-line. Both rules matter: several
/// selectors are prefixes of others (<c>#!sql</c> of <c>#!sql-connect</c>,
/// <c>#!sql-bulk</c>, <c>#!sql-merge</c>, <c>#!sql-run</c>, <c>#!sql-deploy</c>;
/// <c>#!dax</c> of <c>#!dax-connect</c>), and getting it wrong routes a cell to
/// the wrong handler. Because ordering is derived here from selector length
/// rather than from registration order, registering in any order is safe.
/// See CellSelectorOrderingTest.
/// </para>
/// </summary>
public sealed class CellLanguageRegistry {
    private readonly List<Func<IReadOnlyList<ICellLanguage>>> _factories;

    /// <summary>A registry with no languages: cells run as C# only.</summary>
    public static CellLanguageRegistry Empty { get; } =
        new CellLanguageRegistry(Array.Empty<Func<ICellLanguage>>());

    /// <summary>
    /// The registry used by engines constructed without an explicit one. Set
    /// once by the composition root (the CLI, or a test fixture) before any
    /// engine is created.
    /// </summary>
    public static CellLanguageRegistry Default { get; set; } = Empty;

    /// <summary>
    /// Script contributions from packages that are not cell languages (e.g. the
    /// Fabric provider). Set by the composition root alongside
    /// <see cref="Default"/>.
    /// </summary>
    public static IReadOnlyList<ScriptContribution> DefaultContributions { get; set; }
        = Array.Empty<ScriptContribution>();

    /// <param name="factories">
    /// One factory per language. Factories rather than instances: a cell
    /// language owns per-notebook state (connection registries, a PowerShell
    /// runspace), so every engine must get its own set. Sharing instances across
    /// engines leaks one notebook's connections into another.
    /// </param>
    public CellLanguageRegistry(IEnumerable<Func<ICellLanguage>> factories)
        : this((factories ?? Array.Empty<Func<ICellLanguage>>())
            .Where(f => f != null)
            .Select<Func<ICellLanguage>, Func<IReadOnlyList<ICellLanguage>>>(
                f => () => new[] { f() })) {
    }

    /// <param name="families">
    /// One factory per <em>family</em> of languages that share per-notebook state.
    /// <para>
    /// The SQL dialects are why this exists: T-SQL, Oracle SQL and generic SQL are
    /// three languages and one set of connections. A connection is a property of
    /// the notebook, not of the dialect that happens to name it — declare it once
    /// and every dialect resolves it, and a name means one thing in a notebook
    /// rather than one thing per dialect. That requires them to share a session,
    /// and a session must not outlive its engine, so the sharing has to happen
    /// inside the factory call rather than in a variable the factories close over.
    /// </para>
    /// </param>
    public CellLanguageRegistry(IEnumerable<Func<IReadOnlyList<ICellLanguage>>> families) {
        _factories = (families ?? Array.Empty<Func<IReadOnlyList<ICellLanguage>>>())
            .Where(f => f != null).ToList();
    }

    /// <summary>Builds a fresh set of language instances for one engine.</summary>
    public CellLanguageSet CreateSet() =>
        new CellLanguageSet(_factories
            .SelectMany(f => f() ?? Array.Empty<ICellLanguage>())
            .Where(l => l != null));

}

/// <summary>
/// One engine's cell languages: the instances plus the selector table.
/// <para>
/// Matching is <b>longest selector first</b>, and a selector only matches when
/// it is followed by whitespace or end-of-line. Both rules matter: several
/// selectors are prefixes of others (<c>#!sql</c> of <c>#!sql-connect</c>,
/// <c>#!sql-bulk</c>, <c>#!sql-merge</c>, <c>#!sql-run</c>, <c>#!sql-deploy</c>;
/// <c>#!dax</c> of <c>#!dax-connect</c>). Because the order is derived here from
/// selector length rather than from registration order, registering in any order
/// is safe. See CellSelectorOrderingTest.
/// </para>
/// </summary>
public sealed class CellLanguageSet {
    private readonly List<(string Selector, ICellLanguage Language)> _bySelector;
    private readonly List<ICellLanguage> _languages;

    public CellLanguageSet(IEnumerable<ICellLanguage> languages) {
        _languages = (languages ?? Array.Empty<ICellLanguage>()).Where(l => l != null).ToList();
        _bySelector = new List<(string, ICellLanguage)>();
        RebuildSelectorTable();
    }

    /// <summary>
    /// Adds a language to this set at run time (a plugin loaded mid-session) and
    /// re-derives the selector table, so longest-first dispatch keeps holding no
    /// matter when a language arrived. Registration-order independence is the
    /// same guarantee the constructor gives.
    /// </summary>
    public void Add(ICellLanguage language) {
        if (language == null) {
            return;
        }
        _languages.Add(language);
        RebuildSelectorTable();
    }

    private void RebuildSelectorTable() {
        _bySelector.Clear();
        _bySelector.AddRange(_languages
            .SelectMany(l => (l.Selectors ?? Array.Empty<string>()).Select(s => (Selector: s, Language: l)))
            .OrderByDescending(p => p.Selector.Length)
            .ThenBy(p => p.Selector, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>This engine's languages, in registration order.</summary>
    public IReadOnlyList<ICellLanguage> Languages => _languages;


    /// <summary>The registered language with this id, or null.</summary>
    public ICellLanguage ById(string id) =>
        _languages.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The single registered language of type <typeparamref name="T"/>, or null.</summary>
    public T Get<T>() where T : class, ICellLanguage => _languages.OfType<T>().FirstOrDefault();

    /// <summary>Every registered language's script contribution.</summary>
    public IEnumerable<ScriptContribution> ScriptContributions =>
        _languages.Select(l => l.ScriptContribution).Where(c => c != null);

    /// <summary>Wire descriptors for every registered language, in registration order.</summary>
    public IReadOnlyList<LanguageDescriptor> Describe() =>
        _languages.Select(LanguageDescriptor.From).ToList();

    /// <summary>
    /// Routes a cell, or returns null when no selector matches (the cell is C#).
    /// </summary>
    public CellMatch Match(string cellText) {
        if (string.IsNullOrEmpty(cellText)) {
            return null;
        }

        var normalized = cellText.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0) {
            index++;
        }
        if (index >= lines.Length) {
            return null;
        }

        var firstLine = lines[index].Trim();
        foreach (var (selector, language) in _bySelector) {
            if (!firstLine.StartsWith(selector, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            // The selector must be a whole token: "#!sql" must not claim
            // "#!sqlfoo" (and, with longest-first above, never "#!sql-bulk").
            if (firstLine.Length > selector.Length && !char.IsWhiteSpace(firstLine[selector.Length])) {
                continue;
            }
            var body = string.Join("\n", lines, index + 1, lines.Length - index - 1);
            return new CellMatch(language, new CellInvocation(selector, firstLine, body, normalized));
        }
        return null;
    }
}

/// <summary>A routed cell: which language answers it, and the split-up cell.</summary>
public sealed class CellMatch {
    public CellMatch(ICellLanguage language, CellInvocation cell) {
        Language = language;
        Cell = cell;
    }

    public ICellLanguage Language { get; }
    public CellInvocation Cell { get; }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using Dotnet.Script.DependencyModel.Context;
using Dotnet.Script.DependencyModel.NuGet;
using Dotnet.Script.DependencyModel.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Scripting.Hosting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using Microsoft.Extensions.Logging;
using LogFactory = Dotnet.Script.DependencyModel.Logging.LogFactory;
using ScriptLogLevel = Dotnet.Script.DependencyModel.Logging.LogLevel;

namespace ClrKernel.Core.Scripting;

public class InteractiveScriptEngine : ICellExecutionContext, ICellVariables {
    private ScriptState<object> _scriptState;

    private ScriptOptions _scriptOptions;

    private InteractiveScriptGlobals _globals;

    private StringBuilder _interactiveOutput;

    private RuntimeDependencyResolver _runtimeDependencyResolver;

    private ILogger _logger;

    private string _currentDirectory;

    // Where Dotnet.Script generates its NuGet restore scratch projects: the
    // directory its ScriptProjectProvider is constructed with, under the system
    // temp so no dotnet-script/ tree lands in a user's folder. The directory
    // handed to GetDependenciesForCode is a different thing — it is mirrored
    // *underneath* this one, and it is where Dotnet.Script looks for the nearest
    // NuGet.Config. That call gets the notebook's directory, so a NuGet.Config in
    // the notebook's folder or any folder above it is the one restore sees.
    //
    // Dotnet.Script passes that file as --configfile, which NuGet treats as the
    // whole configuration — the user-level config and its nuget.org would be
    // gone. So when there is a repo config, the restore is run here first, the
    // way `dotnet restore` in a repo would run it (see PreRestoreWithRepoConfig),
    // and Dotnet.Script's own restore then finds every package already in the
    // global packages folder, which NuGet consults before any source.
    private readonly string _dependencyScratchDirectory;
    private readonly Dotnet.Script.DependencyModel.ProjectSystem.ScriptProjectProvider _projectProvider;

    private string[] _references;

    // `#r "project:"` builds, one set per session: which output is loaded for
    // each project, so a re-run can replace it. Build output goes to Console.Out
    // — the cell's stream, once a front has proxied it — as it arrives.
    private readonly ProjectReferences _projects = new(line => Console.Out.WriteLine(line));

    // Ordered, successfully-compiled submissions (the initial usings preamble
    // then each executed cell). Language services replay these to reconstruct
    // the script context for completion without re-running anything.
    private readonly List<string> _submissions = new();

    // Default using-static directives that expose the cell-callable helpers
    // (GetVariable, HTML, Display, ...). Always handed to language services as a
    // preamble so completion offers them even before the first cell has run.
    private static readonly string[] _builtInUsingStatics = {
        "using static ClrKernel.Core.Scripting.Extensions;",
    };

    // The built-ins plus whatever the registered languages contribute (e.g. the
    // Sql package's `using static ClrKernel.Language.Sql.SqlGlobals;`).
    private readonly string[] _usingStatics;

    private readonly CellLanguageSet _languages;

    private readonly List<ScriptContribution> _contributions;

    /// <summary>The using-static preamble handed to language services.</summary>
    internal string[] DefaultUsingStatics => _usingStatics;

    /// <summary>The cell languages this session dispatches to.</summary>
    public CellLanguageSet Languages => _languages;

    private readonly List<Core.Primitives.ConnectionProviderDescriptor> _connectionProviders;

    /// <summary>This session's connection-provider descriptors (built-ins plus any
    /// registered by a package loaded mid-session).</summary>
    public IReadOnlyList<Core.Primitives.ConnectionProviderDescriptor> ConnectionProviders => _connectionProviders;

    /// <summary>The providers a language's connection UI should offer.</summary>
    public IEnumerable<Core.Primitives.ConnectionProviderDescriptor> ConnectionProvidersFor(string languageId) =>
        _connectionProviders.FindAll(p =>
            p.LanguageIds.Any(id => string.Equals(id, languageId, StringComparison.OrdinalIgnoreCase)));

    public static string RefsFilePath { get; set; }

    /// <summary>
    /// The engine for the running kernel session; lets cell-callable helpers
    /// (e.g. GetVariable in Extensions) reach the current script state.
    /// </summary>
    public static InteractiveScriptEngine Current { get; private set; }

    /// <summary>
    /// Returns the value of a session variable (defined by an earlier cell —
    /// including papermill/dotnet-repl injected parameter cells), or null.
    /// </summary>
    public object GetVariableValue(string name) {
        return _scriptState?.GetVariable(name)?.Value;
    }

    /// <summary>
    /// An immutable snapshot of the accumulated script context — resolved
    /// references, imported namespaces, and prior submissions — for building a
    /// parallel Roslyn workspace (completion/hover/signature help) in lockstep
    /// with execution. Copied so language services never observe a half-applied
    /// <c>#r</c> resolution.
    /// </summary>
    public ScriptStateSnapshot SnapshotState() =>
        new(_scriptOptions.MetadataReferences.ToArray(),
            _scriptOptions.Imports.ToArray(),
            _submissions.ToArray(),
            string.Join("\n", DefaultUsingStatics));

    public InteractiveScriptEngine(string currentDir, ILogger logger)
        : this(currentDir, logger, null, CellLanguageRegistry.DefaultContributions) { }

    /// <param name="currentDir">Working directory for relative paths (#load, #!import).</param>
    /// <param name="logger">Engine log sink; goes to stderr in the server hosts.</param>
    /// <param name="languages">
    /// Cell languages available to this session; null uses
    /// <see cref="CellLanguageRegistry.Default"/>, set by the composition root.
    /// </param>
    /// <param name="extraContributions">
    /// Script contributions from packages that are reachable from C# cells but
    /// own no #! selector (e.g. the Fabric provider).
    /// </param>
    public InteractiveScriptEngine(
        string currentDir,
        ILogger logger,
        CellLanguageRegistry languages,
        IEnumerable<ScriptContribution> extraContributions) {
        Current = this;
        _languages = (languages ?? CellLanguageRegistry.Default).CreateSet();
        _connectionProviders = new List<Core.Primitives.ConnectionProviderDescriptor>(ConnectionProviderRegistry.Default);
        // #!import routes non-C# blocks with this session's own language set, so
        // languages added mid-session are honored inside imported files too.
        _importer.Languages = () => _languages.Describe();
        _contributions = _languages.ScriptContributions
            .Concat(extraContributions ?? Enumerable.Empty<ScriptContribution>())
            .ToList();
        _usingStatics = _builtInUsingStatics
            .Concat(_contributions.SelectMany(c => c.UsingStatics))
            .ToArray();
        _currentDirectory = currentDir;
        // The secrets this kernel was started with are secrets whether or not a
        // cell goes through the store for them — Environment.GetEnvironmentVariable
        // and `#!bash env` print the same value.
        SecretRedaction.SeedFromEnvironment();
        _dependencyScratchDirectory = Path.Combine(Path.GetTempPath(), "clrkernel", "restore");
        Directory.CreateDirectory(_dependencyScratchDirectory);
        _logger = logger;
        _scriptOptions = CreateScriptOptions();

        var referencesFile = RefsFilePath;

        if (!string.IsNullOrEmpty(referencesFile) && File.Exists(referencesFile)) {
            _references = File.ReadAllLines(referencesFile, Encoding.UTF8);
        }

        LogFactory logFactory = (t) => (level, m, e) => {
            logger.Log(MapLogLevel(level), m, e);
        };
        _projectProvider = new Dotnet.Script.DependencyModel.ProjectSystem.ScriptProjectProvider(logFactory, _dependencyScratchDirectory);
        _runtimeDependencyResolver = new RuntimeDependencyResolver(_projectProvider, logFactory, true);

        _interactiveOutput = new StringBuilder();
        _globals = new InteractiveScriptGlobals(new StringWriter(_interactiveOutput), CSharpObjectFormatter.Instance);
    }

    private LogLevel MapLogLevel(ScriptLogLevel logLevel) {
        switch (logLevel) {
            case (ScriptLogLevel.Critical):
                return LogLevel.Critical;
            case (ScriptLogLevel.Error):
                return LogLevel.Error;
            case (ScriptLogLevel.Warning):
                return LogLevel.Warning;
            case (ScriptLogLevel.Trace):
                return LogLevel.Trace;
            case (ScriptLogLevel.Debug):
                return LogLevel.Debug;
            default:
                return LogLevel.Information;
        }
    }

    private readonly NotebookImporter _importer = new();

    /// <summary>
    /// Executes a cell. Lines holding a #!import directive are handled by the
    /// kernel (loading the referenced .dib/.ipynb/.csx/.cs into this session's
    /// script state); everything else is compiled as C# script. A cell may mix
    /// directives and code — segments run in order and the last segment's value
    /// is the cell result.
    /// </summary>
    private static bool IsImporterDirective(string line) =>
        NotebookImporter.TryParseRegister(line, out _, out _) ||
        NotebookImporter.TryParseDirective(line, out _, out _);

    public async Task<object> ExecuteAsync(string statement) {
        // #!share lines come out first: they hand a value from one language's
        // session to this cell's, and the cell then runs as if it had been there.
        var (shared, shares) = ShareDirective.Extract(statement);
        if (shares.Count > 0) {
            statement = shared;
            await ApplySharesAsync(statement, shares).ConfigureAwait(false);
        }

        // A #! selector routes the cell to a registered language. The registry
        // matches longest-selector-first, so #!sql-connect can never be swallowed
        // by #!sql (see CellSelectorOrderingTest).
        var match = _languages.Match(statement);
        if (match != null) {
            var languageResult = await match.Language.ExecuteAsync(match.Cell, this).ConfigureAwait(false);
            // Languages and providers return display concepts; the wire bundle is
            // built here so they never touch a MIME type.
            // A raw value — an F# cell's trailing expression, a KQL DataTable — takes
            // the same road a C# trailing value does; the fronts only render bundles.
            return languageResult switch {
                null => null,
                DisplayData ready => ready,
                IDisplayValue concept => MimeBundler.Bundle(concept),
                _ => MimeBundler.Bundle(new DisplayObject(languageResult)),
            };
        }

        if (!statement.Split('\n').Any(IsImporterDirective)) {
            return await ExecuteCoreAsync(statement);
        }

        object result = null;
        var buffer = new StringBuilder();

        async Task FlushAsync() {
            var code = buffer.ToString();
            buffer.Clear();
            if (code.Trim().Length > 0) {
                result = await ExecuteCoreAsync(code);
            }
        }

        foreach (var line in statement.Replace("\r\n", "\n").Split('\n')) {
            if (NotebookImporter.TryParseRegister(line, out var prefixName, out var prefixPath)) {
                await FlushAsync();
                _importer.RegisterPrefix(prefixName, prefixPath);
                _logger.LogInformation($"#!import: registered prefix '{prefixName}' -> {_importer.ResolvePath(prefixName + "://")}");
                result = null;
            } else if (NotebookImporter.TryParseDirective(line, out var path, out var force)) {
                await FlushAsync();
                var loaded = await _importer.ImportAsync(path, force, block => ExecuteAsync(block));
                _logger.LogInformation(loaded
                    ? $"#!import: loaded {_importer.ResolvePath(path)}"
                    : $"#!import: already loaded, skipped (use --force to rerun): {_importer.ResolvePath(path)}");
                result = null;
            } else {
                buffer.AppendLine(line);
            }
        }
        await FlushAsync();

        return result;
    }

    /// <summary>The notebook's working directory (ICellExecutionContext).</summary>
    public string WorkingDirectory => _currentDirectory;

    /// <summary>
    /// Runs a C# fragment in the session's script state and records it as a
    /// submission (ICellExecutionContext), so language services replaying the
    /// session see it. Used by #!sql-connect to bind a connection variable.
    /// </summary>
    public async Task RunScriptAsync(string code) {
        await EnsureScriptStateAsync().ConfigureAwait(false);
        _scriptState = await _scriptState.ContinueWithAsync(code, _scriptOptions).ConfigureAwait(false);
        _submissions.Add(code);
    }

    // --- #!share ---------------------------------------------------------------

    private async Task ApplySharesAsync(string statement, IReadOnlyList<ShareDirective> shares) {
        var match = _languages.Match(statement);
        var target = match == null ? this : match.Language as ICellVariables
            ?? throw new InvalidOperationException(
                $"#!share: {match.Language.DisplayName} cells cannot receive variables.");
        foreach (var share in shares) {
            var value = FetchShared(share);
            if (target == this) {
                await SetVariableAsync(share.As, value).ConfigureAwait(false);
            } else {
                target.SetVariable(share.As, value);
            }
        }
    }

    private object FetchShared(ShareDirective share) {
        ICellVariables source;
        string sourceName;
        if (ShareDirective.IsCSharp(share.From)) {
            source = this;
            sourceName = "C#";
        } else {
            var language = _languages.ById(share.From)
                ?? _languages.Languages.FirstOrDefault(l => l.LanguageTags.Contains(share.From, StringComparer.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"#!share: no language '{share.From}' in this session.");
            source = language as ICellVariables
                ?? throw new InvalidOperationException($"#!share: {language.DisplayName} cells have no variables to share.");
            sourceName = language.DisplayName;
        }
        if (!source.TryGetVariable(share.Name, out var value)) {
            throw new InvalidOperationException($"#!share: {sourceName} has no variable '{share.Name}'.");
        }
        return value;
    }

    /// <summary>The C# script state's variable of that name (the latest binding), if any (ICellVariables).</summary>
    public bool TryGetVariable(string name, out object value) {
        var variable = _scriptState?.Variables.LastOrDefault(v => v.Name == name);
        value = variable?.Value;
        return variable != null;
    }

    /// <summary>Binds a value in the C# script state; a submission, so it needs the async form.</summary>
    void ICellVariables.SetVariable(string name, object value) =>
        SetVariableAsync(name, value).GetAwaiter().GetResult();

    /// <summary>
    /// Gives <paramref name="value"/> a name in the C# script state: put on the
    /// shelf, taken back by a <c>var name = (T)…</c> submission — the one way a
    /// live object enters a Roslyn script. The static type is the value's runtime
    /// type where the script can spell it, so members complete afterwards.
    /// </summary>
    public async Task SetVariableAsync(string name, object value) {
        if (!Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name)) {
            throw new ArgumentException($"'{name}' is not a valid C# identifier.", nameof(name));
        }
        var key = SharedValues.Put(value);
        if (value != null) {
            EnsureReferenced(value.GetType());
        }
        var type = ShareDirective.CSharpTypeName(value?.GetType());
        await RunScriptAsync(
            $"{type} {name} = ({type})global::ClrKernel.Core.Primitives.SharedValues.Take(\"{key}\");").ConfigureAwait(false);
    }

    // The assemblies a shared value's type is made of — FSharp.Core for an F# list
    // — must be references before the script can spell the type.
    private void EnsureReferenced(Type type) {
        if (type == null) {
            return;
        }
        if (type.IsArray) {
            EnsureReferenced(type.GetElementType());
            return;
        }
        foreach (var argument in type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes) {
            EnsureReferenced(argument);
        }
        var location = type.Assembly.IsDynamic ? null : type.Assembly.Location;
        if (string.IsNullOrEmpty(location)
            || _scriptOptions.MetadataReferences.Any(r => string.Equals(r.Display, location, StringComparison.OrdinalIgnoreCase))) {
            return;
        }
        _scriptOptions = _scriptOptions.AddReferences(MetadataReference.CreateFromFile(location));
    }

    private async Task<object> ExecuteCoreAsync(string statement) {
        statement = PrepareStatement(statement);

        await EnsureScriptStateAsync();
        // A plugin registered by the #r above may have contributed using-static
        // lines; they run first so the statement itself can already use them.
        if (_pendingPluginPrelude.Count > 0) {
            foreach (var prelude in _pendingPluginPrelude) {
                _scriptState = await _scriptState.ContinueWithAsync(prelude, _scriptOptions);
                _submissions.Add(prelude);
            }
            _pendingPluginPrelude.Clear();
        }
        try {
            _scriptState = await _scriptState.ContinueWithAsync(statement, _scriptOptions);
        } catch (CompilationErrorException e) when (e.Diagnostics.Any(d => d.Id is "CS1109" or "CS7021")) {
            // CS1109: script mode nests a cell's classes inside the submission
            // type, which makes `this` extension methods illegal. CS7021: a
            // namespace declaration is illegal in script code at all. Compile
            // the cell as a real class library instead — project mode — and
            // reference it, so its types work everywhere.
            CompileCellAsLibrary(statement);
            return null;
        }

        // Record the successfully-compiled submission (this line is only reached
        // when the submission compiled and ran) so language services can rebuild
        // the same script context for completion/hover/signature help.
        _submissions.Add(statement);

        if (_scriptState.ReturnValue == null) {
            return null;
        }

        var value = _scriptState.ReturnValue;
        if (value is DisplayData displayData) {
            return displayData;
        }

        // A display handle is a structure whose content is already on screen —
        // formatting it would print the handle after the value (the old
        // trailing-Display() bug).
        if (value is DisplayCell) {
            return null;
        }

        // Rich default rendering through the formatter registry — the same path
        // Display(value) takes, so a trailing value and Display() render
        // identically (sequences as tables, objects as property tables, a
        // type-hint badge; see ClrKernel.Formatting.Html).
        return MimeBundler.Bundle(value as IDisplayValue ?? new DisplayObject(value));
    }

    public object Execute(string statement) {
        return ExecuteAsync(statement).Result;
    }

    // --- Cells compiled as class libraries (extension methods) --------------

    /// <summary>File-name prefix of assemblies emitted from notebook cells, so
    /// tooling can tell a superseded cell library from a live one.</summary>
    public const string CellLibraryPrefix = "clrkernel-cell-";

    // Which emitted library currently declares each cell-defined type name
    // (namespace-qualified); re-declaring a type swaps the old library out of
    // the references. _cellLibraryImports remembers the namespaces each library
    // contributed to the session imports, so they leave with it.
    private readonly Dictionary<string, MetadataReference> _cellLibraryByType = new();
    private readonly Dictionary<MetadataReference, string[]> _cellLibraryImports = new();
    private System.Runtime.Loader.AssemblyLoadContext _cellLoadContext;

    /// <summary>
    /// True for a line that is a script directive (<c>#r</c>, <c>#load</c>,
    /// <c>#i</c>) or a cell magic (<c>#!…</c>) rather than C# code.
    /// </summary>
    public static bool IsDirectiveLine(string line) {
        var t = line.TrimStart();
        return t.StartsWith("#r", StringComparison.Ordinal)
            || t.StartsWith("#i", StringComparison.Ordinal)
            || t.StartsWith("#load", StringComparison.Ordinal)
            || t.StartsWith("#!", StringComparison.Ordinal);
    }

    /// <summary>Drops directive lines, keeping everything else (usings, code, classes).</summary>
    public static string StripDirectives(string submission) {
        if (submission.IndexOf('#') < 0) {
            return submission;
        }
        return string.Join("\n", submission.Replace("\r\n", "\n").Split('\n').Where(l => !IsDirectiveLine(l)));
    }

    /// <summary>
    /// The reference file paths execution and IntelliSense share: file-backed
    /// loaded assemblies (minus superseded cell libraries) overlaid by the
    /// engine's explicit references. Keyed by assembly FILE NAME, not path — the
    /// same assembly loaded from two places would otherwise put duplicate
    /// identities into a compilation, and symbol resolution for every type it
    /// touches silently fails. The engine's references WIN: they are the
    /// versions cell execution actually uses.
    /// </summary>
    public static IEnumerable<string> ReferencePaths(IEnumerable<MetadataReference> engineReferences) {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) {
                continue;
            }
            var file = Path.GetFileName(assembly.Location);
            // Cell-compiled libraries stay loaded after being superseded by a
            // re-run; only the engine's references know the live one.
            if (file.StartsWith(CellLibraryPrefix, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            byName.TryAdd(file, assembly.Location);
        }
        foreach (var reference in engineReferences) {
            if (reference is PortableExecutableReference pe && !string.IsNullOrEmpty(pe.FilePath)) {
                byName[Path.GetFileName(pe.FilePath)] = pe.FilePath;
            }
        }
        return byName.Values;
    }

    private static bool IsLoadedType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies().Any(a => {
            try {
                return !a.IsDynamic && a.GetType(fullName, throwOnError: false) != null;
            } catch {
                return false;
            }
        });

    private void CompileCellAsLibrary(string code) {
        // #r/#load lines were already resolved by the script path; in the
        // regular-mode parse below they'd be illegal (CS7010), so drop them.
        code = StripDirectives(code);
        var parseOptions = new CSharpParseOptions(languageVersion: LanguageVersion.Preview);
        var cellRoot = CSharpSyntaxTree.ParseText(code, parseOptions).GetCompilationUnitRoot();
        if (cellRoot.Members.Any(m => m is GlobalStatementSyntax)) {
            throw new InvalidOperationException(
                "Extension methods are compiled as a class library, and that only works for a cell " +
                "containing nothing but type declarations (and usings). Put the static class in a cell of its own.");
        }

        // The session's imports, so the cell's types see what cells see. Script
        // imports may name a TYPE (host-style `using Console;`), which regular
        // C# spells `using static`.
        var source = string.Join("\n", _scriptOptions.Imports.Select(i =>
                (IsLoadedType(i) ? "using static " : "using ") + i + ";"))
            + "\n" + code;
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);

        // Same reference surface as execution — the shared dedupe policy.
        var referencePaths = ReferencePaths(_scriptOptions.MetadataReferences).ToList();

        // Content-hashed assembly name over source AND references: an edited
        // cell (or the same cell against different package versions) gets a NEW
        // identity, and an unchanged re-run reuses the already-emitted file.
        string hash;
        using (var sha = System.Security.Cryptography.SHA256.Create()) {
            var cacheKey = source + "\n" + string.Join("\n", referencePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(cacheKey))).Substring(0, 12);
        }
        var name = CellLibraryPrefix + hash;
        var directory = Path.Combine(Path.GetTempPath(), "clrkernel", "cell-libraries");
        Directory.CreateDirectory(directory);
        var dllPath = Path.Combine(directory, name + ".dll");

        if (!File.Exists(dllPath)) {
            var compilation = CSharpCompilation.Create(name, new[] { tree },
                referencePaths.Select(p => MetadataReference.CreateFromFile(p)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            using var pe = new MemoryStream();
            using var xml = new MemoryStream();
            var emit = compilation.Emit(pe, xmlDocumentationStream: xml);
            if (!emit.Success) {
                throw new InvalidOperationException("The cell failed to compile as a class library:\n" + string.Join("\n",
                    emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())));
            }
            // The XML docs beside the dll give hover/completion the /// summaries.
            // Docs first, dll moved into place last: the dll's existence is the
            // cache check, so a torn write (kernel killed mid-emit, concurrent
            // kernel racing the same path) is never mistaken for a complete one.
            File.WriteAllBytes(Path.ChangeExtension(dllPath, ".xml"), xml.ToArray());
            var staging = dllPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(staging, pe.ToArray());
            File.Move(staging, dllPath, overwrite: true);
        }

        // Cell libraries load into their own context. A library's dependency (a
        // #r'd package, say) can be a NEWER version of an assembly the kernel
        // itself ships — the default context's simple-name slot is then taken by
        // the kernel's copy (TPA) and can never satisfy the newer request. The
        // last-chance hook loads such dependencies here from the engine's
        // reference paths; framework and kernel-shipped assemblies always unify
        // with the default context (a second copy would split type identity).
        if (_cellLoadContext == null) {
            _cellLoadContext = new System.Runtime.Loader.AssemblyLoadContext("clrkernel-cell-libraries");
            var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty;
            var appBase = AppContext.BaseDirectory ?? string.Empty;
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) => {
                var requested = new AssemblyName(args.Name).Name;
                var path = ReferencePaths(_scriptOptions.MetadataReferences).FirstOrDefault(p =>
                    Path.GetFileNameWithoutExtension(p).Equals(requested, StringComparison.OrdinalIgnoreCase));
                if (path == null
                    || path.StartsWith(frameworkDir, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(appBase, StringComparison.OrdinalIgnoreCase)) {
                    return null;
                }
                return _cellLoadContext.LoadFromAssemblyPath(path);
            };
        }

        _cellLoadContext.LoadFromAssemblyPath(dllPath);
        var newReference = MetadataReference.CreateFromFile(dllPath);

        // Namespace-qualified names of the cell's TOP-LEVEL types only:
        // descending into type bodies would let unrelated cells' nested helper
        // names (Options, Builder, …) evict each other's libraries.
        var typeNames = DeclaredTypeNames(cellRoot.Members, "").Distinct().ToList();
        var superseded = typeNames
            .Where(_cellLibraryByType.ContainsKey)
            .Select(t => _cellLibraryByType[t])
            .Distinct()
            .ToList();
        _scriptOptions = _scriptOptions.WithReferences(
            _scriptOptions.MetadataReferences.Where(r => !superseded.Contains(r)).Append(newReference));
        // Dropping a library orphans every OTHER name it declared too; clear
        // those map entries so they don't point at a dead reference.
        foreach (var stale in _cellLibraryByType.Where(kv => superseded.Contains(kv.Value)).Select(kv => kv.Key).ToList()) {
            _cellLibraryByType.Remove(stale);
        }
        foreach (var typeName in typeNames) {
            _cellLibraryByType[typeName] = newReference;
        }

        // Namespaces declared in the cell become session imports, so other
        // cells use the types unqualified — and a superseded library's imports
        // leave with it, because an import no live library resolves any more
        // would fail every later submission until a kernel restart.
        var declared = cellRoot.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
            .Select(n => n.Name.ToString()).Distinct().ToList();
        var alive = _cellLibraryImports.Where(kv => !superseded.Contains(kv.Key))
            .SelectMany(kv => kv.Value).Concat(declared).ToHashSet();
        var removedImports = superseded
            .SelectMany(r => _cellLibraryImports.TryGetValue(r, out var imports) ? imports : Array.Empty<string>())
            .Where(ns => !alive.Contains(ns))
            .Distinct()
            .ToList();
        foreach (var reference in superseded) {
            _cellLibraryImports.Remove(reference);
        }
        _cellLibraryImports[newReference] = declared.ToArray();
        if (removedImports.Count > 0) {
            _scriptOptions = _scriptOptions.WithImports(_scriptOptions.Imports.Where(i => !removedImports.Contains(i)));
        }
        foreach (var ns in declared) {
            if (!_scriptOptions.Imports.Contains(ns)) {
                _scriptOptions = _scriptOptions.AddImports(ns);
            }
        }

        _logger.LogInformation(
            $"cell compiled as class library {name}.dll ({string.Join(", ", typeNames)})");
    }

    private static IEnumerable<string> DeclaredTypeNames(IEnumerable<MemberDeclarationSyntax> members, string prefix) {
        foreach (var member in members) {
            switch (member) {
                case BaseNamespaceDeclarationSyntax ns:
                    foreach (var nested in DeclaredTypeNames(ns.Members, prefix + ns.Name + ".")) {
                        yield return nested;
                    }
                    break;
                case BaseTypeDeclarationSyntax type:
                    yield return prefix + type.Identifier.Text;
                    break;
                case DelegateDeclarationSyntax d:
                    yield return prefix + d.Identifier.Text;
                    break;
            }
        }
    }

    // Initializes the persistent C# script state (default usings + any #r/#load
    // references) once, on first use. Shared by #!csharp cells and by side-effect
    // submissions such as the variable bound after #!sql-connect.
    private async Task EnsureScriptStateAsync() {
        if (_scriptState != null) {
            return;
        }
        string[] usingStatements = DefaultUsingStatics;
        var references = _references;
        if (references != null && references.Any()) {
            foreach (var line in references) {
                if (line.StartsWith("#r ") || line.StartsWith("#load ")) {
                    TryLoadReferenceFromScript(line);
                }
            }
            usingStatements = references.Union(usingStatements).ToArray();
        }
        _scriptState = await CSharpScript.RunAsync(string.Join("\r\n", usingStatements), _scriptOptions, globals: _globals);
    }

    // A C# string literal for an arbitrary value (quotes/backslashes escaped).
    private static string CSharpStringLiteral(string value) =>
        "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private bool TryLoadReferenceFromScript(string statement) {
        // A #r / #load / #i (nuget source) directive can appear on any line of a
        // cell (e.g. after a comment); the resolver parses the full code, so only
        // skip resolution when no line carries a directive at all.
        var hasReferenceDirective = statement.Split('\n').Any(line => {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("#r ") || trimmed.StartsWith("#load ") || trimmed.StartsWith("#i ");
        });
        if (!hasReferenceDirective) {
            return false;
        }

        if (statement.Contains("nuget:", StringComparison.OrdinalIgnoreCase)) {
            PreRestoreWithRepoConfig(statement);
        }
        // The notebook's directory, for the NuGet.Config nearest to it; the scratch
        // project itself still lands under the temp root (see the field's note).
        var lineRuntimeDependencies = _runtimeDependencyResolver.GetDependenciesForCode(_currentDirectory, ScriptMode.REPL, new string[0], statement);
        var lineDependencies = lineRuntimeDependencies.SelectMany(rtd => rtd.Assemblies).Distinct();
        var scriptMap = lineRuntimeDependencies.ToDictionary(rdt => rdt.Name, rdt => rdt.Scripts);

        if (scriptMap.Count > 0) {
            _scriptOptions =
                _scriptOptions.WithSourceResolver(
                    new NuGetSourceReferenceResolver(
                        new SourceFileResolver(ImmutableArray<string>.Empty, _currentDirectory), scriptMap));
        }

        foreach (var runtimeDependency in lineDependencies) {
            _logger.LogDebug("Adding reference to a runtime dependency => " + runtimeDependency);
            _scriptOptions = _scriptOptions.AddReferences(MetadataReference.CreateFromFile(runtimeDependency.Path));
            ScanForPlugins(runtimeDependency.Path);
        }

        // A direct `#r "path.dll"` is resolved by Roslyn's metadata resolver, not
        // the nuget resolver above — scan those for plugin exports too.
        foreach (var line in statement.Split('\n')) {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("#r ", StringComparison.Ordinal)) {
                continue;
            }
            var argument = trimmed.Substring(3).Trim().Trim('"');
            if (!argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            var path = Path.IsPathRooted(argument) ? argument : Path.Combine(_currentDirectory, argument);
            if (File.Exists(path)) {
                ScanForPlugins(path);
            }
        }

        return true;
    }

    /// <summary>
    /// The nearest <c>NuGet.Config</c> from the notebook's directory upward, or
    /// null. By name, case-insensitively, which is how NuGet finds it.
    /// </summary>
    private string RepoNuGetConfig() {
        for (var directory = _currentDirectory; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory)) {
            if (!Directory.Exists(directory)) {
                continue;
            }
            var found = Directory.EnumerateFiles(directory)
                .FirstOrDefault(f => Path.GetFileName(f).Equals("nuget.config", StringComparison.OrdinalIgnoreCase));
            if (found != null) {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Restores the cell's packages the way <c>dotnet restore</c> in a repo would,
    /// before Dotnet.Script restores them its way.
    ///
    /// <para>
    /// Dotnet.Script hands the nearest NuGet.Config to restore as
    /// <c>--configfile</c>, and NuGet treats that file as the whole configuration:
    /// the user-level config is not merged, so a repo file that names only a
    /// private feed loses nuget.org — and every dependency the private package
    /// has there. That is not what a NuGet.Config means anywhere else. A repo
    /// config adds to the user's, and says <c>&lt;clear/&gt;</c> when it means
    /// "only these".
    /// </para>
    /// <para>
    /// There is no hook to change what Dotnet.Script passes, so the fix is to make
    /// its restore moot: generate the same project it will (same path — the REPL
    /// subfolder is what <c>GetDependenciesForCode</c> adds), restore it with
    /// <c>RestoreRootConfigDirectory</c> at the notebook's folder, which is
    /// NuGet's own hierarchical discovery from there, and let every package land
    /// in the global packages folder. NuGet consults that folder before any
    /// source, so the exclusive restore that follows finds all of it and asks no
    /// feed for anything. Skipped when there is no repo config: then the two
    /// restores would see the same file, and the second is the only one needed.
    /// </para>
    /// </summary>
    private void PreRestoreWithRepoConfig(string statement) {
        var config = RepoNuGetConfig();
        if (config == null) {
            return;
        }
        var environment = Dotnet.Script.DependencyModel.Environment.ScriptEnvironment.Default;
        var project = _projectProvider.CreateProjectForRepl(
            statement, Path.Combine(_currentDirectory, "REPL"), environment.TargetFramework);
        var (code, output) = DotnetCli.Run(DotnetCli.Locate(), new[] {
            "restore", project.Path, "-r", environment.RuntimeIdentifier, "-v", "q", "-nologo", "-nodeReuse:false",
            $"-p:RestoreRootConfigDirectory={_currentDirectory}",
        }, null, ProjectReferences.BuildTimeout);
        if (code != 0) {
            throw new InvalidOperationException(
                $"#r \"nuget:\": restore failed. The sources were {Path.GetFileName(config)} at "
                + $"{Path.GetDirectoryName(config)} plus your user-level NuGet.Config, unless the repo file "
                + "says <clear/>.\n" + output.Trim());
        }
    }

    // --- Runtime plugins ------------------------------------------------------

    /// <summary>Raised when a #r-loaded assembly registered a new cell language or
    /// connection provider with THIS session — hosts forward it to their clients.</summary>
    public event Action LanguagesChanged;

    private readonly HashSet<string> _scannedPluginPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pendingPluginPrelude = new();

    // Loads a newly-referenced assembly (default load context — plugin types must
    // share identity with this assembly's contracts) and registers its exports.
    private void ScanForPlugins(string assemblyPath) {
        if (!_scannedPluginPaths.Add(assemblyPath)) {
            return;
        }
        try {
            RegisterPlugins(System.Reflection.Assembly.LoadFrom(assemblyPath));
        } catch (Exception e) {
            // A bad plugin must not break the #r that pulled it in.
            _logger.LogWarning("Plugin scan failed for {Path}: {Error}", assemblyPath, e.Message);
        }
    }

    /// <summary>
    /// Registers the cell languages and connection providers an assembly exports
    /// (via <see cref="CellLanguageExportAttribute"/> /
    /// <see cref="Core.Primitives.ConnectionProviderExportAttribute"/>) with this
    /// session only. Languages whose Id — and providers whose Type — are already
    /// registered are skipped, so built-in assemblies and repeat loads are no-ops.
    /// Returns true when anything new was registered.
    /// </summary>
    public bool RegisterPlugins(System.Reflection.Assembly assembly) {
        var changed = false;
        foreach (var export in assembly.GetCustomAttributes(typeof(CellLanguageExportAttribute), inherit: false)
                     .Cast<CellLanguageExportAttribute>()) {
            if (Activator.CreateInstance(export.LanguageType) is not ICellLanguage language ||
                _languages.ById(language.Id) != null) {
                continue;
            }
            _languages.Add(language);
            ApplyContribution(language.ScriptContribution);
            _logger.LogInformation("Registered cell language '{Id}' from {Assembly}", language.Id, assembly.GetName().Name);
            changed = true;
        }
        foreach (var export in assembly.GetCustomAttributes(typeof(Core.Primitives.ConnectionProviderExportAttribute), inherit: false)
                     .Cast<Core.Primitives.ConnectionProviderExportAttribute>()) {
            var descriptor = export.DescriptorSource
                .GetProperty("Descriptor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.GetValue(null) as Core.Primitives.ConnectionProviderDescriptor;
            if (descriptor == null || _connectionProviders.Exists(p =>
                    string.Equals(p.Type, descriptor.Type, StringComparison.OrdinalIgnoreCase))) {
                continue;
            }
            _connectionProviders.Add(descriptor);
            _logger.LogInformation("Registered connection provider '{Type}' from {Assembly}", descriptor.Type, assembly.GetName().Name);
            changed = true;
        }
        if (changed) {
            LanguagesChanged?.Invoke();
        }
        return changed;
    }

    // A plugin's contribution lands on the LIVE session: references and imports
    // apply to every later submission; using-statics run as the next submission's
    // prelude (they are legal script statements).
    private void ApplyContribution(ScriptContribution contribution) {
        if (contribution == null) {
            return;
        }
        foreach (var reference in contribution.References) {
            _scriptOptions = _scriptOptions.AddReferences(MetadataReference.CreateFromFile(reference.Location));
        }
        if (contribution.Imports.Count > 0) {
            _scriptOptions = _scriptOptions.AddImports(contribution.Imports);
        }
        _pendingPluginPrelude.AddRange(contribution.UsingStatics.Select(u => u.TrimEnd(';') + ";"));
    }

    private string PrepareStatement(string statement) {
        statement = ResolveProjectReferences(statement);
        TryLoadReferenceFromScript(statement);
        return NormalizeTrailingExpression(statement);
    }

    // --- #r "project:" -----------------------------------------------------------

    /// <summary>
    /// Builds each <c>#r "project: …"</c> line's project and references its output,
    /// returning the statement with those lines blanked.
    ///
    /// <para>
    /// Blanked rather than rewritten to <c>#r "…/Out.dll"</c>: the references are
    /// added to the session's options the way a NuGet package's are, so Roslyn
    /// never sees the directive — neither here, where it would look for a file
    /// called <c>project: …</c>, nor in the language service's replay of this
    /// submission. The line stays as an empty one so diagnostics keep their line
    /// numbers.
    /// </para>
    /// </summary>
    private string ResolveProjectReferences(string statement) {
        if (statement.IndexOf(ProjectReferenceRequest.Prefix, StringComparison.OrdinalIgnoreCase) < 0) {
            return statement;
        }
        var lines = statement.Split('\n');
        var changed = false;
        for (var i = 0; i < lines.Length; i++) {
            // Relative to the file the line is in: the notebook, or the library a
            // #!import is running — the rule imports themselves follow.
            var request = ProjectReferenceRequest.TryParse(
                lines[i], _importer.IsImporting ? _importer.ActivePath : _currentDirectory);
            if (request == null) {
                continue;
            }
            var result = _projects.Resolve(request);
            foreach (var warning in result.Warnings) {
                Console.Out.WriteLine("warning: " + warning);
            }
            if (result.Reloaded) {
                // The previous build of the same project leaves first: two copies
                // of one assembly identity is what Roslyn refuses as CS1703.
                var superseded = new HashSet<string>(result.Superseded, StringComparer.OrdinalIgnoreCase);
                _scriptOptions = _scriptOptions.WithReferences(_scriptOptions.MetadataReferences
                    .Where(r => r is not PortableExecutableReference pe || pe.FilePath == null || !superseded.Contains(pe.FilePath))
                    .Concat(result.ReferencePaths.Select(path => MetadataReference.CreateFromFile(path))));
                ScanForPlugins(result.AssemblyPath);
                _logger.LogInformation("#r project: referenced {Assembly}", result.AssemblyPath);
            }
            lines[i] = string.Empty;
            changed = true;
        }
        return changed ? string.Join("\n", lines) : statement;
    }

    private static readonly CSharpParseOptions _scriptParseOptions =
        new(kind: SourceCodeKind.Script, languageVersion: LanguageVersion.Preview);

    /// <summary>
    /// Makes a cell that is a single "value" expression print like the REPL
    /// expects. Writing just <c>x</c> already returns its value, but adding the
    /// semicolon a linter asks for (<c>x;</c>) is a C# error — a bare expression
    /// (identifier, member access, literal, arithmetic, …) can't be a statement
    /// (CS0201). When the whole cell is exactly one such expression statement, we
    /// drop the trailing semicolon so it becomes the submission's value and is
    /// displayed. Expressions that <em>are</em> legal statements — calls,
    /// assignments, <c>new</c>, <c>++</c>/<c>--</c>, <c>await</c> — are left
    /// untouched, so nothing that already works changes behavior.
    /// </summary>
    private static string NormalizeTrailingExpression(string statement) {
        if (string.IsNullOrWhiteSpace(statement)) {
            return statement;
        }

        SyntaxTree tree;
        try {
            tree = CSharpSyntaxTree.ParseText(statement, _scriptParseOptions);
        } catch {
            return statement;
        }

        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) {
            return statement; // don't touch code that doesn't even parse
        }

        if (tree.GetRoot() is not CompilationUnitSyntax root
            || root.Members.Count != 1
            || root.Members[0] is not GlobalStatementSyntax global
            || global.Statement is not ExpressionStatementSyntax expressionStatement) {
            return statement;
        }

        var semicolon = expressionStatement.SemicolonToken;
        if (semicolon.IsMissing || semicolon.Span.Length == 0) {
            return statement; // already an unterminated trailing expression — prints as-is
        }

        if (IsLegalStatementExpression(expressionStatement.Expression)) {
            return statement; // a valid statement (call/assignment/…): keep normal semantics
        }

        // Remove just the trailing semicolon so the expression's value is returned.
        var index = semicolon.SpanStart;
        return statement.Substring(0, index) + statement.Substring(index + 1);
    }

    // Expressions C# allows as a statement (so `expr;` is valid and discards the
    // value). Everything else would be CS0201 as a statement.
    private static bool IsLegalStatementExpression(ExpressionSyntax expression) =>
        expression is InvocationExpressionSyntax
        || expression is ObjectCreationExpressionSyntax
        || expression is ImplicitObjectCreationExpressionSyntax
        || expression is AssignmentExpressionSyntax
        || expression is AwaitExpressionSyntax
        || expression is ConditionalAccessExpressionSyntax
        || expression.IsKind(SyntaxKind.PostIncrementExpression)
        || expression.IsKind(SyntaxKind.PostDecrementExpression)
        || expression.IsKind(SyntaxKind.PreIncrementExpression)
        || expression.IsKind(SyntaxKind.PreDecrementExpression);

    private ScriptOptions CreateScriptOptions() {
        var dir = AppContext.BaseDirectory;

        var options = ScriptOptions.Default;
        options = AddDefaultImports(options);

        var mscorlib = typeof(object).GetTypeInfo().Assembly;
        var systemCore = typeof(System.Linq.Enumerable).GetTypeInfo().Assembly;

        var references = new[]
            {
                mscorlib,
                systemCore,
                Assembly.GetAssembly(typeof(System.Dynamic.DynamicObject)),// System.Code
                Assembly.GetAssembly(typeof(Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo)),// Microsoft.CSharp
                Assembly.GetAssembly(typeof(System.Dynamic.ExpandoObject)),// System.Dynamic
                this.GetType().Assembly, // ClrKernel.Core.Scripting (Extensions, GetVariable)
                typeof(DisplayData).Assembly, // ClrKernel.Core.Primitives (display API)
            };

        // Everything else comes from the registered languages and providers --
        // which is why Core.Scripting references none of them.
        options = options.AddReferences(references)
            .AddReferences(_contributions.SelectMany(c => c.References).Distinct());

        return options;
    }

    private ScriptOptions AddDefaultImports(ScriptOptions scriptOptions) {
        var workingDir = AppContext.BaseDirectory;

        return scriptOptions
            .AddImports(_contributions.SelectMany(c => c.Imports).Distinct())
            .AddImports(new[] {
            "ClrKernel.Core.Primitives", // DisplayAs/DisplayedValue live updates
            "System",
            "System.IO",
            "System.Collections",
            "System.Collections.Generic",
            "System.Console",
            "System.Diagnostics",
            "System.Dynamic",
            "System.Linq",
            "System.Linq.Expressions",
            "System.Text",
            "System.Threading.Tasks"
        }).WithSourceResolver(new SourceFileResolver(ImmutableArray<string>.Empty, workingDir))
            .WithMetadataResolver(new NuGetMetadataReferenceResolver(ScriptMetadataResolver.Default.WithBaseDirectory(workingDir)))
            .WithEmitDebugInformation(true)
            .WithFileEncoding(Encoding.UTF8);
    }
}

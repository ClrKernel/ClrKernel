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

public class InteractiveScriptEngine : ICellExecutionContext {
    private ScriptState<object> _scriptState;

    private ScriptOptions _scriptOptions;

    private InteractiveScriptGlobals _globals;

    private StringBuilder _interactiveOutput;

    private RuntimeDependencyResolver _runtimeDependencyResolver;

    private ILogger _logger;

    private string _currentDirectory;

    // Where Dotnet.Script generates its NuGet restore scratch projects. This
    // Dotnet.Script version roots the scratch at the directory we pass to
    // GetDependenciesForCode (mirroring its absolute path underneath), so
    // passing the notebook's directory would litter user folders with a
    // dotnet-script/ tree. Anchor it under the system temp instead.
    // Note: nearest-NuGet.Config discovery anchors here too, so feed config
    // comes from user/machine level; per-workspace feeds work via #i "nuget:<url>".
    private readonly string _dependencyScratchDirectory;

    private string[] _references;

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
        _contributions = _languages.ScriptContributions
            .Concat(extraContributions ?? Enumerable.Empty<ScriptContribution>())
            .ToList();
        _usingStatics = _builtInUsingStatics
            .Concat(_contributions.SelectMany(c => c.UsingStatics))
            .ToArray();
        _currentDirectory = currentDir;
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
        var projectProvider = new Dotnet.Script.DependencyModel.ProjectSystem.ScriptProjectProvider(logFactory, _dependencyScratchDirectory);
        _runtimeDependencyResolver = new RuntimeDependencyResolver(projectProvider, logFactory, true);

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
        // A #! selector routes the cell to a registered language. The registry
        // matches longest-selector-first, so #!sql-connect can never be swallowed
        // by #!sql (see CellSelectorOrderingTest).
        var match = _languages.Match(statement);
        if (match != null) {
            var languageResult = await match.Language.ExecuteAsync(match.Cell, this).ConfigureAwait(false);
            // Languages and providers return display concepts; the wire bundle is
            // built here so they never touch a MIME type.
            return languageResult is IDisplayValue concept ? MimeBundler.Bundle(concept) : languageResult;
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

    private async Task<object> ExecuteCoreAsync(string statement) {
        statement = PrepareStatement(statement);

        await EnsureScriptStateAsync();
        _scriptState = await _scriptState.ContinueWithAsync(statement, _scriptOptions);

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

        var lineRuntimeDependencies = _runtimeDependencyResolver.GetDependenciesForCode(_dependencyScratchDirectory, ScriptMode.REPL, new string[0], statement);
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
        }

        return true;
    }

    private string PrepareStatement(string statement) {
        TryLoadReferenceFromScript(statement);
        return NormalizeTrailingExpression(statement);
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

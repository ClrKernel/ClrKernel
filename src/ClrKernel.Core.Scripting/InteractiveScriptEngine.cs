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

public class InteractiveScriptEngine {
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
    internal static readonly string[] DefaultUsingStatics = {
        "using static ClrKernel.Core.Scripting.Extensions;",
        "using static ClrKernel.Core.Primitives.DisplayDataEmitter;"
    };

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

    public InteractiveScriptEngine(string currentDir, ILogger logger) {
        Current = this;
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

    // Lazily created on the first #!http cell; holds HTTP session state (file
    // variables, named responses) so requests chain across cells like one
    // growing .http file.
    private ClrKernel.Http.HttpSession _httpSession;

    // A cell whose first non-blank line is the #!http selector runs as a
    // .http document (VS Code REST Client syntax) instead of C#.
    private static bool TryStripHttpSelector(string statement, out string body) {
        body = null;
        var normalized = statement.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0) {
            index++;
        }
        if (index >= lines.Length) {
            return false;
        }
        var selector = lines[index].Trim();
        if (!selector.Equals("#!http", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        body = string.Join("\n", lines, index + 1, lines.Length - index - 1);
        return true;
    }

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

    // Lazily created on the first #!pwsh cell (or completion query); hosts the
    // persistent PowerShell runspace so state and completions share one session.
    private ClrKernel.PowerShell.PowerShellSession _powerShellSession;

    /// <summary>The session's PowerShell host, created on demand.</summary>
    public ClrKernel.PowerShell.PowerShellSession PowerShell =>
        _powerShellSession ??= new ClrKernel.PowerShell.PowerShellSession();

    private static readonly string[] _pwshSelectors = { "#!pwsh", "#!powershell" };

    // Lazily created on the first #!sql / #!sql-connect cell; holds the named SQL
    // connections and the OS-backed secret store for the session.
    private ClrKernel.Sql.SqlSession _sqlSession;

    /// <summary>The session's SQL connections/executor, created on demand.</summary>
    public ClrKernel.Sql.SqlSession Sql =>
        _sqlSession ??= new ClrKernel.Sql.SqlSession();

    // Lazily created on the first #!dax / #!dax-connect cell; holds the named cube
    // (Analysis Services / Fabric) connections for DAX cells.
    private ClrKernel.AnalysisServices.SsasSession _ssasSession;

    /// <summary>The session's cube (Analysis Services) connections, created on demand.</summary>
    public ClrKernel.AnalysisServices.SsasSession Cubes =>
        _ssasSession ??= new ClrKernel.AnalysisServices.SsasSession();

    // A cell whose first non-blank line begins with #!dax-connect registers one or
    // more named cube connections (one per #!dax-connect line).
    private static bool TryStripDaxConnect(string statement, out System.Collections.Generic.List<string> connectLines) {
        connectLines = null;
        var lines = statement.Replace("\r\n", "\n").Split('\n');
        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0) {
            index++;
        }
        if (index >= lines.Length ||
            !lines[index].TrimStart().StartsWith("#!dax-connect", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        connectLines = new System.Collections.Generic.List<string>();
        foreach (var line in lines) {
            if (line.TrimStart().StartsWith("#!dax-connect", StringComparison.OrdinalIgnoreCase)) {
                connectLines.Add(line.Trim());
            }
        }
        return connectLines.Count > 0;
    }

    // A cell whose first non-blank line begins with #!dax (but not #!dax-connect)
    // runs DAX. Strips that selector line, capturing an inline --connections cube.
    private static bool TryStripDaxSelector(string statement, out string body, out string inlineCube) {
        body = null;
        inlineCube = null;
        var lines = statement.Replace("\r\n", "\n").Split('\n');
        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0) {
            index++;
        }
        if (index >= lines.Length) {
            return false;
        }
        var first = lines[index].TrimStart();
        if (!first.StartsWith("#!dax", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("#!dax-connect", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        inlineCube = ClrKernel.AnalysisServices.DaxDirectives.SelectorConnection(lines[index]);
        body = string.Join("\n", lines, index + 1, lines.Length - index - 1);
        return true;
    }

    // A cell whose first non-blank line begins with #!sql-connect registers one or
    // more named connections (one per #!sql-connect line). Returns those lines.
    private static bool TryStripSqlConnect(string statement, out System.Collections.Generic.List<string> connectLines) {
        connectLines = null;
        var lines = statement.Replace("\r\n", "\n").Split('\n');
        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0) {
            index++;
        }
        if (index >= lines.Length ||
            !lines[index].TrimStart().StartsWith("#!sql-connect", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        connectLines = new System.Collections.Generic.List<string>();
        foreach (var line in lines) {
            if (line.TrimStart().StartsWith("#!sql-connect", StringComparison.OrdinalIgnoreCase)) {
                connectLines.Add(line.Trim());
            }
        }
        return connectLines.Count > 0;
    }

    // A single-line magic cell: if the first non-blank line begins with the given
    // selector, return that whole line (the directive) for the magic to parse.
    private static bool TryStripFirstLineSelector(string statement, string selector, out string line) {
        line = null;
        var lines = statement.Replace("\r\n", "\n").Split('\n');
        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0) {
            index++;
        }
        if (index >= lines.Length ||
            !lines[index].TrimStart().StartsWith(selector, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        line = lines[index].Trim();
        return true;
    }

    // A cell whose first non-blank line begins with #!sql (but not #!sql-connect)
    // runs as SQL. Strips that selector line, capturing an inline --connections
    // name so it can be re-expressed as a leading SQL comment for the executor.
    private static bool TryStripSqlSelector(string statement, out string body, out string inlineConnection) {
        body = null;
        inlineConnection = null;
        var lines = statement.Replace("\r\n", "\n").Split('\n');
        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0) {
            index++;
        }
        if (index >= lines.Length) {
            return false;
        }
        var first = lines[index].TrimStart();
        if (!first.StartsWith("#!sql", StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith("#!sql-connect", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        inlineConnection = ClrKernel.Sql.SqlDirectives.SelectorConnection(lines[index]);
        body = string.Join("\n", lines, index + 1, lines.Length - index - 1);
        return true;
    }

    // A cell whose first non-blank line is the given selector runs in that
    // language instead of C#.
    private static bool TryStripSelector(string statement, string selector, out string body) =>
        TryStripSelector(statement, new[] { selector }, out body);

    // A cell whose first non-blank line is one of these selectors runs in the
    // named language rather than as C#.
    private static bool TryStripSelector(string statement, string[] selectors, out string body) {
        body = null;
        var lines = statement.Replace("\r\n", "\n").Split('\n');
        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0) {
            index++;
        }
        if (index >= lines.Length) {
            return false;
        }
        var selector = lines[index].Trim();
        if (!selectors.Any(s => selector.Equals(s, StringComparison.OrdinalIgnoreCase))) {
            return false;
        }
        body = string.Join("\n", lines, index + 1, lines.Length - index - 1);
        return true;
    }

    public async Task<object> ExecuteAsync(string statement) {
        // #!http cells run as .http documents (their response cards are emitted
        // as display data); nothing flows back to the C# script state.
        if (TryStripHttpSelector(statement, out var httpBody)) {
            _httpSession ??= new ClrKernel.Http.HttpSession(_currentDirectory);
            await _httpSession.ExecuteAsync(httpBody);
            return null;
        }

        // #!mermaid cells render a diagram (offline, self-contained) and return
        // it as display data; nothing flows into the C# script state.
        if (TryStripSelector(statement, "#!mermaid", out var mermaidBody)) {
            return await Task.FromResult(ClrKernel.Mermaid.MermaidRenderer.Render(mermaidBody));
        }

        // #!pwsh / #!powershell cells run in the PowerShell runspace and return
        // their output as display data; nothing flows into the C# script state.
        if (TryStripSelector(statement, _pwshSelectors, out var pwshBody)) {
            return await Task.FromResult(PowerShell.Execute(pwshBody));
        }

        // #!sql-connect cells register named connections (no query runs); return a
        // short confirmation listing what is now available.
        if (TryStripSqlConnect(statement, out var connectLines)) {
            var names = new System.Collections.Generic.List<string>();
            var bound = new System.Collections.Generic.List<string>();
            foreach (var line in connectLines) {
                var directive = Sql.Connect(line);
                names.Add(directive.Spec.Name);
                // Bind a C# variable so #!csharp cells can use the connection directly,
                // e.g. `dw.Query("...").Results()`. The variable is a SqlDatabase for the
                // just-registered connection.
                if (!string.IsNullOrEmpty(directive.Variable)) {
                    await EnsureScriptStateAsync().ConfigureAwait(false);
                    var binding = $"var {directive.Variable} = Sql.Database({CSharpStringLiteral(directive.Spec.Name)});";
                    _scriptState = await _scriptState.ContinueWithAsync(binding, _scriptOptions).ConfigureAwait(false);
                    _submissions.Add(binding);
                    bound.Add(directive.Variable);
                }
            }
            var label = names.Count == 1 ? $"Connected: {names[0]}" : $"Connected: {string.Join(", ", names)}";
            if (bound.Count > 0) {
                label += bound.Count == 1
                    ? $" → C# variable `{bound[0]}`"
                    : $" → C# variables {string.Join(", ", bound.ConvertAll(b => "`" + b + "`"))}";
            }
            return new ClrKernel.Core.Primitives.DisplayData($"{label} (default: {Sql.Connections.DefaultName})");
        }

        // #!sql-bulk copies rows from one connection into a table on another; the
        // cell returns a summary (a progress bar streams during the copy).
        if (TryStripFirstLineSelector(statement, "#!sql-bulk", out var bulkLine)) {
            return await Task.FromResult(Sql.ExecuteBulk(bulkLine));
        }

        // #!sql-merge upserts a target from a source and returns per-action counts.
        if (TryStripFirstLineSelector(statement, "#!sql-merge", out var mergeLine)) {
            return await Task.FromResult(Sql.ExecuteMerge(mergeLine));
        }

        // #!sql-run executes the registered pipeline steps as a dependency DAG
        // (independent steps run in parallel) with a live status board.
        if (TryStripFirstLineSelector(statement, "#!sql-run", out var runLine)) {
            return await Task.FromResult(Sql.ExecuteRun(runLine));
        }

        // #!sql-deploy applies a folder of .sql definitions idempotently.
        if (TryStripFirstLineSelector(statement, "#!sql-deploy", out var deployLine)) {
            return await Task.FromResult(Sql.ExecuteDeploy(deployLine));
        }

        // #!sql cells run T-SQL against a named (or default) connection; each result
        // set is emitted as an interactive grid and the cell returns a run summary.
        if (TryStripSqlSelector(statement, out var sqlBody, out var inlineConnection)) {
            var cellText = string.IsNullOrEmpty(inlineConnection)
                ? sqlBody
                : "-- connections " + inlineConnection + "\n" + sqlBody;
            return await Task.FromResult(Sql.Execute(cellText));
        }

        // #!dax-connect cells register named cube (Analysis Services / Fabric)
        // connections; return a short confirmation.
        if (TryStripDaxConnect(statement, out var daxConnectLines)) {
            var names = new System.Collections.Generic.List<string>();
            foreach (var line in daxConnectLines) {
                names.Add(Cubes.Connect(line));
            }
            var summary = $"Connected cube(s): {string.Join(", ", names)} (default: {Cubes.Cubes.DefaultName})";
            return await Task.FromResult(new ClrKernel.Core.Primitives.DisplayData(summary));
        }

        // #!dax cells run DAX against a named (or default) cube and return a grid.
        if (TryStripDaxSelector(statement, out var daxBody, out var inlineCube)) {
            var cellText = string.IsNullOrEmpty(inlineCube)
                ? daxBody
                : "-- connections " + inlineCube + "\n" + daxBody;
            return await Task.FromResult(Cubes.Execute(cellText));
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

        var displayData = _scriptState.ReturnValue as DisplayData;

        if (displayData != null) {
            return displayData;
        }

        // Rich default rendering: sequences become HTML tables, objects a
        // property table, anonymous types a clean { x = 10 }, all with a
        // type-hint badge — instead of Roslyn's CSharpObjectFormatter output.
        return ResultFormatter.Format(_scriptState.ReturnValue);
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
                typeof(ClrKernel.Mermaid.MermaidRenderer).Assembly, // ClrKernel.Mermaid (DisplayMermaid)
                typeof(ClrKernel.Sql.SqlSession).Assembly, // ClrKernel.Sql (Sql.BulkCopy / Sql.Merge)
                typeof(ClrKernel.AnalysisServices.Ssas).Assembly, // ClrKernel.AnalysisServices (Ssas.Connect)
                typeof(ClrKernel.Fabric.FabricConnection).Assembly // ClrKernel.Fabric (Fabric.Connect / warehouse bulk-insert)
            };

        options = options.AddReferences(references);

        return options;
    }

    private ScriptOptions AddDefaultImports(ScriptOptions scriptOptions) {
        var workingDir = AppContext.BaseDirectory;

        return scriptOptions.AddImports(new[] {
            "ClrKernel.Core.Primitives", // DisplayAs/DisplayedValue live updates
            "ClrKernel.Mermaid", // DisplayMermaid() helper
            "ClrKernel.Sql", // SqlSession, MergeSpec (Sql.BulkCopy / Sql.Merge)
            "ClrKernel.Sql.Etl", // BulkCopyOptions, MergeSpec, DataTableBuilder
            "ClrKernel.AnalysisServices", // Ssas.Connect / ProcessPartitions (SSAS/Fabric)
            "ClrKernel.Fabric", // Fabric.Connect() → warehouse bulk-insert / reload-batch
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

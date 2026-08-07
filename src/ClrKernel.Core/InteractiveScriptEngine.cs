using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using ClrKernel.Primitives;
using Dotnet.Script.DependencyModel.Context;
using Dotnet.Script.DependencyModel.NuGet;
using Dotnet.Script.DependencyModel.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Scripting.Hosting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using LogFactory = Dotnet.Script.DependencyModel.Logging.LogFactory;
using ScriptLogLevel = Dotnet.Script.DependencyModel.Logging.LogLevel;

namespace ClrKernel.Core;

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

        if (_scriptState == null) {
            var usingStatements = new[]
            {
                "using static ClrKernel.Core.Extensions;",
                "using static ClrKernel.Primitives.DisplayDataEmitter;"
            };


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
            _scriptState = await _scriptState.ContinueWithAsync(statement, _scriptOptions);
        } else {
            _scriptState = await _scriptState.ContinueWithAsync(statement, _scriptOptions);
        }

        if (_scriptState.ReturnValue == null) {
            return null;
        }

        var jdisplayData = _scriptState.ReturnValue as JObject;

        if (jdisplayData != null) {
            return jdisplayData;
        }

        var displayData = _scriptState.ReturnValue as DisplayData;

        if (displayData != null) {
            return displayData;
        }

        _globals.Print(_scriptState.ReturnValue);

        var output = _interactiveOutput.ToString();
        _interactiveOutput.Clear();

        return new DisplayData(output);
    }

    public object Execute(string statement) {
        return ExecuteAsync(statement).Result;
    }

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
        return statement;
    }

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
                this.GetType().Assembly, // ClrKernel.Core (Extensions, GetVariable)
                typeof(DisplayData).Assembly // ClrKernel.Primitives (display API)
            };

        options = options.AddReferences(references);

        return options;
    }

    private ScriptOptions AddDefaultImports(ScriptOptions scriptOptions) {
        var workingDir = AppContext.BaseDirectory;

        return scriptOptions.AddImports(new[] {
            "ClrKernel.Primitives", // DisplayAs/DisplayedValue live updates
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

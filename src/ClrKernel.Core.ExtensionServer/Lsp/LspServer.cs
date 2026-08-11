using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.LanguageServices;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace ClrKernel.Core.ExtensionServer.Lsp;

/// <summary>Params for the custom clrkernel/execute request.</summary>
public sealed class ExecuteParams {
    public string CellId { get; set; }
    public string Code { get; set; }
}

/// <summary>Params for clrkernel/sql/addConnection: a #!sql-connect line plus an
/// optional secret to store (never written to a file).</summary>
public sealed class SqlConnectParams {
    public string Directive { get; set; }
    public string Secret { get; set; }
}

/// <summary>Params for clrkernel/sql/storeSecret.</summary>
public sealed class SqlSecretParams {
    public string SecretRef { get; set; }
    public string Secret { get; set; }
}

/// <summary>Params for connection lookups by name.</summary>
public sealed class SqlNameParams {
    public string Name { get; set; }
}

/// <summary>Params for connections.json discovery/auto-load: the notebook's directory.</summary>
public sealed class SqlConfigDirParams {
    public string Directory { get; set; }
}

/// <summary>Params for saving a registered connection to a connections.json file.</summary>
public sealed class SqlSaveConfigParams {
    public string Name { get; set; }
    public string FilePath { get; set; }
}

/// <summary>
/// The unified ClrKernel language server (Option A): standard LSP language
/// features (completion, hover, signature help) and cell execution
/// (clrkernel/execute + clrkernel/display notifications) over one connection,
/// backed by a single <see cref="InteractiveScriptEngine"/> so completion sees
/// the live REPL state — prior-cell symbols, #r "nuget:" types, and imports.
/// </summary>
public sealed class LspServer {
    private readonly InteractiveScriptEngine _engine;
    private readonly ScriptLanguageService _language = new();
    private readonly ILogger _logger;

    // Full-sync document store: uri -> current text (notebook cells and files).
    private readonly ConcurrentDictionary<string, string> _documents = new();

    // uri -> languageId (from didOpen), so language features dispatch by language
    // (csharp -> Roslyn, powershell -> the PowerShell runspace).
    private readonly ConcurrentDictionary<string, string> _languages = new();

    // Serializes execution against language queries so completion never reads a
    // half-applied #r; both are cheap when the engine is idle.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Set by the host after the JsonRpc connection is created.</summary>
    public JsonRpc Rpc { get; set; }

    public LspServer(ILoggerFactory loggerFactory) {
        _engine = new InteractiveScriptEngine(Environment.CurrentDirectory, loggerFactory.CreateLogger(nameof(InteractiveScriptEngine)));
        _logger = loggerFactory.CreateLogger(nameof(LspServer));
    }

    // --- Lifecycle ---------------------------------------------------------

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public InitializeResult Initialize(JsonElement _) {
        return new InitializeResult {
            Capabilities = new ServerCapabilities {
                TextDocumentSync = 1, // full
                CompletionProvider = new CompletionOptions {
                    TriggerCharacters = new List<string> { ".", " " },
                    ResolveProvider = false,
                },
                HoverProvider = true,
                SignatureHelpProvider = new SignatureHelpOptions {
                    TriggerCharacters = new List<string> { "(", "," },
                },
            },
            ServerInfo = new ServerInfo {
                Name = "ClrKernel",
                Version = typeof(LspServer).Assembly.GetName().Version?.ToString(),
            },
        };
    }

    [JsonRpcMethod("initialized")]
    public void Initialized() {
        _logger.LogInformation("LSP initialized");
    }

    [JsonRpcMethod("shutdown")]
    public object Shutdown() => null;

    [JsonRpcMethod("exit")]
    public void Exit() {
        _ = Task.Run(async () => {
            await Task.Delay(100).ConfigureAwait(false);
            Environment.Exit(0);
        });
    }

    // --- Document sync -----------------------------------------------------

    [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
    public void DidOpen(DidOpenTextDocumentParams p) {
        if (p?.TextDocument?.Uri != null) {
            _documents[p.TextDocument.Uri] = p.TextDocument.Text ?? string.Empty;
            _languages[p.TextDocument.Uri] = p.TextDocument.LanguageId ?? "csharp";
            PublishDiagnostics(p.TextDocument.Uri);
        }
    }

    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void DidChange(DidChangeTextDocumentParams p) {
        if (p?.TextDocument?.Uri == null || p.ContentChanges == null || p.ContentChanges.Count == 0) {
            return;
        }
        // Full sync: last change carries the whole document.
        _documents[p.TextDocument.Uri] = p.ContentChanges[^1].Text ?? string.Empty;
        PublishDiagnostics(p.TextDocument.Uri);
    }

    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DidCloseTextDocumentParams p) {
        if (p?.TextDocument?.Uri != null) {
            _documents.TryRemove(p.TextDocument.Uri, out _);
            _languages.TryRemove(p.TextDocument.Uri, out _);
        }
    }

    // Live T-SQL syntax checking: on every open/change of a sql document, parse
    // with ScriptDom and push diagnostics. No-op for other languages.
    // Sessions come from the cell-language registry, not from engine properties:
    // the engine no longer knows these types.
    private ClrKernel.Sql.SqlSession Sql =>
        _engine.Languages.Get<ClrKernel.Sql.SqlCellLanguage>()?.Session;

    private ClrKernel.AnalysisServices.SsasSession Cubes =>
        _engine.Languages.Get<ClrKernel.AnalysisServices.DaxCellLanguage>()?.Session;

    private ClrKernel.Language.PowerShell.PowerShellSession PowerShell =>
        _engine.Languages.Get<ClrKernel.Language.PowerShell.PowerShellCellLanguage>()?.Session;

    private void PublishDiagnostics(string uri) {
        if (Rpc == null || uri == null) {
            return;
        }
        if (!_languages.TryGetValue(uri, out var lang)) {
            return;
        }
        var services = _engine.Languages.ById(lang)?.Services;
        if (services == null) {
            return;
        }
        var text = _documents.TryGetValue(uri, out var t) ? t : string.Empty;
        var diagnostics = new List<Diagnostic>();
        try {
            foreach (var d in services.Diagnose(text)) {
                diagnostics.Add(new Diagnostic {
                    Range = new Range {
                        Start = new Position { Line = d.Line, Character = d.Column },
                        End = new Position { Line = d.EndLine, Character = d.EndColumn },
                    },
                    Severity = 1,
                    Source = "clrkernel-" + lang,
                    Code = d.Code.ToString(),
                    Message = d.Message,
                });
            }
        } catch (Exception e) {
            _logger.LogWarning(e, "diagnostics failed for {Language}", lang);
            return;
        }
        _ = Rpc.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
            new PublishDiagnosticsParams { Uri = uri, Diagnostics = diagnostics });
    }

    // Language features dispatch by the cell's languageId through the registry,
    // so adding a language means registering it, not editing this file. (The
    // clrkernel/sql/* and clrkernel/dax/* connection RPCs below are the
    // documented exception -- see HANDOFF-17 section 4.2.)
    private ICellLanguageServices ServicesFor(TextDocumentPositionParams p) {
        if (p?.TextDocument?.Uri == null || !_languages.TryGetValue(p.TextDocument.Uri, out var lang)) {
            return null;
        }
        return _engine.Languages.ById(lang)?.Services;
    }

    // Every open cell of one language, so completion can see sibling cells.
    private LanguageServiceContext ContextFor(string languageId) {
        var open = new List<string>();
        foreach (var kv in _languages) {
            if (kv.Value.Equals(languageId, StringComparison.OrdinalIgnoreCase)
                && _documents.TryGetValue(kv.Key, out var text)) {
                open.Add(text);
            }
        }
        return new LanguageServiceContext(open);
    }

    // --- Language features -------------------------------------------------

    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionList> Completion(TextDocumentPositionParams p) {
        var (code, offset) = Resolve(p);
        if (code == null) {
            return new CompletionList();
        }

        var services = ServicesFor(p);
        if (services != null) {
            return await LanguageCompletion(services, p, code, offset).ConfigureAwait(false);
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        CompletionResultDto result;
        try {
            result = await _language.GetCompletionsAsync(_engine.SnapshotState(), code, offset).ConfigureAwait(false);
        } finally {
            _gate.Release();
        }

        var startPos = OffsetToPosition(code, result.ReplaceStart);
        var endPos = OffsetToPosition(code, result.ReplaceStart + result.ReplaceLength);
        var list = new CompletionList { IsIncomplete = false };
        foreach (var item in result.Items) {
            list.Items.Add(new CompletionItem {
                Label = item.Label,
                Kind = MapKind(item.Kind),
                Detail = string.IsNullOrEmpty(item.Detail) ? null : item.Detail,
                SortText = item.SortText,
                FilterText = item.FilterText,
                InsertText = item.InsertText,
                TextEdit = new TextEdit {
                    Range = new Range { Start = startPos, End = endPos },
                    NewText = item.InsertText,
                },
            });
        }
        return list;
    }

    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public async Task<Hover> Hover(TextDocumentPositionParams p) {
        var (code, offset) = Resolve(p);
        if (code == null) {
            return null;
        }

        var services = ServicesFor(p);
        if (services != null) {
            return await LanguageHover(services, code, offset).ConfigureAwait(false);
        }


        await _gate.WaitAsync().ConfigureAwait(false);
        HoverDto hover;
        try {
            hover = await _language.GetHoverAsync(_engine.SnapshotState(), code, offset).ConfigureAwait(false);
        } finally {
            _gate.Release();
        }

        if (hover == null || string.IsNullOrEmpty(hover.Markdown)) {
            return null;
        }
        return new Hover {
            Contents = new MarkupContent { Kind = "markdown", Value = "```csharp\n" + hover.Markdown + "\n```" },
            Range = new Range {
                Start = OffsetToPosition(code, hover.Start),
                End = OffsetToPosition(code, hover.Start + hover.Length),
            },
        };
    }

    [JsonRpcMethod("textDocument/signatureHelp", UseSingleObjectParameterDeserialization = true)]
    public async Task<SignatureHelp> SignatureHelp(TextDocumentPositionParams p) {
        var (code, offset) = Resolve(p);
        if (code == null) {
            return null;
        }

        var services = ServicesFor(p);
        if (services != null) {
            return await LanguageSignatureHelp(services, code, offset).ConfigureAwait(false);
        }


        await _gate.WaitAsync().ConfigureAwait(false);
        SignatureHelpDto help;
        try {
            help = await _language.GetSignatureHelpAsync(_engine.SnapshotState(), code, offset).ConfigureAwait(false);
        } finally {
            _gate.Release();
        }

        if (help == null || help.Signatures.Count == 0) {
            return null;
        }
        var result = new SignatureHelp {
            ActiveSignature = help.ActiveSignature,
            ActiveParameter = help.ActiveParameter,
        };
        foreach (var sig in help.Signatures) {
            var info = new SignatureInformation { Label = sig.Label };
            foreach (var param in sig.Parameters) {
                info.Parameters.Add(new ParameterInformation { Label = param.Label });
            }
            result.Signatures.Add(info);
        }
        return result;
    }

    // --- SQL language features --------------------------------------------

    private static readonly System.Text.RegularExpressions.Regex _stepDeclaration =
        new System.Text.RegularExpressions.Regex(@"(?im)^\s*--\s*step\s+([A-Za-z0-9_-]+)");

    // LSP CompletionItemKind values.
    // --- Language-neutral feature plumbing ---------------------------------

    private async Task<CompletionList> LanguageCompletion(
        ICellLanguageServices services, TextDocumentPositionParams p, string code, int offset) {
        CompletionResult completion;
        try {
            var languageId = _languages[p.TextDocument.Uri];
            completion = await services.CompleteAsync(code, offset, ContextFor(languageId)).ConfigureAwait(false);
        } catch (Exception e) {
            _logger.LogWarning(e, "completion failed");
            return new CompletionList();
        }
        if (completion == null) {
            return new CompletionList();
        }

        var startPos = OffsetToPosition(code, completion.ReplaceStart);
        var endPos = OffsetToPosition(code, completion.ReplaceStart + completion.ReplaceLength);
        var list = new CompletionList { IsIncomplete = false };
        foreach (var item in completion.Items) {
            list.Items.Add(new CompletionItem {
                Label = item.Label,
                Kind = MapCompletionKind(item.Kind),
                Detail = item.Detail,
                InsertText = item.InsertText,
                TextEdit = new TextEdit {
                    Range = new Range { Start = startPos, End = endPos },
                    NewText = item.InsertText,
                },
            });
        }
        return list;
    }

    private async Task<Hover> LanguageHover(ICellLanguageServices services, string code, int offset) {
        HoverResult hover;
        try {
            hover = await services.HoverAsync(code, offset).ConfigureAwait(false);
        } catch (Exception e) {
            _logger.LogWarning(e, "hover failed");
            return null;
        }
        if (hover == null || string.IsNullOrEmpty(hover.Markdown)) {
            return null;
        }
        return new Hover {
            Contents = new MarkupContent { Kind = "markdown", Value = hover.Markdown },
            Range = new Range {
                Start = OffsetToPosition(code, hover.Start),
                End = OffsetToPosition(code, hover.Start + hover.Length),
            },
        };
    }

    private async Task<SignatureHelp> LanguageSignatureHelp(
        ICellLanguageServices services, string code, int offset) {
        SignatureHelpResult help;
        try {
            help = await services.SignatureHelpAsync(code, offset).ConfigureAwait(false);
        } catch (Exception e) {
            _logger.LogWarning(e, "signature help failed");
            return null;
        }
        if (help == null || help.Signatures.Count == 0) {
            return null;
        }
        var result = new SignatureHelp {
            ActiveSignature = help.ActiveSignature,
            ActiveParameter = help.ActiveParameter,
        };
        foreach (var sig in help.Signatures) {
            var info = new SignatureInformation { Label = sig.Label };
            foreach (var param in sig.Parameters) {
                info.Parameters.Add(new ParameterInformation { Label = param.Label });
            }
            result.Signatures.Add(info);
        }
        return result;
    }

    // Completion kinds from every language funnel through one map: the SQL/DAX
    // vocabulary ("keyword", "function", …) and PowerShell's CompletionResultType
    // names ("Command", "ParameterName", …).
    private static int MapCompletionKind(string kind) => kind switch {
        "Command" => 3,        // Function
        "ParameterName" => 10, // Property
        "Variable" => 6,       // Variable
        "Property" => 10,      // Property
        "Method" => 2,         // Method
        "ProviderItem" => 17,  // File
        "ProviderContainer" => 19, // Folder
        "Type" => 7,           // Class
        "Namespace" => 9,      // Module
        _ => MapSqlKind(kind),
    };

    private static int MapSqlKind(string kind) => kind switch {
        "keyword" => 14,   // Keyword
        "function" => 3,   // Function
        "type" => 7,       // Class
        "magic" => 15,     // Snippet
        "flag" => 10,      // Property
        "directive" => 15, // Snippet
        "connection" => 21,// Constant
        "step" => 6,       // Variable
        "value" => 12,     // Value
        _ => 1,
    };

    // --- SQL connection management (custom methods for the extension UI) ---

    /// <summary>Lists registered connections (secret-free) for the connection panel.</summary>
    [JsonRpcMethod("clrkernel/sql/listConnections")]
    public object SqlListConnections() {
        var sql = Sql;
        var items = new List<object>();
        foreach (var c in sql.Connections.All) {
            items.Add(new {
                name = c.Name,
                server = c.Server,
                database = c.Database,
                auth = c.Auth.ToString(),
                user = c.User,
                describe = c.Describe(),
                needsSecret = c.NeedsSecret,
                secretRef = c.EffectiveSecretRef,
                isDefault = string.Equals(c.Name, sql.Connections.DefaultName, StringComparison.OrdinalIgnoreCase),
            });
        }
        return new { defaultName = sql.Connections.DefaultName, connections = items };
    }

    /// <summary>Registers/updates a connection from a #!sql-connect line built by the UI.</summary>
    [JsonRpcMethod("clrkernel/sql/addConnection", UseSingleObjectParameterDeserialization = true)]
    public object SqlAddConnection(SqlConnectParams p) {
        try {
            var spec = Sql.Connect(p?.Directive ?? string.Empty).Spec;
            if (!string.IsNullOrEmpty(p?.Secret)) {
                Sql.StoreSecret(spec.EffectiveSecretRef, p.Secret);
            }
            return new { ok = true, name = spec.Name, secretRef = spec.EffectiveSecretRef };
        } catch (Exception e) {
            return new { ok = false, error = e.Message };
        }
    }

    /// <summary>Stores a secret (password) in the OS credential store for a ref.</summary>
    [JsonRpcMethod("clrkernel/sql/storeSecret", UseSingleObjectParameterDeserialization = true)]
    public object SqlStoreSecret(SqlSecretParams p) {
        try {
            var provider = Sql.StoreSecret(p?.SecretRef ?? string.Empty, p?.Secret ?? string.Empty);
            return new { ok = true, provider };
        } catch (Exception e) {
            return new { ok = false, error = e.Message };
        }
    }

    /// <summary>Removes a connection from the session registry.</summary>
    [JsonRpcMethod("clrkernel/sql/removeConnection", UseSingleObjectParameterDeserialization = true)]
    public object SqlRemoveConnection(SqlNameParams p) {
        var removed = Sql.Connections.Remove(p?.Name ?? string.Empty);
        return new { ok = removed };
    }

    /// <summary>Sets the default connection.</summary>
    [JsonRpcMethod("clrkernel/sql/setDefault", UseSingleObjectParameterDeserialization = true)]
    public object SqlSetDefault(SqlNameParams p) {
        try {
            Sql.Connections.SetDefault(p?.Name ?? string.Empty);
            return new { ok = true };
        } catch (Exception e) {
            return new { ok = false, error = e.Message };
        }
    }

    /// <summary>Reports whether a connections.json exists at/above the notebook's directory,
    /// and the connection names it holds — so the UI can offer to confirm or choose a file.</summary>
    [JsonRpcMethod("clrkernel/sql/configStatus", UseSingleObjectParameterDeserialization = true)]
    public object SqlConfigStatus(SqlConfigDirParams p) {
        try {
            var path = Sql.FindConfigFile(NullIfBlank(p?.Directory));
            var names = path != null ? Sql.ConfigConnectionNames(path) : System.Array.Empty<string>();
            return new { ok = true, found = path != null, path, names };
        } catch (Exception e) {
            return new { ok = false, error = e.Message };
        }
    }

    /// <summary>Registers SqlServer entries from the nearest connections.json into the session
    /// (called on notebook open so saved connections are available without re-adding them).</summary>
    [JsonRpcMethod("clrkernel/sql/loadConnectionsConfig", UseSingleObjectParameterDeserialization = true)]
    public object SqlLoadConnectionsConfig(SqlConfigDirParams p) {
        try {
            var loaded = Sql.LoadFromConfig(NullIfBlank(p?.Directory));
            return new { ok = true, loaded };
        } catch (Exception e) {
            return new { ok = false, error = e.Message };
        }
    }

    /// <summary>Writes a registered connection into a connections.json file (secret-free).</summary>
    [JsonRpcMethod("clrkernel/sql/saveConnection", UseSingleObjectParameterDeserialization = true)]
    public object SqlSaveConnection(SqlSaveConfigParams p) {
        try {
            var path = Sql.SaveConnectionToConfig(p?.Name ?? string.Empty, p?.FilePath ?? string.Empty);
            return new { ok = true, path };
        } catch (Exception e) {
            return new { ok = false, error = e.Message };
        }
    }

    private static string NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // --- DAX cube connection management (custom methods for the extension UI) ---

    /// <summary>Lists registered cubes for the DAX connection panel.</summary>
    [JsonRpcMethod("clrkernel/dax/listConnections")]
    public object DaxListConnections() {
        var cubes = Cubes.Cubes;
        var items = new List<object>();
        foreach (var (name, spec) in cubes.All) {
            items.Add(new {
                name,
                describe = spec.Describe(),
                server = spec.Server,
                database = spec.Database,
                auth = spec.Auth.ToString(),
                isDefault = string.Equals(name, cubes.DefaultName, StringComparison.OrdinalIgnoreCase),
            });
        }
        return new { defaultName = cubes.DefaultName, connections = items };
    }

    /// <summary>Registers a cube from a #!dax-connect line built by the UI.</summary>
    [JsonRpcMethod("clrkernel/dax/addConnection", UseSingleObjectParameterDeserialization = true)]
    public object DaxAddConnection(SqlConnectParams p) {
        try {
            var name = Cubes.Connect(p?.Directive ?? string.Empty);
            return new { ok = true, name };
        } catch (Exception e) {
            return new { ok = false, error = e.Message };
        }
    }

    /// <summary>Removes a cube from the session.</summary>
    [JsonRpcMethod("clrkernel/dax/removeConnection", UseSingleObjectParameterDeserialization = true)]
    public object DaxRemoveConnection(SqlNameParams p) {
        var removed = Cubes.Cubes.Remove(p?.Name ?? string.Empty);
        return new { ok = removed };
    }

    // --- Execution (custom methods) ---------------------------------------

    [JsonRpcMethod("clrkernel/execute", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> Execute(ExecuteParams p) {
        var cellId = p?.CellId;
        var code = p?.Code ?? string.Empty;

        await _gate.WaitAsync().ConfigureAwait(false);
        try {
            void Notify(string method, DisplayData data) =>
                _ = Rpc?.NotifyWithParameterObjectAsync(method, new { cellId, data = data.Data, transient = data.Transient });

            DisplayDataEmitter.DisplayDataHandler = data => Notify("clrkernel/display", data);
            DisplayDataEmitter.UpdateDisplayDataHandler = data => Notify("clrkernel/updateDisplay", data);

            object result = null;
            using (var consoleProxy = new ConsoleProxy(line => Notify("clrkernel/display", new DisplayData(line)))) {
                consoleProxy.StartRedirect();
                result = await _engine.ExecuteAsync(code).ConfigureAwait(false);
            }

            return new {
                cellId,
                status = "ok",
                data = result switch {
                    DisplayData displayData => displayData.Data,
                    null => null,
                    var other => new Dictionary<string, object> { ["text/plain"] = other.ToString() },
                },
            };
        } catch (Exception e) {
            _logger.LogError(e, "execute failed for cell {CellId}", cellId);
            return new {
                cellId,
                status = "error",
                error = new { name = e.GetType().Name, message = e.Message, stack = e.StackTrace },
            };
        } finally {
            DisplayDataEmitter.DisplayDataHandler = null;
            DisplayDataEmitter.UpdateDisplayDataHandler = null;
            _gate.Release();
        }
    }

    // --- Helpers -----------------------------------------------------------

    private (string code, int offset) Resolve(TextDocumentPositionParams p) {
        if (p?.TextDocument?.Uri == null || !_documents.TryGetValue(p.TextDocument.Uri, out var text)) {
            return (null, 0);
        }
        return (text, PositionToOffset(text, p.Position));
    }

    private static int PositionToOffset(string text, Position position) {
        if (position == null) {
            return 0;
        }
        int line = 0, offset = 0;
        while (line < position.Line && offset < text.Length) {
            var nl = text.IndexOf('\n', offset);
            if (nl < 0) {
                return text.Length;
            }
            offset = nl + 1;
            line++;
        }
        return Math.Min(offset + position.Character, text.Length);
    }

    private static Position OffsetToPosition(string text, int offset) {
        offset = Math.Max(0, Math.Min(offset, text.Length));
        int line = 0, lineStart = 0;
        for (int i = 0; i < offset; i++) {
            if (text[i] == '\n') {
                line++;
                lineStart = i + 1;
            }
        }
        return new Position { Line = line, Character = offset - lineStart };
    }

    private static readonly Dictionary<string, int> _kindMap = new(StringComparer.Ordinal) {
        ["Class"] = 7,
        ["Delegate"] = 7,
        ["Structure"] = 22,
        ["Interface"] = 8,
        ["Enum"] = 13,
        ["EnumMember"] = 20,
        ["Constant"] = 21,
        ["Field"] = 5,
        ["Event"] = 23,
        ["Method"] = 2,
        ["ExtensionMethod"] = 2,
        ["Property"] = 10,
        ["Local"] = 6,
        ["Parameter"] = 6,
        ["RangeVariable"] = 6,
        ["Namespace"] = 9,
        ["Module"] = 9,
        ["Keyword"] = 14,
        ["Operator"] = 24,
        ["TypeParameter"] = 25,
    };

    private static int MapKind(string tag) =>
        tag != null && _kindMap.TryGetValue(tag, out var kind) ? kind : 1; // Text

    // PowerShell CompletionResultType name -> LSP CompletionItemKind.
    private static readonly Dictionary<string, int> _powerShellKindMap = new(StringComparer.Ordinal) {
        ["Command"] = 3,           // Function
        ["ParameterName"] = 5,     // Field
        ["ParameterValue"] = 12,   // Value
        ["Variable"] = 6,          // Variable
        ["Property"] = 10,         // Property
        ["Method"] = 2,            // Method
        ["ProviderItem"] = 17,     // File
        ["ProviderContainer"] = 19,// Folder
        ["Type"] = 7,              // Class
        ["Namespace"] = 9,         // Module
        ["Keyword"] = 14,          // Keyword
        ["DynamicKeyword"] = 14,   // Keyword
        ["History"] = 1,           // Text
        ["Text"] = 1,              // Text
    };

}

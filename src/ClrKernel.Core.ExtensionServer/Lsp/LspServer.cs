using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            PublishSqlDiagnostics(p.TextDocument.Uri);
        }
    }

    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void DidChange(DidChangeTextDocumentParams p) {
        if (p?.TextDocument?.Uri == null || p.ContentChanges == null || p.ContentChanges.Count == 0) {
            return;
        }
        // Full sync: last change carries the whole document.
        _documents[p.TextDocument.Uri] = p.ContentChanges[^1].Text ?? string.Empty;
        PublishSqlDiagnostics(p.TextDocument.Uri);
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
    private void PublishSqlDiagnostics(string uri) {
        if (Rpc == null || uri == null) {
            return;
        }
        if (!_languages.TryGetValue(uri, out var lang) || !lang.Equals("sql", StringComparison.OrdinalIgnoreCase)) {
            return;
        }
        var text = _documents.TryGetValue(uri, out var t) ? t : string.Empty;
        var diagnostics = new List<Diagnostic>();
        try {
            foreach (var d in ClrKernel.Sql.TSqlSyntax.Check(text)) {
                diagnostics.Add(new Diagnostic {
                    Range = new Range {
                        Start = new Position { Line = d.Line, Character = d.Column },
                        End = new Position { Line = d.EndLine, Character = d.EndColumn },
                    },
                    Severity = 1,
                    Source = "clrkernel-sql",
                    Code = d.Number.ToString(),
                    Message = d.Message,
                });
            }
        } catch (Exception e) {
            _logger.LogWarning(e, "SQL diagnostics failed");
            return;
        }
        _ = Rpc.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics",
            new PublishDiagnosticsParams { Uri = uri, Diagnostics = diagnostics });
    }

    private bool IsPowerShell(TextDocumentPositionParams p) =>
        p?.TextDocument?.Uri != null
        && _languages.TryGetValue(p.TextDocument.Uri, out var lang)
        && lang.Equals("powershell", StringComparison.OrdinalIgnoreCase);

    private bool IsSql(TextDocumentPositionParams p) =>
        p?.TextDocument?.Uri != null
        && _languages.TryGetValue(p.TextDocument.Uri, out var lang)
        && lang.Equals("sql", StringComparison.OrdinalIgnoreCase);

    private bool IsDax(TextDocumentPositionParams p) =>
        p?.TextDocument?.Uri != null
        && _languages.TryGetValue(p.TextDocument.Uri, out var lang)
        && lang.Equals("dax", StringComparison.OrdinalIgnoreCase);

    // --- Language features -------------------------------------------------

    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionList> Completion(TextDocumentPositionParams p) {
        var (code, offset) = Resolve(p);
        if (code == null) {
            return new CompletionList();
        }

        if (IsPowerShell(p)) {
            return await PowerShellCompletion(code, offset).ConfigureAwait(false);
        }

        if (IsSql(p)) {
            return SqlCompletion(code, offset);
        }

        if (IsDax(p)) {
            return DaxCompletion(code, offset);
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

    // PowerShell completions come from the session runspace, so they reflect
    // cmdlets, parameters, provider paths, and session-defined variables.
    private async Task<CompletionList> PowerShellCompletion(string code, int offset) {
        await _gate.WaitAsync().ConfigureAwait(false);
        ClrKernel.Language.PowerShell.PowerShellCompletion completion;
        try {
            completion = await Task.Run(() => _engine.PowerShell.Complete(code, offset)).ConfigureAwait(false);
        } catch (Exception e) {
            _logger.LogWarning(e, "PowerShell completion failed");
            return new CompletionList();
        } finally {
            _gate.Release();
        }

        var startPos = OffsetToPosition(code, completion.ReplaceStart);
        var endPos = OffsetToPosition(code, completion.ReplaceStart + completion.ReplaceLength);
        var list = new CompletionList { IsIncomplete = false };
        foreach (var item in completion.Items) {
            list.Items.Add(new CompletionItem {
                Label = item.Label,
                Kind = MapPowerShellKind(item.Kind),
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

    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public async Task<Hover> Hover(TextDocumentPositionParams p) {
        var (code, offset) = Resolve(p);
        if (code == null) {
            return null;
        }

        if (IsPowerShell(p)) {
            return await PowerShellHover(code, offset).ConfigureAwait(false);
        }

        if (IsSql(p)) {
            return SqlHover(code, offset);
        }

        if (IsDax(p)) {
            return DaxHover(code, offset);
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

    // PowerShell hover: command help, parameter, or session variable type/value.
    private async Task<Hover> PowerShellHover(string code, int offset) {
        await _gate.WaitAsync().ConfigureAwait(false);
        ClrKernel.Language.PowerShell.PowerShellHover hover;
        try {
            hover = await Task.Run(() => _engine.PowerShell.Hover(code, offset)).ConfigureAwait(false);
        } catch (Exception e) {
            _logger.LogWarning(e, "PowerShell hover failed");
            return null;
        } finally {
            _gate.Release();
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

    // PowerShell signature help: one signature per parameter set of the command.
    private async Task<SignatureHelp> PowerShellSignatureHelp(string code, int offset) {
        await _gate.WaitAsync().ConfigureAwait(false);
        ClrKernel.Language.PowerShell.PowerShellSignatureHelp help;
        try {
            help = await Task.Run(() => _engine.PowerShell.SignatureHelp(code, offset)).ConfigureAwait(false);
        } catch (Exception e) {
            _logger.LogWarning(e, "PowerShell signature help failed");
            return null;
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

    [JsonRpcMethod("textDocument/signatureHelp", UseSingleObjectParameterDeserialization = true)]
    public async Task<SignatureHelp> SignatureHelp(TextDocumentPositionParams p) {
        var (code, offset) = Resolve(p);
        if (code == null) {
            return null;
        }

        if (IsPowerShell(p)) {
            return await PowerShellSignatureHelp(code, offset).ConfigureAwait(false);
        }

        if (IsSql(p) || IsDax(p)) {
            return null; // signature help is not offered for SQL/DAX in this foundation
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

    // T-SQL keyword / function / type completion (offline, no connection needed).
    // Session-aware completion context: connection names + pipeline step names
    // (from registered steps and any -- step declared in open SQL cells).
    private ClrKernel.Sql.SqlCompletionContext BuildSqlContext() {
        var connections = _engine.Sql.Connections.All.Select(c => c.Name).ToList();
        var steps = new HashSet<string>(_engine.Sql.Pipeline.All.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _languages) {
            if (!kv.Value.Equals("sql", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            if (_documents.TryGetValue(kv.Key, out var text)) {
                foreach (Match m in _stepDeclaration.Matches(text)) {
                    steps.Add(m.Groups[1].Value);
                }
            }
        }
        return new ClrKernel.Sql.SqlCompletionContext {
            ConnectionNames = connections,
            StepNames = steps.ToList(),
        };
    }

    private static readonly System.Text.RegularExpressions.Regex _stepDeclaration =
        new System.Text.RegularExpressions.Regex(@"(?im)^\s*--\s*step\s+([A-Za-z0-9_-]+)");

    private CompletionList SqlCompletion(string code, int offset) {
        ClrKernel.Sql.SqlCompletion completion;
        try {
            completion = ClrKernel.Sql.SqlLanguage.Complete(code, offset, BuildSqlContext());
        } catch (Exception e) {
            _logger.LogWarning(e, "SQL completion failed");
            return new CompletionList();
        }
        var startPos = OffsetToPosition(code, completion.ReplaceStart);
        var endPos = OffsetToPosition(code, completion.ReplaceStart + completion.ReplaceLength);
        var list = new CompletionList { IsIncomplete = false };
        foreach (var item in completion.Items) {
            list.Items.Add(new CompletionItem {
                Label = item.Label,
                Kind = MapSqlKind(item.Kind),
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

    private Hover SqlHover(string code, int offset) {
        ClrKernel.Sql.SqlHover hover;
        try {
            hover = ClrKernel.Sql.SqlLanguage.Hover(code, offset);
        } catch (Exception e) {
            _logger.LogWarning(e, "SQL hover failed");
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

    private CompletionList DaxCompletion(string code, int offset) {
        ClrKernel.AnalysisServices.DaxCompletion completion;
        try {
            var context = new ClrKernel.AnalysisServices.DaxCompletionContext {
                CubeNames = _engine.Cubes.Cubes.Names.ToList(),
            };
            completion = ClrKernel.AnalysisServices.DaxLanguage.Complete(code, offset, context);
        } catch (Exception e) {
            _logger.LogWarning(e, "DAX completion failed");
            return new CompletionList();
        }
        var startPos = OffsetToPosition(code, completion.ReplaceStart);
        var endPos = OffsetToPosition(code, completion.ReplaceStart + completion.ReplaceLength);
        var list = new CompletionList { IsIncomplete = false };
        foreach (var item in completion.Items) {
            list.Items.Add(new CompletionItem {
                Label = item.Label,
                Kind = MapSqlKind(item.Kind == "cube" ? "connection" : item.Kind),
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

    private Hover DaxHover(string code, int offset) {
        ClrKernel.AnalysisServices.DaxHover hover;
        try {
            hover = ClrKernel.AnalysisServices.DaxLanguage.Hover(code, offset);
        } catch (Exception e) {
            _logger.LogWarning(e, "DAX hover failed");
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

    // LSP CompletionItemKind values.
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
        var sql = _engine.Sql;
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
            var spec = _engine.Sql.Connect(p?.Directive ?? string.Empty).Spec;
            if (!string.IsNullOrEmpty(p?.Secret)) {
                _engine.Sql.StoreSecret(spec.EffectiveSecretRef, p.Secret);
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
            var provider = _engine.Sql.StoreSecret(p?.SecretRef ?? string.Empty, p?.Secret ?? string.Empty);
            return new { ok = true, provider };
        } catch (Exception e) {
            return new { ok = false, error = e.Message };
        }
    }

    /// <summary>Removes a connection from the session registry.</summary>
    [JsonRpcMethod("clrkernel/sql/removeConnection", UseSingleObjectParameterDeserialization = true)]
    public object SqlRemoveConnection(SqlNameParams p) {
        var removed = _engine.Sql.Connections.Remove(p?.Name ?? string.Empty);
        return new { ok = removed };
    }

    /// <summary>Sets the default connection.</summary>
    [JsonRpcMethod("clrkernel/sql/setDefault", UseSingleObjectParameterDeserialization = true)]
    public object SqlSetDefault(SqlNameParams p) {
        try {
            _engine.Sql.Connections.SetDefault(p?.Name ?? string.Empty);
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
            var path = _engine.Sql.FindConfigFile(NullIfBlank(p?.Directory));
            var names = path != null ? _engine.Sql.ConfigConnectionNames(path) : System.Array.Empty<string>();
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
            var loaded = _engine.Sql.LoadFromConfig(NullIfBlank(p?.Directory));
            return new { ok = true, loaded };
        } catch (Exception e) {
            return new { ok = false, error = e.Message };
        }
    }

    /// <summary>Writes a registered connection into a connections.json file (secret-free).</summary>
    [JsonRpcMethod("clrkernel/sql/saveConnection", UseSingleObjectParameterDeserialization = true)]
    public object SqlSaveConnection(SqlSaveConfigParams p) {
        try {
            var path = _engine.Sql.SaveConnectionToConfig(p?.Name ?? string.Empty, p?.FilePath ?? string.Empty);
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
        var cubes = _engine.Cubes.Cubes;
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
            var name = _engine.Cubes.Connect(p?.Directive ?? string.Empty);
            return new { ok = true, name };
        } catch (Exception e) {
            return new { ok = false, error = e.Message };
        }
    }

    /// <summary>Removes a cube from the session.</summary>
    [JsonRpcMethod("clrkernel/dax/removeConnection", UseSingleObjectParameterDeserialization = true)]
    public object DaxRemoveConnection(SqlNameParams p) {
        var removed = _engine.Cubes.Cubes.Remove(p?.Name ?? string.Empty);
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

    private static int MapPowerShellKind(string type) =>
        type != null && _powerShellKindMap.TryGetValue(type, out var kind) ? kind : 1; // Text
}

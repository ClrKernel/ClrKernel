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

/// <summary>
/// Params for the <c>clrkernel/connections/*</c> methods. One shape for every language: the
/// <see cref="LanguageId"/> selects the catalog, and each method reads the fields it needs.
/// </summary>
/// <remarks>
/// The connect <see cref="Directive"/> is the language's own <c>#!x-connect</c> line. Keeping the
/// directive as the wire format means the UI never has to model each provider's options — it
/// builds the line a user could have typed, and the language parses it.
/// </remarks>
public sealed class ConnectionParams {
    /// <summary>Which language's connections — "sql", "dax", … Required.</summary>
    public string LanguageId { get; set; }

    /// <summary>A <c>#!x-connect</c> line, for add.</summary>
    public string Directive { get; set; }

    /// <summary>A password to store against the new connection's secret ref. Never persisted
    /// to a notebook or config file.</summary>
    public string Secret { get; set; }

    /// <summary>A connection name, for remove / setDefault / saveConfig.</summary>
    public string Name { get; set; }

    /// <summary>The notebook's directory, for the connections.json methods.</summary>
    public string Directory { get; set; }

    /// <summary>Target file, for saveConfig.</summary>
    public string FilePath { get; set; }

    /// <summary>The notebook whose session holds the connections.</summary>
    public string NotebookUri { get; set; }
}

/// <summary>
/// The unified ClrKernel language server (Option A): standard LSP language
/// features (completion, hover, signature help) and cell execution
/// (clrkernel/execute + clrkernel/display notifications) over one connection.
/// Each NOTEBOOK gets its own <see cref="InteractiveScriptEngine"/> session —
/// keyed by the notebook path carried in every cell URI — so completion sees
/// that notebook's live REPL state (prior-cell symbols, #r "nuget:" types,
/// imports) and never another notebook's variables.
/// </summary>
public sealed class LspServer {
    // One engine + language service per notebook. A cell URI is
    // "vscode-notebook-cell:/path/to/nb.md#cellId": the path identifies the
    // notebook, and the extension's cellId IS the cell URI, so every request
    // that matters can be routed without protocol changes.
    private sealed class NotebookSession {
        public InteractiveScriptEngine Engine;
        public ScriptLanguageService Language;
    }

    private readonly ConcurrentDictionary<string, NotebookSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILoggerFactory _loggerFactory;
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
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger(nameof(LspServer));
    }

    /// <summary>
    /// The notebook identity behind a cell/document URI or a notebook URI: the
    /// file path when one can be parsed (a cell URI and the notebook's own URI
    /// differ only in scheme and fragment), the fragment-stripped URI otherwise.
    /// </summary>
    internal static string NotebookKeyFor(string uri) {
        if (string.IsNullOrEmpty(uri)) {
            return string.Empty;
        }
        var hash = uri.IndexOf('#');
        var trimmed = hash < 0 ? uri : uri[..hash];
        try {
            var path = new Uri(trimmed).LocalPath;
            if (!string.IsNullOrEmpty(path)) {
                // A Windows cell URI parses to "/C:/…" where a file URI gives "C:\…".
                if (path.Length >= 3 && path[0] == '/' && path[2] == ':') {
                    path = path[1..];
                }
                return path.Replace('\\', '/');
            }
        } catch (UriFormatException) {
            // Not a URI (test harnesses send bare ids) — the raw string is the key.
        }
        return trimmed;
    }

    private NotebookSession SessionFor(string uri) =>
        _sessions.GetOrAdd(NotebookKeyFor(uri), key => new NotebookSession {
            Engine = new InteractiveScriptEngine(
                DirectoryFor(key), _loggerFactory.CreateLogger(nameof(InteractiveScriptEngine))),
            Language = new ScriptLanguageService(),
        });

    // The notebook's own folder, so #load and relative paths resolve beside it.
    private static string DirectoryFor(string notebookKey) {
        try {
            var directory = System.IO.Path.GetDirectoryName(notebookKey);
            if (!string.IsNullOrEmpty(directory) && System.IO.Directory.Exists(directory)) {
                return directory;
            }
        } catch (ArgumentException) {
            // Invalid path characters — fall through to the process directory.
        }
        return Environment.CurrentDirectory;
    }

    // --- Lifecycle ---------------------------------------------------------

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public InitializeResult Initialize(JsonElement _) {
        return new InitializeResult {
            Capabilities = new ServerCapabilities {
                TextDocumentSync = 1, // full
                CompletionProvider = new CompletionOptions {
                    TriggerCharacters = new List<string> { ".", " " },
                    ResolveProvider = true,
                },
                HoverProvider = true,
                SignatureHelpProvider = new SignatureHelpOptions {
                    TriggerCharacters = new List<string> { "(", "," },
                },
                DefinitionProvider = true,
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

    private void PublishDiagnostics(string uri) {
        if (Rpc == null || uri == null) {
            return;
        }
        if (!_languages.TryGetValue(uri, out var lang)) {
            return;
        }
        var services = SessionFor(uri).Engine.Languages.ById(lang)?.Services;
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
        return SessionFor(p.TextDocument.Uri).Engine.Languages.ById(lang)?.Services;
    }

    // Every open cell of one language IN THE SAME NOTEBOOK, so completion can
    // see sibling cells without leaking another notebook's.
    private LanguageServiceContext ContextFor(string languageId, string notebookKey) {
        var open = new List<string>();
        foreach (var kv in _languages) {
            if (kv.Value.Equals(languageId, StringComparison.OrdinalIgnoreCase)
                && NotebookKeyFor(kv.Key).Equals(notebookKey, StringComparison.OrdinalIgnoreCase)
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

        var session = SessionFor(p.TextDocument.Uri);
        await _gate.WaitAsync().ConfigureAwait(false);
        CompletionResultDto result;
        int generation;
        try {
            result = await session.Language.GetCompletionsAsync(session.Engine.SnapshotState(), code, offset).ConfigureAwait(false);
            generation = session.Language.LastCompletionGeneration;
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
                Data = $"{generation}:{list.Items.Count}:{NotebookKeyFor(p.TextDocument.Uri)}",
                TextEdit = new TextEdit {
                    Range = new Range { Start = startPos, End = endPos },
                    NewText = item.InsertText,
                },
            });
        }
        return list;
    }

    // Fills in the focused item's documentation (signature + /// summary) lazily,
    // IDE-style. Items without Data — the non-C# language cells — pass through.
    [JsonRpcMethod("completionItem/resolve", UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionItem> ResolveCompletionItem(CompletionItem item) {
        // Data is "<generation>:<index>:<notebookKey>": the key routes to the
        // notebook's session, and the generation pins the index to the list
        // that produced it, so a resolve queued behind a newer completion
        // can't serve another symbol's documentation.
        var parts = item?.Data?.ToString().Split(':', 3);
        if (parts is not { Length: 3 }
            || !int.TryParse(parts[0], out var generation)
            || !int.TryParse(parts[1], out var index)
            || !_sessions.TryGetValue(parts[2], out var session)) {
            return item;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        string text;
        try {
            text = await session.Language.GetCompletionDocumentationAsync(generation, index).ConfigureAwait(false);
        } catch {
            text = null; // documentation is cosmetic — never fault the RPC channel for it
        } finally {
            _gate.Release();
        }
        if (string.IsNullOrEmpty(text)) {
            return item;
        }

        // First line is the signature; the rest is prose documentation.
        var newline = text.IndexOf('\n');
        var value = newline < 0
            ? "```csharp\n" + text + "\n```"
            : "```csharp\n" + text[..newline].TrimEnd() + "\n```\n" + text[(newline + 1)..];
        item.Documentation = new MarkupContent { Kind = "markdown", Value = value };
        return item;
    }

    [JsonRpcMethod("textDocument/definition", UseSingleObjectParameterDeserialization = true)]
    public async Task<List<LocationLink>> Definition(TextDocumentPositionParams p, CancellationToken cancellationToken = default) {
        var (code, offset) = Resolve(p);
        if (code == null || ServicesFor(p) != null) {
            return new List<LocationLink>(); // only C# cells carry definitions today
        }

        var session = SessionFor(p.TextDocument.Uri);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ClrKernel.Core.LanguageServices.DefinitionResultDto result;
        try {
            result = await session.Language.GetDefinitionsAsync(session.Engine.SnapshotState(), code, offset, cancellationToken)
                .ConfigureAwait(false);
        } finally {
            _gate.Release();
        }

        // Metadata symbol: serve the decompiled type as a virtual document the
        // client resolves through clrkernel/metadataSource.
        if (result.Locations.Count == 0 && result.Metadata != null) {
            _metadataSources[result.Metadata.Key] = result.Metadata.Text;
            var selection = new Range {
                Start = OffsetToPosition(result.Metadata.Text, result.Metadata.Start),
                End = OffsetToPosition(result.Metadata.Text, result.Metadata.Start + result.Metadata.Length),
            };
            return new List<LocationLink> {
                new() {
                    TargetUri = "clrkernel-metadata:/" + result.Metadata.Key,
                    TargetRange = selection,
                    TargetSelectionRange = selection,
                },
            };
        }

        // Candidate cells for prior-submission definitions: the current cell first
        // (a definition executed from it still lives there), then every other open
        // cell of the same language in the same notebook.
        var currentUri = p.TextDocument.Uri;
        var notebookKey = NotebookKeyFor(currentUri);
        var language = _languages.TryGetValue(currentUri, out var l) ? l : "csharp";
        var candidates = new List<(string Uri, string Text)> { (currentUri, code) };
        foreach (var kv in _languages) {
            if (kv.Key != currentUri
                && kv.Value.Equals(language, StringComparison.OrdinalIgnoreCase)
                && NotebookKeyFor(kv.Key).Equals(notebookKey, StringComparison.OrdinalIgnoreCase)
                && _documents.TryGetValue(kv.Key, out var text)) {
                candidates.Add((kv.Key, text));
            }
        }
        return MapDefinitions(result.Locations, currentUri, code, candidates);
    }

    // Decompiled sources by virtual-document key, kept for the client's content
    // provider to fetch (and refetch, e.g. after a window reload).
    private readonly ConcurrentDictionary<string, string> _metadataSources = new();

    [JsonRpcMethod("clrkernel/metadataSource", UseSingleObjectParameterDeserialization = true)]
    public object MetadataSource(MetadataSourceParams p) {
        var text = p?.Key != null && _metadataSources.TryGetValue(p.Key, out var t)
            ? t
            : "// Decompiled source is no longer available - use Go to Definition again.";
        return new { text };
    }

    public sealed class MetadataSourceParams {
        public string Key { get; set; }
    }

    /// <summary>
    /// Turns service definitions into LSP location links. A current-cell definition
    /// maps by offset (the peek frames the whole declaration when known); a definition
    /// from an executed submission is found by locating its defining line in an open
    /// cell (exact line first, then whitespace-insensitive with the column shifted).
    /// A line no open cell contains — edited since execution, or from a closed
    /// notebook — yields no location.
    /// </summary>
    internal static List<LocationLink> MapDefinitions(
        IReadOnlyList<ClrKernel.Core.LanguageServices.DefinitionLocationDto> definitions,
        string currentUri, string currentCode, IReadOnlyList<(string Uri, string Text)> openDocs) {
        var locations = new List<LocationLink>();
        foreach (var definition in definitions) {
            if (definition.InCurrentCell) {
                var selection = new Range {
                    Start = OffsetToPosition(currentCode, definition.Start),
                    End = OffsetToPosition(currentCode, definition.Start + definition.Length),
                };
                var target = definition.FullStart >= 0
                    ? new Range {
                        Start = OffsetToPosition(currentCode, definition.FullStart),
                        End = OffsetToPosition(currentCode, definition.FullStart + definition.FullLength),
                    }
                    : selection;
                locations.Add(new LocationLink {
                    TargetUri = currentUri,
                    TargetRange = target,
                    TargetSelectionRange = selection,
                });
                continue;
            }
            if (string.IsNullOrWhiteSpace(definition.SourceLine)) {
                continue;
            }
            foreach (var (uri, text) in openDocs) {
                var start = FindLine(text, definition.SourceLine, out var columnAdjust);
                if (start < 0) {
                    continue;
                }
                var offset = start + definition.ColumnInLine + columnAdjust;
                var selection = new Range {
                    Start = OffsetToPosition(text, offset),
                    End = OffsetToPosition(text, offset + definition.Length),
                };
                locations.Add(new LocationLink {
                    TargetUri = uri,
                    TargetRange = selection,
                    TargetSelectionRange = selection,
                });
                break;
            }
        }
        return locations;
    }

    // The offset of the first line in <paramref name="text"/> equal to
    // <paramref name="line"/> — exact match preferred, then trimmed-equal with
    // the column adjusted by the indentation difference. -1 when absent.
    private static int FindLine(string text, string line, out int columnAdjust) {
        columnAdjust = 0;
        var offset = 0;
        var trimmedTarget = line.Trim();
        var fallbackStart = -1;
        var fallbackAdjust = 0;
        foreach (var docLine in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n')) {
            if (docLine == line) {
                return offset;
            }
            if (fallbackStart < 0 && docLine.Trim() == trimmedTarget && trimmedTarget.Length > 0) {
                fallbackStart = offset;
                fallbackAdjust = LeadingWhitespace(docLine) - LeadingWhitespace(line);
            }
            offset += docLine.Length + 1;
        }
        if (fallbackStart >= 0) {
            columnAdjust = fallbackAdjust;
            return fallbackStart;
        }
        return -1;
    }

    private static int LeadingWhitespace(string line) {
        var i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) {
            i++;
        }
        return i;
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


        var session = SessionFor(p.TextDocument.Uri);
        await _gate.WaitAsync().ConfigureAwait(false);
        HoverDto hover;
        try {
            hover = await session.Language.GetHoverAsync(session.Engine.SnapshotState(), code, offset).ConfigureAwait(false);
        } finally {
            _gate.Release();
        }

        if (hover == null || string.IsNullOrEmpty(hover.Markdown)) {
            return null;
        }
        var value = "```csharp\n" + hover.Markdown + "\n```";
        if (!string.IsNullOrEmpty(hover.Documentation)) {
            value += "\n\n" + hover.Documentation;
        }
        return new Hover {
            Contents = new MarkupContent { Kind = "markdown", Value = value },
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


        var session = SessionFor(p.TextDocument.Uri);
        await _gate.WaitAsync().ConfigureAwait(false);
        SignatureHelpDto help;
        try {
            help = await session.Language.GetSignatureHelpAsync(session.Engine.SnapshotState(), code, offset).ConfigureAwait(false);
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
            completion = await services.CompleteAsync(
                code, offset, ContextFor(languageId, NotebookKeyFor(p.TextDocument.Uri))).ConfigureAwait(false);
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

    // --- Connection management (custom methods for the extension UI) ------
    //
    // One set of methods for every language, routed through IConnectionCatalog the same way
    // language features route through ICellLanguageServices. This replaced eight clrkernel/sql/*
    // and three clrkernel/dax/* methods that were the same four operations written twice, and is
    // what lets this host reference no Language.* package at all.

    /// <summary>The named connections for a language, secret-free, for the connection panel.</summary>
    [JsonRpcMethod("clrkernel/connections/list", UseSingleObjectParameterDeserialization = true)]
    public object ConnectionsList(ConnectionParams p) {
        var catalog = CatalogFor(p);
        if (catalog is null) {
            return new { ok = false, error = NoCatalog(p) };
        }
        var items = new List<object>();
        foreach (var c in catalog.List()) {
            items.Add(new {
                name = c.Name,
                server = c.Server,
                database = c.Database,
                auth = c.Auth,
                user = c.User,
                describe = c.Describe,
                needsSecret = c.NeedsSecret,
                secretRef = c.SecretRef,
                isDefault = c.IsDefault,
            });
        }
        return new { ok = true, defaultName = catalog.DefaultName, connections = items };
    }

    /// <summary>Registers/updates a connection from a connect directive built by the UI.</summary>
    [JsonRpcMethod("clrkernel/connections/add", UseSingleObjectParameterDeserialization = true)]
    public object ConnectionsAdd(ConnectionParams p) => Guarded(p, catalog => {
        var name = catalog.Add(p?.Directive ?? string.Empty, p?.Secret);
        return new { ok = true, name };
    });

    /// <summary>Removes a connection from the session.</summary>
    [JsonRpcMethod("clrkernel/connections/remove", UseSingleObjectParameterDeserialization = true)]
    public object ConnectionsRemove(ConnectionParams p) =>
        Guarded(p, catalog => new { ok = catalog.Remove(p?.Name ?? string.Empty) });

    /// <summary>Sets the default connection.</summary>
    [JsonRpcMethod("clrkernel/connections/setDefault", UseSingleObjectParameterDeserialization = true)]
    public object ConnectionsSetDefault(ConnectionParams p) => Guarded(p, catalog => {
        catalog.SetDefault(p?.Name ?? string.Empty);
        return new { ok = true };
    });

    /// <summary>Whether a connections.json exists at/above the notebook's directory, and what
    /// it holds — so the UI can offer to confirm or choose a file.</summary>
    [JsonRpcMethod("clrkernel/connections/configStatus", UseSingleObjectParameterDeserialization = true)]
    public object ConnectionsConfigStatus(ConnectionParams p) => GuardedConfig(p, config => {
        var status = config.Status(p?.Directory);
        return new { ok = true, found = status.Found, path = status.Path, names = status.Names };
    });

    /// <summary>Registers this language's entries from the nearest connections.json (called on
    /// notebook open, so saved connections are available without re-adding them).</summary>
    [JsonRpcMethod("clrkernel/connections/loadConfig", UseSingleObjectParameterDeserialization = true)]
    public object ConnectionsLoadConfig(ConnectionParams p) =>
        GuardedConfig(p, config => new { ok = true, loaded = config.LoadFromConfig(p?.Directory) });

    /// <summary>Writes a registered connection into a connections.json file (secret-free).</summary>
    [JsonRpcMethod("clrkernel/connections/saveConfig", UseSingleObjectParameterDeserialization = true)]
    public object ConnectionsSaveConfig(ConnectionParams p) =>
        GuardedConfig(p, config => new { ok = true, path = config.SaveToConfig(p?.Name ?? string.Empty, p?.FilePath ?? string.Empty) });

    // Connections live in the notebook's session. The UI sends the notebook's
    // URI; when an older extension doesn't, a lone session is unambiguous.
    private IConnectionCatalog CatalogFor(ConnectionParams p) =>
        SessionForConnections(p)?.Engine.Languages.ById(p?.LanguageId ?? string.Empty)?.Connections;

    private NotebookSession SessionForConnections(ConnectionParams p) {
        if (!string.IsNullOrEmpty(p?.NotebookUri)) {
            return SessionFor(p.NotebookUri);
        }
        var all = _sessions.Values;
        return all.Count == 1 ? System.Linq.Enumerable.First(all) : null;
    }

    private static string NoCatalog(ConnectionParams p) =>
        $"No connection support for language '{p?.LanguageId}'.";

    // Every method reports failure the same way ({ ok = false, error }) rather than throwing, so
    // the UI can show the server's message instead of a transport-level JSON-RPC fault.
    private object Guarded(ConnectionParams p, Func<IConnectionCatalog, object> act) {
        var catalog = CatalogFor(p);
        if (catalog is null) {
            return new { ok = false, error = NoCatalog(p) };
        }
        try {
            return act(catalog);
        } catch (Exception e) {
            return new { ok = false, error = e.Message };
        }
    }

    // A language only offers these when its catalog also implements IConfigBackedConnections —
    // a type check, not a capability flag, so a provider without config files simply says so.
    private object GuardedConfig(ConnectionParams p, Func<IConfigBackedConnections, object> act) {
        if (CatalogFor(p) is not IConfigBackedConnections config) {
            return new { ok = false, error = $"Language '{p?.LanguageId}' has no connections.json support." };
        }
        try {
            return act(config);
        } catch (Exception e) {
            return new { ok = false, error = e.Message };
        }
    }

    // --- Execution (custom methods) ---------------------------------------

    public sealed class RestartParams {
        public string NotebookUri { get; set; }
    }

    /// <summary>
    /// Drops ONE notebook's session — variables, connections, cell libraries,
    /// language runspaces — leaving every other notebook's state untouched. The
    /// next request from that notebook starts a fresh engine.
    /// </summary>
    [JsonRpcMethod("clrkernel/restart", UseSingleObjectParameterDeserialization = true)]
    public object Restart(RestartParams p) {
        var key = NotebookKeyFor(p?.NotebookUri);
        var restarted = key.Length > 0 && _sessions.TryRemove(key, out _);
        _logger.LogInformation("session restart for {Notebook}: {Restarted}", key, restarted);
        return new { ok = true, restarted };
    }

    [JsonRpcMethod("clrkernel/execute", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> Execute(ExecuteParams p) {
        var cellId = p?.CellId;
        var code = p?.Code ?? string.Empty;

        // The cellId is the cell's URI, so it routes to the notebook's session.
        var session = SessionFor(cellId);
        await _gate.WaitAsync().ConfigureAwait(false);
        try {
            void Notify(string method, DisplayData data) =>
                _ = Rpc?.NotifyWithParameterObjectAsync(method, new { cellId, data = data.Data, transient = data.Transient });

            EnsureDisplayHooked();
            _currentCellId = cellId;

            object result = null;
            using (var consoleProxy = new ConsoleProxy(line => Notify("clrkernel/display", new DisplayData(line)))) {
                consoleProxy.StartRedirect();
                result = await session.Engine.ExecuteAsync(code).ConfigureAwait(false);
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
            _currentCellId = null;
            _gate.Release();
        }
    }

    // The single display channel: display cells raise DisplayValues events; this
    // host bundles the concept and routes to the notebook cell that created the
    // display — remembered per display_id, so updates from background work reach
    // the right output after the cell has finished.
    private string _currentCellId;
    private bool _displayHooked;
    private readonly Dictionary<string, string> _displayCells = new();

    private void EnsureDisplayHooked() {
        if (_displayHooked) {
            return;
        }
        _displayHooked = true;
        DisplayValues.OnCellDisplayed += cell => {
            var cellId = _currentCellId;
            if (cellId == null) {
                return;
            }
            lock (_displayCells) {
                _displayCells[cell.DisplayId] = cellId;
            }
            NotifyDisplay("clrkernel/display", cellId, MimeBundler.Bundle(cell));
        };
        DisplayValues.OnCellUpdated += cell => {
            string cellId;
            lock (_displayCells) {
                _displayCells.TryGetValue(cell.DisplayId, out cellId);
            }
            cellId ??= _currentCellId;
            if (cellId != null) {
                NotifyDisplay("clrkernel/updateDisplay", cellId, MimeBundler.Bundle(cell));
            }
        };
    }

    private void NotifyDisplay(string method, string cellId, DisplayData data) =>
        _ = Rpc?.NotifyWithParameterObjectAsync(method, new { cellId, data = data.Data, transient = data.Transient });

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

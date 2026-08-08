using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core;
using ClrKernel.LanguageServices;
using ClrKernel.Primitives;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace ClrKernel.Server.Lsp;

/// <summary>Params for the custom clrkernel/execute request.</summary>
public sealed class ExecuteParams {
    public string CellId { get; set; }
    public string Code { get; set; }
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
        }
    }

    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void DidChange(DidChangeTextDocumentParams p) {
        if (p?.TextDocument?.Uri == null || p.ContentChanges == null || p.ContentChanges.Count == 0) {
            return;
        }
        // Full sync: last change carries the whole document.
        _documents[p.TextDocument.Uri] = p.ContentChanges[^1].Text ?? string.Empty;
    }

    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DidCloseTextDocumentParams p) {
        if (p?.TextDocument?.Uri != null) {
            _documents.TryRemove(p.TextDocument.Uri, out _);
        }
    }

    // --- Language features -------------------------------------------------

    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionList> Completion(TextDocumentPositionParams p) {
        var (code, offset) = Resolve(p);
        if (code == null) {
            return new CompletionList();
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
}

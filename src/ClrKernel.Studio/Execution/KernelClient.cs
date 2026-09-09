using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using StreamJsonRpc;

namespace ClrKernel.Studio;

/// <summary>Which RPC surface a kernel process speaks.</summary>
public enum KernelMode {
    /// <summary><c>clrkernel serve</c> — execution and nothing else. What scheduled
    /// job runs use: they need no language features, and it is the surface
    /// <see cref="JobExecutor"/> has always driven.</summary>
    Serve,

    /// <summary><c>clrkernel lsp</c> — execution <em>and</em> language features over one
    /// connection, the same server the VS Code extension drives. The editor uses this so
    /// completion sees the live REPL, and so a feature added to the server reaches both
    /// front ends instead of one.</summary>
    Lsp,
}

/// <summary>
/// JSON-RPC client for a <c>clrkernel</c> child process. Content-Length framed
/// requests plus notifications streaming output while a cell runs.
/// <para>
/// Both kernel surfaces carry the same payloads — <c>{cellId, code}</c> in,
/// <c>{cellId, status, data|error}</c> back, <c>{cellId, data, transient}</c> on a
/// display — and differ only in what the methods are called. That is why this is a
/// name map rather than a second client.
/// </para>
/// </summary>
public sealed class KernelClient : IDisposable {
    private readonly JsonRpc _rpc;
    private readonly KernelMode _mode;

    private bool Lsp => _mode == KernelMode.Lsp;

    /// <summary>The reason and exception of the last disconnect, or null while connected.</summary>
    public string LastDisconnect { get; private set; }

    /// <summary>Raised for every display/updateDisplay notification from the kernel.</summary>
    public event Action<DisplayNotification> DisplayReceived;

    /// <summary>Raised when a session's language set grew — a package loaded with
    /// <c>#r</c> registering a cell language mid-notebook. The set decides how the
    /// notebook's cells are parsed, so a stale one is not cosmetic.</summary>
    public event Action<LanguagesReply> LanguagesChanged;

    /// <summary>Raised when the server reports (or retracts) a document's problems.
    /// Push, not reply: it arrives after a didOpen/didChange, unprompted.</summary>
    public event Action<DiagnosticsNotification> DiagnosticsReceived;

    /// <param name="sendingStream">Stream requests are written to (the child's stdin).</param>
    /// <param name="receivingStream">Stream replies are read from (the child's stdout).</param>
    /// <param name="mode">Which surface the child was started with.</param>
    public KernelClient(Stream sendingStream, Stream receivingStream, KernelMode mode = KernelMode.Serve) {
        _mode = mode;
        // Mirrors the kernel hosts' wire shape: camelCase names, so the
        // LanguageDescriptor payload in the initialize reply binds directly.
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        formatter.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        _rpc = new JsonRpc(new CountingHandler(sendingStream, receivingStream, formatter, _displays));
        _rpc.AddLocalRpcTarget(new NotificationSink(this));
        // Why the connection went, for the error a caller sees. StreamJsonRpc's own
        // "connection lost before the request could complete" names no cause.
        _rpc.Disconnected += (_, e) => LastDisconnect = $"{e.Reason}: {e.Description} {e.Exception}";
        _rpc.StartListening();
    }

    // Display notifications read off the wire versus handed to DisplayReceived.
    //
    // The kernel writes a cell's displays and then its reply, in that order. The
    // client reads them in that order too — but hands each notification to a
    // thread-pool thread and completes the reply's task on another, so whoever
    // awaited the reply can run before the last display has reached its handler.
    // A job executor then wrote the artifact without it, on a busy CI runner,
    // once in many runs. Counting what was *read* (in the handler, where order is
    // certain) against what was *handled* lets ExecuteAsync wait for the gap to
    // close before it returns, so "the reply came back" means "and every display
    // before it has been delivered".
    private readonly DisplayCount _displays = new();

    private sealed class DisplayCount {
        private readonly object _gate = new();
        private long _read;
        private long _handled;
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Read() => Interlocked.Increment(ref _read);

        public void Handled() {
            TaskCompletionSource signal;
            lock (_gate) {
                _handled++;
                signal = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            signal.TrySetResult();
        }

        /// <summary>Waits until every display read so far has been handled.</summary>
        public async Task DrainAsync(CancellationToken cancellationToken) {
            var target = Interlocked.Read(ref _read);
            while (true) {
                Task wait;
                lock (_gate) {
                    if (_handled >= target) {
                        return;
                    }
                    wait = _changed.Task;
                }
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static readonly HashSet<string> _displayMethods = new(StringComparer.Ordinal) {
        "display", "updateDisplay", "clrkernel/display", "clrkernel/updateDisplay",
    };

    /// <summary>
    /// The wire handler, with one job added: notice each display as it is read.
    /// Everything else — buffer hand-back after deserialization, disposal — is
    /// forwarded, because JsonRpc looks for those contracts on the handler it was
    /// given and the inner one is what actually holds the buffers and the streams.
    /// </summary>
    /// <summary>
    /// The wire handler, with one job added: notice each display as it is read.
    ///
    /// <para>
    /// A subclass rather than a wrapper around <see cref="HeaderDelimitedMessageHandler"/>,
    /// and not by preference: JsonRpc hands a message's buffer back to its handler
    /// through an interface that is internal to StreamJsonRpc, so a handler that only
    /// delegates never advances the reader, and the next read finds itself in the
    /// middle of the last body — "No Content-Length header detected", on every
    /// message after the first. Overriding the read keeps the base class's contract
    /// intact.
    /// </para>
    /// </summary>
    private sealed class CountingHandler : HeaderDelimitedMessageHandler {
        private readonly DisplayCount _displays;

        public CountingHandler(Stream sending, Stream receiving, IJsonRpcMessageFormatter formatter, DisplayCount displays)
            : base(sending, receiving, formatter) {
            _displays = displays;
        }

        protected override async ValueTask<StreamJsonRpc.Protocol.JsonRpcMessage> ReadCoreAsync(CancellationToken cancellationToken) {
            var message = await base.ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (message is StreamJsonRpc.Protocol.JsonRpcRequest { IsNotification: true } request
                && _displayMethods.Contains(request.Method)) {
                _displays.Read();
            }
            return message;
        }
    }

    public async Task<InitializeReply> InitializeAsync(CancellationToken cancellationToken = default) {
        if (!Lsp) {
            return await _rpc.InvokeWithCancellationAsync<InitializeReply>(
                "initialize", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        // The LSP handshake carries the same facts in LSP's shape. Read it as JSON
        // rather than binding four nested types to reach four fields.
        var result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "initialize",
            new { processId = Environment.ProcessId, rootUri = (string)null, capabilities = new { } },
            cancellationToken).ConfigureAwait(false);
        // The server does nothing with this today, but a half-finished handshake is
        // the kind of difference that bites once it does.
        await _rpc.NotifyAsync("initialized").ConfigureAwait(false);
        return new InitializeReply {
            Name = Text(result, "serverInfo", "name"),
            Version = Text(result, "serverInfo", "version"),
            LanguagesElement = At(result, "capabilities", "experimental", "clrkernel", "languages"),
            // Read from the handshake rather than restated here. A second copy of
            // "the characters that open a completion" is a copy that goes stale
            // silently — the server would start offering something the web editor
            // never asks for, and only VS Code would benefit.
            CompletionTriggers = Strings(At(result, "capabilities", "completionProvider", "triggerCharacters")),
            SignatureTriggers = Strings(At(result, "capabilities", "signatureHelpProvider", "triggerCharacters")),
        };
    }

    /// <summary>
    /// One language-feature request, forwarded verbatim. The reply is LSP's own
    /// shape and travels to the browser unchanged: Monaco needs converting either
    /// way, and re-modelling these types in C# would only add somewhere for them to
    /// disagree with the server.
    /// </summary>
    public Task<JsonElement> LanguageRequestAsync(
        string method, object parameters, CancellationToken cancellationToken = default) =>
        _rpc.InvokeWithParameterObjectAsync<JsonElement>(method, parameters, cancellationToken);

    /// <summary>
    /// The cell languages for one notebook's <em>live</em> session, which is not what
    /// the handshake answers: <c>lsp</c> has no session yet at initialize time, so it
    /// replies from a fresh registry and a language loaded by <c>#r</c> would be
    /// missing from it. Null when the kernel has no such call — the caller keeps the
    /// handshake's list rather than losing every language.
    /// </summary>
    public async Task<IReadOnlyList<LanguageDescriptor>> LanguagesAsync(
        string notebookUri, CancellationToken cancellationToken = default) {
        if (!Lsp) {
            return null;
        }
        try {
            var reply = await _rpc.InvokeWithParameterObjectAsync<LanguagesReply>(
                "clrkernel/languages", new { notebookUri }, cancellationToken).ConfigureAwait(false);
            return reply?.Languages;
        } catch (RemoteMethodNotFoundException) {
            return null;
        }
    }

    // --- Document sync -----------------------------------------------------
    //
    // Language features answer about a document the server holds, not about text
    // passed in with the question, so a cell has to be open before completion or
    // hover can say anything about it. Notifications, so typing never waits on a
    // round trip. No-ops on serve, which has no documents.

    public Task DidOpenAsync(string uri, string languageId, int version, string text) =>
        Lsp
            ? _rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new {
                textDocument = new { uri, languageId, version, text },
            })
            : Task.CompletedTask;

    public Task DidChangeAsync(string uri, int version, string text) =>
        Lsp
            ? _rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new {
                textDocument = new { uri, version },
                // Full sync: one change carrying the whole document, which is what
                // the server advertises (textDocumentSync: 1).
                contentChanges = new[] { new { text } },
            })
            : Task.CompletedTask;

    public Task DidCloseAsync(string uri) =>
        Lsp
            ? _rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new {
                textDocument = new { uri },
            })
            : Task.CompletedTask;

    public async Task<ExecuteReply> ExecuteAsync(string cellId, string code, CancellationToken cancellationToken = default) {
        var reply = await _rpc.InvokeWithParameterObjectAsync<ExecuteReply>(
            Lsp ? "clrkernel/execute" : "execute", new { cellId, code }, cancellationToken).ConfigureAwait(false);
        // Every display read before this reply has been handled by the time the
        // caller sees it — see DisplayCount.
        await _displays.DrainAsync(cancellationToken).ConfigureAwait(false);
        return reply;
    }

    /// <summary>The connection providers a language offers, and the settings each
    /// one takes — the schema the editor's connection wizard renders. Same payload
    /// the LSP surface serves, so the web UI and VS Code build the same directive.</summary>
    /// <param name="notebookUri">Required, not optional: the lsp surface resolves the
    /// session from it, and answers <c>{ok: false}</c> without one.</param>
    public Task<DescribeConnectionsReply> DescribeConnectionsAsync(
        string languageId, string notebookUri, CancellationToken cancellationToken = default) =>
        Lsp
            ? _rpc.InvokeWithParameterObjectAsync<DescribeConnectionsReply>(
                "clrkernel/connections/describe", new { notebookUri, languageId }, cancellationToken)
            : _rpc.InvokeWithParameterObjectAsync<DescribeConnectionsReply>(
                "describeConnections", new { languageId }, cancellationToken);

    /// <summary>Asks the kernel to exit; the caller still owns killing the process if it lingers.</summary>
    public async Task ShutdownAsync() {
        try {
            if (Lsp) {
                // LSP's shutdown is a request that must be answered first; the server
                // only quits on the notification that follows it.
                await _rpc.InvokeAsync<object>("shutdown");
                await _rpc.NotifyAsync("exit");
            } else {
                await _rpc.NotifyAsync("shutdown");
            }
        } catch (Exception) {
            // The connection may already be gone; the process gets killed regardless.
        }
    }

    // Walks a path of property names, giving up at the first one missing — an older
    // kernel simply has no experimental payload.
    private static JsonElement? At(JsonElement root, params string[] names) {
        var current = root;
        foreach (var name in names) {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) {
                return null;
            }
        }
        return current;
    }

    private static string Text(JsonElement root, params string[] names) =>
        At(root, names) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement? element) {
        if (element is not { ValueKind: JsonValueKind.Array } array) {
            return Array.Empty<string>();
        }
        var values = new List<string>();
        foreach (var item in array.EnumerateArray()) {
            if (item.ValueKind == JsonValueKind.String) {
                values.Add(item.GetString());
            }
        }
        return values;
    }

    public void Dispose() => _rpc.Dispose();

    private void Deliver(DisplayNotification notification) {
        try {
            DisplayReceived?.Invoke(notification);
        } finally {
            // Counted after the handlers, throw or not: a handler that failed is
            // still one that had its turn, and a drain waiting on it must not hang.
            _displays.Handled();
        }
    }

    private sealed class NotificationSink {
        private readonly KernelClient _client;
        public NotificationSink(KernelClient client) => _client = client;

        [JsonRpcMethod("display", UseSingleObjectParameterDeserialization = true)]
        public void Display(DisplayNotification notification) => _client.Deliver(notification);

        [JsonRpcMethod("updateDisplay", UseSingleObjectParameterDeserialization = true)]
        public void UpdateDisplay(DisplayNotification notification) => _client.Deliver(notification);

        // The lsp surface names the same two notifications differently and sends the
        // same payload. Binding both sets means one sink rather than a mode switch.
        [JsonRpcMethod("clrkernel/display", UseSingleObjectParameterDeserialization = true)]
        public void LspDisplay(DisplayNotification notification) => _client.Deliver(notification);

        [JsonRpcMethod("clrkernel/updateDisplay", UseSingleObjectParameterDeserialization = true)]
        public void LspUpdateDisplay(DisplayNotification notification) => _client.Deliver(notification);

        [JsonRpcMethod("clrkernel/languagesChanged", UseSingleObjectParameterDeserialization = true)]
        public void LspLanguagesChanged(LanguagesReply notification) => _client.LanguagesChanged?.Invoke(notification);

        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void PublishDiagnostics(DiagnosticsNotification notification) =>
            _client.DiagnosticsReceived?.Invoke(notification);
    }
}

/// <summary>
/// Problems the server found in one document. An <em>empty</em> list is the
/// retraction — the server sends one when a cell's last error is fixed, or when a
/// language change means the previous language's complaints no longer apply — so
/// treating empty as "nothing to do" leaves a fixed error on screen forever.
/// </summary>
public sealed class DiagnosticsNotification {
    [JsonPropertyName("uri")]
    public string Uri { get; set; }

    [JsonPropertyName("diagnostics")]
    public JsonElement? Diagnostics { get; set; }
}

/// <summary>The cell languages a kernel reports — the payload of both
/// <c>clrkernel/languages</c> and the <c>languagesChanged</c> notification.</summary>
public sealed class LanguagesReply {
    [JsonPropertyName("languages")]
    public JsonElement? LanguagesElement { get; set; }

    [JsonIgnore]
    public IReadOnlyList<LanguageDescriptor> Languages => _languages ??= KernelJson.ReadLanguages(LanguagesElement);

    private IReadOnlyList<LanguageDescriptor> _languages;
}

public sealed class InitializeReply {
    [JsonPropertyName("name")]
    public string Name { get; set; }
    [JsonPropertyName("version")]
    public string Version { get; set; }

    /// <summary>
    /// The raw <c>languages</c> value. Deliberately untyped: kernels before 0.10
    /// answered with a list of bare names (<c>["csharp"]</c>), and binding that
    /// straight to descriptors would throw and fail the whole run against an
    /// older kernel. <see cref="Languages"/> reads whatever is usable.
    /// </summary>
    [JsonPropertyName("languages")]
    public JsonElement? LanguagesElement { get; set; }

    /// <summary>The cell languages this kernel executes — used to parse the
    /// notebook so exactly those tagged blocks become code cells. Empty (an old kernel,
    /// or none registered) degrades to C#-only parsing.</summary>
    [JsonIgnore]
    public IReadOnlyList<LanguageDescriptor> Languages => _languages ??= KernelJson.ReadLanguages(LanguagesElement);

    private IReadOnlyList<LanguageDescriptor> _languages;

    /// <summary>Characters that should open a completion list, as the server declares
    /// them. Empty from <c>serve</c>, which has no language features.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> CompletionTriggers { get; set; } = Array.Empty<string>();

    /// <summary>Characters that should open signature help — <c>(</c> and <c>,</c>.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> SignatureTriggers { get; set; } = Array.Empty<string>();
}

/// <summary>
/// The connection providers for one language. Kernels older than 0.10 do not
/// answer <c>describeConnections</c> at all, and a provider shape this build does
/// not understand is skipped rather than failing the whole list — the same
/// tolerance <see cref="InitializeReply.Languages"/> applies.
/// </summary>
public sealed class DescribeConnectionsReply {
    [JsonPropertyName("providers")]
    public JsonElement? ProvidersElement { get; set; }

    private IReadOnlyList<ConnectionProviderDescriptor> _providers;

    [JsonIgnore]
    public IReadOnlyList<ConnectionProviderDescriptor> Providers =>
        _providers ??= Read(ProvidersElement);

    private static IReadOnlyList<ConnectionProviderDescriptor> Read(JsonElement? element) {
        var providers = new List<ConnectionProviderDescriptor>();
        if (element is not { ValueKind: JsonValueKind.Array } array) {
            return providers;
        }
        foreach (var item in array.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object) {
                continue;
            }
            try {
                var descriptor = item.Deserialize<ConnectionProviderDescriptor>(KernelJson.Options);
                if (!string.IsNullOrEmpty(descriptor?.Type)) {
                    providers.Add(descriptor);
                }
            } catch (JsonException) {
                // A provider shape this build doesn't understand: skip it, keep the rest.
            }
        }
        return providers;
    }
}

/// <summary>How the kernel's payloads are shaped on the wire — camelCase names and
/// string enums, matching what the hosts serialize.</summary>
internal static class KernelJson {
    public static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Reads a <c>languages</c> array however it arrives. Anything that is
    /// not a descriptor object is ignored rather than fatal: pre-0.10 kernels listed
    /// bare names, and a shape this build doesn't understand should cost one language
    /// rather than the whole list.</summary>
    public static IReadOnlyList<LanguageDescriptor> ReadLanguages(JsonElement? element) {
        var languages = new List<LanguageDescriptor>();
        if (element is not { ValueKind: JsonValueKind.Array } array) {
            return languages;
        }
        foreach (var item in array.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object) {
                continue;
            }
            try {
                var descriptor = item.Deserialize<LanguageDescriptor>(Options);
                if (!string.IsNullOrEmpty(descriptor?.Id)) {
                    languages.Add(descriptor);
                }
            } catch (JsonException) {
                // A descriptor shape this build doesn't understand: skip it, keep the rest.
            }
        }
        return languages;
    }
}

/// <summary>A display/updateDisplay notification: a mime bundle for a cell.</summary>
public sealed class DisplayNotification {
    [JsonPropertyName("cellId")]
    public string CellId { get; set; }
    [JsonPropertyName("data")]
    public Dictionary<string, JsonElement> Data { get; set; }
    [JsonPropertyName("transient")]
    public Dictionary<string, JsonElement> Transient { get; set; }
}

public sealed class ExecuteReply {
    [JsonPropertyName("cellId")]
    public string CellId { get; set; }
    /// <summary>"ok" or "error".</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; }
    /// <summary>The trailing-expression mime bundle, when the cell produced one.</summary>
    [JsonPropertyName("data")]
    public Dictionary<string, JsonElement> Data { get; set; }
    [JsonPropertyName("error")]
    public ExecuteError Error { get; set; }

    public bool Ok => Status == "ok";
}

public sealed class ExecuteError {
    [JsonPropertyName("name")]
    public string Name { get; set; }
    [JsonPropertyName("message")]
    public string Message { get; set; }
    [JsonPropertyName("stack")]
    public string Stack { get; set; }
}

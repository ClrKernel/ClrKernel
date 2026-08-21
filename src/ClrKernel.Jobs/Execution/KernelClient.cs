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

namespace ClrKernel.Jobs;

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

    /// <summary>Raised for every display/updateDisplay notification from the kernel.</summary>
    public event Action<DisplayNotification> DisplayReceived;

    /// <summary>Raised when a session's language set grew — a package loaded with
    /// <c>#r</c> registering a cell language mid-notebook. The set decides how the
    /// notebook's cells are parsed, so a stale one is not cosmetic.</summary>
    public event Action<LanguagesReply> LanguagesChanged;

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
        var handler = new HeaderDelimitedMessageHandler(sendingStream, receivingStream, formatter);
        _rpc = new JsonRpc(handler);
        _rpc.AddLocalRpcTarget(new NotificationSink(this));
        _rpc.StartListening();
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
        };
    }

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

    public Task<ExecuteReply> ExecuteAsync(string cellId, string code, CancellationToken cancellationToken = default) =>
        _rpc.InvokeWithParameterObjectAsync<ExecuteReply>(
            Lsp ? "clrkernel/execute" : "execute", new { cellId, code }, cancellationToken);

    /// <summary>The connection providers a language offers, and the settings each
    /// one takes — the schema the editor's connection wizard renders. Same payload
    /// the LSP surface serves, so the web UI and VS Code build the same directive.</summary>
    public Task<DescribeConnectionsReply> DescribeConnectionsAsync(
        string languageId, string notebookUri = null, CancellationToken cancellationToken = default) =>
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

    public void Dispose() => _rpc.Dispose();

    private sealed class NotificationSink {
        private readonly KernelClient _client;
        public NotificationSink(KernelClient client) => _client = client;

        [JsonRpcMethod("display", UseSingleObjectParameterDeserialization = true)]
        public void Display(DisplayNotification notification) => _client.DisplayReceived?.Invoke(notification);

        [JsonRpcMethod("updateDisplay", UseSingleObjectParameterDeserialization = true)]
        public void UpdateDisplay(DisplayNotification notification) => _client.DisplayReceived?.Invoke(notification);

        // The lsp surface names the same two notifications differently and sends the
        // same payload. Binding both sets means one sink rather than a mode switch.
        [JsonRpcMethod("clrkernel/display", UseSingleObjectParameterDeserialization = true)]
        public void LspDisplay(DisplayNotification notification) => _client.DisplayReceived?.Invoke(notification);

        [JsonRpcMethod("clrkernel/updateDisplay", UseSingleObjectParameterDeserialization = true)]
        public void LspUpdateDisplay(DisplayNotification notification) => _client.DisplayReceived?.Invoke(notification);

        [JsonRpcMethod("clrkernel/languagesChanged", UseSingleObjectParameterDeserialization = true)]
        public void LspLanguagesChanged(LanguagesReply notification) => _client.LanguagesChanged?.Invoke(notification);
    }
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Scripting;
using StreamJsonRpc;

namespace ClrKernel.Jobs;

/// <summary>
/// JSON-RPC client for a <c>clrkernel serve</c> process — the same stdio protocol the
/// VS Code extension speaks: Content-Length framed requests (<c>initialize</c>,
/// <c>execute</c>, <c>shutdown</c>) plus <c>display</c>/<c>updateDisplay</c>
/// notifications streaming output while a cell runs.
/// </summary>
public sealed class KernelClient : IDisposable {
    private readonly JsonRpc _rpc;

    /// <summary>Raised for every display/updateDisplay notification from the kernel.</summary>
    public event Action<DisplayNotification> DisplayReceived;

    /// <param name="sendingStream">Stream requests are written to (the child's stdin).</param>
    /// <param name="receivingStream">Stream replies are read from (the child's stdout).</param>
    public KernelClient(Stream sendingStream, Stream receivingStream) {
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

    public Task<InitializeReply> InitializeAsync(CancellationToken cancellationToken = default) =>
        _rpc.InvokeWithCancellationAsync<InitializeReply>("initialize", cancellationToken: cancellationToken);

    public Task<ExecuteReply> ExecuteAsync(string cellId, string code, CancellationToken cancellationToken = default) =>
        _rpc.InvokeWithParameterObjectAsync<ExecuteReply>("execute", new { cellId, code }, cancellationToken);

    /// <summary>Asks the kernel to exit; the caller still owns killing the process if it lingers.</summary>
    public async Task ShutdownAsync() {
        try {
            await _rpc.NotifyAsync("shutdown");
        } catch (Exception) {
            // The connection may already be gone; the process gets killed regardless.
        }
    }

    public void Dispose() => _rpc.Dispose();

    private sealed class NotificationSink {
        private readonly KernelClient _client;
        public NotificationSink(KernelClient client) => _client = client;

        [JsonRpcMethod("display", UseSingleObjectParameterDeserialization = true)]
        public void Display(DisplayNotification notification) => _client.DisplayReceived?.Invoke(notification);

        [JsonRpcMethod("updateDisplay", UseSingleObjectParameterDeserialization = true)]
        public void UpdateDisplay(DisplayNotification notification) => _client.DisplayReceived?.Invoke(notification);
    }
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
    public IReadOnlyList<LanguageDescriptor> Languages => _languages ??= ReadLanguages(LanguagesElement);

    private IReadOnlyList<LanguageDescriptor> _languages;

    private static readonly JsonSerializerOptions _descriptorOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // Anything that is not a descriptor object is ignored rather than fatal.
    private static IReadOnlyList<LanguageDescriptor> ReadLanguages(JsonElement? element) {
        var languages = new List<LanguageDescriptor>();
        if (element is not { ValueKind: JsonValueKind.Array } array) {
            return languages;
        }
        foreach (var item in array.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object) {
                continue; // pre-0.10 kernels listed bare names
            }
            try {
                var descriptor = item.Deserialize<LanguageDescriptor>(_descriptorOptions);
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

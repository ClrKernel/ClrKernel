using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
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
        var handler = new HeaderDelimitedMessageHandler(sendingStream, receivingStream, new SystemTextJsonFormatter());
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

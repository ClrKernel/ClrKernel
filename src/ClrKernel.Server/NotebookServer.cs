using System;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core;
using ClrKernel.Primitives;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace ClrKernel.Server;

/// <summary>
/// The JSON-RPC target for a notebook session. Requests come from the editor
/// (VS Code NotebookController); display output flows back as notifications
/// tagged with the requesting cell's id:
///   display        { cellId, data, transient }   — new output for a cell
///   updateDisplay  { cellId, data, transient }   — update output in place (display_id in transient)
/// </summary>
public class NotebookServer {
    private readonly InteractiveScriptEngine _engine;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    /// <summary>Set by Program after the JsonRpc connection is created.</summary>
    public JsonRpc Rpc { get; set; }

    public NotebookServer(ILoggerFactory loggerFactory) {
        _engine = new InteractiveScriptEngine(Environment.CurrentDirectory, loggerFactory.CreateLogger(nameof(InteractiveScriptEngine)));
        _logger = loggerFactory.CreateLogger(nameof(NotebookServer));
    }

    [JsonRpcMethod("initialize")]
    public object Initialize() {
        return new {
            name = "ClrKernel.Server",
            version = typeof(NotebookServer).Assembly.GetName().Version?.ToString(),
            languages = new[] { "csharp" },
        };
    }

    [JsonRpcMethod("execute")]
    public async Task<object> ExecuteAsync(string cellId, string code) {
        await _executionLock.WaitAsync().ConfigureAwait(false);
        try {
            void Notify(string method, DisplayData data) {
                // fire-and-forget: notifications must not block execution.
                // Named parameter object (not positional array) for easy
                // consumption by vscode-jsonrpc clients.
                _ = Rpc?.NotifyWithParameterObjectAsync(method, new { cellId, data = data.Data, transient = data.Transient });
            }

            DisplayDataEmitter.DisplayDataHandler = data => Notify("display", data);
            DisplayDataEmitter.UpdateDisplayDataHandler = data => Notify("updateDisplay", data);

            object result = null;
            using (var consoleProxy = new ConsoleProxy(line => Notify("display", new DisplayData(line)))) {
                consoleProxy.StartRedirect();
                result = await _engine.ExecuteAsync(code).ConfigureAwait(false);
            }

            return new {
                cellId,
                status = "ok",
                data = result switch {
                    DisplayData displayData => displayData.Data,
                    JObject jObject => jObject,
                    null => null,
                    var other => new JObject { ["text/plain"] = other.ToString() },
                },
            };
        } catch (Exception e) {
            _logger.LogError(e, "execute failed for cell {CellId}", cellId);
            return new {
                cellId,
                status = "error",
                error = new {
                    name = e.GetType().Name,
                    message = e.Message,
                    stack = e.StackTrace,
                },
            };
        } finally {
            DisplayDataEmitter.DisplayDataHandler = null;
            DisplayDataEmitter.UpdateDisplayDataHandler = null;
            _executionLock.Release();
        }
    }

    [JsonRpcMethod("shutdown")]
    public void Shutdown() {
        _logger.LogInformation("shutdown requested");
        // Give the response time to flush, then exit.
        _ = Task.Run(async () => {
            await Task.Delay(100).ConfigureAwait(false);
            Environment.Exit(0);
        });
    }
}

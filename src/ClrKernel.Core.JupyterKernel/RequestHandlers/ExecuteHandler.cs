using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.JupyterKernel.Kernels;
using ClrKernel.Core.JupyterKernel.Protocols;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Core.JupyterKernel.RequestHandlers;

public class ExecuteHandler<T> where T : ExecuteRequest {
    private MessageSender _ioPub;
    private MessageSender _shell;
    private int _executionCount = 0;
    private InteractiveScriptEngine _scriptEngine;
    private ILogger _logger;

    // Display routing: each display cell remembers the request that created it, so
    // updates from background work (timers, progress loops) keep publishing against
    // the originating cell's output after the request has completed.
    private Message<T> _currentRequest;
    private readonly Dictionary<string, Message<T>> _displayParents = new Dictionary<string, Message<T>>();

    public ExecuteHandler(MessageSender ioPub, MessageSender shell, ILoggerFactory loggerFactory) {
        this._ioPub = ioPub;
        this._shell = shell;
        this._scriptEngine = new InteractiveScriptEngine(AppContext.BaseDirectory, loggerFactory.CreateLogger(nameof(InteractiveScriptEngine)));
        this._logger = loggerFactory.CreateLogger(nameof(ExecuteHandler<T>));

        // The single display channel: cells raise DisplayValues events; this host
        // bundles the concept and routes by display_id.
        DisplayValues.OnCellDisplayed += cell => {
            var parent = _currentRequest;
            if (parent == null) {
                return;
            }
            lock (_displayParents) {
                _displayParents[cell.DisplayId] = parent;
            }
            _ioPub.Send(parent, MimeBundler.Bundle(cell), MessageType.DisplayData);
        };
        DisplayValues.OnCellUpdated += cell => {
            Message<T> parent;
            lock (_displayParents) {
                _displayParents.TryGetValue(cell.DisplayId, out parent);
            }
            parent ??= _currentRequest;
            if (parent != null) {
                _ioPub.Send(parent, MimeBundler.Bundle(cell), MessageType.UpdateDisplayData);
            }
        };
    }

    private ConsoleProxy CreateConsoleProxy(Action<DisplayData> displayDataHandler) {
        return new ConsoleProxy((line) => {
            var data = new DisplayData {
                Data = new Dictionary<string, object>
                {
                    { "text/plain", line }
                }
            };

            displayDataHandler(data);
        });
    }

    // Returns a Task (rather than async void) so the kernel loop can wait for
    // the cell to finish before publishing status: idle. With async void, any
    // cell containing an await yielded control immediately, idle was sent while
    // the cell was still running, and Jupyter clients (nbclient/papermill)
    // stopped collecting the cell's output — logs from awaited cells vanished.
    public async Task ProcessAsync(Message<T> message) {
        object result = null;
        ExecuteReply executeReply = null;
        try {
            _currentRequest = message;

            using (var consoleProxy = CreateConsoleProxy(data => _ioPub.Send(message, data, MessageType.DisplayData))) {
                consoleProxy.StartRedirect();
                result = await _scriptEngine.ExecuteAsync(message.Content.Code);
            }
            executeReply = new ExecuteReplyOk {
                ExecutionCount = _executionCount++,
                Payload = new List<Dictionary<string, string>>(),
                UserExpressions = new Dictionary<string, string>()
            };
        } catch (Exception e) {
            executeReply = new ExecuteReplyError {
                ExecutionCount = _executionCount,
                EName = e.GetType().Name,
                EValue = e.Message,
                Traceback = e.StackTrace.Split(Environment.NewLine).ToList()
            };

            _logger.LogError(e, "Failed to run the code: " + message.Content.Code);

            var error = e.Message + Environment.NewLine + e.StackTrace;
            var errorDisplay = new DisplayData();
            errorDisplay.Data["text/plain"] = error;
            errorDisplay.Data["text/html"] = $"<p style=\"color:red;\">{error}</p>";
            _ioPub.Send(message, errorDisplay, MessageType.DisplayData);
        } finally {
            _currentRequest = null;
        }

        if (result != null) {
            _ioPub.Send(message, result, MessageType.DisplayData);
        }

        // send execute reply to shell socket
        _shell.Send(message, executeReply, MessageType.ExecuteReply);

        // Publish idle only now that the cell (including awaited work) has fully
        // completed. Previously the kernel loop sent idle as soon as this method
        // yielded at its first await, so Jupyter clients stopped collecting
        // output for any cell containing an await and its logs were lost.
        _ioPub.Send(message, new Status { ExecutionState = StatusType.Idle }, MessageType.Status);
    }
}

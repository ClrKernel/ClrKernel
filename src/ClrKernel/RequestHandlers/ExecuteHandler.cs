using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core;
using ClrKernel.Kernels;
using ClrKernel.Primitives;
using ClrKernel.Protocols;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace ClrKernel.RequestHandlers;

public class ExecuteHandler<T> where T : ExecuteRequest {
    private MessageSender _ioPub;
    private MessageSender _shell;
    private int _executionCount = 0;
    private InteractiveScriptEngine _scriptEngine;
    private ILogger _logger;

    public ExecuteHandler(MessageSender ioPub, MessageSender shell, ILoggerFactory loggerFactory) {
        this._ioPub = ioPub;
        this._shell = shell;
        this._scriptEngine = new InteractiveScriptEngine(AppContext.BaseDirectory, loggerFactory.CreateLogger(nameof(InteractiveScriptEngine)));
        this._logger = loggerFactory.CreateLogger(nameof(ExecuteHandler<T>));
    }

    private ConsoleProxy CreateConsoleProxy(Action<DisplayData> displayDataHandler) {
        return new ConsoleProxy((line) => {
            var data = new DisplayData {
                Data = new JObject
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
            var displayDataHandler = new Action<DisplayData>((data) => {
                _ioPub.Send(message, data, MessageType.DisplayData);
            });

            DisplayDataEmitter.DisplayDataHandler = displayDataHandler;
            DisplayDataEmitter.UpdateDisplayDataHandler = (data) => {
                _ioPub.Send(message, data, MessageType.UpdateDisplayData);
            };

            using (var consoleProxy = CreateConsoleProxy(displayDataHandler)) {
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
            _ioPub.Send(message, new DisplayData(error, $"<p style=\"color:red;\">{error}</p>"), MessageType.DisplayData);
        } finally {
            DisplayDataEmitter.DisplayDataHandler = null;
            DisplayDataEmitter.UpdateDisplayDataHandler = null;
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

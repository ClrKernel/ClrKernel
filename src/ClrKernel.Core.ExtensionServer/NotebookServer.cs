using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace ClrKernel.Core.ExtensionServer;

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
        // A #r-loaded plugin changed the session's languages/providers: tell the
        // client so it can re-parse language tags and refresh any language UI.
        _engine.LanguagesChanged += () => _ = Rpc?.NotifyWithParameterObjectAsync(
            "languagesChanged", new {
                languages = _engine.Languages.Describe(),
                connectionProviders = _engine.ConnectionProviders,
            });
    }

    [JsonRpcMethod("initialize")]
    public object Initialize() {
        return new {
            name = "ClrKernel.Core.ExtensionServer",
            version = typeof(NotebookServer).Assembly.GetName().Version?.ToString(),
            // The full descriptor list: a client parses notebooks with exactly
            // the languages this kernel can execute (C# is implicit — it is the
            // unmatched fallthrough, not a registered language).
            languages = _engine.Languages.Describe(),
        };
    }

    /// <summary>
    /// The connection-provider descriptors for a language — same payload the LSP
    /// surface serves via clrkernel/connections/describe.
    /// <para>
    /// No language means every one this session knows, including the providers that
    /// belong to no cell language at all (Fabric, Oracle, ODBC, JDBC are used from C#
    /// cells, so they name none). Asking per language can never reach those, and a
    /// caller cataloguing what this kernel can connect to needs them.
    /// </para>
    /// </summary>
    [JsonRpcMethod("describeConnections")]
    public object DescribeConnections(string languageId) =>
        new {
            providers = string.IsNullOrWhiteSpace(languageId)
                ? _engine.ConnectionProviders
                : _engine.ConnectionProvidersFor(languageId),
        };

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

            EnsureDisplayHooked();
            _currentCellId = cellId;

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
                    null => null,
                    var other => new Dictionary<string, object> { ["text/plain"] = other.ToString() },
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
            _currentCellId = null;
            _executionLock.Release();
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
            NotifyDisplay("display", cellId, MimeBundler.Bundle(cell));
        };
        DisplayValues.OnCellUpdated += cell => {
            string cellId;
            lock (_displayCells) {
                _displayCells.TryGetValue(cell.DisplayId, out cellId);
            }
            cellId ??= _currentCellId;
            if (cellId != null) {
                NotifyDisplay("updateDisplay", cellId, MimeBundler.Bundle(cell));
            }
        };
    }

    private void NotifyDisplay(string method, string cellId, DisplayData data) =>
        _ = Rpc?.NotifyWithParameterObjectAsync(method, new { cellId, data = data.Data, transient = data.Transient });

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

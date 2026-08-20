using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Runner;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Jobs;

/// <summary>What a cell did, for the editor to render.</summary>
public sealed class SessionCellState {
    public string Status { get; set; } = "pending";
    public int? ExecutionCount { get; set; }
    public JsonArray Outputs { get; set; } = new();
    public bool Truncated { get; set; }

    /// <summary>display_id → index in <see cref="Outputs"/>, so an updateDisplay
    /// replaces the output it belongs to instead of appending a new one.</summary>
    internal Dictionary<string, int> ByDisplayId { get; } = new();
}

/// <summary>
/// A warm kernel for one notebook being edited: variables persist across cells
/// the way they do in VS Code, so a session is worth keeping alive between runs.
/// <para>
/// Nothing here touches the run store. Interactive execution leaves no Run rows,
/// so it can never satisfy the promotion gate — by construction rather than by a
/// predicate someone has to remember.
/// </para>
/// </summary>
public sealed class NotebookSession : IDisposable {
    // A runaway cell (`while (true) Console.WriteLine(…)`) must not grow the
    // server's memory without bound.
    private const int _maxOutputsPerCell = 200;
    private const int _maxOutputBytesPerCell = 256 * 1024;

    private readonly string _clrkernelPath;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _oneRunAtATime = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<string, SessionCellState> _cells = new();
    private readonly List<string> _kernelLog = new();

    private KernelProcess _kernel;
    private int _executionCount;

    private readonly Func<CancellationToken, Task<KernelProcess>> _startKernel;

    public NotebookSession(
        string id, string notebookPath, string clrkernelPath, Action<string> log = null,
        Func<CancellationToken, Task<KernelProcess>> startKernel = null) {
        Id = id;
        NotebookPath = notebookPath;
        _clrkernelPath = clrkernelPath;
        _log = log;
        // Tests supply a kernel over an in-memory stream; production spawns one.
        _startKernel = startKernel ?? DefaultStartAsync;
        LastActivity = DateTime.UtcNow;
    }

    private Task<KernelProcess> DefaultStartAsync(CancellationToken cancellationToken) =>
        Task.FromResult(KernelProcess.Start(
            _clrkernelPath, System.IO.Path.GetDirectoryName(NotebookPath), Note));

    public string Id { get; }
    public string NotebookPath { get; }
    public DateTime LastActivity { get; private set; }
    public IReadOnlyList<LanguageDescriptor> Languages { get; private set; } = Array.Empty<LanguageDescriptor>();
    public string KernelName { get; private set; }
    public string KernelVersion { get; private set; }

    /// <summary>True while a run is in flight — the editor disables its run buttons.</summary>
    public bool Busy => _oneRunAtATime.CurrentCount == 0;

    /// <summary>Set when the kernel died and was respawned: the session's variables
    /// are gone, which the editor says out loud rather than letting the user wonder.</summary>
    public bool KernelRestarted { get; private set; }

    /// <summary>Starts the kernel if it is not running, and returns it. A kernel that
    /// exited — a cell can call Environment.Exit — is replaced transparently.</summary>
    public async Task<KernelProcess> EnsureKernelAsync(CancellationToken cancellationToken) {
        if (_kernel != null && !_kernel.HasExited) {
            return _kernel;
        }
        if (_kernel != null) {
            KernelRestarted = true;
            Note("kernel exited; starting a fresh one (variables are gone)");
            _kernel.Dispose();
            _kernel = null;
        }
        var kernel = await _startKernel(cancellationToken).ConfigureAwait(false);
        // Subscribed for the kernel's whole life, not per run: a display that
        // arrives just after a cell's reply — a progress bar finishing, or
        // background work reporting — still belongs to that cell.
        kernel.Client.DisplayReceived += Record;
        var info = await kernel.InitializeAsync(cancellationToken).ConfigureAwait(false);
        KernelName = info.Name;
        KernelVersion = info.Version;
        Languages = info.Languages;
        _kernel = kernel;
        return kernel;
    }

    /// <summary>
    /// Runs cells in order against the warm kernel. Returns false when a run is
    /// already in flight — one at a time per session, which is what the kernel
    /// does anyway. Stops at the first failure and marks the rest skipped, the
    /// same papermill semantics a scheduled run uses, so what you see here
    /// predicts what the job will do.
    /// </summary>
    public bool TryStartRun(
        IReadOnlyList<MarkdownCell> cells, IReadOnlyList<string> ids, out Task completion) {
        // Acquire synchronously so the caller knows immediately whether it won the
        // slot, then run in the background: a long cell must not hold an HTTP
        // request open, and the editor polls for progress anyway.
        if (!_oneRunAtATime.Wait(0)) {
            completion = Task.CompletedTask;
            return false;
        }
        Touch();
        lock (_stateGate) {
            foreach (var id in ids) {
                _cells[id] = new SessionCellState { Status = "pending" };
            }
        }
        completion = Task.Run(async () => {
            try {
                await RunAsync(cells, ids, CancellationToken.None).ConfigureAwait(false);
            } finally {
                Touch();
                _oneRunAtATime.Release();
            }
        });
        return true;
    }

    private async Task RunAsync(IReadOnlyList<MarkdownCell> cells, IReadOnlyList<string> ids, CancellationToken cancellationToken) {
        KernelProcess kernel;
        try {
            kernel = await EnsureKernelAsync(cancellationToken).ConfigureAwait(false);
        } catch (Exception e) {
            // No kernel: say so on every cell rather than leaving them pending forever.
            foreach (var id in ids) {
                AddOutput(id, IpynbWriter.ErrorOutput(e.GetType().Name, e.Message, new[] { e.Message }));
                SetStatus(id, "failed");
            }
            return;
        }

        var languages = Languages;
        {
            var failed = false;
            for (var i = 0; i < cells.Count; i++) {
                var id = ids[i];
                if (failed) {
                    // Papermill semantics, the same as a scheduled run: what you see
                    // here predicts what the job will do.
                    SetStatus(id, "skipped");
                    continue;
                }
                SetStatus(id, "running");
                var code = NotebookMarkdown.ExecutableSource(cells[i], languages);
                ExecuteReply reply;
                try {
                    reply = await kernel.Client.ExecuteAsync(id, code, cancellationToken).ConfigureAwait(false);
                } catch (Exception e) when (e is not OperationCanceledException) {
                    // The kernel died mid-cell: report it on the cell rather than
                    // losing the run silently.
                    AddOutput(id, IpynbWriter.ErrorOutput(e.GetType().Name, e.Message, new[] { e.Message }));
                    SetStatus(id, "failed");
                    failed = true;
                    continue;
                }
                Apply(id, reply);
                failed = !reply.Ok;
            }
        }
    }

    /// <summary>The connection providers a language offers in <em>this</em> session —
    /// asked of the live kernel, so providers a package added with <c>#r</c> mid-session
    /// are included.</summary>
    public async Task<IReadOnlyList<ConnectionProviderDescriptor>> DescribeConnectionsAsync(
        string languageId, CancellationToken cancellationToken) {
        var kernel = await EnsureKernelAsync(cancellationToken).ConfigureAwait(false);
        Touch();
        var reply = await kernel.Client.DescribeConnectionsAsync(languageId, cancellationToken)
            .ConfigureAwait(false);
        return reply?.Providers ?? Array.Empty<ConnectionProviderDescriptor>();
    }

    /// <summary>The state of every cell this session has run, for polling.</summary>
    public IReadOnlyDictionary<string, SessionCellState> Snapshot() {
        lock (_stateGate) {
            return _cells.ToDictionary(kv => kv.Key, kv => kv.Value);
        }
    }

    /// <summary>The last lines the kernel wrote to stderr — the first place to look
    /// when a session will not start.</summary>
    public IReadOnlyList<string> KernelLog() {
        lock (_stateGate) {
            return _kernelLog.ToList();
        }
    }

    public void Touch() => LastActivity = DateTime.UtcNow;

    private void Apply(string id, ExecuteReply reply) {
        if (reply.Data is { Count: > 0 }) {
            AddOutput(id, IpynbWriter.ExecuteResultOutput(++_executionCount, ToBundle(reply.Data)));
            SetExecutionCount(id, _executionCount);
        } else if (reply.Ok) {
            SetExecutionCount(id, ++_executionCount);
        }
        if (reply.Error != null) {
            AddOutput(id, IpynbWriter.ErrorOutput(
                reply.Error.Name, reply.Error.Message,
                (reply.Error.Stack ?? string.Empty).Split('\n')));
        }
        SetStatus(id, reply.Ok ? "succeeded" : "failed");
    }

    // display appends; updateDisplay replaces the output it first created —
    // otherwise a progress bar would emit hundreds of outputs and hit the cap.
    private void Record(DisplayNotification notification) {
        if (notification?.CellId == null || notification.Data == null) {
            return;
        }
        var output = IpynbWriter.DisplayDataOutput(ToBundle(notification.Data));
        var displayId = notification.Transient != null &&
            notification.Transient.TryGetValue("display_id", out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        lock (_stateGate) {
            var cell = State(notification.CellId);
            if (displayId != null && cell.ByDisplayId.TryGetValue(displayId, out var index) &&
                index < cell.Outputs.Count) {
                cell.Outputs[index] = output;
                return;
            }
            if (!Append(cell, output)) {
                return;
            }
            if (displayId != null) {
                cell.ByDisplayId[displayId] = cell.Outputs.Count - 1;
            }
        }
    }

    private void AddOutput(string id, JsonObject output) {
        lock (_stateGate) {
            Append(State(id), output);
        }
    }

    private static bool Append(SessionCellState cell, JsonObject output) {
        if (cell.Truncated) {
            return false;
        }
        if (cell.Outputs.Count >= _maxOutputsPerCell || Size(cell) > _maxOutputBytesPerCell) {
            cell.Truncated = true;
            cell.Outputs.Add(IpynbWriter.StreamOutput("stdout",
                "… output truncated: this cell produced more than the editor keeps.\n"));
            return false;
        }
        cell.Outputs.Add(output);
        return true;
    }

    private static int Size(SessionCellState cell) => cell.Outputs.ToJsonString().Length;

    private SessionCellState State(string id) {
        if (!_cells.TryGetValue(id, out var cell)) {
            _cells[id] = cell = new SessionCellState();
        }
        return cell;
    }

    private void SetStatus(string id, string status) {
        lock (_stateGate) {
            State(id).Status = status;
        }
    }

    private void SetExecutionCount(string id, int count) {
        lock (_stateGate) {
            State(id).ExecutionCount = count;
        }
    }

    private static Dictionary<string, object> ToBundle(Dictionary<string, JsonElement> data) =>
        data.ToDictionary(kv => kv.Key, kv => (object)(kv.Value.ValueKind == JsonValueKind.String
            ? kv.Value.GetString()
            : kv.Value.ToString()));

    private void Note(string line) {
        _log?.Invoke(line);
        lock (_stateGate) {
            _kernelLog.Add(line);
            if (_kernelLog.Count > 50) {
                _kernelLog.RemoveAt(0);
            }
        }
    }

    public void Dispose() {
        _kernel?.Dispose();
        _kernel = null;
        _oneRunAtATime.Dispose();
    }
}

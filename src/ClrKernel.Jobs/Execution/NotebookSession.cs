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

/// <summary>
/// One cell as the editor currently has it, for document sync. <see cref="Source"/>
/// is the cell's own text — <em>not</em> what executing it would run: the editor's
/// cursor positions are offsets into this, and a prepended selector line would shift
/// every one of them by a line.
/// </summary>
public sealed class NotebookSyncCell {
    public string Id { get; set; }

    /// <summary>The cell's language as the kernel names it — <c>sql</c>, <c>pwsh</c>,
    /// <c>csharp-script</c> for C#. Not the editor's syntax mode: Monaco calls C#
    /// cells <c>csharp</c>, and the server dispatches its language services off this.</summary>
    public string LanguageId { get; set; }

    public string Source { get; set; }
}

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
    private readonly Action<IReadOnlyList<LanguageDescriptor>> _onLanguages;

    /// <summary>What this session has told the kernel is open, by cell URI. Only ever
    /// touched from <see cref="SyncAsync"/>, which the API serializes per notebook.</summary>
    private readonly Dictionary<string, OpenDocument> _synced = new(StringComparer.Ordinal);

    private sealed class OpenDocument {
        public string LanguageId { get; set; }
        public string Text { get; set; }
        public int Version { get; set; }
    }

    /// <param name="onLanguages">Called whenever this session's language set is
    /// established or changes. The set decides how a notebook's fenced blocks become
    /// cells, and parsing happens outside any session — so a set that only lives here
    /// would leave a language loaded by <c>#r</c> executable but unparseable.</param>
    public NotebookSession(
        string id, string notebookPath, string clrkernelPath, Action<string> log = null,
        Func<CancellationToken, Task<KernelProcess>> startKernel = null,
        Action<IReadOnlyList<LanguageDescriptor>> onLanguages = null) {
        Id = id;
        NotebookPath = notebookPath;
        NotebookUri = ToNotebookUri(notebookPath);
        _clrkernelPath = clrkernelPath;
        _log = log;
        _onLanguages = onLanguages;
        // Tests supply a kernel over an in-memory stream; production spawns one.
        _startKernel = startKernel ?? DefaultStartAsync;
        LastActivity = DateTime.UtcNow;
    }

    private Task<KernelProcess> DefaultStartAsync(CancellationToken cancellationToken) =>
        Task.FromResult(KernelProcess.Start(
            _clrkernelPath, System.IO.Path.GetDirectoryName(NotebookPath), Note, KernelMode.Lsp));

    public string Id { get; }
    public string NotebookPath { get; }

    /// <summary>
    /// This notebook as the kernel addresses it. The <c>lsp</c> surface keys its
    /// sessions off the path parsed out of a cell URI, so the ids the editor uses
    /// (<c>c0</c>, <c>c1</c>) have to be qualified by the notebook before they go on
    /// the wire — bare ones would give every cell a kernel of its own. Same URI shape
    /// VS Code sends, so cells from the web editor take the server's one code path.
    /// </summary>
    public string NotebookUri { get; }

    /// <summary>One cell's URI: <c>vscode-notebook-cell:/path/to/nb.md#c3</c>.</summary>
    public string CellUri(string cellId) => $"{NotebookUri}#{cellId}";

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
        // A fresh process holds no documents. Keeping the old record would make the
        // next sync send didChange for cells it never opened: the text would land,
        // but the languageId would not, and the cell would silently drop to the
        // C# fallback instead of its own language's services.
        _synced.Clear();
        var kernel = await _startKernel(cancellationToken).ConfigureAwait(false);
        // Subscribed for the kernel's whole life, not per run: a display that
        // arrives just after a cell's reply — a progress bar finishing, or
        // background work reporting — still belongs to that cell.
        kernel.Client.DisplayReceived += Record;
        // A package loaded with #r can register a cell language mid-notebook, and the
        // language set is what decides how cells parse. Take the update rather than
        // running the rest of the session against the set that existed at startup.
        kernel.Client.LanguagesChanged += reply => {
            if (reply?.Languages is { Count: > 0 } languages) {
                SetLanguages(languages);
            }
        };
        var info = await kernel.InitializeAsync(cancellationToken).ConfigureAwait(false);
        KernelName = info.Name;
        KernelVersion = info.Version;
        // The handshake answers from a fresh registry (no session exists yet); this
        // asks the notebook's own session. Falls back when the kernel has no such call.
        SetLanguages(await kernel.Client.LanguagesAsync(NotebookUri, cancellationToken).ConfigureAwait(false)
            ?? info.Languages);
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
                    reply = await kernel.Client.ExecuteAsync(CellUri(id), code, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Tells the kernel what the editor currently has open, so language features have
    /// something to answer about. The diff is <em>authoritative</em>, not additive: a
    /// cell the caller does not list is closed. Completion gathers context from every
    /// open document in the notebook, so a cell that was deleted but never closed
    /// would keep offering its symbols for the life of the session.
    /// <para>
    /// Cheap when nothing changed, which is what makes it safe to call on a keystroke:
    /// the work is a dictionary comparison, and only differences go on the wire.
    /// </para>
    /// </summary>
    public async Task<int> SyncAsync(IReadOnlyList<NotebookSyncCell> cells, CancellationToken cancellationToken) {
        var kernel = await EnsureKernelAsync(cancellationToken).ConfigureAwait(false);
        Touch();

        var wanted = new Dictionary<string, NotebookSyncCell>(StringComparer.Ordinal);
        foreach (var cell in cells ?? Array.Empty<NotebookSyncCell>()) {
            if (!string.IsNullOrEmpty(cell?.Id)) {
                wanted[CellUri(cell.Id)] = cell; // a duplicate id is the later cell
            }
        }

        var sent = 0;
        foreach (var uri in _synced.Keys.Where(u => !wanted.ContainsKey(u)).ToList()) {
            await kernel.Client.DidCloseAsync(uri).ConfigureAwait(false);
            _synced.Remove(uri);
            sent++;
        }

        foreach (var (uri, cell) in wanted) {
            var languageId = string.IsNullOrEmpty(cell.LanguageId) ? "csharp-script" : cell.LanguageId;
            var source = cell.Source ?? string.Empty;
            if (!_synced.TryGetValue(uri, out var open)) {
                await kernel.Client.DidOpenAsync(uri, languageId, 1, source).ConfigureAwait(false);
                _synced[uri] = new OpenDocument { LanguageId = languageId, Text = source, Version = 1 };
                sent++;
            } else if (!string.Equals(open.LanguageId, languageId, StringComparison.Ordinal)) {
                // A language change is a close and a reopen, not an edit — the same
                // thing VS Code does, and what makes the old language's diagnostics
                // get retracted instead of outliving the cell.
                await kernel.Client.DidCloseAsync(uri).ConfigureAwait(false);
                await kernel.Client.DidOpenAsync(uri, languageId, open.Version + 1, source).ConfigureAwait(false);
                _synced[uri] = new OpenDocument {
                    LanguageId = languageId,
                    Text = source,
                    Version = open.Version + 1,
                };
                sent++;
            } else if (!string.Equals(open.Text, source, StringComparison.Ordinal)) {
                open.Version++;
                await kernel.Client.DidChangeAsync(uri, open.Version, source).ConfigureAwait(false);
                open.Text = source;
                sent++;
            }
        }
        return sent;
    }

    /// <summary>The connection providers a language offers in <em>this</em> session —
    /// asked of the live kernel, so providers a package added with <c>#r</c> mid-session
    /// are included.</summary>
    public async Task<IReadOnlyList<ConnectionProviderDescriptor>> DescribeConnectionsAsync(
        string languageId, CancellationToken cancellationToken) {
        var kernel = await EnsureKernelAsync(cancellationToken).ConfigureAwait(false);
        Touch();
        var reply = await kernel.Client.DescribeConnectionsAsync(languageId, NotebookUri, cancellationToken)
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
    //
    // ponytail: outputs are ordered by arrival, so a cell's stdout can land AFTER its
    // trailing result even though the kernel sent it first — the notification handler
    // and the reply's continuation race on the threadpool. Pre-existing and identical
    // under both kernel surfaces (measured, not assumed), so it is not an lsp
    // regression. Fixing it properly means carrying a sequence number on the wire and
    // sorting by it; worth doing when someone is bothered enough to notice.
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
            // The kernel echoes the cell URI it was given; the editor's state is
            // keyed by the plain id.
            var cell = State(CellIdFrom(notification.CellId));
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

    // Executing a cell and parsing the file into cells read this set from two
    // different places, so they are told together or they drift: the language would
    // run when you pressed ▶ and its fenced block would stop being a code cell on
    // the next reload.
    private void SetLanguages(IReadOnlyList<LanguageDescriptor> languages) {
        Languages = languages ?? Array.Empty<LanguageDescriptor>();
        if (Languages.Count > 0) {
            _onLanguages?.Invoke(Languages);
        }
    }

    private static string ToNotebookUri(string notebookPath) {
        try {
            // AbsolutePath, not the raw path: a notebook with a space in its name
            // must escape it, and NotebookKeyFor unescapes on the way back.
            return "vscode-notebook-cell:" + new Uri(notebookPath).AbsolutePath;
        } catch (UriFormatException) {
            return "vscode-notebook-cell:" + notebookPath;
        }
    }

    // The cell id the editor knows, back out of the URI the kernel answers with.
    private static string CellIdFrom(string cellUri) {
        var hash = cellUri?.IndexOf('#') ?? -1;
        return hash < 0 ? cellUri : cellUri[(hash + 1)..];
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

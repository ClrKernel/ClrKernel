using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Studio;

/// <summary>
/// The warm kernels behind the web editor: one per notebook being edited, kept
/// alive between runs so variables persist, evicted when idle and killed on
/// shutdown.
/// <para>
/// Registered as a singleton <em>and</em> a hosted service, the way
/// <see cref="SchedulerService"/> is — endpoints inject it, and the host stops
/// it. Nothing else in the tool holds a long-lived child process, so this class
/// owns that responsibility outright.
/// </para>
/// </summary>
public sealed class NotebookSessionManager : BackgroundService {
    /// <summary>A kernel is a process and a Roslyn session; four open notebooks is
    /// plenty for one person editing, and bounds what a stray browser tab can cost.</summary>
    public const int MaxSessions = 4;

    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a hand-driven test or prod session lingers. Shorter than an
    /// editor's, because nobody is editing: long enough to read the output and run
    /// the next cell, short enough that state assembled by hand is not still there
    /// an hour later.
    /// </summary>
    public static readonly TimeSpan EphemeralIdleTimeout = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, NotebookSession> _sessions = new(StringComparer.Ordinal);
    private readonly JobsOptions _options;
    private readonly ILogger _logger;
    private readonly KernelLanguages _languages;
    private readonly SemaphoreSlim _starting = new(1, 1);

    public NotebookSessionManager(
        JobsOptions options, ILogger<NotebookSessionManager> logger, KernelLanguages languages = null) {
        _options = options;
        _logger = logger;
        _languages = languages;
    }

    /// <summary>An existing session under this key, or null.</summary>
    public NotebookSession Find(string key) =>
        _sessions.TryGetValue(key, out var session) ? session : null;

    public IReadOnlyList<NotebookSession> Sessions => _sessions.Values.ToList();

    /// <summary>
    /// The session for a notebook, started if needed. Throws when every slot is
    /// held by a busy session — the message names them, so the answer ("stop that
    /// run, or close that notebook") is obvious.
    /// </summary>
    /// <param name="key">
    /// What this session is filed under. The notebook's path for the editor, so
    /// re-opening a notebook finds the kernel you left running. Hand-driving test or
    /// prod adds the person to it: two people running the same production notebook
    /// must not be sharing one kernel, whatever else is true.
    /// </param>
    public async Task<NotebookSession> GetOrStartAsync(
        string notebookPath, CancellationToken cancellationToken,
        string key = null, bool ephemeral = false) {
        key ??= notebookPath;
        if (_sessions.TryGetValue(key, out var existing)) {
            existing.Touch();
            return existing;
        }
        await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (_sessions.TryGetValue(key, out existing)) {
                existing.Touch();
                return existing;
            }
            MakeRoom();
            var session = new NotebookSession(
                Guid.NewGuid().ToString("N"), notebookPath, _options.ClrKernelPath,
                line => _logger.LogDebug("[{Notebook}] {Line}", notebookPath, line),
                // A live kernel outranks the cached probe, and keeps outranking it: a
                // language registered mid-session by #r has to reach the parser too,
                // or its cells stop being cells the next time the file is read.
                onLanguages: languages => _languages?.Seed(languages)) { Ephemeral = ephemeral };
            // Start the kernel here rather than on first run, so a broken
            // configuration is reported when the editor opens, not mid-cell.
            await session.EnsureKernelAsync(cancellationToken).ConfigureAwait(false);
            _sessions[key] = session;
            _logger.LogInformation("Notebook session started for {Notebook} ({Kernel} {Version}).",
                notebookPath, session.KernelName, session.KernelVersion);
            return session;
        } finally {
            _starting.Release();
        }
    }

    /// <summary>
    /// Drops a session and its kernel. This is also the only interrupt available:
    /// neither kernel RPC surface can cancel a running cell, so a wedged one is
    /// stopped by killing the process.
    /// </summary>
    public bool Restart(string key) {
        if (!_sessions.TryRemove(key, out var session)) {
            return false;
        }
        _logger.LogInformation("Notebook session restarted for {Notebook}.", session.NotebookPath);
        session.Dispose();
        return true;
    }

    /// <summary>
    /// How a notebook is named in a message. The parent folder as well as the file:
    /// two projects may each hold an <c>etl.nb.md</c>, and "all sessions are busy
    /// (etl.nb.md, etl.nb.md)" names nothing at all.
    /// </summary>
    private static string Name(string notebookPath) =>
        System.IO.Path.Combine(
            System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(notebookPath)) ?? string.Empty,
            System.IO.Path.GetFileName(notebookPath)).Replace('\\', '/');

    // Evicts the least recently used idle session when the cap is reached.
    private void MakeRoom() {
        while (_sessions.Count >= MaxSessions) {
            var victim = _sessions.Values
                .Where(s => !s.Busy)
                .OrderBy(s => s.LastActivity)
                .FirstOrDefault();
            if (victim == null) {
                throw new InvalidOperationException(
                    $"All {MaxSessions} notebook sessions are running cells ({string.Join(", ",
                        _sessions.Values.Select(s => Name(s.NotebookPath)))}). " +
                    "Wait for one to finish, or restart its kernel.");
            }
            _sessions.TryRemove(
                _sessions.First(entry => ReferenceEquals(entry.Value, victim)).Key, out _);
            _logger.LogInformation("Evicting idle notebook session for {Notebook}.", victim.NotebookPath);
            victim.Dispose();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                var now = DateTime.UtcNow;
                foreach (var session in _sessions.ToList()) {
                    var idle = session.Value.Ephemeral ? EphemeralIdleTimeout : IdleTimeout;
                    if (session.Value.Busy || now - session.Value.LastActivity < idle) {
                        continue;
                    }
                    if (_sessions.TryRemove(session.Key, out _)) {
                        _logger.LogInformation(
                            "Notebook session for {Notebook} idle for {Minutes:0} minutes; stopping its kernel.",
                            session.Value.NotebookPath, idle.TotalMinutes);
                        session.Value.Dispose();
                    }
                }
            }
        } catch (OperationCanceledException) {
            // Shutting down.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken) {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in _sessions.ToList()) {
            _sessions.TryRemove(entry.Key, out _);
            entry.Value.Dispose();
        }
    }

    public override void Dispose() {
        foreach (var session in _sessions.Values.ToList()) {
            session.Dispose();
        }
        _sessions.Clear();
        _starting.Dispose();
        base.Dispose();
    }
}

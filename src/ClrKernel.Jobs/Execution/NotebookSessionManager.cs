using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Jobs;

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

    /// <summary>An existing session for this notebook, or null.</summary>
    public NotebookSession Find(string notebookPath) =>
        _sessions.TryGetValue(notebookPath, out var session) ? session : null;

    public IReadOnlyList<NotebookSession> Sessions => _sessions.Values.ToList();

    /// <summary>
    /// The session for a notebook, started if needed. Throws when every slot is
    /// held by a busy session — the message names them, so the answer ("stop that
    /// run, or close that notebook") is obvious.
    /// </summary>
    public async Task<NotebookSession> GetOrStartAsync(string notebookPath, CancellationToken cancellationToken) {
        if (_sessions.TryGetValue(notebookPath, out var existing)) {
            existing.Touch();
            return existing;
        }
        await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (_sessions.TryGetValue(notebookPath, out existing)) {
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
                onLanguages: languages => _languages?.Seed(languages));
            // Start the kernel here rather than on first run, so a broken
            // configuration is reported when the editor opens, not mid-cell.
            await session.EnsureKernelAsync(cancellationToken).ConfigureAwait(false);
            _sessions[notebookPath] = session;
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
    public bool Restart(string notebookPath) {
        if (!_sessions.TryRemove(notebookPath, out var session)) {
            return false;
        }
        _logger.LogInformation("Notebook session restarted for {Notebook}.", notebookPath);
        session.Dispose();
        return true;
    }

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
                        _sessions.Values.Select(s => System.IO.Path.GetFileName(s.NotebookPath)))}). " +
                    "Wait for one to finish, or restart its kernel.");
            }
            _sessions.TryRemove(victim.NotebookPath, out _);
            _logger.LogInformation("Evicting idle notebook session for {Notebook}.", victim.NotebookPath);
            victim.Dispose();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                var cutoff = DateTime.UtcNow - IdleTimeout;
                foreach (var session in _sessions.Values.Where(s => !s.Busy && s.LastActivity < cutoff).ToList()) {
                    if (_sessions.TryRemove(session.NotebookPath, out _)) {
                        _logger.LogInformation("Notebook session for {Notebook} idle for {Minutes:0} minutes; stopping its kernel.",
                            session.NotebookPath, IdleTimeout.TotalMinutes);
                        session.Dispose();
                    }
                }
            }
        } catch (OperationCanceledException) {
            // Shutting down.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken) {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var session in _sessions.Values.ToList()) {
            _sessions.TryRemove(session.NotebookPath, out _);
            session.Dispose();
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

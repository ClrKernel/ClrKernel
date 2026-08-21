using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ClrKernel.Jobs;

/// <summary>
/// A running <c>clrkernel</c> child and the JSON-RPC client attached to it —
/// owned together, because the client's Dispose closes the connection but never
/// kills the process. Used by job runs (one <c>serve</c> per run) and by the
/// editor's warm notebook sessions (one <c>lsp</c> per notebook).
/// </summary>
public sealed class KernelProcess : IDisposable {
    private readonly Process _process;
    private readonly Action<string> _log;

    private KernelProcess(Process process, KernelClient client, Action<string> log) {
        _process = process;
        Client = client;
        _log = log;
    }

    public KernelClient Client { get; }

    /// <summary>A kernel backed by an existing connection rather than a child
    /// process — the seam that lets sessions be tested against a fake kernel over
    /// an in-memory stream, with no clrkernel binary present.</summary>
    internal static KernelProcess ForClient(KernelClient client) => new(null, client, null);

    /// <summary>True when the kernel has died — a cell can call Environment.Exit,
    /// and shutdown does exactly that.</summary>
    public bool HasExited {
        get {
            if (_process == null) {
                return false; // an injected connection lives as long as its owner
            }
            try {
                return _process.HasExited;
            } catch (InvalidOperationException) {
                return true;
            }
        }
    }

    /// <summary>Spawns a kernel with its working directory set so a notebook's
    /// <c>#!import</c> and <c>connections.json</c> resolve beside it.</summary>
    /// <param name="mode">Which surface to start. Job runs take the default
    /// <c>serve</c>; the editor takes <c>lsp</c>, so its cells reach the same server
    /// VS Code drives and get language features from it.</param>
    public static KernelProcess Start(
        string configuredPath, string workingDirectory, Action<string> log,
        KernelMode mode = KernelMode.Serve) {
        var clrkernel = ClrKernelLocator.Find(configuredPath);
        var argument = mode == KernelMode.Lsp ? "lsp" : "serve";
        var process = new Process {
            StartInfo = new ProcessStartInfo {
                FileName = clrkernel,
                Arguments = argument,
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
            EnableRaisingEvents = true,
        };
        process.ErrorDataReceived += (_, e) => {
            if (e.Data != null) {
                log?.Invoke($"kernel: {e.Data}");
            }
        };
        log?.Invoke($"Starting {clrkernel} {argument} (cwd {workingDirectory})");
        process.Start();
        process.BeginErrorReadLine();
        var client = new KernelClient(
            process.StandardInput.BaseStream, process.StandardOutput.BaseStream, mode);
        return new KernelProcess(process, client, log);
    }

    /// <summary>Initializes with a hard cap: a kernel that never answers would
    /// otherwise hang an untimed caller forever.</summary>
    public Task<InitializeReply> InitializeAsync(CancellationToken cancellationToken) =>
        InitializeWithTimeoutAsync(Client, cancellationToken);

    /// <summary>The same cap over a client the caller already owns — the test seam
    /// drives an in-memory duplex stream with no process behind it.</summary>
    internal static async Task<InitializeReply> InitializeWithTimeoutAsync(
        KernelClient client, CancellationToken cancellationToken) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try {
            return await client.InitializeAsync(timeout.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            throw new InvalidOperationException("The kernel did not answer initialize within 60s.");
        }
    }

    public void Dispose() {
        Client.Dispose();
        if (_process == null) {
            return;
        }
        try {
            if (!_process.HasExited) {
                _process.WaitForExit(2000);
            }
            if (!_process.HasExited) {
                _process.Kill(entireProcessTree: true);
            }
        } catch (Exception e) {
            _log?.Invoke($"could not stop the kernel: {e.Message}");
        } finally {
            _process.Dispose();
        }
    }
}

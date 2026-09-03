using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;

namespace ClrKernel.Language.Python;

/// <summary>What a cell did: its output, and whether it raised.</summary>
public sealed class PythonRunResult {
    public string Output { get; init; } = string.Empty;
    public string Error { get; init; }
    public bool Failed => Error != null;

    /// <summary>What the cell ended on, and anything it drew — a DataFrame as a
    /// <see cref="DisplayTable"/>, a figure as a PNG <see cref="DisplayBytes"/>.</summary>
    public IReadOnlyList<IDisplayValue> Displays { get; init; } = Array.Empty<IDisplayValue>();
}

/// <summary>
/// One resident interpreter, shared by every <c>#!python</c> cell in a notebook,
/// so names bound in one cell are there in the next.
///
/// <para>
/// Beside the process rather than inside it: a segfault in a native wheel takes
/// the interpreter and not the notebook's kernel, and a run is cancelled by
/// killing it — which is the only interrupt this kernel has anywhere. The
/// precedent is Studio's <c>KernelProcess</c>, a long-lived child over stdio, not
/// <c>PowerShellSession</c>, which hosts its runtime in-process.
/// </para>
/// <para>
/// The protocol is one framed JSON message per line, in one direction each way,
/// on stdout alone — see driver.py for why it is not stdout plus stderr.
/// </para>
/// </summary>
public sealed class PythonSession : IDisposable {
    private readonly object _lock = new();
    private Process _process;
    private string _driverPath;
    private bool _disposed;

    /// <summary>The interpreter this session is running, once it has started.</summary>
    public string Executable { get; private set; }

    /// <summary>What the interpreter reported at startup ("3.13.15"), or null.</summary>
    public string Version { get; private set; }

    /// <summary>Called with each chunk of output as it arrives, so a long cell shows
    /// progress instead of nothing followed by everything.</summary>
    public Action<string> OnOutput { get; set; }

    /// <summary>
    /// Runs one cell, returning when the interpreter says it is done.
    /// </summary>
    public Task<PythonRunResult> ExecuteAsync(
        string code, string workingDirectory, CancellationToken cancellationToken = default) =>
        SendAsync(new Dictionary<string, string> {
            ["t"] = "run",
            // Base64 so a cell's own newlines cannot break the framing, whatever it
            // contains — a docstring with a brace in it is not the host's problem.
            ["code"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(code ?? string.Empty)),
        }, workingDirectory, cancellationToken);

    /// <summary>One request, then everything the interpreter says until it is done.</summary>
    private async Task<PythonRunResult> SendAsync(
        Dictionary<string, string> request, string workingDirectory, CancellationToken cancellationToken) {
        await EnsureInterpreterAsync(cancellationToken).ConfigureAwait(false);
        var process = Start(workingDirectory);
        var all = new StringBuilder();

        var displays = new List<IDisplayValue>();
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);

        while (true) {
            var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
            if (line == null) {
                // The interpreter died mid-cell. Whatever it printed first is still
                // the most useful thing to show.
                Restart();
                return new PythonRunResult {
                    Output = all.ToString(),
                    Error = "The Python interpreter exited during this cell.",
                };
            }
            if (cancellationToken.IsCancellationRequested) {
                Restart();
                cancellationToken.ThrowIfCancellationRequested();
            }
            var message = Parse(line);
            if (message == null) {
                continue;
            }
            switch (message.Value.Type) {
                case "out":
                    all.Append(message.Value.Data);
                    OnOutput?.Invoke(message.Value.Data);
                    break;
                case "display":
                    if (ParseDisplay(line) is { } display) {
                        displays.Add(display);
                    }
                    break;
                case "done":
                    return new PythonRunResult {
                        Output = all.ToString(),
                        Error = message.Value.Status == "ok" ? null : message.Value.Data,
                        Displays = displays,
                    };
            }
        }
    }

    /// <summary>
    /// A display message as the concept it names. Re-parsed rather than threaded
    /// through <see cref="Parse"/>, whose tuple is for the three common messages;
    /// these are rare and structural.
    /// </summary>
    private static IDisplayValue ParseDisplay(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (root.TryGetProperty("kind", out var k) ? k.GetString() : null) {
                case "bytes":
                    return new DisplayBytes(
                        Convert.FromBase64String(root.GetProperty("d").GetString()),
                        root.GetProperty("mime").GetString());
                case "table":
                    var rows = root.GetProperty("rows").EnumerateArray()
                        .Select(r => (IReadOnlyList<string>)r.EnumerateArray()
                            .Select(c => c.ValueKind == JsonValueKind.Null ? null : c.GetString()).ToList())
                        .ToList();
                    return new DisplayTable(
                        rows,
                        root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList(),
                        rows,
                        root.GetProperty("types").EnumerateArray().Select(t => t.GetString()).ToList(),
                        root.TryGetProperty("total", out var total) ? total.GetInt32() : null);
                default:
                    return null;
            }
        } catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException) {
            // A malformed display is not worth losing the cell's output over.
            return null;
        }
    }

    private (string Type, string Status, string Data)? Parse(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("t", out var t) ? t.GetString() : null;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            var data = root.TryGetProperty("d", out var d) ? d.GetString()
                : root.TryGetProperty("error", out var e) ? e.GetString()
                : null;
            if (type == "ready") {
                Version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            }
            return (type, status, data);
        } catch (JsonException) {
            // Not ours: an interpreter that writes to fd 1 behind Python's back
            // (a C extension, a crash handler) must not take the session down.
            return null;
        }
    }

    /// <summary>
    /// Makes sure there is an interpreter to start, installing one if this machine
    /// has none — the first `#!python` cell on a fresh machine, and only then.
    ///
    /// <para>
    /// Outside the lock, deliberately: it downloads, and holding the lock every
    /// other cell waits on for the length of a 60 MB fetch would stall the notebook
    /// rather than just this cell.
    /// </para>
    /// </summary>
    private async Task EnsureInterpreterAsync(CancellationToken cancellationToken) {
        if (_process is { HasExited: false } || PythonInterpreter.Resolve() != null) {
            return;
        }
        var installed = await PythonProvisioner
            .EnsureAsync(message => OnOutput?.Invoke(message + Environment.NewLine), cancellationToken)
            .ConfigureAwait(false);
        if (installed == null) {
            throw new PythonCellException(
                PythonInterpreter.NotFoundMessage()
                + $" Automatic installation is off ({PythonProvisioner.AutoInstallVariable}).");
        }
    }

    /// <summary>Where <c>#!python-install</c> puts packages for this session; set
    /// once the interpreter starts, because it derives from the notebook.</summary>
    public string Packages { get; private set; }

    /// <summary>
    /// Makes packages installed since the interpreter started importable. The import
    /// machinery caches each <c>sys.path</c> directory as it first saw it, so without
    /// this a package installed underneath a running interpreter stays invisible.
    /// </summary>
    public Task<PythonRunResult> RefreshPackagesAsync(
        string workingDirectory, CancellationToken cancellationToken = default) =>
        SendAsync(new Dictionary<string, string> { ["t"] = "refresh" }, workingDirectory, cancellationToken);

    private Process Start(string workingDirectory) {
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is { HasExited: false }) {
                return _process;
            }
            var executable = PythonInterpreter.Resolve()
                ?? throw new PythonCellException(PythonInterpreter.NotFoundMessage());
            Executable = executable;
            _driverPath ??= WriteDriver();

            var start = new ProcessStartInfo {
                FileName = executable,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Directory.Exists(workingDirectory)
                    ? workingDirectory
                    : Directory.GetCurrentDirectory(),
            };
            // Headless: matplotlib otherwise picks a GUI backend, and opening a window
            // from a kernel is at best useless and at worst a hang. Set rather than
            // appended to, but only when the user has not chosen one themselves.
            start.Environment["MPLBACKEND"] =
                Environment.GetEnvironmentVariable("MPLBACKEND") is { Length: > 0 } chosen ? chosen : "Agg";

            // -u: unbuffered. Without it the driver's framed lines sit in a pipe
            // buffer and a cell that prints then sleeps shows nothing until it ends.
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add(_driverPath);
            // This notebook's installed packages, put on sys.path by the driver.
            Packages = PythonEnvironment.PackagesFor(workingDirectory);
            start.ArgumentList.Add(Packages);
            // The interpreter's own stdout is the protocol; anything a library writes
            // to fd 2 is diagnostics, and is drained so it cannot fill a pipe and
            // wedge the process.
            Process process;
            try {
                process = Process.Start(start);
            } catch (Exception e) {
                throw new PythonCellException(
                    $"Could not start Python at '{executable}': {e.Message}", e);
            }
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();

            // The handshake, so a broken interpreter fails here rather than on the
            // first cell that happens to run.
            var ready = process.StandardOutput.ReadLine();
            if (ready == null || Parse(ready) is not { Type: "ready" }) {
                var detail = ready == null ? "it exited immediately" : $"it said: {ready}";
                try {
                    process.Kill(entireProcessTree: true);
                } catch (Exception) {
                    // Already gone.
                }
                throw new PythonCellException(
                    $"Python at '{executable}' did not start as a notebook interpreter — {detail}.");
            }
            _process = process;
            return process;
        }
    }

    /// <summary>Drops the interpreter. The next cell gets a fresh one with no state —
    /// which is what "restart kernel" means for this language.</summary>
    public void Restart() {
        lock (_lock) {
            KillProcess();
        }
    }

    private void KillProcess() {
        var process = _process;
        _process = null;
        Version = null;
        if (process == null) {
            return;
        }
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        } catch (Exception) {
            // Exited between the check and the kill.
        } finally {
            process.Dispose();
        }
    }

    /// <summary>
    /// The driver, unpacked beside the interpreter's other state.
    ///
    /// <para>
    /// An embedded resource rather than a file next to the assembly: this package
    /// is also loaded by `#r "nuget: …"` into a running session, where there is no
    /// content directory to read from.
    /// </para>
    /// </summary>
    private static string WriteDriver() {
        var directory = Path.Combine(PythonInterpreter.Home(), "driver");
        Directory.CreateDirectory(directory);
        var assembly = typeof(PythonSession).Assembly;
        using var stream = assembly.GetManifestResourceStream("ClrKernel.Language.Python.driver.py")
            ?? throw new PythonCellException("The Python driver is missing from this build.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        // Versioned by content, so an upgraded kernel does not run an old driver and
        // two kernels of different versions do not fight over one file.
        var name = "driver-" + Hash(text) + ".py";
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) {
            File.WriteAllText(path, text);
        }
        return path;
    }

    private static string Hash(string text) {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            KillProcess();
        }
    }
}

/// <summary>A cell that could not run, or raised.</summary>
public sealed class PythonCellException : Exception {
    public PythonCellException(string message) : base(message) { }
    public PythonCellException(string message, Exception inner) : base(message, inner) { }
}

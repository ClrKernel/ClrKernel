using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ClrKernel.Language.Shell;

/// <summary>
/// Runs shell cells. Each cell is one <c>shell -c</c> process, but the session keeps
/// notebook semantics: the working directory and exported environment are captured
/// after every cell (via a small wrapper appended to the script) and restored for
/// the next one, so <c>cd</c> and <c>export</c> carry across cells. Colour is
/// forced on (<c>TERM</c>, <c>CLICOLOR_FORCE</c>, <c>FORCE_COLOR</c>) because a
/// captured pipe is not a TTY and most tools would silently go monochrome; the
/// resulting ANSI escapes are the cell's output, rendered by the registered
/// console-text formatters.
/// </summary>
public sealed class ShellSession {
    private readonly object _lock = new();
    private string _workingDirectory;
    private Dictionary<string, string> _environment;

    // Named SSH targets (#!shell-connect / connections.json "$type": "Ssh"),
    // and the last-seen remote working directory per target so `cd` carries
    // across remote cells the way it does locally.
    private readonly Dictionary<string, ShellConnectionSpec> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _remoteCwd = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _remoteShells = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers an SSH target from a <c>#!shell-connect</c> line.</summary>
    public ShellConnectionSpec Connect(string directiveLine) {
        var spec = ShellDirectives.ParseConnect(directiveLine);
        lock (_lock) {
            _connections[spec.Name] = spec;
            _remoteCwd.Remove(spec.Name);
            _remoteShells.Remove(spec.Name);
        }
        return spec;
    }

    /// <summary>Registers every <c>"$type": "Ssh"</c> entry from the nearest
    /// connections.json (+ .local overlay). Returns the names loaded.</summary>
    public IReadOnlyList<string> LoadFromConfig(string startDirectory = null) {
        var loaded = new List<string>();
        foreach (var file in ClrKernel.Database.ConnectionConfig.FindFiles(startDirectory)) {
            foreach (var node in ClrKernel.Database.ConnectionConfig.LoadAllRaw(file)) {
                if (!node.IsType(ShellConnectionConfig.TypeName)) {
                    continue;
                }
                var spec = ShellConnectionConfig.FromNode(node);
                lock (_lock) {
                    _connections[spec.Name] = spec;
                }
                if (!loaded.Contains(node.Name)) {
                    loaded.Add(node.Name);
                }
            }
        }
        return loaded;
    }

    private ShellConnectionSpec Resolve(string connectionName) {
        lock (_lock) {
            if (_connections.TryGetValue(connectionName, out var spec)) {
                return spec;
            }
        }
        LoadFromConfig(); // a saved target may not have been loaded yet
        lock (_lock) {
            if (_connections.TryGetValue(connectionName, out var spec)) {
                return spec;
            }
            throw new ShellCellException(
                $"No SSH connection named '{connectionName}'. " +
                (_connections.Count == 0
                    ? "Add one with #!shell-connect --name <n> --host <host> [--user <u>]."
                    : $"Known connections: {string.Join(", ", _connections.Keys)}."));
        }
    }

    // Shell-managed variables that must not be replayed into the next process:
    // the shell derives them itself, and a stale PWD would fight the real cwd.
    private static readonly string[] _unportable = { "_", "SHLVL", "PWD", "OLDPWD" };

    public sealed class ShellRunResult {
        public string Output { get; set; }
        public int ExitCode { get; set; }
    }

    /// <summary>Runs a cell on a registered SSH target via the system <c>ssh</c> client.
    /// The script travels on stdin, so no remote-quoting games. The remote shell is the
    /// requested one when the target has it, otherwise auto-detected once per target
    /// (bash → sh → pwsh → powershell), which is what makes shell cells work against
    /// Windows OpenSSH boxes whose default shell is cmd/PowerShell. The remote working
    /// directory persists per target (exported env does not — each remote cell is a
    /// fresh login).</summary>
    public async Task<ShellRunResult> ExecuteRemoteAsync(string shell, string script, string connectionName) {
        var spec = Resolve(connectionName);
        var resolved = await ResolveRemoteShellAsync(spec, shell).ConfigureAwait(false);
        string remoteCwd;
        lock (_lock) {
            _remoteCwd.TryGetValue(spec.Name, out remoteCwd);
        }

        var isPowerShell = resolved is "pwsh" or "powershell";
        var remoteCommand = isPowerShell
            ? resolved + " -NoProfile -NonInteractive -Command -"
            : resolved + " -s";
        var wrapped = isPowerShell
            ? BuildPowerShellWrapper(script, remoteCwd)
            : BuildPosixWrapper(script, remoteCwd);

        var run = await RunSshAsync(spec, remoteCommand, wrapped).ConfigureAwait(false);
        if (run.ExitCode == 255) {
            // 255 is ssh's own failure code, distinct from the remote command's.
            throw new ShellCellException(
                $"ssh to {spec.Describe()} failed: {(run.Stderr.Length > 0 ? run.Stderr : "connection error")}. " +
                "Key-based auth is required (BatchMode) — check your keys/agent or ~/.ssh/config.");
        }

        var (output, capturedCwd) = StripCwdMarker(run.Stdout);
        if (capturedCwd != null) {
            lock (_lock) {
                _remoteCwd[spec.Name] = capturedCwd;
            }
        }
        // Local stderr is ssh client chatter (host-key notices, the post-quantum
        // warning, …): surface it only when the command failed, where it may also
        // carry the actual reason.
        if (run.ExitCode != 0 && run.Stderr.Length > 0) {
            output = output.Length > 0 ? output + "\n" + run.Stderr : run.Stderr;
        }
        return new ShellRunResult { Output = output.TrimEnd('\r', '\n'), ExitCode = run.ExitCode };
    }

    // What actually runs cells on the target, chosen once per target and cached:
    // the requested shell if present, else the first of the fallbacks that answers.
    private async Task<string> ResolveRemoteShellAsync(ShellConnectionSpec spec, string requestedShell) {
        if (!string.IsNullOrEmpty(spec.RemoteShell)) {
            return spec.RemoteShell.ToLowerInvariant();
        }
        lock (_lock) {
            if (_remoteShells.TryGetValue(spec.Name, out var cached)) {
                return cached;
            }
        }

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(requestedShell) && !candidates.Contains(requestedShell)) {
            candidates.Add(requestedShell);
        }
        foreach (var fallback in new[] { "bash", "sh", "pwsh", "powershell" }) {
            if (!candidates.Contains(fallback)) {
                candidates.Add(fallback);
            }
        }

        var failures = new List<string>();
        foreach (var candidate in candidates) {
            var probe = await RunSshAsync(spec, ProbeCommandFor(candidate), null).ConfigureAwait(false);
            if (probe.ExitCode == 255) {
                throw new ShellCellException(
                    $"ssh to {spec.Describe()} failed: {(probe.Stderr.Length > 0 ? probe.Stderr : "connection error")}. " +
                    "Key-based auth is required (BatchMode) — check your keys/agent or ~/.ssh/config.");
            }
            if (probe.ExitCode == 0 && probe.Stdout.Contains("ck-ok")) {
                lock (_lock) {
                    _remoteShells[spec.Name] = candidate;
                }
                return candidate;
            }
            failures.Add(candidate);
        }
        throw new ShellCellException(
            $"No usable shell found on {spec.Describe()} (tried: {string.Join(", ", failures)}). " +
            "Set one explicitly with #!shell-connect ... --remote-shell <shell>.");
    }

    /// <summary>The probe that answers "does this shell exist on the target?" — valid
    /// under cmd.exe, PowerShell, and POSIX login shells alike.</summary>
    internal static string ProbeCommandFor(string candidate) =>
        candidate is "pwsh" or "powershell"
            ? candidate + " -NoProfile -NonInteractive -Command \"Write-Output ck-ok\""
            : candidate + " -c \"echo ck-ok\"";

    internal static string BuildPosixWrapper(string script, string remoteCwd) =>
        "exec 2>&1\n" +
        // Not a TTY on the remote end either: advertise colour there too.
        "export TERM=xterm-256color CLICOLOR=1 CLICOLOR_FORCE=1 FORCE_COLOR=1\n" +
        "unset NO_COLOR\n" +
        (remoteCwd != null ? $"cd '{remoteCwd.Replace("'", "'\\''")}' 2>/dev/null\n" : "") +
        (script ?? string.Empty) + "\n" +
        "__ck_rc=$?\n" +
        $"printf '{_cwdMarker}%s{_cwdMarker}' \"$PWD\"\n" +
        "exit $__ck_rc\n";

    // Windows PowerShell 5.1 compatible (no PS7-only syntax): capture $?/$LASTEXITCODE,
    // print the cwd marker, exit with the script's code.
    internal static string BuildPowerShellWrapper(string script, string remoteCwd) =>
        (remoteCwd != null
            ? $"Set-Location -LiteralPath '{remoteCwd.Replace("'", "''")}' -ErrorAction SilentlyContinue\n"
            : "") +
        (script ?? string.Empty) + "\n" +
        "$__ck_ok = $?\n" +
        "$__ck_rc = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } elseif ($__ck_ok) { 0 } else { 1 }\n" +
        "Write-Output ([string][char]1 + (Get-Location).Path + [string][char]1)\n" +
        "exit $__ck_rc\n";

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunSshAsync(
        ShellConnectionSpec spec, string remoteCommand, string stdin) {
        var start = new ProcessStartInfo {
            FileName = "ssh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in spec.BuildSshArguments(remoteCommand)) {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try {
            process.Start();
        } catch (Win32Exception e) {
            throw new ShellCellException("'ssh' was not found on PATH. Remote shell cells need an OpenSSH client.", e);
        }
        if (stdin != null) {
            await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        }
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode,
            await stdout.ConfigureAwait(false),
            (await stderr.ConfigureAwait(false)).TrimEnd('\r', '\n'));
    }

    // \x01 can't appear in ordinary output, so the marker survives any script.
    private const string _cwdMarker = "\u0001";

    /// <summary>Splits the trailing <c>\x01&lt;pwd&gt;\x01</c> the wrapper printed off the
    /// output. Missing marker (early exit) leaves the cwd unknown.</summary>
    internal static (string Output, string Cwd) StripCwdMarker(string raw) {
        raw ??= string.Empty;
        var end = raw.LastIndexOf(_cwdMarker, StringComparison.Ordinal);
        if (end <= 0) {
            return (raw, null);
        }
        var start = raw.LastIndexOf(_cwdMarker, end - 1, StringComparison.Ordinal);
        if (start < 0) {
            return (raw, null);
        }
        var cwd = raw.Substring(start + 1, end - start - 1);
        var output = raw.Remove(start, end - start + 1);
        return (output, cwd.Length > 0 ? cwd : null);
    }

    public async Task<ShellRunResult> ExecuteAsync(string shell, string script, string fallbackWorkingDirectory) {
        string cwdFile = Path.GetTempFileName();
        string envFile = Path.GetTempFileName();
        try {
            var wrapped =
                // Merge stderr into stdout from here on: interleaving is preserved
                // and progress/diagnostic output lands in cell order.
                "exec 2>&1\n" +
                (script ?? string.Empty) + "\n" +
                "__ck_rc=$?\n" +
                $"pwd > '{cwdFile}'\n" +
                // NUL-separated survives newlines in values; plain env is the fallback.
                $"env -0 > '{envFile}' 2>/dev/null || env > '{envFile}'\n" +
                "exit $__ck_rc\n";

            var start = new ProcessStartInfo {
                FileName = shell,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(wrapped);

            lock (_lock) {
                start.WorkingDirectory = FirstExistingDirectory(_workingDirectory, fallbackWorkingDirectory)
                    ?? Directory.GetCurrentDirectory();
                if (_environment != null) {
                    start.Environment.Clear();
                    foreach (var pair in _environment) {
                        start.Environment[pair.Key] = pair.Value;
                    }
                }
            }
            ForceColour(start.Environment);

            using var process = new Process { StartInfo = start };
            try {
                process.Start();
            } catch (Win32Exception e) {
                throw new ShellCellException(
                    $"'{shell}' was not found on PATH. Shell cells need it installed " +
                    "(on Windows: Git Bash or WSL provides bash).", e);
            }
            process.StandardInput.Close();

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);

            // exec 2>&1 runs first, so stderr is normally empty — anything here
            // failed before the wrapper (e.g. the shell rejecting the script).
            var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));

            lock (_lock) {
                var cwd = ReadCapturedCwd(cwdFile);
                if (cwd != null) {
                    _workingDirectory = cwd;
                }
                var env = ReadCapturedEnvironment(envFile);
                if (env != null) {
                    _environment = env;
                }
            }

            return new ShellRunResult {
                Output = output.TrimEnd('\r', '\n'),
                ExitCode = process.ExitCode,
            };
        } finally {
            TryDelete(cwdFile);
            TryDelete(envFile);
        }
    }

    private static void ForceColour(IDictionary<string, string> environment) {
        if (!environment.ContainsKey("TERM") || string.IsNullOrEmpty(environment["TERM"]) || environment["TERM"] == "dumb") {
            environment["TERM"] = "xterm-256color";
        }
        environment["CLICOLOR"] = "1";
        environment["CLICOLOR_FORCE"] = "1";
        environment["FORCE_COLOR"] = "1";
        environment.Remove("NO_COLOR");
    }

    private static string FirstExistingDirectory(params string[] candidates) {
        foreach (var candidate in candidates) {
            if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate)) {
                return candidate;
            }
        }
        return null;
    }

    private static string ReadCapturedCwd(string file) {
        try {
            var text = File.ReadAllText(file).Trim();
            return text.Length > 0 && Directory.Exists(text) ? text : null;
        } catch {
            return null;
        }
    }

    private static Dictionary<string, string> ReadCapturedEnvironment(string file) {
        try {
            var raw = File.ReadAllText(file);
            if (raw.Length == 0) {
                return null; // the cell exited early (explicit exit): keep the previous env
            }
            var entries = raw.Contains('\0')
                ? raw.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries) {
                var eq = entry.IndexOf('=');
                if (eq <= 0) {
                    continue;
                }
                var key = entry.Substring(0, eq);
                if (Array.IndexOf(_unportable, key) >= 0) {
                    continue;
                }
                env[key] = entry.Substring(eq + 1);
            }
            return env.Count > 0 ? env : null;
        } catch {
            return null;
        }
    }

    private static void TryDelete(string file) {
        try { File.Delete(file); } catch { /* best effort */ }
    }
}

/// <summary>A shell cell failure the host shows as an error output.</summary>
public sealed class ShellCellException : Exception {
    public ShellCellException(string message, Exception inner = null) : base(message, inner) { }
}

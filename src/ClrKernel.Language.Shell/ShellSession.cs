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

    // Shell-managed variables that must not be replayed into the next process:
    // the shell derives them itself, and a stale PWD would fight the real cwd.
    private static readonly string[] _unportable = { "_", "SHLVL", "PWD", "OLDPWD" };

    public sealed class ShellRunResult {
        public string Output { get; set; }
        public int ExitCode { get; set; }
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

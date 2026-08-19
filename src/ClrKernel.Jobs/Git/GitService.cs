using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Jobs;

/// <summary>Thrown when a git command fails; the message carries the command and stderr.</summary>
public sealed class GitException : Exception {
    public GitException(string message) : base(message) { }
}

/// <summary>
/// The git layer behind dev→prod promotion: a bare repo at
/// <c>&lt;workspace&gt;/.repo.git</c> with two worktrees — <c>dev</c> (branch dev,
/// where editing happens) and <c>prod</c> (branch main, what the scheduler runs).
/// <para>
/// Shells out to the git CLI, hardened for a server: no ambient config is trusted
/// (identity, safe.directory and gpg-signing are pinned per invocation), prompts are
/// impossible (<c>GIT_TERMINAL_PROMPT=0</c>, ssh batch mode), every command has a
/// hard timeout, and one semaphore serializes git operations <em>and the file writes
/// that precede commits</em> — a save is write+add+commit as one critical section,
/// or racing saves would commit each other's bytes under the wrong message.
/// </para>
/// </summary>
public sealed class GitService {
    public const string DevBranch = "dev";
    public const string ProdBranch = "main";

    private readonly string _workspace;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _authorName;
    private readonly string _authorEmail;

    /// <summary>Hard per-command timeout; internal so tests can shrink it.</summary>
    internal TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public GitService(string workspace, ILogger logger, string authorName = null, string authorEmail = null) {
        _workspace = Path.GetFullPath(workspace);
        _logger = logger;
        _authorName = string.IsNullOrWhiteSpace(authorName) ? "clrkernel-jobs" : authorName;
        _authorEmail = string.IsNullOrWhiteSpace(authorEmail) ? "jobs@clrkernel.local" : authorEmail;
    }

    public string Workspace => _workspace;
    public string BareRepoPath => Path.Combine(_workspace, ".repo.git");
    public string DevPath => Path.Combine(_workspace, DevBranch);
    public string ProdPath => Path.Combine(_workspace, "prod");

    /// <summary>True when the bare repo and both worktrees exist.</summary>
    public bool LayoutExists =>
        Directory.Exists(BareRepoPath) && Directory.Exists(DevPath) && Directory.Exists(ProdPath);

    public string PathFor(string environment) =>
        environment == "prod" ? ProdPath : DevPath;

    // --- the critical section -------------------------------------------------

    /// <summary>
    /// Runs <paramref name="action"/> holding the git lock. File writes that will be
    /// committed MUST happen inside this, in the same hold as their commit.
    /// </summary>
    public T WithLock<T>(Func<T> action) {
        _gate.Wait();
        try {
            return action();
        } finally {
            _gate.Release();
        }
    }

    public void WithLock(Action action) => WithLock<object>(() => {
        action();
        return null;
    });

    // --- plumbing ---------------------------------------------------------------

    /// <summary>Runs git with pinned identity/safety config. Throws on failure.</summary>
    private string Run(string workdir, params string[] args) {
        var (code, stdout, stderr) = TryRun(workdir, args);
        if (code != 0) {
            throw new GitException(
                $"git {string.Join(' ', args)} failed ({code}): {Truncate(stderr.Trim(), 500)}");
        }
        return stdout;
    }

    private (int Code, string Stdout, string Stderr) TryRun(string workdir, params string[] args) {
        var psi = new ProcessStartInfo {
            FileName = "git",
            WorkingDirectory = workdir,
            // stdin is redirected and closed immediately: commands that read it
            // (mktree with no input) must see EOF, not inherit a terminal and hang.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Pinned config: never trust (or require) ambient gitconfig.
        foreach (var arg in new[] {
            "-c", $"user.name={_authorName}",
            "-c", $"user.email={_authorEmail}",
            "-c", "safe.directory=*",
            "-c", "commit.gpgsign=false",
            "-c", "core.autocrlf=false",
        }) {
            psi.ArgumentList.Add(arg);
        }
        foreach (var arg in args) {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_SSH_COMMAND"] = "ssh -oBatchMode=yes";
        psi.Environment["GIT_AUTHOR_NAME"] = _authorName;
        psi.Environment["GIT_AUTHOR_EMAIL"] = _authorEmail;
        psi.Environment["GIT_COMMITTER_NAME"] = _authorName;
        psi.Environment["GIT_COMMITTER_EMAIL"] = _authorEmail;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { stderr.AppendLine(e.Data); } };

        try {
            process.Start();
        } catch (Exception e) {
            throw new GitException(
                "git is not installed or not on PATH — the dev/prod workflow needs it. " + e.Message);
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)CommandTimeout.TotalMilliseconds)) {
            try {
                process.Kill(entireProcessTree: true);
            } catch (Exception) {
                // Exited in the window between the check and the kill.
            }
            throw new GitException(
                $"git {string.Join(' ', args)} exceeded {CommandTimeout.TotalSeconds:0}s and was killed.");
        }
        process.WaitForExit(); // flush async readers
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>Test hook: run a raw git command in the bare repo (timeout path etc.).</summary>
    internal string RunForTests(params string[] args) => Run(BareRepoPath, args);

    // --- layout -----------------------------------------------------------------

    /// <summary>
    /// Creates the workspace layout. Existing loose files in the workspace are
    /// adopted into dev and promoted to main so an existing notebooks folder keeps
    /// working. Idempotent: an intact layout is left alone; a half-formed one gets
    /// instructions rather than guesses.
    /// </summary>
    public string Init() {
        return WithLock(() => {
            if (LayoutExists) {
                Repair();
                return "workspace already initialized";
            }
            if (Directory.Exists(BareRepoPath) || Directory.Exists(DevPath) || Directory.Exists(ProdPath)) {
                throw new GitException(
                    $"The workspace at {_workspace} is half-initialized (some of .repo.git/dev/prod " +
                    "exist). Move or remove them, then run `clrkernel-jobs git init` again.");
            }

            Directory.CreateDirectory(_workspace);
            Run(_workspace, "init", "--bare", "--initial-branch", ProdBranch, BareRepoPath);

            // The first commit must exist before worktrees can be added, and
            // `worktree add --orphan` needs git ≥2.42 (newer than Debian bookworm).
            // Plumbing works everywhere: empty tree -> empty commit -> main.
            var emptyTree = Run(BareRepoPath, "mktree").Trim();          // no stdin = empty tree
            var initial = Run(BareRepoPath, "commit-tree", emptyTree, "-m", "initial").Trim();
            Run(BareRepoPath, "update-ref", $"refs/heads/{ProdBranch}", initial);
            Run(BareRepoPath, "branch", DevBranch, ProdBranch);
            Run(BareRepoPath, "worktree", "add", DevPath, DevBranch);
            Run(BareRepoPath, "worktree", "add", ProdPath, ProdBranch);

            // Adopt loose files: they move into dev, and main fast-forwards so prod
            // starts equal to dev (everything existing is implicitly approved).
            var adopted = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(_workspace)) {
                var name = Path.GetFileName(entry);
                if (name is ".repo.git" or "dev" or "prod" || name.StartsWith('.')
                    || name is NotificationChannels.FileName or "settings.json") {
                    continue;
                }
                var target = Path.Combine(DevPath, name);
                Directory.Move(entry, target); // moves files too
                adopted++;
            }
            if (adopted > 0) {
                Run(DevPath, "add", "-A");
                Run(DevPath, "commit", "-m", "adopt existing notebooks");
                Run(ProdPath, "merge", "--ff-only", DevBranch);
            }
            _logger.LogInformation("Initialized git workspace at {Workspace} ({Adopted} adopted).",
                _workspace, adopted);
            return adopted > 0
                ? $"initialized; adopted {adopted} existing item(s) into dev and promoted them"
                : "initialized";
        });
    }

    /// <summary>Fixes worktree gitdir pointers after the workspace moved (volumes do).</summary>
    public void Repair() {
        Run(BareRepoPath, "worktree", "repair", DevPath, ProdPath);
    }

    // --- queries (callers may hold the lock; these take it for one-off use) ------

    public string HeadSha(string environment) =>
        Run(PathFor(environment), "rev-parse", "HEAD").Trim();

    /// <summary>Uncommitted changes under the given paths (or anywhere when none given).</summary>
    public bool IsDirty(string environment, params string[] paths) {
        var args = new List<string> { "status", "--porcelain" };
        if (paths.Length > 0) {
            args.Add("--");
            args.AddRange(paths);
        }
        return Run(PathFor(environment), args.ToArray()).Trim().Length > 0;
    }

    /// <summary>True when the paths are identical between the two refs.</summary>
    public bool UnchangedBetween(string fromRef, string toRef, params string[] paths) {
        var args = new List<string> { "diff", "--quiet", fromRef, toRef };
        if (paths.Length > 0) {
            args.Add("--");
            args.AddRange(paths);
        }
        return TryRun(DevPath, args.ToArray()).Code == 0;
    }

    /// <summary>Unified diff of a path between prod (main) and dev.</summary>
    public string UnifiedDiff(string path) =>
        Run(DevPath, "diff", ProdBranch, DevBranch, "--", path);

    /// <summary>name-status lines (A/M/D\tpath) between prod and dev for the paths.</summary>
    public IReadOnlyList<(char Status, string Path)> NameStatus(params string[] paths) {
        var args = new List<string> { "diff", "--name-status", ProdBranch, DevBranch };
        if (paths.Length > 0) {
            args.Add("--");
            args.AddRange(paths);
        }
        return Run(DevPath, args.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 2)
            .Select(parts => (parts[0].Trim()[0], parts[^1].Trim()))
            .ToList();
    }

    // --- mutations (callers MUST hold the lock via WithLock) ----------------------

    /// <summary>Stages and commits the paths in an environment. No-op when nothing changed.</summary>
    public void Commit(string environment, string message, params string[] paths) {
        var worktree = PathFor(environment);
        var addArgs = new List<string> { "add", "-A" };
        if (paths.Length > 0) {
            addArgs.Add("--");
            addArgs.AddRange(paths);
        }
        Run(worktree, addArgs.ToArray());
        var staged = TryRun(worktree, "diff", "--cached", "--quiet");
        if (staged.Code == 0) {
            return; // nothing to commit
        }
        Run(worktree, "commit", "-m", message);
    }

    /// <summary>Copies a path's dev content into the prod worktree (stages it).</summary>
    public void CheckoutIntoProd(string path) =>
        Run(ProdPath, "checkout", DevBranch, "--", path);

    /// <summary>Removes a path from prod (stages the deletion).</summary>
    public void RemoveFromProd(string path) =>
        Run(ProdPath, "rm", "--quiet", "--", path);

    /// <summary>Commits whatever is staged in prod.</summary>
    public string CommitProd(string message) {
        Run(ProdPath, "commit", "-m", message);
        return HeadSha("prod");
    }

    // --- push ---------------------------------------------------------------------

    /// <summary>Last push outcome, surfaced in /api/health so failures are never silent.</summary>
    public (DateTime? At, bool Ok, string Error) LastPush { get; private set; } = (null, true, null);

    /// <summary>
    /// Pushes both branches when a remote is configured. Best effort by design: a
    /// promotion must never fail because the network did, but the outcome is recorded.
    /// </summary>
    public void TryPush(string remote) {
        if (string.IsNullOrWhiteSpace(remote)) {
            return;
        }
        var result = TryRun(BareRepoPath, "push", remote, $"{DevBranch}:{DevBranch}", $"{ProdBranch}:{ProdBranch}");
        LastPush = (DateTime.UtcNow, result.Code == 0,
            result.Code == 0 ? null : Truncate(result.Stderr.Trim(), 300));
        if (result.Code != 0) {
            _logger.LogWarning("git push to {Remote} failed: {Error}", remote, LastPush.Error);
        }
    }
}

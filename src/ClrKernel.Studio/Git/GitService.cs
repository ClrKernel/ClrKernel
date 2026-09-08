using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Studio;

/// <summary>Thrown when a git command fails; the message carries the command and stderr.</summary>
public sealed class GitException : Exception {
    public GitException(string message) : base(message) { }
}

/// <summary>
/// The git layer behind test→prod promotion: a bare repo at
/// <c>&lt;workspace&gt;/.repo.git</c> with two worktrees — <c>test</c> (branch test,
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
    public const string TestBranch = "test";
    /// <summary>What <see cref="TestBranch"/> was called before 0.10. Migrated on Init.</summary>
    internal const string LegacyTestBranch = "dev";
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
        _authorName = string.IsNullOrWhiteSpace(authorName) ? "clrkernel-studio" : authorName;
        _authorEmail = string.IsNullOrWhiteSpace(authorEmail) ? "studio@clrkernel.local" : authorEmail;
    }

    public string Workspace => _workspace;
    public string BareRepoPath => Path.Combine(_workspace, ".repo.git");
    public string TestPath => Path.Combine(_workspace, TestBranch);
    public string ProdPath => Path.Combine(_workspace, "prod");

    /// <summary>True when the bare repo and both worktrees exist.</summary>
    public bool LayoutExists =>
        Directory.Exists(BareRepoPath) && Directory.Exists(TestPath) && Directory.Exists(ProdPath);

    /// <summary>
    /// The worktree a branch is checked out in. Throws for a branch this workspace
    /// does not have — deliberately, rather than falling back to test: an unknown
    /// branch string used to resolve there, and now that test refuses writes, a
    /// fallback would be a write landing in the one place nobody may write.
    /// </summary>
    /// <summary>
    /// The git ref behind an environment name. They are the same word except for
    /// production, which the app calls <c>prod</c> and git calls <c>main</c> — so
    /// anything handing one of these to git has to translate, and anything handing
    /// it to <see cref="PathFor"/> must not.
    /// </summary>
    public static string RefFor(string branch) => branch == "prod" ? ProdBranch : branch;

    public string PathFor(string branch) => branch switch {
        "prod" => ProdPath,
        TestBranch => TestPath,
        _ when IsUserBranch(branch) => UserPath(UserOf(branch)),
        _ => throw new GitException($"This workspace has no branch '{branch}'."),
    };

    private string LegacyTestPath => Path.Combine(_workspace, LegacyTestBranch);

    // --- personal branches --------------------------------------------------

    /// <summary>
    /// Branches are named for the account's <em>handle</em> — its username — not for
    /// its display name and no longer for its id.
    ///
    /// <para>
    /// The rule that made it an id still holds: what git sees has to be unique and
    /// must not change under a year of commits, and a display name is neither. A
    /// username is both, which is why it exists (see <see cref="UserName"/>). What
    /// it adds is legibility — the branch, the worktree directory and the author of
    /// every commit were all a guid.
    /// </para>
    /// </summary>
    public const string UserBranchPrefix = "user/";

    /// <summary>
    /// Whether this names somebody's own branch.
    ///
    /// <para>
    /// A guid counts as well as a handle: a workspace upgraded from 0.11 still holds
    /// <c>user/&lt;guid&gt;</c> branches until the startup pass renames them, and
    /// this predicate decides whether a branch is personal <em>at all</em>. Saying
    /// no about one does not throw — it routes the request to the wrong root and
    /// answers "test and prod are read-only" about somebody's own notebook.
    /// </para>
    /// </summary>
    public static bool IsUserBranch(string branch) =>
        HandleOf(branch) is { Length: > 0 } handle
        && (UserName.IsValid(handle) || Guid.TryParse(handle, out _));

    public static string BranchForUser(string handle) => UserBranchPrefix + handle;

    /// <summary>The handle a personal branch is named for, or null.</summary>
    public static string HandleOf(string branch) =>
        branch != null && branch.StartsWith(UserBranchPrefix, StringComparison.Ordinal)
            ? branch[UserBranchPrefix.Length..]
            : null;

    private static string UserOf(string branch) => branch[UserBranchPrefix.Length..];

    /// <summary>One worktree per person per project, beside test/ and prod/.</summary>
    public string UserPath(string handle) => Path.Combine(_workspace, "user-" + handle);

    public bool HasUserWorktree(string handle) => Directory.Exists(UserPath(handle));

    /// <summary>
    /// Creates someone's branch and worktree if they have none, cut from test.
    /// <para>
    /// Lazily, on first use rather than at account creation: most people will never
    /// touch most projects, and an empty worktree per person per project is a lot of
    /// disk to keep for that. Idempotent, and it holds the workspace lock — this
    /// mutates the repo, and a promotion running at the same moment must not
    /// interleave with it.
    /// </para>
    /// </summary>
    public string EnsureUserWorktree(string handle) {
        var path = UserPath(handle);
        if (Directory.Exists(path)) {
            return path;
        }
        return WithLock(() => {
            if (Directory.Exists(path)) {
                return path;
            }
            var branch = BranchForUser(handle);
            var exists = TryRun(BareRepoPath, "show-ref", "--verify", "--quiet",
                $"refs/heads/{branch}").Code == 0;
            if (!exists) {
                Run(BareRepoPath, "branch", branch, TestBranch);
            }
            Run(BareRepoPath, "worktree", "add", path, branch);
            _logger.LogInformation("Created worktree {Path} on {Branch}.", path, branch);
            return path;
        });
    }

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
    private string Run(string workdir, params string[] args) =>
        RunAs(workdir, null, null, args);

    private string RunAs(string workdir, string authorName, string authorEmail, params string[] args) {
        var (code, stdout, stderr) = TryRunAs(workdir, authorName, authorEmail, args);
        if (code != 0) {
            throw new GitException(
                $"git {string.Join(' ', args)} failed ({code}): {Truncate(stderr.Trim(), 500)}");
        }
        return stdout;
    }

    private (int Code, string Stdout, string Stderr) TryRun(string workdir, params string[] args) =>
        TryRunAs(workdir, null, null, args);

    /// <param name="authorName">
    /// Who the commit is by. Null keeps the server's own identity, which is right
    /// for promotions and adoptions — those are the tool acting, not a person.
    /// </param>
    /// <remarks>
    /// Named apart from <c>TryRun</c> on purpose. As an overload, a plain
    /// <c>TryRun(dir, "merge-base", "--is-ancestor", a, b)</c> binds the first two
    /// git arguments as the author name and email — it compiles, it runs, and it
    /// runs the wrong command.
    /// </remarks>
    private (int Code, string Stdout, string Stderr) TryRunAs(
        string workdir, string authorName, string authorEmail, params string[] args) {
        var name = string.IsNullOrWhiteSpace(authorName) ? _authorName : authorName;
        var email = string.IsNullOrWhiteSpace(authorEmail) ? _authorEmail : authorEmail;
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
            "-c", $"user.name={name}",
            "-c", $"user.email={email}",
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
        psi.Environment["GIT_AUTHOR_NAME"] = name;
        psi.Environment["GIT_AUTHOR_EMAIL"] = email;
        // The committer stays the server on a personal branch too: the tool made the
        // commit, the person wrote what is in it, and git models that distinction.
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
                "git is not installed or not on PATH — the test/prod workflow needs it. " + e.Message);
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
    /// adopted into test and promoted to main so an existing notebooks folder keeps
    /// working. Idempotent: an intact layout is left alone; a half-formed one gets
    /// instructions rather than guesses.
    /// </summary>
    public string Init() {
        return WithLock(() => {
            if (LayoutExists) {
                Repair();
                return "workspace already initialized";
            }
            if (Directory.Exists(BareRepoPath) || Directory.Exists(TestPath) || Directory.Exists(ProdPath)) {
                throw new GitException(
                    $"The workspace at {_workspace} is half-initialized (some of .repo.git/test/prod " +
                    "exist). Move or remove them, then run `clrkernel-studio git init` again.");
            }

            Directory.CreateDirectory(_workspace);
            Run(_workspace, "init", "--bare", "--initial-branch", ProdBranch, BareRepoPath);

            // The first commit must exist before worktrees can be added, and
            // `worktree add --orphan` needs git ≥2.42 (newer than Debian bookworm).
            // Plumbing works everywhere: empty tree -> empty commit -> main.
            var emptyTree = Run(BareRepoPath, "mktree").Trim();          // no stdin = empty tree
            var initial = Run(BareRepoPath, "commit-tree", emptyTree, "-m", "initial").Trim();
            Run(BareRepoPath, "update-ref", $"refs/heads/{ProdBranch}", initial);
            Run(BareRepoPath, "branch", TestBranch, ProdBranch);
            Run(BareRepoPath, "worktree", "add", TestPath, TestBranch);
            Run(BareRepoPath, "worktree", "add", ProdPath, ProdBranch);

            // Adopt loose files: they move into test, and main fast-forwards so prod
            // starts equal to test (everything existing is implicitly approved).
            var adopted = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(_workspace)) {
                var name = Path.GetFileName(entry);
                if (name is ".repo.git" or TestBranch or "prod" || name.StartsWith('.')
                    || name is NotificationChannels.FileName or "settings.json") {
                    continue;
                }
                var target = Path.Combine(TestPath, name);
                Directory.Move(entry, target); // moves files too
                adopted++;
            }
            if (adopted > 0) {
                Run(TestPath, "add", "-A");
                Run(TestPath, "commit", "-m", "adopt existing notebooks");
                Run(ProdPath, "merge", "--ff-only", TestBranch);
            }
            ExcludeScratch();
            _logger.LogInformation("Initialized git workspace at {Workspace} ({Adopted} adopted).",
                _workspace, adopted);
            return adopted > 0
                ? $"initialized; adopted {adopted} existing item(s) into test and promoted them"
                : "initialized";
        });
    }

    /// <summary>
    /// Renames the pre-0.10 <c>dev</c> worktree and branch to <c>test</c>. Both are
    /// renames in place — no history is rewritten and nothing is copied — so the
    /// only unsafe case is a workspace that already has both names, which is left
    /// alone with a warning rather than merged. Returns true when it changed something.
    /// <para>
    /// A remote's <c>dev</c> branch is deliberately <em>not</em> touched: deleting a
    /// branch on a shared remote is not this process's call to make. The new branch
    /// is pushed alongside it and the stale one is reported.
    /// </para>
    /// </summary>
    public bool MigrateLegacyLayout() {
        return WithLock(() => {
            if (!Directory.Exists(BareRepoPath)) {
                return false;
            }
            var hasLegacyBranch = TryRun(
                BareRepoPath, "show-ref", "--verify", "--quiet", $"refs/heads/{LegacyTestBranch}").Code == 0;
            var hasTestBranch = TryRun(
                BareRepoPath, "show-ref", "--verify", "--quiet", $"refs/heads/{TestBranch}").Code == 0;
            if (Directory.Exists(LegacyTestPath) && Directory.Exists(TestPath)) {
                _logger.LogWarning(
                    "Workspace {Workspace} has both a dev/ and a test/ worktree. Leaving both alone — " +
                    "test/ is the live one; move dev/ aside once you are sure nothing in it is unsaved.",
                    _workspace);
                return false;
            }

            var changed = false;
            if (hasLegacyBranch && !hasTestBranch) {
                // Renames the branch even where the worktree has it checked out: git
                // rewrites that worktree's HEAD as part of the rename.
                Run(BareRepoPath, "branch", "-m", LegacyTestBranch, TestBranch);
                changed = true;
            }
            if (Directory.Exists(LegacyTestPath) && !Directory.Exists(TestPath)) {
                Run(BareRepoPath, "worktree", "move", LegacyTestPath, TestPath);
                changed = true;
            }
            if (changed) {
                _logger.LogInformation(
                    "Renamed the dev branch and worktree to test in {Workspace}.", _workspace);
            }
            return changed;
        });
    }

    /// <summary>
    /// Moves somebody's branch and worktree to a new handle, in place.
    ///
    /// <para>
    /// Modelled on <see cref="MigrateLegacyLayout"/>, and for the same reasons: a
    /// rename rather than a copy, so no history is rewritten and nothing is
    /// duplicated; <c>worktree move</c> rather than a directory move, because the
    /// worktree's gitdir pointer and its entry under
    /// <c>.repo.git/worktrees/&lt;name&gt;</c> have to travel with it; and the
    /// workspace lock, because a promotion running at this moment must not
    /// interleave with it.
    /// </para>
    /// <para>
    /// Refuses when the target already exists rather than merging into it — two
    /// people's work is not this method's to combine. Returns null when it did
    /// something (including when there was nothing to do), and the reason otherwise.
    /// Idempotent: renaming to a handle that is already in place is a no-op.
    /// </para>
    /// <para>
    /// The remote is deliberately untouched, exactly as the dev → test migration
    /// leaves it: personal branches are never pushed by <see cref="TryPush"/>, and
    /// deleting a branch on a shared remote is not this process's call.
    /// </para>
    /// </summary>
    public string RenameUser(string from, string to) {
        if (string.Equals(from, to, StringComparison.Ordinal)) {
            return null;
        }
        if (UserName.Problem(to) is { } invalid) {
            return invalid;
        }
        return WithLock(() => {
            var oldPath = UserPath(from);
            var newPath = UserPath(to);
            var oldBranch = BranchForUser(from);
            var newBranch = BranchForUser(to);

            if (Directory.Exists(newPath) || HasBranch(newBranch)) {
                return $"This project already has a branch or worktree called '{to}'.";
            }
            if (HasBranch(oldBranch)) {
                // Renames the branch even where a worktree has it checked out — git
                // rewrites that worktree's HEAD as part of the rename.
                Run(BareRepoPath, "branch", "-m", oldBranch, newBranch);
            }
            if (Directory.Exists(oldPath)) {
                Run(BareRepoPath, "worktree", "move", oldPath, newPath);
            }
            _logger.LogInformation(
                "Renamed {Old} to {New} in {Workspace}.", oldBranch, newBranch, _workspace);
            return null;
        });
    }

    private bool HasBranch(string branch) =>
        TryRun(BareRepoPath, "show-ref", "--verify", "--quiet", $"refs/heads/{branch}").Code == 0;

    /// <summary>Fixes worktree gitdir pointers after the workspace moved (volumes do).</summary>
    public void Repair() {
        Run(BareRepoPath, "worktree", "repair", TestPath, ProdPath);
        SweepRerunWorktrees();
        ExcludeScratch();
    }

    /// <summary>
    /// Removes checkouts of the past left behind by a process that died mid-run.
    /// <para>
    /// The directories have to go, not just the registrations: <c>git worktree
    /// prune</c> only forgets worktrees whose folder is already gone, so after a
    /// crash the checkout stays on disk and prune does nothing about it. The prune
    /// afterwards is for the other direction — somebody deleted the folder by hand.
    /// </para>
    /// <para>
    /// Safe where it is called from, which is startup: this process cannot have a
    /// rerun in flight before it has begun. It is deliberately not called anywhere
    /// else, because a sweep during normal running would delete the tree a live
    /// rerun is reading.
    /// </para>
    /// </summary>
    private void SweepRerunWorktrees() {
        foreach (var stale in Directory.EnumerateDirectories(_workspace, RerunWorktreePrefix + "*")) {
            RemoveRerunWorktree(stale);
        }
        Run(BareRepoPath, "worktree", "prune");
    }

    /// <summary>The prefix every rerun-at-a-sha checkout lives under.</summary>
    /// <remarks>
    /// Deliberately not <c>user-</c>: <see cref="UserWorktrees"/> scans that prefix
    /// and would report a transient checkout as somebody's branch. It sits beside
    /// the worktrees rather than inside one, so <see cref="JobCatalog"/> — which
    /// scans <c>&lt;root&gt;/test</c> and <c>&lt;root&gt;/prod</c> — never sees the
    /// jobs in it and cannot schedule a job out of a copy of the past.
    /// </remarks>
    public const string RerunWorktreePrefix = "rerun-";

    /// <summary>
    /// Checks the whole tree out at one commit, detached, and returns the path.
    /// <para>
    /// The whole tree, not the one notebook: <c>#!import</c>, <c>connections.json</c>
    /// and every sibling resolve relative to the notebook, so a single file copied
    /// somewhere else is a different program that happens to share a name. This is
    /// what makes "rerun the exact failed version" mean what it says.
    /// </para>
    /// <para>The caller removes it — see <see cref="RemoveRerunWorktree"/>.</para>
    /// </summary>
    public string AddRerunWorktree(string sha) => WithLock(() => {
        var path = Path.Combine(_workspace, RerunWorktreePrefix + Guid.NewGuid().ToString("N")[..12]);
        Run(BareRepoPath, "worktree", "add", "--detach", path, sha);
        return path;
    });

    /// <summary>Removes a checkout from <see cref="AddRerunWorktree"/>. Never throws:
    /// this runs in a finally, and a failed cleanup must not lose the run's result.</summary>
    public void RemoveRerunWorktree(string path) {
        if (string.IsNullOrEmpty(path)) {
            return;
        }
        try {
            WithLock(() => Run(BareRepoPath, "worktree", "remove", "--force", path));
        } catch (Exception e) {
            _logger.LogWarning("Could not remove the rerun worktree {Path}: {Error}", path, e.Message);
        }
    }

    /// <summary>
    /// Where a person's unsaved scratch work lives inside their worktree — the query
    /// editor's buffer, which is a notebook on disk but is not their notebooks.
    /// </summary>
    public const string ScratchDirectory = ".scratch";

    /// <summary>
    /// Teaches the repo to ignore <see cref="ScratchDirectory"/>, once, for every
    /// worktree at the same time.
    /// <para>
    /// Both halves matter and they are different code paths. Without it
    /// <c>status --porcelain</c> reports the scratch file, so <see cref="StandingOf"/>
    /// says Dirty forever — a Push button that never clears — and <see cref="CommitAs"/>
    /// with no pathspec sweeps the file into test on the next push.
    /// </para>
    /// </summary>
    private void ExcludeScratch() => EnsureExcluded(ScratchDirectory + "/");

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
        return TryRun(TestPath, args.ToArray()).Code == 0;
    }

    /// <summary>Unified diff of a path between prod (main) and test.</summary>
    public string UnifiedDiff(string path) =>
        Run(TestPath, "diff", ProdBranch, TestBranch, "--", path);

    /// <summary>name-status lines (A/M/D\tpath) between prod and test for the paths.</summary>
    public IReadOnlyList<(char Status, string Path)> NameStatus(params string[] paths) {
        var args = new List<string> { "diff", "--name-status", ProdBranch, TestBranch };
        if (paths.Length > 0) {
            args.Add("--");
            args.AddRange(paths);
        }
        return Run(TestPath, args.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 2)
            .Select(parts => (parts[0].Trim()[0], parts[^1].Trim()))
            .ToList();
    }

    // --- mutations (callers MUST hold the lock via WithLock) ----------------------

    /// <summary>Stages and commits the paths in a branch. No-op when nothing changed.</summary>
    public void Commit(string branch, string message, params string[] paths) =>
        CommitAs(branch, message, null, null, paths);

    /// <summary>The same, attributed to a person. Returns false when nothing changed.</summary>
    public bool CommitAs(
        string branch, string message, string authorName, string authorEmail, params string[] paths) {
        var worktree = PathFor(branch);
        var addArgs = new List<string> { "add", "-A" };
        if (paths.Length > 0) {
            addArgs.Add("--");
            addArgs.AddRange(paths);
        } else {
            // A save writes beside the file and renames over it. A crash between
            // those two leaves the staging file behind, and `add -A` would then
            // commit it — a stray half-notebook arriving in test on the next push.
            addArgs.Add("--");
            addArgs.Add(".");
            addArgs.Add(":(exclude)**/.*.saving");
        }
        Run(worktree, addArgs.ToArray());
        var staged = TryRun(worktree, "diff", "--cached", "--quiet");
        if (staged.Code == 0) {
            return false; // nothing to commit
        }
        RunAs(worktree, authorName, authorEmail, "commit", "-m", message);
        return true;
    }

    /// <summary>
    /// Commits one path in a branch, and only that path. Returns false when it did
    /// not change.
    /// <para>
    /// A pathspec-limited commit rather than <see cref="CommitAs"/>, because this is
    /// used on the prod worktree, where a promotion may already have staged other
    /// files. <c>git commit -- path</c> commits that path whatever else is in the
    /// index; a bare <c>git commit</c> would sweep a half-finished promotion in with
    /// it.
    /// </para>
    /// </summary>
    public bool CommitPath(string branch, string relativePath, string message) {
        var worktree = PathFor(branch);
        // A path that is neither on disk nor in the index is not "nothing to commit",
        // it is a pathspec error — `git add` exits 128 with "did not match any files"
        // and takes the caller down with it. That is the state a server with no shared
        // connections at all is in.
        var tracked = TryRun(worktree, "ls-files", "--error-unmatch", "--", relativePath).Code == 0;
        if (!tracked && !File.Exists(System.IO.Path.Combine(worktree, relativePath))) {
            return false;
        }
        // -f, because the same path is deliberately in info/exclude: a personal
        // branch that does not track it gets an ignored copy so its owner keeps the
        // shared list, and git would otherwise refuse to stage the real one here.
        // Forcing says what is meant — this path belongs in this branch.
        Run(worktree, "add", "-f", "--", relativePath);
        if (TryRun(worktree, "diff", "--cached", "--quiet", "--", relativePath).Code == 0) {
            return false;
        }
        Run(worktree, "commit", "-m", message, "--", relativePath);
        return true;
    }

    /// <summary>
    /// Makes sure a pattern is in the repo's <c>info/exclude</c>, so a generated file
    /// never shows up as untracked work.
    /// <para>
    /// <c>info/exclude</c> rather than a committed <c>.gitignore</c>: it is not
    /// versioned, so writing it commits nothing and puts nobody's branch behind, and
    /// git reads it from the <em>common</em> directory, which every linked worktree
    /// shares. The path is asked for rather than assumed — it is the bare repo in
    /// this layout, and that is a coincidence worth not depending on.
    /// </para>
    /// </summary>
    /// <summary>Whether a path is tracked on the branch checked out in a worktree.</summary>
    public bool Tracks(string branch, string relativePath) =>
        TryRun(PathFor(branch), "ls-files", "--error-unmatch", "--", relativePath).Code == 0;

    public void EnsureExcluded(string pattern) {
        var common = TryRun(TestPath, "rev-parse", "--git-common-dir").Stdout.Trim();
        if (common.Length == 0) {
            return;
        }
        var directory = System.IO.Path.IsPathRooted(common)
            ? common
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(TestPath, common));
        var file = System.IO.Path.Combine(directory, "info", "exclude");
        var lines = File.Exists(file) ? File.ReadAllLines(file) : Array.Empty<string>();
        if (lines.Any(line => line.Trim() == pattern)) {
            return;
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
        File.AppendAllText(file, (lines.Length > 0 ? "\n" : string.Empty) + pattern + "\n");
    }

    /// <summary>Copies a path's test content into the prod worktree (stages it).</summary>
    public void CheckoutIntoProd(string path) =>
        Run(ProdPath, "checkout", TestBranch, "--", path);

    /// <summary>Removes a path from prod (stages the deletion).</summary>
    public void RemoveFromProd(string path) =>
        Run(ProdPath, "rm", "--quiet", "--", path);

    /// <summary>Commits whatever is staged in prod.</summary>
    public string CommitProd(string message) {
        Run(ProdPath, "commit", "-m", message);
        return HeadSha("prod");
    }

    /// <summary>
    /// One personal worktree, as an admin deciding whether to prune sees it.
    /// <para>
    /// <see cref="Handle"/> is what the directory is named. <see cref="UserId"/> is
    /// who that is, and is null when nothing answers to the handle — an orphan left
    /// by a deleted account, which is precisely what a prune screen exists to show.
    /// </para>
    /// </summary>
    public sealed record UserWorktree(
        string Handle, Guid? UserId, string Path, DateTime LastCommit, bool Dirty, bool Merged);

    /// <summary>
    /// Every personal worktree in this workspace.
    ///
    /// <para>
    /// The directory is named for a handle, and a handle is not an account id, so
    /// something has to map one to the other. That something is passed in: this
    /// class knows about git and a workspace, and handing it an auth store to hold
    /// would be a dependency pointing the wrong way.
    /// </para>
    /// </summary>
    public IReadOnlyList<UserWorktree> UserWorktrees(Func<string, Guid?> resolve = null) {
        if (!Directory.Exists(_workspace)) {
            return Array.Empty<UserWorktree>();
        }
        var found = new List<UserWorktree>();
        foreach (var directory in Directory.EnumerateDirectories(_workspace, "user-*")) {
            var handle = System.IO.Path.GetFileName(directory)["user-".Length..];
            if (handle.Length == 0) {
                continue;
            }
            found.Add(Describe(handle, resolve?.Invoke(handle), directory));
        }
        return found.OrderBy(w => w.LastCommit).ToList();
    }

    private UserWorktree Describe(string handle, Guid? user, string directory) {
        var branch = BranchForUser(handle);
        var stamp = TryRun(directory, "log", "-1", "--format=%ct", branch);
        var seconds = long.TryParse(stamp.Stdout.Trim(), out var value) ? value : 0;
        return new UserWorktree(
            handle,
            user,
            directory,
            DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime,
            Dirty: TryRun(directory, "status", "--porcelain").Stdout.Trim().Length > 0,
            // Everything on it is already in test, so removing it loses nothing.
            Merged: TryRun(directory, "merge-base", "--is-ancestor", branch, TestBranch).Code == 0);
    }

    /// <summary>
    /// Removes a personal worktree and its branch.
    /// <para>
    /// Refuses while there is uncommitted work or a commit test has not seen, unless
    /// <paramref name="force"/> — that is somebody's unfinished work, and the person
    /// deleting it is by definition not the person who wrote it. Returns the reason
    /// when it declines, and null when it removed one.
    /// </para>
    /// </summary>
    public string RemoveUserWorktree(string handle, bool force) {
        return WithLock(() => {
            var path = UserPath(handle);
            if (!Directory.Exists(path)) {
                return "There is no worktree for that account here.";
            }
            var state = Describe(handle, null, path);
            if (!force && (state.Dirty || !state.Merged)) {
                return state.Dirty
                    ? "That branch has work that was never saved to test."
                    : "That branch has commits test has never seen.";
            }
            Run(BareRepoPath, "worktree", "remove", "--force", path);
            Run(BareRepoPath, "branch", "-D", BranchForUser(handle));
            _logger.LogInformation("Removed worktree {Path}.", path);
            return null;
        });
    }

    /// <summary>
    /// Removes personal worktrees nobody has touched for a while — but only the ones
    /// that are clean <em>and</em> fully in test, so what goes is always a copy of
    /// something that already exists elsewhere. An idle branch with unpushed work
    /// stays until a person decides about it.
    /// </summary>
    public IReadOnlyList<string> PruneIdleUserWorktrees(TimeSpan idle, DateTime now) {
        var pruned = new List<string>();
        foreach (var worktree in UserWorktrees()) {
            if (worktree.Dirty || !worktree.Merged || now - worktree.LastCommit < idle) {
                continue;
            }
            if (RemoveUserWorktree(worktree.Handle, force: false) == null) {
                pruned.Add(worktree.Handle);
            }
        }
        return pruned;
    }

    // --- personal branch → test -----------------------------------------------

    /// <summary>Where one person's branch stands relative to test.</summary>
    /// <param name="BehindFiles">
    /// The files test has changed since the two branches parted — the per-file
    /// half of <paramref name="Behind"/>. A file only you have never appears in
    /// it, which is the point: "your branch is behind" is true of the branch and
    /// says nothing about the file somebody happens to have open.
    /// </param>
    public sealed record BranchStanding(
        bool Dirty, int Ahead, int Behind, IReadOnlyList<string> Conflicts,
        IReadOnlyList<string> BehindFiles);

    /// <summary>
    /// Uncommitted work, and how far the branch has moved either way. <c>Behind</c>
    /// is what blocks a push: test having moved on means the merge has to happen in
    /// the person's own worktree, where they can see it, rather than in test.
    /// </summary>
    public BranchStanding StandingOf(string handle) {
        var worktree = UserPath(handle);
        if (!Directory.Exists(worktree)) {
            return new BranchStanding(false, 0, 0, Array.Empty<string>(), Array.Empty<string>());
        }
        var counts = Run(worktree, "rev-list", "--left-right", "--count",
            $"{BranchForUser(handle)}...{TestBranch}").Trim().Split('\t', ' ');
        var behindCount = counts.Length > 1 && int.TryParse(counts[^1], out var b) ? b : 0;
        return new BranchStanding(
            Dirty: Run(worktree, "status", "--porcelain").Trim().Length > 0,
            Ahead: counts.Length > 0 && int.TryParse(counts[0], out var ahead) ? ahead : 0,
            Behind: behindCount,
            Conflicts: ConflictsIn(worktree),
            // Three dots: what test changed since the merge base, not what the two
            // branches differ by. Two dots would also list every file *you* changed
            // and call it something test had moved on.
            BehindFiles: behindCount == 0
                ? Array.Empty<string>()
                : Run(worktree, "diff", "--name-only", $"{BranchForUser(handle)}...{TestBranch}")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .Where(f => f.Length > 0)
                    .ToList());
    }

    private IReadOnlyList<string> ConflictsIn(string worktree) =>
        Run(worktree, "diff", "--name-only", "--diff-filter=U")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToList();

    /// <summary>What a push did, or why it did not.</summary>
    public sealed record PushResult(bool Pushed, string Sha, string Error, bool NeedsUpdate);

    /// <summary>
    /// Commits everything in the person's worktree and fast-forwards test onto it.
    /// <para>
    /// Refuses outright when test has moved since the branch diverged, rather than
    /// merging into test on their behalf: the merge belongs in their own worktree
    /// where they can look at it, and a conflict resolved by a server is a conflict
    /// resolved by nobody. <c>Update from test</c> is the way forward from there.
    /// </para>
    /// </summary>
    public PushResult PushToTest(string handle, string message, string authorName, string authorEmail) {
        return WithLock(() => {
            var branch = BranchForUser(handle);
            var worktree = UserPath(handle);
            if (!Directory.Exists(worktree)) {
                return new PushResult(false, null, "You have nothing to push here yet.", false);
            }
            if (ConflictsIn(worktree).Count > 0) {
                return new PushResult(false, null,
                    "Resolve the conflicted files first, then push.", false);
            }

            // Everything in the worktree becomes one commit with the message they
            // typed. This is the point where saved work turns into history.
            CommitAs(branch, message, authorName, authorEmail);

            var behind = TryRun(worktree, "merge-base", "--is-ancestor", TestBranch, branch);
            if (behind.Code != 0) {
                return new PushResult(false, null,
                    "test has moved on since you branched. Update from test first, " +
                    "then push.", true);
            }
            var ff = TryRun(TestPath, "merge", "--ff-only", branch);
            if (ff.Code != 0) {
                return new PushResult(false, null, Truncate(ff.Stderr.Trim(), 300), true);
            }
            return new PushResult(true, HeadSha(TestBranch), null, false);
        });
    }

    /// <summary>
    /// Merges test into the person's branch, inside their own worktree. Returns the
    /// conflicted files — never a resolution: taking one side automatically is how a
    /// merge silently loses work.
    /// </summary>
    public IReadOnlyList<string> UpdateFromTest(string handle, string authorName, string authorEmail) {
        return WithLock(() => {
            var worktree = UserPath(handle);
            if (!Directory.Exists(worktree)) {
                return Array.Empty<string>();
            }
            // Uncommitted work first: a merge refuses to start over a dirty tree, and
            // stashing it would hide it exactly when it matters.
            CommitAs(BranchForUser(handle), "work in progress before updating from test",
                authorName, authorEmail);
            var merge = TryRunAs(worktree, authorName, authorEmail, "merge", "--no-edit", TestBranch);
            return merge.Code == 0 ? Array.Empty<string>() : ConflictsIn(worktree);
        });
    }


    // --- history -------------------------------------------------------------

    /// <summary>One commit, and the files it touched when that was asked for.</summary>
    /// <param name="Parents">
    /// Short shas, in git's order — first parent first. The graph needs them: a
    /// commit with two is a merge, and which lane a line comes from is the only
    /// thing that says the branches ever met.
    /// </param>
    public sealed record CommitEntry(
        string Sha, string ShortSha, string Author, DateTime When, string Subject,
        IReadOnlyList<string> Parents, IReadOnlyList<CommitFile> Files);

    /// <summary>A path and what happened to it. <c>Status</c> is git's letter: A, M, D, R.</summary>
    public sealed record CommitFile(string Status, string Path);

    /// <summary>
    /// The fields, NUL-separated, one commit per record.
    ///
    /// <para>
    ///  starts a record and NUL separates the fields inside it, because a
    /// commit subject can contain anything a person can type — including newlines
    /// and pipes, which is what every simpler delimiter here would have been.
    /// </para>
    /// </summary>
    private const string _commitFormat = "%x01%H%x00%h%x00%an%x00%aI%x00%s%x00%P";

    /// <summary>
    /// Commits on a branch, newest first. <paramref name="withFiles"/> asks git for
    /// each one's name-status too, which is what a merge preview needs and a plain
    /// history list does not.
    /// </summary>
    public IReadOnlyList<CommitEntry> History(
        string branch, int limit = 50, bool withFiles = false, string path = null) {
        var args = new List<string> { $"--max-count={Math.Clamp(limit, 1, 500)}", branch };
        if (!string.IsNullOrEmpty(path)) {
            // `--` so a path that looks like a ref is still a path. A file called
            // `test` is not the test branch, and git would otherwise guess.
            args.Add("--");
            args.Add(path);
        }
        return CommitsFrom(BareRepoPath, withFiles, args.ToArray());
    }

    /// <summary>
    /// A file as one commit left it, or null when that commit does not have it —
    /// which is the honest answer for the commit that added it (nothing before)
    /// and the one that deleted it (nothing after).
    /// </summary>
    public string FileAt(string reference, string path) {
        var result = TryRun(BareRepoPath, "show", $"{reference}:{path}");
        return result.Code == 0 ? result.Stdout : null;
    }

    /// <summary>
    /// What one commit did to one file: the text on either side of it.
    ///
    /// <para>
    /// The parent is <c>sha^</c>, which is the first parent for a merge — the same
    /// side <see cref="CommitsFrom"/> lists a merge's files against, so the diff
    /// and the file list agree about what a merge changed. A root commit has no
    /// parent and <c>Before</c> is null there rather than empty: "this file did
    /// not exist" and "this file was empty" are different, and only one of them is
    /// true.
    /// </para>
    /// </summary>
    public (string Before, string After) FileChange(string sha, string path) =>
        (FileAt($"{sha}^", path), FileAt(sha, path));

    /// <summary>
    /// What merging test would bring: the commits on test that this branch has not
    /// got, newest first, each with the files it touches.
    /// </summary>
    public IReadOnlyList<CommitEntry> IncomingFromTest(string handle, int limit = 50) =>
        CommitsFrom(BareRepoPath, true, $"--max-count={Math.Clamp(limit, 1, 500)}",
            $"{BranchForUser(handle)}..{TestBranch}");

    /// <summary>
    /// And the other lane: this branch's own commits since it parted from test.
    /// Two dots the other way round — what test has not got.
    /// </summary>
    public IReadOnlyList<CommitEntry> OutgoingToTest(string handle, int limit = 50) =>
        CommitsFrom(BareRepoPath, false, $"--max-count={Math.Clamp(limit, 1, 500)}",
            $"{TestBranch}..{BranchForUser(handle)}");

    /// <summary>Where the two branches parted, or null when they share nothing.</summary>
    public string MergeBaseWithTest(string handle) {
        var result = TryRun(BareRepoPath, "merge-base", BranchForUser(handle), TestBranch);
        return result.Code == 0 && result.Stdout.Trim() is { Length: > 0 } sha ? sha : null;
    }

    /// <summary>Files written but not committed, as git reports them.</summary>
    public IReadOnlyList<CommitFile> Uncommitted(string handle) {
        var worktree = UserPath(handle);
        if (!Directory.Exists(worktree)) {
            return Array.Empty<CommitFile>();
        }
        return Run(worktree, "status", "--porcelain")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 3)
            // "XY path", where X is the index and Y the worktree. `??` is untracked.
            .Select(line => new CommitFile(line[..2].Trim() is { Length: > 0 } st ? st : "?",
                line[3..].Trim().Trim('"')))
            .ToList();
    }

    private IReadOnlyList<CommitEntry> CommitsFrom(string workdir, bool withFiles, params string[] range) {
        var args = new List<string> { "log", "--format=" + _commitFormat };
        if (withFiles) {
            args.Add("--name-status");
            // A merge's diff against its first parent, rather than the empty listing
            // git gives a merge by default — otherwise every merge in the range looks
            // like it touched nothing.
            args.Add("-m");
            args.Add("--first-parent");
        }
        args.AddRange(range);
        var result = TryRun(workdir, args.ToArray());
        if (result.Code != 0) {
            // An unborn branch, or a range naming something that is not there yet.
            // Nothing to show is a real answer here, and a throw would take the page
            // with it.
            return Array.Empty<CommitEntry>();
        }
        return result.Stdout
            .Split('\u0001', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseCommit)
            .Where(c => c != null)
            .ToList();
    }

    private static CommitEntry ParseCommit(string record) {
        var head = record.Split('\n', 2);
        var fields = head[0].Split('\0');
        if (fields.Length < 6) {
            return null;
        }
        var files = head.Length < 2
            ? new List<CommitFile>()
            : head[1]
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2)
                // A rename is "R100 old new": the last field is where it ended up,
                // which is the one somebody is looking for in the list.
                .Select(parts => new CommitFile(parts[0].Trim(), parts[^1].Trim()))
                .ToList();
        return new CommitEntry(
            Sha: fields[0].Trim(),
            ShortSha: fields[1].Trim(),
            Author: fields[2].Trim(),
            When: DateTime.TryParse(fields[3].Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
                ? when
                : default,
            Subject: fields[4].Trim(),
            Parents: fields[5].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries),
            Files: files);
    }


    /// <summary>One row of a folder listing, with the commit that last touched it.</summary>
    public sealed record Entry(
        string Name, string Path, bool IsDirectory, long Size, DateTime Modified,
        CommitEntry LastCommit);

    /// <summary>
    /// One folder of a branch's worktree, with the commit that last touched each
    /// entry — the shape Azure DevOps shows, and the reason it is worth having is
    /// the column nobody can get from the filesystem: <em>why</em> a file changed.
    ///
    /// <para>
    /// One <c>git log</c> walk for the whole folder rather than one per entry: a
    /// folder of forty files would otherwise be forty processes, and the walk stops
    /// as soon as every entry has been attributed.
    /// </para>
    /// </summary>
    public IReadOnlyList<Entry> Contents(string branch, string relative) {
        var root = PathFor(branch);
        var reference = RefFor(branch);
        var folder = string.IsNullOrEmpty(relative) ? root : System.IO.Path.Combine(root, relative);
        if (!Directory.Exists(folder)) {
            return Array.Empty<Entry>();
        }
        var prefix = string.IsNullOrEmpty(relative) ? "" : relative.Replace('\\', '/').TrimEnd('/') + "/";
        var rows = new List<Entry>();
        foreach (var directory in Directory.EnumerateDirectories(folder)) {
            var name = System.IO.Path.GetFileName(directory);
            // The git admin directory is not content, and neither is anything a
            // person put a dot in front of.
            if (name.StartsWith('.')) {
                continue;
            }
            rows.Add(new Entry(name, prefix + name, true, 0, Directory.GetLastWriteTimeUtc(directory), null));
        }
        foreach (var file in Directory.EnumerateFiles(folder)) {
            var info = new FileInfo(file);
            if (info.Name.StartsWith('.')) {
                continue;
            }
            rows.Add(new Entry(info.Name, prefix + info.Name, false, info.Length, info.LastWriteTimeUtc, null));
        }

        var attributed = LastCommitsUnder(reference, relative, rows.Select(r => r.Path).ToList());
        return rows
            .Select(r => attributed.TryGetValue(r.Path, out var commit) ? r with { LastCommit = commit } : r)
            .OrderByDescending(r => r.IsDirectory)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The newest commit touching each of <paramref name="paths"/>, walking the
    /// branch once. A directory is attributed by anything underneath it.
    /// </summary>
    private Dictionary<string, CommitEntry> LastCommitsUnder(
        string branch, string relative, IReadOnlyList<string> paths) {
        var found = new Dictionary<string, CommitEntry>(StringComparer.Ordinal);
        if (paths.Count == 0) {
            return found;
        }
        var args = new List<string> { "log", "--format=" + _commitFormat, "--name-status", "-m", "--first-parent", branch };
        if (!string.IsNullOrEmpty(relative)) {
            args.Add("--");
            args.Add(relative);
        }
        var result = TryRun(BareRepoPath, args.ToArray());
        if (result.Code != 0) {
            return found;
        }
        foreach (var commit in result.Stdout.Split('\u0001', StringSplitOptions.RemoveEmptyEntries)
                     .Select(ParseCommit).Where(c => c != null)) {
            foreach (var touched in commit.Files) {
                foreach (var path in paths) {
                    if (found.ContainsKey(path)) {
                        continue;
                    }
                    // A folder is whatever is under it; a file is itself.
                    if (touched.Path == path || touched.Path.StartsWith(path + "/", StringComparison.Ordinal)) {
                        found[path] = commit with { Files = Array.Empty<CommitFile>() };
                    }
                }
            }
            if (found.Count == paths.Count) {
                break;
            }
        }
        return found;
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
        var result = TryRun(BareRepoPath, "push", remote, $"{TestBranch}:{TestBranch}", $"{ProdBranch}:{ProdBranch}");
        LastPush = (DateTime.UtcNow, result.Code == 0,
            result.Code == 0 ? null : Truncate(result.Stderr.Trim(), 300));
        if (result.Code != 0) {
            _logger.LogWarning("git push to {Remote} failed: {Error}", remote, LastPush.Error);
        }
    }
}

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
        _authorName = string.IsNullOrWhiteSpace(authorName) ? "clrkernel-jobs" : authorName;
        _authorEmail = string.IsNullOrWhiteSpace(authorEmail) ? "jobs@clrkernel.local" : authorEmail;
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
    public string PathFor(string branch) => branch switch {
        "prod" => ProdPath,
        TestBranch => TestPath,
        _ when IsUserBranch(branch) => UserPath(UserOf(branch)),
        _ => throw new GitException($"This workspace has no branch '{branch}'."),
    };

    private string LegacyTestPath => Path.Combine(_workspace, LegacyTestBranch);

    // --- personal branches --------------------------------------------------

    /// <summary>
    /// Branches are named for the account id, not for anything a person types. A
    /// display name can change and is not unique; the branch a year of commits sits
    /// on cannot. Every screen shows the display name — this is what git sees.
    /// </summary>
    public const string UserBranchPrefix = "user/";

    public static bool IsUserBranch(string branch) =>
        branch != null && branch.StartsWith(UserBranchPrefix, StringComparison.Ordinal)
        && Guid.TryParse(branch[UserBranchPrefix.Length..], out _);

    public static string BranchForUser(Guid userId) => UserBranchPrefix + userId.ToString("D");

    private static string UserOf(string branch) => branch[UserBranchPrefix.Length..];

    /// <summary>One worktree per person per project, beside test/ and prod/.</summary>
    public string UserPath(string userId) => Path.Combine(_workspace, "user-" + userId);

    public bool HasUserWorktree(Guid userId) => Directory.Exists(UserPath(userId.ToString("D")));

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
    public string EnsureUserWorktree(Guid userId) {
        var name = userId.ToString("D");
        var path = UserPath(name);
        if (Directory.Exists(path)) {
            return path;
        }
        return WithLock(() => {
            if (Directory.Exists(path)) {
                return path;
            }
            var branch = BranchForUser(userId);
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

    /// <summary>Fixes worktree gitdir pointers after the workspace moved (volumes do).</summary>
    public void Repair() {
        Run(BareRepoPath, "worktree", "repair", TestPath, ProdPath);
        ExcludeScratch();
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

    /// <summary>One personal worktree, as an admin deciding whether to prune sees it.</summary>
    public sealed record UserWorktree(
        Guid UserId, string Path, DateTime LastCommit, bool Dirty, bool Merged);

    /// <summary>Every personal worktree in this workspace.</summary>
    public IReadOnlyList<UserWorktree> UserWorktrees() {
        if (!Directory.Exists(_workspace)) {
            return Array.Empty<UserWorktree>();
        }
        var found = new List<UserWorktree>();
        foreach (var directory in Directory.EnumerateDirectories(_workspace, "user-*")) {
            if (!Guid.TryParse(System.IO.Path.GetFileName(directory)["user-".Length..], out var user)) {
                continue;
            }
            found.Add(Describe(user, directory));
        }
        return found.OrderBy(w => w.LastCommit).ToList();
    }

    private UserWorktree Describe(Guid user, string directory) {
        var branch = BranchForUser(user);
        var stamp = TryRun(directory, "log", "-1", "--format=%ct", branch);
        var seconds = long.TryParse(stamp.Stdout.Trim(), out var value) ? value : 0;
        return new UserWorktree(
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
    public string RemoveUserWorktree(Guid userId, bool force) {
        return WithLock(() => {
            var path = UserPath(userId.ToString("D"));
            if (!Directory.Exists(path)) {
                return "There is no worktree for that account here.";
            }
            var state = Describe(userId, path);
            if (!force && (state.Dirty || !state.Merged)) {
                return state.Dirty
                    ? "That branch has work that was never saved to test."
                    : "That branch has commits test has never seen.";
            }
            Run(BareRepoPath, "worktree", "remove", "--force", path);
            Run(BareRepoPath, "branch", "-D", BranchForUser(userId));
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
    public IReadOnlyList<Guid> PruneIdleUserWorktrees(TimeSpan idle, DateTime now) {
        var pruned = new List<Guid>();
        foreach (var worktree in UserWorktrees()) {
            if (worktree.Dirty || !worktree.Merged || now - worktree.LastCommit < idle) {
                continue;
            }
            if (RemoveUserWorktree(worktree.UserId, force: false) == null) {
                pruned.Add(worktree.UserId);
            }
        }
        return pruned;
    }

    // --- personal branch → test -----------------------------------------------

    /// <summary>Where one person's branch stands relative to test.</summary>
    public sealed record BranchStanding(
        bool Dirty, int Ahead, int Behind, IReadOnlyList<string> Conflicts);

    /// <summary>
    /// Uncommitted work, and how far the branch has moved either way. <c>Behind</c>
    /// is what blocks a push: test having moved on means the merge has to happen in
    /// the person's own worktree, where they can see it, rather than in test.
    /// </summary>
    public BranchStanding StandingOf(Guid userId) {
        var worktree = UserPath(userId.ToString("D"));
        if (!Directory.Exists(worktree)) {
            return new BranchStanding(false, 0, 0, Array.Empty<string>());
        }
        var counts = Run(worktree, "rev-list", "--left-right", "--count",
            $"{BranchForUser(userId)}...{TestBranch}").Trim().Split('\t', ' ');
        return new BranchStanding(
            Dirty: Run(worktree, "status", "--porcelain").Trim().Length > 0,
            Ahead: counts.Length > 0 && int.TryParse(counts[0], out var ahead) ? ahead : 0,
            Behind: counts.Length > 1 && int.TryParse(counts[^1], out var behind) ? behind : 0,
            Conflicts: ConflictsIn(worktree));
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
    public PushResult PushToTest(Guid userId, string message, string authorName, string authorEmail) {
        return WithLock(() => {
            var branch = BranchForUser(userId);
            var worktree = UserPath(userId.ToString("D"));
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
    public IReadOnlyList<string> UpdateFromTest(Guid userId, string authorName, string authorEmail) {
        return WithLock(() => {
            var worktree = UserPath(userId.ToString("D"));
            if (!Directory.Exists(worktree)) {
                return Array.Empty<string>();
            }
            // Uncommitted work first: a merge refuses to start over a dirty tree, and
            // stashing it would hide it exactly when it matters.
            CommitAs(BranchForUser(userId), "work in progress before updating from test",
                authorName, authorEmail);
            var merge = TryRunAs(worktree, authorName, authorEmail, "merge", "--no-edit", TestBranch);
            return merge.Code == 0 ? Array.Empty<string>() : ConflictsIn(worktree);
        });
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

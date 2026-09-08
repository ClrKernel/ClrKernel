using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Studio.UnitTest;

/// <summary>
/// GitService against real repos in temp directories — the promotion workflow
/// stands on these primitives, so they run the actual git binary, not a fake.
/// </summary>
[TestClass]
public class GitServiceTest {
    private string _dir;
    private GitService _git;

    [TestInitialize]
    public void Setup() {
        _dir = Path.Combine(Path.GetTempPath(), "clrkernel-git-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _git = new GitService(_dir, NullLogger.Instance);
    }

    [TestCleanup]
    public void Cleanup() => TempDirectory.Delete(_dir);

    private void WriteTest(string relative, string content) {
        var path = Path.Combine(_git.TestPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
    }

    [TestMethod]
    public void Init_creates_the_layout_and_is_idempotent() {
        var first = _git.Init();
        StringAssert.Contains(first, "initialized");
        Assert.IsTrue(_git.LayoutExists);
        Assert.IsTrue(Directory.Exists(_git.TestPath));
        Assert.IsTrue(Directory.Exists(_git.ProdPath));
        Assert.AreEqual(_git.HeadSha("test"), _git.HeadSha("prod"), "both start at the initial commit");

        Assert.AreEqual("workspace already initialized", _git.Init());
    }

    [TestMethod]
    public void Init_adopts_existing_files_into_dev_and_promotes_them() {
        File.WriteAllText(Path.Combine(_dir, "etl.nb.md"), "```csharp\n1\n```\n");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "x.jobs.yaml"), "notebook: ./x.nb.md\njobs: [{name: x}]");
        // Runtime config stays at the workspace root, unversioned.
        File.WriteAllText(Path.Combine(_dir, NotificationChannels.FileName), "channels: []");

        var message = _git.Init();
        StringAssert.Contains(message, "adopted 2");

        Assert.IsTrue(File.Exists(Path.Combine(_git.TestPath, "etl.nb.md")), "moved into test");
        Assert.IsTrue(File.Exists(Path.Combine(_git.ProdPath, "etl.nb.md")), "and promoted to prod");
        Assert.IsTrue(File.Exists(Path.Combine(_git.ProdPath, "sub", "x.jobs.yaml")));
        Assert.IsTrue(File.Exists(Path.Combine(_dir, NotificationChannels.FileName)),
            "notifications.yaml stays at the workspace root");
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "etl.nb.md")), "no stray copy left behind");
    }

    // Handles, not ids: a branch and a worktree are named for the username now.
    private const string _ada = "ada";
    private const string _grace = "grace";

    private void WriteUser(string user, string relative, string content) {
        var path = Path.Combine(_git.UserPath(user), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
    }

    [TestMethod]
    public void A_personal_worktree_is_created_once_and_cut_from_test() {
        _git.Init();
        WriteTest("etl.nb.md", "v1\n");
        _git.WithLock(() => _git.Commit("test", "v1"));

        Assert.IsFalse(_git.HasUserWorktree(_ada));
        var path = _git.EnsureUserWorktree(_ada);

        Assert.IsTrue(_git.HasUserWorktree(_ada));
        Assert.AreEqual("v1\n", File.ReadAllText(Path.Combine(path, "etl.nb.md")),
            "it starts as a copy of test");
        Assert.AreEqual(_git.HeadSha("test"), _git.HeadSha(GitService.BranchForUser(_ada)));
        Assert.AreEqual(path, _git.EnsureUserWorktree(_ada), "idempotent");
    }

    /// <summary>
    /// A rename moves the branch, the directory, and the commits on it — and the
    /// worktree keeps working afterwards, which is what `worktree move` buys over
    /// moving the folder.
    /// </summary>
    [TestMethod]
    public void Renaming_moves_the_branch_and_the_worktree_with_its_work() {
        _git.Init();
        _git.EnsureUserWorktree(_ada);
        WriteUser(_ada, "etl.nb.md", "mine\n");
        Assert.IsTrue(_git.PushToTest(_ada, "add etl", "Ada", "a@users.local").Pushed);
        WriteUser(_ada, "wip.nb.md", "not pushed\n");

        Assert.IsNull(_git.RenameUser(_ada, "ada-lovelace"));

        Assert.IsFalse(Directory.Exists(_git.UserPath(_ada)), "the old directory is gone");
        var moved = _git.UserPath("ada-lovelace");
        Assert.IsTrue(Directory.Exists(moved));
        Assert.AreEqual("not pushed\n", File.ReadAllText(Path.Combine(moved, "wip.nb.md")),
            "unsaved work travels with it");

        // Registered at its new address, which is what `worktree move` buys over
        // moving the folder. Moving it leaves the repo's own record pointing at the
        // old path: commands run inside the worktree still work, because its .git
        // file names the admin directory and that has not moved — but `git worktree
        // list` reports where it used to be, and the next `worktree prune` sees a
        // registration whose path is gone and unregisters it.
        // Compared by directory name, which is the part the rename changes, and the
        // only part that reads the same on every platform. Two things defeat the
        // obvious spellings of this assertion:
        //
        //  - git prints these paths with forward slashes on Windows too, where
        //    Path.Combine gives backslashes. A substring test against a .NET path
        //    therefore failed there — and its negative twin passed for the wrong
        //    reason, unable to find the old path either.
        //  - on macOS git resolves /var to /private/var, so a full-path equality
        //    test fails here instead. (A substring test survived that one by luck:
        //    "/private/var/x" does contain "/var/x".)
        //
        // Whole segments rather than a substring regardless, because `user-ada` is
        // a prefix of `user-ada-lovelace` — a substring test finds the old name
        // inside the new one and calls a successful rename a failure.
        var registered = _git.RunForTests("worktree", "list")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].TrimEnd('/'))
            .Select(path => path[(path.LastIndexOf('/') + 1)..])
            .ToList();
        CollectionAssert.Contains(registered, "user-ada-lovelace",
            "registered at its new address; git listed " + string.Join(", ", registered));
        CollectionAssert.DoesNotContain(registered, "user-" + _ada,
            "and the old path is not still registered");

        var standing = _git.StandingOf("ada-lovelace");
        Assert.IsTrue(standing.Dirty, "the unsaved file is still seen as unsaved");
        Assert.IsTrue(_git.PushToTest("ada-lovelace", "wip", "Ada", "a@users.local").Pushed,
            "and it can still push");
    }

    [TestMethod]
    public void Renaming_is_idempotent_and_refuses_to_land_on_somebody_else() {
        _git.Init();
        _git.EnsureUserWorktree(_ada);
        _git.EnsureUserWorktree(_grace);

        Assert.IsNull(_git.RenameUser(_ada, _ada), "renaming to the same handle does nothing");
        Assert.IsTrue(Directory.Exists(_git.UserPath(_ada)));

        var refusal = _git.RenameUser(_ada, _grace);
        Assert.IsNotNull(refusal, "two people's work is not this method's to merge");
        StringAssert.Contains(refusal, _grace);
        Assert.IsTrue(Directory.Exists(_git.UserPath(_ada)), "and it left the original alone");

        StringAssert.Contains(_git.RenameUser(_ada, "Not A Handle"), "username");
    }

    /// <summary>
    /// The upgrade: a workspace whose personal branches are still named for account
    /// ids, with work on them, becomes one named for handles — and the work is still
    /// there afterwards. This is the pass a real 0.11 server runs once, on start.
    /// </summary>
    [TestMethod]
    public void A_workspace_named_for_account_ids_is_renamed_to_handles() {
        _git.Init();
        var id = Guid.NewGuid();
        var old = id.ToString("D");
        _git.EnsureUserWorktree(old);
        WriteUser(old, "wip.nb.md", "unsaved\n");

        Assert.IsNull(_git.RenameUser(old, "jeremy"));

        Assert.IsFalse(Directory.Exists(_git.UserPath(old)));
        Assert.AreEqual("unsaved\n",
            File.ReadAllText(Path.Combine(_git.UserPath("jeremy"), "wip.nb.md")));
        CollectionAssert.Contains(
            _git.UserWorktrees().Select(w => w.Handle).ToList(), "jeremy");
        Assert.IsFalse(_git.UserWorktrees().Any(w => w.Handle == old));

        // And the branch went with it, rather than a directory being renamed under a
        // branch that still has the old name.
        Assert.AreEqual(_git.UserPath("jeremy"), _git.PathFor(GitService.BranchForUser("jeremy")));
        Assert.IsTrue(_git.PushToTest("jeremy", "keep", "Jeremy", "j@users.local").Pushed);
    }

    [TestMethod]
    public void PathFor_refuses_a_branch_this_workspace_does_not_have() {
        _git.Init();
        // Not a fallback to test: an unknown branch resolving there would put a write
        // meant for somebody's own branch into the one nobody may write to.
        Assert.ThrowsExactly<GitException>(() => _git.PathFor("whatever"));

        // `user/…` followed by something no account could be called. This used to be
        // "anything that is not a guid", which `not-a-guid` satisfied; a handle makes
        // that string a perfectly good branch name, so the cases that are actually
        // malformed are the ones a username may not contain.
        foreach (var branch in new[] { "user/", "user/Not A Handle", "user/-dash", "user/UPPER" }) {
            Assert.ThrowsExactly<GitException>(() => _git.PathFor(branch), branch);
        }
    }

    /// <summary>
    /// A workspace upgraded from 0.11 still has `user/&lt;guid&gt;` branches until the
    /// startup pass renames them, so the guid form has to keep resolving — and it is
    /// a predicate, so getting this wrong routes somebody's own notebook to the wrong
    /// root instead of failing.
    /// </summary>
    [TestMethod]
    public void A_branch_named_the_old_way_still_resolves() {
        _git.Init();
        var old = "user/" + Guid.NewGuid().ToString("D");

        Assert.IsTrue(GitService.IsUserBranch(old));
        Assert.AreEqual(_git.UserPath(GitService.HandleOf(old)), _git.PathFor(old));
    }

    [TestMethod]
    public void Pushing_fast_forwards_test_and_names_the_author() {
        _git.Init();
        _git.EnsureUserWorktree(_ada);
        WriteUser(_ada, "etl.nb.md", "mine\n");

        var result = _git.PushToTest(_ada, "add etl", "Ada Lovelace", "ada@users.local");

        Assert.IsTrue(result.Pushed, result.Error);
        Assert.AreEqual("mine\n", File.ReadAllText(Path.Combine(_git.TestPath, "etl.nb.md")));
        Assert.AreEqual(result.Sha, _git.HeadSha("test"));
        StringAssert.Contains(_git.RunForTests("log", "-1", "--format=%an", "test"), "Ada Lovelace");
    }

    /// <summary>
    /// Which files test has moved on, as opposed to how many commits.
    ///
    /// <para>
    /// The report that prompted this: every file said "Update from test", files
    /// that exist only on the person's own branch included. The branch was behind;
    /// the file was not, and the toolbar could not tell the two apart because the
    /// count was all it had.
    /// </para>
    /// </summary>
    /// <summary>
    /// History, and what a merge from test would bring.
    ///
    /// <para>
    /// The subject is whatever somebody typed, so the record and field separators
    /// are \x01 and NUL rather than any character a person can reach: a message
    /// with a pipe, a tab or a newline in it is the one that breaks a parser built
    /// on the obvious delimiters, and it breaks it by silently dropping commits.
    /// </para>
    /// </summary>
    /// <summary>
    /// A folder listing with the commit that last touched each row.
    ///
    /// <para>
    /// The `prod` case is the one worth pinning: the app calls that environment
    /// `prod` and git calls the ref `main`, so a listing that hands the environment
    /// name straight to `git log` attributes nothing and every row comes back with
    /// no commit on it — which looks like a folder nobody has ever changed.
    /// </para>
    /// </summary>
    /// <summary>
    /// One file's history, and what each commit did to it.
    ///
    /// <para>
    /// The edges are the point. A commit that <em>added</em> the file has nothing
    /// before it, and null is the only honest answer there — an empty string says
    /// "it was empty", which is a different thing and renders as a diff with no
    /// changes rather than as a file being created.
    /// </para>
    /// </summary>
    [TestMethod]
    public void One_files_history_and_what_each_commit_did_to_it() {
        _git.Init();
        _git.EnsureUserWorktree(_grace);

        WriteUser(_grace, "reports/monthly.nb.md", "first\n");
        WriteUser(_grace, "other.nb.md", "unrelated\n");
        Assert.IsTrue(_git.PushToTest(_grace, "add the monthly report", "Grace", "g@x").Pushed);

        WriteUser(_grace, "reports/monthly.nb.md", "second\n");
        Assert.IsTrue(_git.PushToTest(_grace, "rework the rollup", "Grace", "g@x").Pushed);

        // Only the commits that touched it — the other file's is not in here, and
        // that is the difference between a file's history and the branch's.
        var history = _git.History(GitService.TestBranch, path: "reports/monthly.nb.md");
        CollectionAssert.AreEqual(
            new[] { "rework the rollup", "add the monthly report" },
            history.Select(c => c.Subject).ToArray(),
            "newest first, and only this file's commits");

        // What the newest one did: second replaced first.
        var latest = _git.FileChange(history[0].Sha, "reports/monthly.nb.md");
        Assert.AreEqual("first\n", latest.Before);
        Assert.AreEqual("second\n", latest.After);

        // And what the oldest did: it created the file, so there is no before.
        var created = _git.FileChange(history[1].Sha, "reports/monthly.nb.md");
        Assert.IsNull(created.Before, "nothing before the commit that added it");
        Assert.AreEqual("first\n", created.After);

        // A path that looks like a ref is still a path.
        WriteUser(_grace, "test", "a file called test\n");
        Assert.IsTrue(_git.PushToTest(_grace, "a file named for a branch", "Grace", "g@x").Pushed);
        var awkward = _git.History(GitService.TestBranch, path: "test");
        Assert.AreEqual("a file named for a branch", awkward[0].Subject,
            "`--` keeps git from reading the path as the branch of the same name");
    }

    /// <summary>
    /// A file's history does not begin again because somebody moved it.
    ///
    /// <para>
    /// Following is only half done unless the diff follows too: each commit's
    /// name-status names the path the file had <em>at that commit</em>, and the
    /// rename commit names both. Read the left side of a rename at the new path and
    /// git finds nothing there, so the commit that only moved a file reads as the
    /// commit that created it — which is why the last assertion here is that
    /// forgetting the old path is what makes <c>Before</c> null.
    /// </para>
    /// </summary>
    [TestMethod]
    public void A_files_history_follows_it_across_a_rename() {
        _git.Init();
        _git.EnsureUserWorktree(_grace);

        // Three lines so the rename below is still recognisably the same file: git
        // detects a rename by similarity, and a one-line file that changes at all
        // is 0% similar to itself.
        WriteUser(_grace, "reports/old.nb.md", "alpha\nbeta\ngamma\n");
        Assert.IsTrue(_git.PushToTest(_grace, "add the report", "Grace", "g@x").Pushed);

        WriteUser(_grace, "reports/old.nb.md", "alpha\nbeta\ndelta\n");
        Assert.IsTrue(_git.PushToTest(_grace, "rework the rollup", "Grace", "g@x").Pushed);

        // Moved and edited in one commit, which is the normal shape of a rename in
        // this app: the editor's rename writes the new path and drops the old.
        File.Delete(Path.Combine(_git.UserPath(_grace), "reports/old.nb.md"));
        WriteUser(_grace, "reports/new.nb.md", "alpha\nbeta\nepsilon\n");
        Assert.IsTrue(_git.PushToTest(_grace, "rename the report", "Grace", "g@x").Pushed);

        var history = _git.History(
            GitService.TestBranch, withFiles: true, path: "reports/new.nb.md");
        CollectionAssert.AreEqual(
            new[] { "rename the report", "rework the rollup", "add the report" },
            history.Select(c => c.Subject).ToArray(),
            "the commits from before the rename are this file's too");

        // The rename commit carries both sides.
        var moved = history[0].Files.Single();
        StringAssert.StartsWith(moved.Status, "R", "git reports it as a rename");
        Assert.AreEqual("reports/new.nb.md", moved.Path);
        Assert.AreEqual("reports/old.nb.md", moved.OldPath);

        // And every older commit names the path the file had then — the one thing
        // that makes their diffs fetchable at all.
        Assert.AreEqual("reports/old.nb.md", history[1].Files.Single().Path);
        Assert.AreEqual("reports/old.nb.md", history[2].Files.Single().Path);
        Assert.IsNull(history[1].Files.Single().OldPath, "only a rename has an old path");

        // The diff across the rename: the left side is read at the old path.
        var across = _git.FileChange(history[0].Sha, moved.Path, moved.OldPath);
        Assert.AreEqual("alpha\nbeta\ndelta\n", across.Before);
        Assert.AreEqual("alpha\nbeta\nepsilon\n", across.After);

        // Forget it and the move reads as a creation — the failure this parameter
        // exists to prevent, pinned rather than described.
        Assert.IsNull(_git.FileChange(history[0].Sha, moved.Path).Before,
            "the new path does not exist in the parent");

        // A commit from before the rename, fetched at the path it used then.
        var earlier = _git.FileChange(history[2].Sha, history[2].Files.Single().Path);
        Assert.IsNull(earlier.Before, "nothing before the commit that added it");
        Assert.AreEqual("alpha\nbeta\ngamma\n", earlier.After);
    }

    [TestMethod]
    public void Contents_lists_a_folder_and_says_what_last_touched_each_row() {
        _git.Init();
        _git.EnsureUserWorktree(_grace);
        WriteUser(_grace, "reports/monthly.nb.md", "hers\n");
        WriteUser(_grace, "top.nb.md", "hers\n");
        Assert.IsTrue(_git.PushToTest(_grace, "add the reports", "Grace", "g@users.local").Pushed);

        var root = _git.Contents(GitService.TestBranch, "");
        CollectionAssert.AreEqual(
            new[] { "reports", "top.nb.md" }, root.Select(e => e.Name).ToArray(),
            "folders first, then files, each sorted by name");
        Assert.IsTrue(root[0].IsDirectory);
        Assert.IsFalse(root[1].IsDirectory);
        Assert.IsTrue(root[1].Size > 0, "a file knows its size");

        // The column the filesystem cannot answer.
        Assert.AreEqual("add the reports", root[0].LastCommit?.Subject, "a folder, by what is under it");
        Assert.AreEqual("add the reports", root[1].LastCommit?.Subject);
        Assert.AreEqual("Grace", root[1].LastCommit?.Author);

        // Descending, and the paths are repo-relative rather than names.
        var inner = _git.Contents(GitService.TestBranch, "reports");
        CollectionAssert.AreEqual(new[] { "reports/monthly.nb.md" }, inner.Select(e => e.Path).ToArray());

        // And prod, whose ref is `main` — the translation the API has to get right.
        _git.CheckoutIntoProd("top.nb.md");
        _git.CommitProd("ship it");
        var shipped = _git.Contents("prod", "");
        Assert.IsTrue(shipped.Count > 0, "prod lists its files");
        Assert.IsNotNull(shipped[0].LastCommit,
            "and attributes them — a listing that passed `prod` to git log would find no ref");
    }

    [TestMethod]
    public void History_and_incoming_survive_a_subject_somebody_typed() {
        _git.Init();
        _git.EnsureUserWorktree(_ada);
        _git.EnsureUserWorktree(_grace);

        var awkward = "fix a|b\tand \"quote\" it";
        WriteUser(_grace, "reports/monthly.nb.md", "hers\n");
        Assert.IsTrue(_git.PushToTest(_grace, awkward, "Grace", "g@users.local").Pushed);

        var history = _git.History(GitService.TestBranch);
        Assert.IsTrue(history.Count > 0, "test has commits");
        Assert.AreEqual(awkward, history[0].Subject, "the message survives the trip");
        Assert.AreEqual("Grace", history[0].Author);
        StringAssert.Matches(history[0].ShortSha, new Regex("^[0-9a-f]{7,}$"));
        Assert.AreNotEqual(default, history[0].When, "and it is dated");

        // Ada parted before that, and has something of her own uncommitted.
        WriteUser(_ada, "only-mine.nb.md", "mine\n");

        var incoming = _git.IncomingFromTest(_ada);
        Assert.AreEqual(1, incoming.Count, "one commit is coming");
        Assert.AreEqual(awkward, incoming[0].Subject);
        CollectionAssert.AreEqual(
            new[] { "reports/monthly.nb.md" }, incoming[0].Files.Select(f => f.Path).ToArray(),
            "and it names the file, with the forward slashes git uses everywhere");
        Assert.AreEqual("A", incoming[0].Files[0].Status, "added, on that commit");

        // What is hers is not called mine: the uncommitted list is the other half of
        // the preview, and it is the half the confirm box never showed.
        var mine = _git.Uncommitted(_ada);
        CollectionAssert.AreEqual(
            new[] { "only-mine.nb.md" }, mine.Select(f => f.Path).ToArray());

        Assert.IsNotNull(_git.MergeBaseWithTest(_ada), "the branches share a base");
    }

    [TestMethod]
    public void BehindFiles_names_what_test_changed_and_nothing_of_yours() {
        _git.Init();
        _git.EnsureUserWorktree(_ada);
        _git.EnsureUserWorktree(_grace);

        // Nested, and with a forward slash: the editor matches these against its
        // own `reports/monthly.nb.md`, so a backslash or a leading `./` here is a
        // badge that silently never appears.
        WriteUser(_grace, "reports/monthly.nb.md", "hers\n");
        WriteUser(_grace, "shared.nb.md", "hers\n");
        Assert.IsTrue(_git.PushToTest(_grace, "hers", "Grace", "g@users.local").Pushed);

        // Ada has a file of her own, committed on her branch and not on test. The
        // refused push is what commits it — `git diff` compares commits, so a file
        // still only in the worktree is invisible to this either way, which is how
        // the first version of this test passed against `..` as well as `...`.
        WriteUser(_ada, "only-mine.nb.md", "mine\n");
        Assert.IsFalse(_git.PushToTest(_ada, "mine", "Ada", "a@users.local").Pushed);
        Assert.AreEqual(1, _git.StandingOf(_ada).Ahead, "her file is committed on her branch");

        var standing = _git.StandingOf(_ada);
        Assert.IsTrue(standing.Behind > 0, "the branch is behind test");
        CollectionAssert.AreEqual(
            new[] { "reports/monthly.nb.md", "shared.nb.md" },
            standing.BehindFiles.OrderBy(f => f, StringComparer.Ordinal).ToArray(),
            "only what test changed — a file test has never seen is not behind it");

        Assert.AreEqual(0, _git.UpdateFromTest(_ada, "Ada", "a@users.local").Count);
        Assert.AreEqual(0, _git.StandingOf(_ada).BehindFiles.Count, "and nothing after a merge");
    }

    [TestMethod]
    public void A_push_is_refused_once_test_has_moved_on() {
        _git.Init();
        _git.EnsureUserWorktree(_ada);
        _git.EnsureUserWorktree(_grace);
        WriteUser(_grace, "grace.nb.md", "hers\n");
        Assert.IsTrue(_git.PushToTest(_grace, "hers", "Grace", "g@users.local").Pushed);

        WriteUser(_ada, "ada.nb.md", "mine\n");
        var refused = _git.PushToTest(_ada, "mine", "Ada", "a@users.local");

        Assert.IsFalse(refused.Pushed);
        Assert.IsTrue(refused.NeedsUpdate);
        StringAssert.Contains(refused.Error, "Update from test");
        Assert.AreEqual(1, _git.StandingOf(_ada).Behind);

        // The work is committed on their branch either way — a refused push is not
        // a lost edit.
        Assert.AreEqual(1, _git.StandingOf(_ada).Ahead);

        Assert.AreEqual(0, _git.UpdateFromTest(_ada, "Ada", "a@users.local").Count);
        Assert.IsTrue(_git.PushToTest(_ada, "mine", "Ada", "a@users.local").Pushed);
        Assert.IsTrue(File.Exists(Path.Combine(_git.TestPath, "ada.nb.md")));
        Assert.IsTrue(File.Exists(Path.Combine(_git.TestPath, "grace.nb.md")));
    }

    [TestMethod]
    public void A_conflicting_update_reports_its_files_and_resolves_nothing() {
        _git.Init();
        WriteTest("shared.nb.md", "base\n");
        _git.WithLock(() => _git.Commit("test", "base"));
        _git.EnsureUserWorktree(_ada);
        _git.EnsureUserWorktree(_grace);

        WriteUser(_grace, "shared.nb.md", "hers\n");
        _git.PushToTest(_grace, "hers", "Grace", "g@users.local");
        WriteUser(_ada, "shared.nb.md", "mine\n");

        var conflicts = _git.UpdateFromTest(_ada, "Ada", "a@users.local");

        CollectionAssert.AreEqual(new[] { "shared.nb.md" }, conflicts.ToArray());
        var left = File.ReadAllText(Path.Combine(_git.UserPath(_ada), "shared.nb.md"));
        StringAssert.Contains(left, "mine", "both sides are left in the file, with markers");
        StringAssert.Contains(left, "hers");
        CollectionAssert.AreEqual(new[] { "shared.nb.md" }, _git.StandingOf(_ada).Conflicts.ToArray());

        var refused = _git.PushToTest(_ada, "mine", "Ada", "a@users.local");
        Assert.IsFalse(refused.Pushed, "a conflicted tree cannot be pushed");
        StringAssert.Contains(refused.Error, "conflicted");
    }

    [TestMethod]
    public void A_worktree_holding_unshared_work_is_not_pruned() {
        _git.Init();
        _git.EnsureUserWorktree(_ada);
        _git.EnsureUserWorktree(_grace);
        WriteUser(_ada, "unsaved.nb.md", "in progress\n");
        WriteUser(_grace, "committed.nb.md", "not pushed\n");
        _git.CommitAs(GitService.BranchForUser(_grace), "wip", "Grace", "g@users.local");

        var later = DateTime.UtcNow.AddDays(400);
        CollectionAssert.AreEqual(
            Array.Empty<Guid>(), _git.PruneIdleUserWorktrees(TimeSpan.FromDays(30), later).ToArray(),
            "idle is not the same as finished");
        Assert.IsTrue(_git.HasUserWorktree(_ada));
        Assert.IsTrue(_git.HasUserWorktree(_grace));

        StringAssert.Contains(_git.RemoveUserWorktree(_ada, force: false), "never saved to test");
        StringAssert.Contains(_git.RemoveUserWorktree(_grace, force: false), "test has never seen");
    }

    [TestMethod]
    public void A_worktree_test_already_has_is_pruned_once_it_goes_quiet() {
        _git.Init();
        _git.EnsureUserWorktree(_ada);
        WriteUser(_ada, "done.nb.md", "shipped\n");
        Assert.IsTrue(_git.PushToTest(_ada, "done", "Ada", "a@users.local").Pushed);

        // Nothing on it that test does not have, so removing it loses nothing.
        Assert.AreEqual(0, _git.PruneIdleUserWorktrees(TimeSpan.FromDays(30), DateTime.UtcNow).Count,
            "and not while it is still recent");
        var pruned = _git.PruneIdleUserWorktrees(TimeSpan.FromDays(30), DateTime.UtcNow.AddDays(31));

        CollectionAssert.AreEqual(new[] { _ada }, pruned.ToArray());
        Assert.IsFalse(_git.HasUserWorktree(_ada));
        Assert.IsTrue(File.Exists(Path.Combine(_git.TestPath, "done.nb.md")), "the work stays in test");
        StringAssert.Contains(_git.RemoveUserWorktree(_grace, force: true), "no worktree",
            "removing one that was never there says so rather than throwing");
    }

    [TestMethod]
    public void Forcing_removes_a_worktree_that_would_otherwise_be_kept() {
        _git.Init();
        _git.EnsureUserWorktree(_ada);
        WriteUser(_ada, "unsaved.nb.md", "in progress\n");

        Assert.IsNull(_git.RemoveUserWorktree(_ada, force: true));
        Assert.IsFalse(_git.HasUserWorktree(_ada));
        // And the branch with it, so re-editing starts from test rather than from
        // whatever was abandoned.
        _git.EnsureUserWorktree(_ada);
        Assert.AreEqual(_git.HeadSha("test"), _git.HeadSha(GitService.BranchForUser(_ada)));
    }

    [TestMethod]
    public void MigrateLegacyLayout_renames_a_0_9_workspace_in_place() {
        _git.Init();
        WriteTest("etl.nb.md", "# etl\n");
        _git.WithLock(() => _git.Commit("test", "add"));
        var sha = _git.HeadSha("test");

        // Wind the workspace back to what 0.9 left behind.
        _git.RunForTests("branch", "-m", GitService.TestBranch, GitService.LegacyTestBranch);
        _git.RunForTests("worktree", "move", _git.TestPath, Path.Combine(_dir, GitService.LegacyTestBranch));
        Assert.IsFalse(_git.LayoutExists, "the 0.9 layout is not the current one");

        Assert.IsTrue(_git.MigrateLegacyLayout());
        Assert.IsTrue(_git.LayoutExists);
        Assert.IsFalse(Directory.Exists(Path.Combine(_dir, GitService.LegacyTestBranch)));
        Assert.AreEqual(sha, _git.HeadSha("test"), "the rename moves no commits");
        Assert.AreEqual("# etl\n", File.ReadAllText(Path.Combine(_git.TestPath, "etl.nb.md")));

        Assert.IsFalse(_git.MigrateLegacyLayout(), "nothing left to do the second time");
    }

    [TestMethod]
    public void MigrateLegacyLayout_leaves_a_workspace_holding_both_alone() {
        _git.Init();
        Directory.CreateDirectory(Path.Combine(_dir, GitService.LegacyTestBranch));

        Assert.IsFalse(_git.MigrateLegacyLayout(), "test/ is live; dev/ is not this process's to merge");
        Assert.IsTrue(Directory.Exists(Path.Combine(_dir, GitService.LegacyTestBranch)));
        Assert.IsTrue(_git.LayoutExists);
    }

    [TestMethod]
    public void Init_refuses_a_half_formed_layout_with_instructions() {
        Directory.CreateDirectory(Path.Combine(_dir, "test"));
        var e = Assert.ThrowsExactly<GitException>(() => _git.Init());
        StringAssert.Contains(e.Message, "half-initialized");
    }

    [TestMethod]
    public void Commits_move_dev_and_leave_prod_alone() {
        _git.Init();
        var before = _git.HeadSha("test");

        _git.WithLock(() => {
            WriteTest("report.nb.md", "v1");
            _git.Commit("test", "edit report.nb.md via web UI");
        });

        Assert.AreNotEqual(before, _git.HeadSha("test"));
        Assert.AreEqual(1, _git.NameStatus().Count, "one path differs between prod and test");
        Assert.IsFalse(_git.IsDirty("test"), "committed, not dirty");

        // Committing again with no changes is a no-op, not an error.
        _git.WithLock(() => _git.Commit("test", "nothing"));
    }

    [TestMethod]
    public void Dirty_detection_sees_uncommitted_edits_per_path() {
        _git.Init();
        _git.WithLock(() => {
            WriteTest("a.nb.md", "committed");
            _git.Commit("test", "add a");
        });
        WriteTest("a.nb.md", "edited but not committed");
        WriteTest("b.nb.md", "brand new");

        Assert.IsTrue(_git.IsDirty("test", "a.nb.md"));
        Assert.IsTrue(_git.IsDirty("test", "b.nb.md"), "untracked counts as dirty");
        Assert.IsFalse(_git.IsDirty("test", "c.nb.md"));
    }

    [TestMethod]
    public void Unchanged_between_a_run_sha_and_dev_is_the_promotion_freshness_check() {
        _git.Init();
        _git.WithLock(() => {
            WriteTest("a.nb.md", "v1");
            _git.Commit("test", "v1");
        });
        var runSha = _git.HeadSha("test");

        Assert.IsTrue(_git.UnchangedBetween(runSha, GitService.TestBranch, "a.nb.md"));

        _git.WithLock(() => {
            WriteTest("a.nb.md", "v2");
            _git.Commit("test", "v2");
        });
        Assert.IsFalse(_git.UnchangedBetween(runSha, GitService.TestBranch, "a.nb.md"),
            "edited since the run: not promotable on that run's evidence");
        Assert.IsTrue(_git.UnchangedBetween(runSha, GitService.TestBranch, "other.nb.md"),
            "unrelated paths do not poison the check");
    }

    [TestMethod]
    public void Promotion_primitives_copy_and_delete_into_prod() {
        _git.Init();
        _git.WithLock(() => {
            WriteTest("keep.nb.md", "v1");
            WriteTest("gone.nb.md", "v1");
            _git.Commit("test", "two files");
        });

        // First promotion: both appear in prod.
        _git.WithLock(() => {
            foreach (var (status, path) in _git.NameStatus("keep.nb.md", "gone.nb.md")) {
                Assert.AreEqual('A', status);
                _git.CheckoutIntoProd(path);
            }
            _git.CommitProd("promote: keep + gone");
        });
        Assert.AreEqual("v1", File.ReadAllText(Path.Combine(_git.ProdPath, "gone.nb.md")));

        // Delete one in test; the diff says D and prod loses it on promotion.
        _git.WithLock(() => {
            File.Delete(Path.Combine(_git.TestPath, "gone.nb.md"));
            _git.Commit("test", "delete gone");
        });
        _git.WithLock(() => {
            var changes = _git.NameStatus("gone.nb.md");
            Assert.AreEqual('D', changes.Single().Status);
            _git.RemoveFromProd("gone.nb.md");
            _git.CommitProd("promote: delete gone");
        });
        Assert.IsFalse(File.Exists(Path.Combine(_git.ProdPath, "gone.nb.md")));
        Assert.IsTrue(File.Exists(Path.Combine(_git.ProdPath, "keep.nb.md")), "siblings untouched");
    }

    [TestMethod]
    public void Unified_diff_shows_dev_against_prod() {
        _git.Init();
        _git.WithLock(() => {
            WriteTest("a.nb.md", "line one\n");
            _git.Commit("test", "add");
        });
        var diff = _git.UnifiedDiff("a.nb.md");
        StringAssert.Contains(diff, "+line one");
    }

    [TestMethod]
    public void Repair_survives_a_moved_workspace() {
        _git.Init();
        _git.WithLock(() => {
            WriteTest("a.nb.md", "x");
            _git.Commit("test", "add");
        });

        var moved = _dir + "-moved";
        Directory.Move(_dir, moved);
        try {
            var reopened = new GitService(moved, NullLogger.Instance);
            reopened.Repair();
            Assert.IsFalse(reopened.IsDirty("test"), "worktree works again after repair");
            Assert.IsNotNull(reopened.HeadSha("prod"));
        } finally {
            Directory.Move(moved, _dir); // so Cleanup finds it
        }
    }

    [TestMethod]
    public void A_hung_command_is_killed_at_the_timeout() {
        _git.Init();
        _git.CommandTimeout = TimeSpan.FromMilliseconds(300);
        // A fetch from a non-routable address blocks until the timeout kills it.
        var e = Assert.ThrowsExactly<GitException>(() =>
            _git.RunForTests("fetch", "http://10.255.255.1/repo.git"));
        StringAssert.Contains(e.Message, "exceeded");
    }

    [TestMethod]
    public void Push_failures_are_recorded_not_thrown() {
        _git.Init();
        _git.CommandTimeout = TimeSpan.FromSeconds(10);
        _git.TryPush("/nonexistent/remote.git");
        Assert.IsNotNull(_git.LastPush.At);
        Assert.IsFalse(_git.LastPush.Ok);
        Assert.IsNotNull(_git.LastPush.Error);
    }
}

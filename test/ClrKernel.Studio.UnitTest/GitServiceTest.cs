using System;
using System.IO;
using System.Linq;
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
        var registered = _git.RunForTests("worktree", "list");
        StringAssert.Contains(registered, moved);
        Assert.IsFalse(registered.Contains(_git.UserPath(_ada) + "\n")
            || registered.Contains(_git.UserPath(_ada) + " "), "the old path is not still registered");

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

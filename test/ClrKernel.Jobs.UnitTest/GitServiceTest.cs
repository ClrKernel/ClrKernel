using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

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
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    private void WriteDev(string relative, string content) {
        var path = Path.Combine(_git.DevPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
    }

    [TestMethod]
    public void Init_creates_the_layout_and_is_idempotent() {
        var first = _git.Init();
        StringAssert.Contains(first, "initialized");
        Assert.IsTrue(_git.LayoutExists);
        Assert.IsTrue(Directory.Exists(_git.DevPath));
        Assert.IsTrue(Directory.Exists(_git.ProdPath));
        Assert.AreEqual(_git.HeadSha("dev"), _git.HeadSha("prod"), "both start at the initial commit");

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

        Assert.IsTrue(File.Exists(Path.Combine(_git.DevPath, "etl.nb.md")), "moved into dev");
        Assert.IsTrue(File.Exists(Path.Combine(_git.ProdPath, "etl.nb.md")), "and promoted to prod");
        Assert.IsTrue(File.Exists(Path.Combine(_git.ProdPath, "sub", "x.jobs.yaml")));
        Assert.IsTrue(File.Exists(Path.Combine(_dir, NotificationChannels.FileName)),
            "notifications.yaml stays at the workspace root");
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "etl.nb.md")), "no stray copy left behind");
    }

    [TestMethod]
    public void Init_refuses_a_half_formed_layout_with_instructions() {
        Directory.CreateDirectory(Path.Combine(_dir, "dev"));
        var e = Assert.ThrowsExactly<GitException>(() => _git.Init());
        StringAssert.Contains(e.Message, "half-initialized");
    }

    [TestMethod]
    public void Commits_move_dev_and_leave_prod_alone() {
        _git.Init();
        var before = _git.HeadSha("dev");

        _git.WithLock(() => {
            WriteDev("report.nb.md", "v1");
            _git.Commit("dev", "edit report.nb.md via web UI");
        });

        Assert.AreNotEqual(before, _git.HeadSha("dev"));
        Assert.AreEqual(1, _git.NameStatus().Count, "one path differs between prod and dev");
        Assert.IsFalse(_git.IsDirty("dev"), "committed, not dirty");

        // Committing again with no changes is a no-op, not an error.
        _git.WithLock(() => _git.Commit("dev", "nothing"));
    }

    [TestMethod]
    public void Dirty_detection_sees_uncommitted_edits_per_path() {
        _git.Init();
        _git.WithLock(() => {
            WriteDev("a.nb.md", "committed");
            _git.Commit("dev", "add a");
        });
        WriteDev("a.nb.md", "edited but not committed");
        WriteDev("b.nb.md", "brand new");

        Assert.IsTrue(_git.IsDirty("dev", "a.nb.md"));
        Assert.IsTrue(_git.IsDirty("dev", "b.nb.md"), "untracked counts as dirty");
        Assert.IsFalse(_git.IsDirty("dev", "c.nb.md"));
    }

    [TestMethod]
    public void Unchanged_between_a_run_sha_and_dev_is_the_promotion_freshness_check() {
        _git.Init();
        _git.WithLock(() => {
            WriteDev("a.nb.md", "v1");
            _git.Commit("dev", "v1");
        });
        var runSha = _git.HeadSha("dev");

        Assert.IsTrue(_git.UnchangedBetween(runSha, GitService.DevBranch, "a.nb.md"));

        _git.WithLock(() => {
            WriteDev("a.nb.md", "v2");
            _git.Commit("dev", "v2");
        });
        Assert.IsFalse(_git.UnchangedBetween(runSha, GitService.DevBranch, "a.nb.md"),
            "edited since the run: not promotable on that run's evidence");
        Assert.IsTrue(_git.UnchangedBetween(runSha, GitService.DevBranch, "other.nb.md"),
            "unrelated paths do not poison the check");
    }

    [TestMethod]
    public void Promotion_primitives_copy_and_delete_into_prod() {
        _git.Init();
        _git.WithLock(() => {
            WriteDev("keep.nb.md", "v1");
            WriteDev("gone.nb.md", "v1");
            _git.Commit("dev", "two files");
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

        // Delete one in dev; the diff says D and prod loses it on promotion.
        _git.WithLock(() => {
            File.Delete(Path.Combine(_git.DevPath, "gone.nb.md"));
            _git.Commit("dev", "delete gone");
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
            WriteDev("a.nb.md", "line one\n");
            _git.Commit("dev", "add");
        });
        var diff = _git.UnifiedDiff("a.nb.md");
        StringAssert.Contains(diff, "+line one");
    }

    [TestMethod]
    public void Repair_survives_a_moved_workspace() {
        _git.Init();
        _git.WithLock(() => {
            WriteDev("a.nb.md", "x");
            _git.Commit("dev", "add");
        });

        var moved = _dir + "-moved";
        Directory.Move(_dir, moved);
        try {
            var reopened = new GitService(moved, NullLogger.Instance);
            reopened.Repair();
            Assert.IsFalse(reopened.IsDirty("dev"), "worktree works again after repair");
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

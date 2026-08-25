using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClrKernel.Core.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClrKernel.Jobs.UnitTest;

/// <summary>
/// The store reaching disk as the files notebooks read, over a real git workspace.
/// <para>
/// A real one, not a fake: every hazard here is git's — whether a generated file
/// makes a worktree dirty, whether committing in test leaves personal branches
/// prunable, whether the exclude a linked worktree reads is the one we wrote. None
/// of those can be answered by a mock.
/// </para>
/// </summary>
[TestClass]
public class ConnectionMaterializerTest {
    private string _root;
    private JobsOptions _options;
    private ProjectRegistry _projects;
    private ConnectionStore _store;
    private ConnectionMaterializer _files;
    private GitService _git;
    private readonly Guid _grace = Guid.NewGuid();
    private readonly Guid _alan = Guid.NewGuid();
    private readonly List<string> _warnings = new();

    [TestInitialize]
    public void Setup() {
        _root = Path.Combine(Path.GetTempPath(), "clrkernel-mat-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "notebooks"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        File.WriteAllText(Path.Combine(_root, "notebooks", "etl.nb.md"), "```csharp\n1+1\n```\n");

        _options = new JobsOptions {
            DataDir = Path.Combine(_root, "data"),
            NotebooksRoot = Path.Combine(_root, "notebooks"),
            GitEnabled = true,
        };
        _projects = new ProjectRegistry(_options, NullLoggerFactory.Instance);
        _git = _projects.GitFor(_projects.Default);
        _git.Init();

        _store = new ConnectionStore(
            _options, SecretStore.ForProviders(new InMemorySecretProvider()),
            NullLogger<ConnectionStore>.Instance);
        // Sync swallows a project's failure so one broken workspace cannot stop the
        // others. That is right in production and useless in a test, so the warnings
        // are captured and every test asserts there were none.
        _files = new ConnectionMaterializer(_projects, _store, new Capturing(_warnings));
    }

    [TestCleanup]
    public void Cleanup() {
        Assert.AreEqual(0, _warnings.Count,
            "materializing must not quietly give up: " + string.Join(" | ", _warnings));
        try {
            Directory.Delete(_root, recursive: true);
        } catch (IOException) {
            // A handle git has not let go of yet; the temp directory is disposable.
        }
    }

    // --- shared -------------------------------------------------------------

    [TestMethod]
    public void ASharedConnectionIsCommittedInTestAndProd() {
        Save("warehouse", ConnectionScope.Shared);
        _files.Sync();

        foreach (var branch in new[] { GitService.TestBranch, "prod" }) {
            var path = Path.Combine(_git.PathFor(branch), ConnectionMaterializer.SharedFileName);
            Assert.IsTrue(File.Exists(path), branch);
            StringAssert.Contains(File.ReadAllText(path), "\"warehouse\"");
            Assert.IsFalse(_git.IsDirty(branch), $"{branch} should have committed it, not left it lying about");
        }
    }

    [TestMethod]
    public void ItIsTheSettingsAndTheSecretsNameAndNothingElse() {
        Save("warehouse", ConnectionScope.Shared);
        _files.Sync();

        var text = File.ReadAllText(
            Path.Combine(_git.TestPath, ConnectionMaterializer.SharedFileName));
        StringAssert.Contains(text, "\"$type\": \"SqlServer\"");
        StringAssert.Contains(text, "\"server\": \"dw.db.local\"");
        // The *name* of a secret, in the shape the config format resolves.
        StringAssert.Contains(text, "\"secret\":");
        StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex("hunter2"),
            "a password written to config is a password that leaks with the config");
    }

    [TestMethod]
    public void RemovingAConnectionRemovesItFromTheFile() {
        var keep = Save("keep", ConnectionScope.Shared);
        var drop = Save("drop", ConnectionScope.Shared);
        _files.Sync();
        var path = Path.Combine(_git.TestPath, ConnectionMaterializer.SharedFileName);
        StringAssert.Contains(File.ReadAllText(path), "\"drop\"");

        _store.Remove(drop.Id);
        _files.Sync();
        var text = File.ReadAllText(path);
        StringAssert.Contains(text, "\"keep\"");
        StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex("\"drop\""),
            "the file is generated, so a connection that is gone has to disappear from it");
        Assert.IsNotNull(keep);
    }

    [TestMethod]
    public void TheLastSharedConnectionLeavingTakesTheFileWithIt() {
        var only = Save("warehouse", ConnectionScope.Shared);
        _files.Sync();
        _store.Remove(only.Id);
        _files.Sync();

        Assert.IsFalse(File.Exists(Path.Combine(_git.TestPath, ConnectionMaterializer.SharedFileName)));
        Assert.IsFalse(_git.IsDirty(GitService.TestBranch), "and the removal is committed too");
    }

    [TestMethod]
    public void ItIsWrittenWithUnixLineEndingsWhicheverMachineWroteIt() {
        Save("warehouse", ConnectionScope.Shared);
        _files.Sync();

        var bytes = File.ReadAllText(Path.Combine(_git.TestPath, ConnectionMaterializer.SharedFileName));
        Assert.IsFalse(bytes.Contains('\r'),
            "a committed file whose line endings follow the server's OS shows every line as changed");
    }

    // --- private ------------------------------------------------------------

    [TestMethod]
    public void APrivateConnectionGoesOnlyToItsOwnersWorktree() {
        _git.EnsureUserWorktree(_grace);
        _git.EnsureUserWorktree(_alan);
        Save("scratch", ConnectionScope.Private, _grace);
        _files.Sync();

        var hers = Path.Combine(WorktreeOf(_grace), ConnectionMaterializer.PrivateFileName);
        var his = Path.Combine(WorktreeOf(_alan), ConnectionMaterializer.PrivateFileName);
        Assert.IsTrue(File.Exists(hers));
        Assert.IsFalse(File.Exists(his), "somebody else's private connection is not theirs to have");
        foreach (var branch in new[] { GitService.TestBranch, "prod" }) {
            Assert.IsFalse(
                File.Exists(Path.Combine(_git.PathFor(branch), ConnectionMaterializer.PrivateFileName)),
                $"and it must never reach {branch}, where the scheduler would resolve it");
        }
    }

    [TestMethod]
    public void ThePrivateOverlayIsNotWorkThatNeedsSaving() {
        _git.EnsureUserWorktree(_grace);
        Save("scratch", ConnectionScope.Private, _grace);
        _files.Sync();

        var branch = GitService.BranchForUser(_grace);
        Assert.IsFalse(_git.IsDirty(branch),
            "an untracked generated file would otherwise read as unsaved work forever");
    }

    [TestMethod]
    public void APersonalBranchStaysPrunableAfterASharedConnectionChanges() {
        _git.EnsureUserWorktree(_grace);
        Save("warehouse", ConnectionScope.Shared);
        Save("scratch", ConnectionScope.Private, _grace);
        _files.Sync();

        var worktree = _git.UserWorktrees().Single(w => w.UserId == _grace);
        Assert.IsFalse(worktree.Dirty);
        Assert.IsTrue(worktree.Merged,
            "committing on a personal branch would make every branch on the server unprunable");
        Assert.IsNull(_git.RemoveUserWorktree(_grace, force: false),
            "and the prune itself has to actually go through");
    }

    [TestMethod]
    public void ANewWorktreeGetsItsOwnersConnectionsWithoutTouchingAnybodyElses() {
        Save("scratch", ConnectionScope.Private, _grace);
        // The connection was saved before this person had ever opened a notebook.
        _files.Sync();
        _git.EnsureUserWorktree(_grace);
        _files.SyncUser(_git, _grace);

        StringAssert.Contains(
            File.ReadAllText(Path.Combine(WorktreeOf(_grace), ConnectionMaterializer.PrivateFileName)),
            "\"scratch\"");
        Assert.IsFalse(_git.IsDirty(GitService.TestBranch),
            "and a branch appearing is not a reason to write to test");
    }

    // --- both together ------------------------------------------------------

    [TestMethod]
    public void TheSharedFileAndTheOverlayEndUpInTheSameDirectory() {
        // ConnectionConfig.FindFiles stops at the first directory holding either, so
        // a worktree with only an overlay would lose every shared connection. The
        // base gets there by the branch descending from test, which is what this
        // asserts: the two are siblings, not one above the other.
        Save("warehouse", ConnectionScope.Shared);
        _files.Sync();
        _git.EnsureUserWorktree(_grace);
        Save("scratch", ConnectionScope.Private, _grace);
        _files.Sync();

        var worktree = WorktreeOf(_grace);
        Assert.IsTrue(File.Exists(Path.Combine(worktree, ConnectionMaterializer.SharedFileName)),
            "the branch was cut from test after the shared file was committed there");
        Assert.IsTrue(File.Exists(Path.Combine(worktree, ConnectionMaterializer.PrivateFileName)));
    }

    // --- helpers ------------------------------------------------------------

    private sealed class Capturing : ILogger<ConnectionMaterializer> {
        private readonly List<string> _messages;

        public Capturing(List<string> messages) => _messages = messages;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception error,
            Func<TState, Exception, string> format) {
            if (level >= LogLevel.Warning) {
                _messages.Add(format(state, error));
            }
        }
    }

    private string WorktreeOf(Guid userId) => _git.PathFor(GitService.BranchForUser(userId));

    private StoredConnection Save(string name, ConnectionScope scope, Guid? owner = null) =>
        _store.Save(
            new StoredConnection {
                Name = name,
                Scope = scope,
                OwnerId = scope == ConnectionScope.Private ? owner ?? _grace : null,
                Type = "SqlServer",
                Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                    ["server"] = "dw.db.local",
                    ["database"] = "datawarehouse",
                    ["auth"] = "sql",
                    ["user"] = "svc",
                },
            },
            password: "hunter2", readOnlyPassword: null);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClrKernel.Core.Primitives;
using ClrKernel.Database;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Jobs;

/// <summary>
/// Writes the connection store out as the <c>connections.json</c> files notebooks
/// actually read.
/// <para>
/// This is what makes <c>#!sql-connect --name warehouse</c> mean the same thing in
/// the web editor, in a scheduled run and in VS Code: the kernel resolves a
/// connection by walking up from the notebook's directory, so the store has to
/// arrive on disk beside the notebooks rather than staying in the data directory.
/// </para>
/// <para>
/// Two files, in the same directory, because <see cref="ConnectionConfig.FindFiles"/>
/// stops at the first directory holding <em>either</em> — a worktree with only a
/// <c>.local</c> overlay would return just that and never walk up to a shared base
/// elsewhere. So the base and the overlay live together or the shared list vanishes
/// for anyone who has a private connection.
/// </para>
/// </summary>
public sealed class ConnectionMaterializer {
    /// <summary>Shared connections. Committed in test and prod.</summary>
    public const string SharedFileName = "connections.json";

    /// <summary>One person's own, in their own worktree, never committed: a file on a
    /// branch is readable by every project viewer and is pushed to the remote when
    /// user branches are pushed, which is the opposite of private.</summary>
    public const string PrivateFileName = "connections.local.json";

    private readonly ProjectRegistry _projects;
    private readonly ConnectionStore _store;
    private readonly ConnectionProviderCatalog _providers;
    private readonly ILogger _logger;

    public ConnectionMaterializer(
        ProjectRegistry projects, ConnectionStore store, ConnectionProviderCatalog providers,
        ILogger<ConnectionMaterializer> logger) {
        _projects = projects;
        _store = store;
        _providers = providers;
        _logger = logger;
    }

    /// <summary>
    /// Brings every project's files in line with the store.
    /// <para>
    /// The whole lot on every change rather than only what moved: a server has a
    /// handful of projects and a handful of worktrees, the commit is a no-op when
    /// nothing differs, and one code path cannot drift out of step with a narrower
    /// one that only runs sometimes.
    /// </para>
    /// </summary>
    public void Sync() {
        foreach (var project in _projects.Projects) {
            try {
                SyncProject(project);
            } catch (Exception e) {
                // One project's broken workspace must not stop the others from
                // getting their connections.
                _logger?.LogWarning(
                    "Could not write connections for '{Project}': {Error}", project.Slug, e.Message);
            }
        }
    }

    private void SyncProject(Project project) {
        var git = _projects.GitFor(project);
        if (git == null) {
            // No worktrees — one flat folder of notebooks. Shared connections go
            // beside them. Private ones do not: there is no per-person directory to
            // put them in, and one file at the root would be everybody's.
            Write(Path.Combine(project.Root, SharedFileName), Shared());
            return;
        }

        // Both names, and the shared one for a reason worth stating: on test and prod
        // it is tracked, where an exclude has no effect and the commit works as
        // before. On a personal branch that predates it, it is untracked — and there
        // it has to be ignored or the copy written below would read as unsaved work.
        git.EnsureExcluded(SharedFileName);
        git.EnsureExcluded(PrivateFileName);
        git.WithLock(() => {
            // Committed, so a connection is live for the scheduler the moment it is
            // saved and an admin who is not a member of the project can still manage
            // it. The cost, chosen deliberately: this puts every personal branch one
            // commit behind test, and being behind is what the branch banner asks
            // people to fix before they push.
            foreach (var branch in new[] { GitService.TestBranch, "prod" }) {
                var path = Path.Combine(git.PathFor(branch), SharedFileName);
                Write(path, Shared());
                if (git.CommitPath(branch, SharedFileName, "connections: update the shared list")) {
                    _logger?.LogInformation(
                        "Wrote the shared connections into {Project}/{Branch}.", project.Slug, branch);
                }
            }

            foreach (var worktree in git.UserWorktrees()) {
                Write(Path.Combine(worktree.Path, PrivateFileName), PrivateFor(worktree.UserId));
                WriteSharedInto(git, worktree);
            }
        });
    }

    /// <summary>
    /// Gives a personal worktree a copy of the shared list — but only while its
    /// branch does not track one.
    /// <para>
    /// A branch cut from test after the file was committed there already has it, and
    /// writing over a tracked file would show as unsaved work and make the worktree
    /// unprunable. A branch cut <em>before</em> has neither — and because
    /// <see cref="ConnectionConfig.FindFiles"/> stops at the first directory holding
    /// either file, its owner's overlay would then be the only thing found and every
    /// shared connection would silently vanish for them. So the untracked case gets a
    /// copy, ignored, until a merge from test replaces it with the real one.
    /// </para>
    /// </summary>
    private void WriteSharedInto(GitService git, GitService.UserWorktree worktree) {
        if (git.Tracks(GitService.BranchForUser(worktree.UserId), SharedFileName)) {
            return;
        }
        Write(Path.Combine(worktree.Path, SharedFileName), Shared());
    }

    /// <summary>
    /// Writes one person's overlay into a worktree that has just been created.
    /// <para>
    /// Targeted rather than a whole <see cref="Sync"/>: a new worktree is not a
    /// change to the shared list, so there is nothing to commit anywhere, and a
    /// branch appearing should not write to test and prod.
    /// </para>
    /// </summary>
    public void SyncUser(GitService git, Guid userId) {
        try {
            git.EnsureExcluded(SharedFileName);
            git.EnsureExcluded(PrivateFileName);
            var branch = GitService.BranchForUser(userId);
            var worktree = git.PathFor(branch);
            Write(Path.Combine(worktree, PrivateFileName), PrivateFor(userId));
            if (!git.Tracks(branch, SharedFileName)) {
                Write(Path.Combine(worktree, SharedFileName), Shared());
            }
        } catch (Exception e) {
            _logger?.LogWarning(
                "Could not write the private connections for {User}: {Error}", userId, e.Message);
        }
    }

    private IReadOnlyList<StoredConnection> Shared() =>
        _store.All.Where(c => c.Scope == ConnectionScope.Shared).ToList();

    private IReadOnlyList<StoredConnection> PrivateFor(Guid userId) =>
        _store.All.Where(c => c.Scope == ConnectionScope.Private && c.OwnerId == userId).ToList();

    /// <summary>
    /// Writes one generated file, or removes it when there is nothing to write.
    /// <para>
    /// Deleted and rebuilt rather than updated in place: <see cref="ConnectionConfig.Upsert"/>
    /// replaces a node or appends one, and has no notion of a node that should no
    /// longer be there. Rebuilding is how a deleted connection actually disappears.
    /// </para>
    /// </summary>
    private void Write(string path, IReadOnlyList<StoredConnection> connections) {
        if (File.Exists(path)) {
            File.Delete(path);
        }
        if (connections.Count == 0) {
            return;
        }
        foreach (var connection in connections) {
            ConnectionConfig.Upsert(path, connection.Name, connection.Type, Properties(connection));
        }
        // LF, whatever wrote it. This file is committed, and a server that changed
        // line endings by being a different operating system would show every line
        // as changed on the next diff.
        File.WriteAllText(path, File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    /// <summary>
    /// A connection as config properties. The stored settings are already keyed by
    /// the provider descriptor's own names, which are the config file's keys — so
    /// this is a copy rather than a translation, and a provider added later needs no
    /// case here.
    /// </summary>
    private IEnumerable<ConfigProperty> Properties(StoredConnection connection) {
        foreach (var setting in connection.Settings) {
            yield return ConfigProperty.Plain(setting.Key, setting.Value);
        }
        // The secret *reference*, never a secret. Which key it goes under is the
        // provider's business: it is whichever of its settings is a credential.
        if (!string.IsNullOrEmpty(connection.SecretRef)) {
            yield return ConfigProperty.Secret(SecretKeyFor(connection.Type), connection.SecretRef);
        }
    }

    private string SecretKeyFor(string type) =>
        _providers.Find(type)?.Settings
            .FirstOrDefault(s => s.Kind == ConnectionSettingKind.SecretRef)?.Name
        ?? "password";
}

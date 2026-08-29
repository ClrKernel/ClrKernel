using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Studio;

/// <summary>
/// The set of registered projects, and the one place that turns a slug into the
/// things that act on it — a <see cref="JobCatalog"/> and, when the project uses the
/// workflow, a <see cref="GitService"/>. Both are cached per project: a GitService
/// owns the lock that serializes writes to its workspace, so handing out a second one
/// would hand out a second lock.
/// <para>
/// A slug nobody registered resolves to null and the caller answers 404, not 403.
/// Projects you cannot see should not be distinguishable from projects that do not
/// exist — that is the rule per-project grants will lean on, and it is free now.
/// </para>
/// </summary>
public sealed class ProjectRegistry {
    private readonly JobsOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, GitService> _git = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, JobCatalog> _catalogs = new(StringComparer.OrdinalIgnoreCase);

    // Replaced wholesale rather than mutated, and only under _writeLock: the
    // scheduler iterates this list on every tick from its own thread, and a List
    // being added to mid-enumeration throws. Readers take the reference once.
    private readonly object _writeLock = new();
    private volatile IReadOnlyList<Project> _projects;

    public ProjectRegistry(JobsOptions options, ILoggerFactory loggerFactory) {
        _options = options;
        _loggerFactory = loggerFactory;
        _projects = ProjectsFile.Read(options.DataDir) ?? new List<Project> { Implicit(options) };
    }

    /// <summary>The slug of the project a server that registered none is running.</summary>
    public const string DefaultSlug = "default";

    /// <summary>
    /// The project a server with no <c>projects.json</c> has: the notebooks root it
    /// was pointed at, named after its folder.
    /// <para>
    /// Its slug is <c>default</c> rather than anything derived from the path, and
    /// deliberately: that is what every run row written before projects existed
    /// already says, so history keeps answering with no rewrite at all. The folder
    /// name is the display name, which is the half people read.
    /// </para>
    /// </summary>
    internal static Project Implicit(JobsOptions options) {
        var root = Path.GetFullPath(options.NotebooksRoot);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        return new Project {
            Slug = DefaultSlug,
            Name = string.IsNullOrWhiteSpace(name) ? "Notebooks" : name,
            Root = root,
            GitEnabled = options.GitEnabled,
        };
    }

    public IReadOnlyList<Project> Projects => _projects;

    /// <summary>Thrown when a registration or edit is refused; the message is for a person.</summary>
    public sealed class ProjectException : Exception {
        public ProjectException(string message) : base(message) { }
    }

    /// <summary>
    /// Registers a project and persists the whole list — including the implicit one,
    /// which would otherwise disappear the moment a second project is written over it.
    /// </summary>
    /// <param name="createdRoot">
    /// True when the folder was made rather than adopted. Returned rather than kept,
    /// because a field on a singleton read after the lock is a field two
    /// registrations can disagree about — and it exists only so the answer can
    /// mention it, which is worth doing: a typo'd path silently becoming an empty
    /// project is the failure this feature introduces.
    /// </param>
    public Project Register(Project project, out bool createdRoot) {
        lock (_writeLock) {
            var slug = string.IsNullOrWhiteSpace(project.Slug)
                ? Project.SlugFor(project.Name)
                : Project.SlugFor(project.Slug);
            if (Find(slug) != null) {
                throw new ProjectException($"A project with the slug '{slug}' is already registered.");
            }
            var registered = project.Clone();
            registered.Slug = slug;
            registered.Name = string.IsNullOrWhiteSpace(project.Name) ? slug : project.Name.Trim();
            registered.Root = ValidRoot(project.Root, slug);

            createdRoot = !Directory.Exists(registered.Root);
            try {
                Directory.CreateDirectory(registered.Root);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                throw new ProjectException($"Could not create {registered.Root}: {e.Message}");
            }

            _projects = _projects.Append(registered).ToList();
            ProjectsFile.Write(_options.DataDir, _projects);
            return registered;
        }
    }

    /// <summary>
    /// Changes what a project is called and how it reaches a remote. The slug and the
    /// root are not editable: the slug is written into every run row, and the root is
    /// where the history those rows describe actually happened.
    /// </summary>
    public Project Update(string slug, Action<Project> edit) {
        lock (_writeLock) {
            if (Find(slug) is not { } existing) {
                return null;
            }
            var updated = existing.Clone();
            edit(updated);
            updated.Slug = existing.Slug;
            updated.Root = existing.Root;
            if (string.IsNullOrWhiteSpace(updated.Name)) {
                updated.Name = existing.Name;
            }

            _projects = _projects.Select(p => p == existing ? updated : p).ToList();
            ProjectsFile.Write(_options.DataDir, _projects);
            // The git layer captured GitEnabled and the author identity when it was
            // built; drop it so the next request builds one from the new settings.
            _git.TryRemove(existing.Slug, out _);
            _catalogs.TryRemove(existing.Slug, out _);
            return updated;
        }
    }

    /// <summary>
    /// Forgets a project. Nothing on disk is touched — the repo, the worktrees and
    /// the run history all stay exactly where they are, and registering the same root
    /// again under the same slug brings all of it back. Returns false for a slug that
    /// was never registered.
    /// </summary>
    public bool Unregister(string slug) {
        lock (_writeLock) {
            if (Find(slug) is not { } existing) {
                return false;
            }
            if (_projects.Count == 1) {
                throw new ProjectException(
                    "This is the only project. A server with none has nothing to show.");
            }
            _projects = _projects.Where(p => p != existing).ToList();
            ProjectsFile.Write(_options.DataDir, _projects);
            _git.TryRemove(existing.Slug, out _);
            _catalogs.TryRemove(existing.Slug, out _);
            return true;
        }
    }

    /// <summary>
    /// An absolute path to a directory that exists and does not overlap a project
    /// already registered. Overlap is the one that is easy to do by accident and
    /// unpleasant afterwards: a project nested inside another means both scan the
    /// same *.jobs.yaml, so the same job is scheduled twice under two names.
    /// </summary>
    private string ValidRoot(string root, string slug) {
        if (string.IsNullOrWhiteSpace(root)) {
            throw new ProjectException("A project needs a folder to live in.");
        }
        // Checked on the input, not on the result: GetFullPath resolves a relative
        // path against the server process's working directory, which would quietly
        // register whatever happens to sit beside the binary.
        if (!Path.IsPathRooted(root.Trim())) {
            throw new ProjectException("Give the project's folder as an absolute path.");
        }
        string full;
        try {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Trim()));
        } catch (Exception e) when (e is ArgumentException or NotSupportedException) {
            throw new ProjectException($"'{root}' is not a usable path.");
        }
        if (File.Exists(full)) {
            throw new ProjectException($"{full} is a file. A project needs a folder.");
        }
        // The data directory holds the database, settings.json and projects.json —
        // server state, not project content. A project rooted inside it would put
        // notebooks and run history in one tree and hand the notebook editor a path
        // to the database.
        var dataDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.DataDir));
        if (Overlaps(full, dataDir)) {
            throw new ProjectException(
                $"{full} is inside the data directory. Projects hold notebooks; that holds the " +
                "run history and the settings.");
        }
        foreach (var other in _projects) {
            var existing = Path.TrimEndingDirectorySeparator(other.Root);
            if (Overlaps(full, existing)) {
                throw new ProjectException(
                    $"{full} overlaps '{other.Slug}' at {existing}. Two projects sharing a folder " +
                    "would both find the same jobs and schedule each of them twice.");
            }
        }
        return full;
    }

    private static bool Overlaps(string a, string b) {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return a.Equals(b, comparison)
            || a.StartsWith(b + Path.DirectorySeparatorChar, comparison)
            || b.StartsWith(a + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>The project a request that named none means — the first registered.</summary>
    public Project Default => _projects[0];

    /// <summary>The project with this slug, or null. Callers answer 404 on null.</summary>
    public Project Find(string slug) =>
        string.IsNullOrWhiteSpace(slug)
            ? null
            : _projects.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>The git layer for a project, or null when it does not use the workflow.</summary>
    public GitService GitFor(Project project) =>
        project is not { GitEnabled: true }
            ? null
            : _git.GetOrAdd(project.Slug, _ => new GitService(
                project.Root, _loggerFactory.CreateLogger<GitService>(),
                _options.GitAuthorName, _options.GitAuthorEmail));

    public JobCatalog CatalogFor(Project project) =>
        _catalogs.GetOrAdd(project.Slug, _ =>
            new JobCatalog(project.Root, project.GitEnabled, GitFor(project)) { Project = project.Slug });

    /// <summary>
    /// The catalog for one branch. test and prod are both in the project's own
    /// catalog; a personal branch has a worktree the project catalog never scans,
    /// so it gets one of its own — a full catalog, because a personal worktree is a
    /// full checkout rather than an overlay, which is what lets its dependencies
    /// resolve against jobs that also exist in test.
    /// </summary>
    public JobCatalog CatalogFor(Project project, string branch) {
        if (!GitService.IsUserBranch(branch) || GitFor(project) is not { } git) {
            return CatalogFor(project);
        }
        return _catalogs.GetOrAdd($"{project.Slug}\u0000{branch}", _ =>
            new JobCatalog(git.PathFor(branch), gitLayout: false, git) {
                Project = project.Slug,
                Environment = MineEnvironment,
            });
    }

    /// <summary>What a personal branch's catalog calls its one environment.</summary>
    public const string MineEnvironment = "mine";

    /// <summary>
    /// Every project's jobs in one result, each tagged with the project it came from.
    /// The scheduler and the jobs list both want all of them; a per-project view is a
    /// filter over this, not a different scan.
    /// </summary>
    public CatalogResult LoadAll() {
        var jobs = new List<JobDefinition>();
        var errors = new List<string>();
        foreach (var project in _projects) {
            var result = CatalogFor(project).Load();
            jobs.AddRange(result.Jobs);
            errors.AddRange(_projects.Count == 1
                ? result.Errors
                : result.Errors.Select(e => $"{project.Slug}: {e}"));
        }
        return new CatalogResult { Jobs = jobs, Errors = errors, Environments = Environments };
    }

    /// <summary>
    /// The environment names in use anywhere. Kept because run history keys on them
    /// and they are the same two everywhere the workflow is on; a project that does
    /// not use git contributes "default".
    /// </summary>
    public IReadOnlyList<string> Environments =>
        _projects.SelectMany(p => CatalogFor(p).Environments).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Renames a pre-0.10 <c>dev</c> worktree in every project that still has one, and
    /// repairs worktree pointers (they are absolute, and volumes move). Returns the
    /// projects that were migrated.
    /// </summary>
    public IReadOnlyList<Project> PrepareWorkspaces() {
        var migrated = new List<Project>();
        foreach (var project in _projects) {
            var git = GitFor(project);
            if (git == null) {
                continue;
            }
            if (git.MigrateLegacyLayout()) {
                migrated.Add(project);
            }
            if (git.LayoutExists) {
                git.Repair();
            }
        }
        return migrated;
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace ClrKernel.Jobs;

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
    private readonly List<Project> _projects;
    private readonly ConcurrentDictionary<string, GitService> _git = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, JobCatalog> _catalogs = new(StringComparer.OrdinalIgnoreCase);

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

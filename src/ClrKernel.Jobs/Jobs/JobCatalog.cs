using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClrKernel.Jobs;

/// <summary>The jobs found in every environment, plus every validation problem.</summary>
public sealed class CatalogResult {
    public IReadOnlyList<JobDefinition> Jobs { get; init; } = new List<JobDefinition>();
    /// <summary>Human-readable problems, each prefixed with the file it came from.</summary>
    public IReadOnlyList<string> Errors { get; init; } = new List<string>();
    /// <summary>The environments this catalog covers ("default", or "dev"+"prod").</summary>
    public IReadOnlyList<string> Environments { get; init; } = new[] { "default" };

    public JobDefinition Find(string environment, string name) =>
        Jobs.FirstOrDefault(j =>
            string.Equals(j.Environment, environment, StringComparison.OrdinalIgnoreCase)
            && string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<JobDefinition> In(string environment) =>
        Jobs.Where(j => string.Equals(j.Environment, environment, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Scans for <c>*.jobs.yaml</c> files, flattens them into <see cref="JobDefinition"/>s,
/// and validates each environment's set: unique names, notebooks exist, dependencies
/// resolve, no cycles. Names are unique <em>per environment</em> — a job existing in
/// both dev and prod is the normal promoted state, not a duplicate.
/// <para>
/// Without git there is one root and one environment ("default"). With the git
/// workflow, dev/ and prod/ worktrees are scanned as separate environments and the
/// bare repo is excluded. Parse results are cached per file by last-write time, so
/// rescanning every scheduler tick / API request is cheap.
/// </para>
/// </summary>
public sealed class JobCatalog {
    private readonly string _notebooksRoot;
    private readonly bool _gitLayout;
    private readonly Dictionary<string, (DateTime MTime, IReadOnlyList<JobDefinition> Jobs, string Error)> _cache =
        new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <param name="gitLayout">Scan &lt;root&gt;/dev and &lt;root&gt;/prod as environments.</param>
    public JobCatalog(string notebooksRoot, bool gitLayout = false) {
        _notebooksRoot = Path.GetFullPath(notebooksRoot);
        _gitLayout = gitLayout;
    }

    public string NotebooksRoot => _notebooksRoot;
    public bool GitLayout => _gitLayout;

    /// <summary>The scan root for one environment (the worktree, or the flat root).</summary>
    public string RootFor(string environment) =>
        _gitLayout ? Path.Combine(_notebooksRoot, environment) : _notebooksRoot;

    public IReadOnlyList<string> Environments =>
        _gitLayout ? new[] { "dev", "prod" } : new[] { "default" };

    /// <summary>Rescans every environment and returns the current jobs and problems.</summary>
    public CatalogResult Load() {
        var jobs = new List<JobDefinition>();
        var errors = new List<string>();

        foreach (var environment in Environments) {
            var root = RootFor(environment);
            if (!Directory.Exists(root)) {
                errors.Add(_gitLayout
                    ? $"{environment}: worktree missing at {root} — run `clrkernel-jobs git init`."
                    : $"Notebooks root not found: {root}");
                continue;
            }
            LoadEnvironment(environment, root, jobs, errors);
        }

        return new CatalogResult { Jobs = jobs, Errors = errors, Environments = Environments };
    }

    private void LoadEnvironment(string environment, string root, List<JobDefinition> jobs, List<string> errors) {
        var found = new List<JobDefinition>();
        lock (_lock) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(root, "*.jobs.yaml", SearchOption.AllDirectories)) {
                seen.Add(file);
                var mtime = File.GetLastWriteTimeUtc(file);
                if (!_cache.TryGetValue(file, out var entry) || entry.MTime != mtime) {
                    try {
                        entry = (mtime, JobsFile.Load(file, root), null);
                    } catch (Exception e) {
                        entry = (mtime, null, e.Message);
                    }
                    _cache[file] = entry;
                }
                if (entry.Error != null) {
                    errors.Add($"{Prefix(environment)}{Relative(root, file)}: {entry.Error}");
                } else {
                    found.AddRange(entry.Jobs);
                }
            }
            foreach (var stale in _cache.Keys
                         .Where(k => k.StartsWith(root, StringComparison.Ordinal) && !seen.Contains(k))
                         .ToList()) {
                _cache.Remove(stale);
            }
        }

        // Per-environment validation: names, notebooks, then the graph.
        var byName = new Dictionary<string, JobDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in found) {
            job.Environment = environment;
            if (byName.TryGetValue(job.Name, out var other)) {
                errors.Add($"{Prefix(environment)}{Relative(root, job.SourceFile)}: duplicate job name " +
                    $"'{job.Name}' (also in {Relative(root, other.SourceFile)}).");
            } else {
                byName[job.Name] = job;
            }
        }
        foreach (var job in byName.Values) {
            if (!File.Exists(job.NotebookPath)) {
                errors.Add($"{Prefix(environment)}{Relative(root, job.SourceFile)}: job '{job.Name}' " +
                    $"notebook not found: {job.NotebookRelative}");
            }
            if (job.Cron != null) {
                try {
                    Cronos.CronExpression.Parse(job.Cron);
                } catch (Cronos.CronFormatException e) {
                    errors.Add($"{Prefix(environment)}{Relative(root, job.SourceFile)}: job '{job.Name}' " +
                        $"has an invalid cron '{job.Cron}': {e.Message}");
                }
            }
        }
        errors.AddRange(new JobGraph(byName.Values).Validate()
            .Select(error => Prefix(environment) + error));

        jobs.AddRange(byName.Values);
    }

    private string Prefix(string environment) => _gitLayout ? environment + ": " : string.Empty;

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClrKernel.Jobs;

/// <summary>The jobs found under the notebooks root, plus every validation problem.</summary>
public sealed class CatalogResult {
    public IReadOnlyList<JobDefinition> Jobs { get; init; } = new List<JobDefinition>();
    /// <summary>Human-readable problems, each prefixed with the file it came from.</summary>
    public IReadOnlyList<string> Errors { get; init; } = new List<string>();

    public JobDefinition Find(string name) =>
        Jobs.FirstOrDefault(j => j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Scans the notebooks root for <c>*.jobs.yaml</c> files, flattens them into
/// <see cref="JobDefinition"/>s, and validates the whole set: unique names,
/// notebooks exist, dependencies resolve, no cycles. Parse results are cached per
/// file by last-write time, so rescanning every scheduler tick / API request is cheap.
/// </summary>
public sealed class JobCatalog {
    private readonly string _notebooksRoot;
    private readonly Dictionary<string, (DateTime MTime, IReadOnlyList<JobDefinition> Jobs, string Error)> _cache =
        new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public JobCatalog(string notebooksRoot) {
        _notebooksRoot = Path.GetFullPath(notebooksRoot);
    }

    public string NotebooksRoot => _notebooksRoot;

    /// <summary>Rescans the tree and returns the current jobs and validation errors.</summary>
    public CatalogResult Load() {
        var jobs = new List<JobDefinition>();
        var errors = new List<string>();

        if (!Directory.Exists(_notebooksRoot)) {
            return new CatalogResult { Errors = new[] { $"Notebooks root not found: {_notebooksRoot}" } };
        }

        lock (_lock) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(_notebooksRoot, "*.jobs.yaml", SearchOption.AllDirectories)) {
                seen.Add(file);
                var mtime = File.GetLastWriteTimeUtc(file);
                if (!_cache.TryGetValue(file, out var entry) || entry.MTime != mtime) {
                    try {
                        entry = (mtime, JobsFile.Load(file, _notebooksRoot), null);
                    } catch (Exception e) {
                        entry = (mtime, null, e.Message);
                    }
                    _cache[file] = entry;
                }
                if (entry.Error != null) {
                    errors.Add($"{Relative(file)}: {entry.Error}");
                } else {
                    jobs.AddRange(entry.Jobs);
                }
            }
            foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList()) {
                _cache.Remove(stale);
            }
        }

        // Cross-file validation: names, notebooks, then the graph.
        var byName = new Dictionary<string, JobDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs) {
            if (byName.TryGetValue(job.Name, out var other)) {
                errors.Add($"{Relative(job.SourceFile)}: duplicate job name '{job.Name}' (also in {Relative(other.SourceFile)}).");
            } else {
                byName[job.Name] = job;
            }
        }
        foreach (var job in byName.Values) {
            if (!File.Exists(job.NotebookPath)) {
                errors.Add($"{Relative(job.SourceFile)}: job '{job.Name}' notebook not found: {job.NotebookRelative}");
            }
            if (job.Cron != null) {
                try {
                    Cronos.CronExpression.Parse(job.Cron);
                } catch (Cronos.CronFormatException e) {
                    errors.Add($"{Relative(job.SourceFile)}: job '{job.Name}' has an invalid cron '{job.Cron}': {e.Message}");
                }
            }
        }
        errors.AddRange(new JobGraph(byName.Values).Validate());

        return new CatalogResult { Jobs = byName.Values.ToList(), Errors = errors };
    }

    private string Relative(string path) =>
        Path.GetRelativePath(_notebooksRoot, path).Replace('\\', '/');
}

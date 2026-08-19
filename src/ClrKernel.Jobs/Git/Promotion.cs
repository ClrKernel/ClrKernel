using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ClrKernel.Jobs;

/// <summary>What the UI shows next to the Promote button.</summary>
public sealed class PromotionEligibility {
    public bool Eligible { get; init; }
    /// <summary>Every reason promotion is blocked — empty when eligible.</summary>
    public List<string> Reasons { get; init; } = new();
    /// <summary>The paths a promotion would carry (notebook + its jobs files).</summary>
    public List<string> Paths { get; init; } = new();
    /// <summary>The green runs serving as evidence, by job name.</summary>
    public Dictionary<string, Guid> EvidenceRuns { get; init; } = new();
    /// <summary>True when this would remove the notebook from prod (deleted in dev).</summary>
    public bool IsDeletion { get; init; }
}

/// <summary>
/// Dev→prod promotion. The unit is the notebook (plus its jobs files): sibling jobs
/// share the notebook, so promoting less than that would silently change their prod
/// behavior anyway. Eligibility demands proof the exact bytes being promoted are the
/// bytes that ran green: every enabled dev job on the notebook has a latest run that
/// Succeeded, without ad-hoc overrides, from a clean tree, and the files are
/// unchanged since that run's commit. The post-promotion prod graph must also
/// validate, so a dependent can't be promoted ahead of its dependency.
/// </summary>
public static class Promotion {
    public static async Task<PromotionEligibility> CheckAsync(
        JobCatalog catalog, GitService git, IRunStore store, string notebookPath) {
        var reasons = new List<string>();
        var evidence = new Dictionary<string, Guid>();
        var catalogResult = catalog.Load();

        var devJobs = catalogResult.In("dev")
            .Where(j => string.Equals(j.NotebookRelative, notebookPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var prodJobs = catalogResult.In("prod")
            .Where(j => string.Equals(j.NotebookRelative, notebookPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var devNotebookExists = File.Exists(Path.Combine(catalog.RootFor("dev"), notebookPath));
        var isDeletion = !devNotebookExists && prodJobs.Count > 0;

        // Everything the promotion carries: the notebook and every jobs file that
        // defines jobs for it, on either side (a yaml deleted in dev must travel too).
        var paths = new List<string> { notebookPath };
        paths.AddRange(devJobs.Select(j => j.SourceFileRelative));
        paths.AddRange(prodJobs.Select(j => j.SourceFileRelative));
        paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (!devNotebookExists && prodJobs.Count == 0 && devJobs.Count == 0) {
            reasons.Add($"Nothing to promote: '{notebookPath}' exists in neither environment.");
        }

        if (isDeletion) {
            // Deleting from prod needs no green run — only that nothing is executing.
            foreach (var job in prodJobs) {
                if (await store.HasActiveRunAsync("prod", job.Name)) {
                    reasons.Add($"'{job.Name}' has a prod run in flight.");
                }
            }
        } else {
            if (devJobs.Count == 0 && devNotebookExists) {
                reasons.Add("No jobs are defined for this notebook in dev — nothing proves it works.");
            }
            foreach (var job in devJobs.Where(j => j.Enabled)) {
                var latest = (await store.QueryRunsAsync(new RunQuery {
                    Environment = "dev",
                    JobName = job.Name,
                    Limit = 1,
                })).FirstOrDefault();

                if (latest == null) {
                    reasons.Add($"'{job.Name}' has never run in dev.");
                    continue;
                }
                if (latest.Status != RunStatus.Succeeded) {
                    reasons.Add($"'{job.Name}' latest dev run is {latest.Status}, not Succeeded.");
                    continue;
                }
                if (latest.HadOverrides) {
                    reasons.Add($"'{job.Name}' latest run used ad-hoc parameter overrides — " +
                        "run it as written before promoting.");
                    continue;
                }
                if (latest.WasDirty || latest.CommitSha == null) {
                    reasons.Add($"'{job.Name}' latest run executed uncommitted content — " +
                        "save (commit) and run again.");
                    continue;
                }
                if (!git.UnchangedBetween(latest.CommitSha, GitService.DevBranch,
                        notebookPath, job.SourceFileRelative)) {
                    reasons.Add($"'{job.Name}' files changed since its green run — run it again.");
                    continue;
                }
                if (await store.HasActiveRunAsync("dev", job.Name)
                    || await store.HasActiveRunAsync("prod", job.Name)) {
                    reasons.Add($"'{job.Name}' has a run in flight.");
                    continue;
                }
                evidence[job.Name] = latest.Id;
            }

            // The prod graph must still validate after the swap: prod jobs not from
            // this notebook + this notebook's dev jobs.
            var simulated = catalogResult.In("prod")
                .Where(j => !string.Equals(j.NotebookRelative, notebookPath, StringComparison.OrdinalIgnoreCase))
                .Concat(devJobs)
                .ToList();
            foreach (var error in new JobGraph(simulated).Validate()) {
                reasons.Add($"Promotion would break prod: {error}");
            }
        }

        // Anything at all changed?
        if (reasons.Count == 0 && git.NameStatus(paths.ToArray()).Count == 0) {
            reasons.Add("dev and prod are identical for these files — nothing to promote.");
        }

        return new PromotionEligibility {
            Eligible = reasons.Count == 0,
            Reasons = reasons,
            Paths = paths,
            EvidenceRuns = evidence,
            IsDeletion = isDeletion,
        };
    }

    /// <summary>
    /// Applies an eligible promotion: name-status diff between main and dev for the
    /// paths, checkout for adds/modifies, rm for deletions, one commit. Runs inside
    /// the git lock — and the catalog scans inside that same lock, so a scheduler
    /// tick can never see half a promotion.
    /// </summary>
    public static string Apply(GitService git, PromotionEligibility eligibility, string notebookPath) {
        return git.WithLock(() => {
            var changes = git.NameStatus(eligibility.Paths.ToArray());
            foreach (var (status, path) in changes) {
                if (status == 'D') {
                    git.RemoveFromProd(path);
                } else {
                    git.CheckoutIntoProd(path);
                }
            }
            var runs = eligibility.EvidenceRuns.Count > 0
                ? $" (runs {string.Join(", ", eligibility.EvidenceRuns.Values.Select(id => id.ToString("N")[..8]))})"
                : string.Empty;
            var verb = eligibility.IsDeletion ? "remove" : "promote";
            return git.CommitProd($"{verb}: {notebookPath}{runs} via web UI");
        });
    }
}

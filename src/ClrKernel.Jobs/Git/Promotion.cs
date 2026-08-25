using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;

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
    /// <summary>True when this would remove the notebook from prod (deleted in test).</summary>
    public bool IsDeletion { get; init; }
}

/// <summary>
/// Test→prod promotion. The unit is the notebook (plus its jobs files): sibling jobs
/// share the notebook, so promoting less than that would silently change their prod
/// behavior anyway. Eligibility demands proof the exact bytes being promoted are the
/// bytes that ran green: every enabled test job on the notebook has a latest run that
/// Succeeded, without ad-hoc overrides, from a clean tree, and the files are
/// unchanged since that run's commit. The post-promotion prod graph must also
/// validate, so a dependent can't be promoted ahead of its dependency.
/// </summary>
public static class Promotion {
    /// <param name="connections">
    /// The saved connections, when the caller has them. A notebook naming a private
    /// one cannot be promoted: private connections are never written into test or
    /// prod, so the scheduled run would fail looking for a name that is not there.
    /// Omitted, the check simply does not ask.
    /// </param>
    public static async Task<PromotionEligibility> CheckAsync(
        Project project, ProjectRegistry projects, IRunStore store, string notebookPath,
        ConnectionStore connections = null, IReadOnlyList<LanguageDescriptor> languages = null,
        IReadOnlyList<ConnectionProviderDescriptor> providers = null) {
        var catalog = projects.CatalogFor(project);
        var git = projects.GitFor(project);
        var reasons = new List<string>();
        var evidence = new Dictionary<string, Guid>();
        var catalogResult = catalog.Load();

        var testJobs = catalogResult.In(project.Slug, GitService.TestBranch)
            .Where(j => string.Equals(j.NotebookRelative, notebookPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var prodJobs = catalogResult.In(project.Slug, "prod")
            .Where(j => string.Equals(j.NotebookRelative, notebookPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var testNotebookExists = File.Exists(Path.Combine(catalog.RootFor(GitService.TestBranch), notebookPath));
        var isDeletion = !testNotebookExists && prodJobs.Count > 0;

        // Everything the promotion carries: the notebook and every jobs file that
        // defines jobs for it, on either side (a yaml deleted in test must travel too).
        var paths = new List<string> { notebookPath };
        paths.AddRange(testJobs.Select(j => j.SourceFileRelative));
        paths.AddRange(prodJobs.Select(j => j.SourceFileRelative));
        paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (!testNotebookExists && prodJobs.Count == 0 && testJobs.Count == 0) {
            reasons.Add($"Nothing to promote: '{notebookPath}' exists in neither environment.");
        }

        if (isDeletion) {
            // Deleting from prod needs no green run — only that nothing is executing.
            foreach (var job in prodJobs) {
                if (await store.HasActiveRunAsync(project.Slug, "prod", job.Name)) {
                    reasons.Add($"'{job.Name}' has a prod run in flight.");
                }
            }
        } else {
            if (testJobs.Count == 0 && testNotebookExists) {
                reasons.Add("No jobs are defined for this notebook in test — nothing proves it works.");
            }
            foreach (var job in testJobs.Where(j => j.Enabled)) {
                var latest = (await store.QueryRunsAsync(new RunQuery {
                    Project = project.Slug,
                    Environment = GitService.TestBranch,
                    JobName = job.Name,
                    Limit = 1,
                })).FirstOrDefault();

                if (latest == null) {
                    reasons.Add($"'{job.Name}' has never run in test.");
                    continue;
                }
                if (latest.Status != RunStatus.Succeeded) {
                    reasons.Add($"'{job.Name}' latest test run is {latest.Status}, not Succeeded.");
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
                if (!git.UnchangedBetween(latest.CommitSha, GitService.TestBranch,
                        notebookPath, job.SourceFileRelative)) {
                    reasons.Add($"'{job.Name}' files changed since its green run — run it again.");
                    continue;
                }
                if (await store.HasActiveRunAsync(project.Slug, GitService.TestBranch, job.Name)
                    || await store.HasActiveRunAsync(project.Slug, "prod", job.Name)) {
                    reasons.Add($"'{job.Name}' has a run in flight.");
                    continue;
                }
                evidence[job.Name] = latest.Id;
            }

            // The prod graph must still validate after the swap: prod jobs not from
            // this notebook + this notebook’s test jobs.
            var simulated = catalogResult.In(project.Slug, "prod")
                .Where(j => !string.Equals(j.NotebookRelative, notebookPath, StringComparison.OrdinalIgnoreCase))
                .Concat(testJobs)
                .ToList();
            foreach (var error in new JobGraph(simulated).Validate()) {
                reasons.Add($"Promotion would break prod: {error}");
            }
        }

        // Anything at all changed?
        foreach (var name in PrivateReferences(catalog, notebookPath, connections, languages, providers)) {
            reasons.Add($"'{notebookPath}' uses the private connection '{name}'. " +
                "Private connections resolve only for the person who owns them, so a scheduled " +
                "run would fail. Make it a shared connection, or point the notebook at one.");
        }

        if (reasons.Count == 0 && git.NameStatus(paths.ToArray()).Count == 0) {
            reasons.Add("test and prod are identical for these files — nothing to promote.");
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
    /// Applies an eligible promotion: name-status diff between main and test for the
    /// paths, checkout for adds/modifies, rm for deletions, one commit. Runs inside
    /// the git lock — and the catalog scans inside that same lock, so a scheduler
    /// tick can never see half a promotion.
    /// </summary>
    /// <summary>
    /// The private connections the test copy of a notebook names.
    /// <para>
    /// The test copy, because that is the one being promoted. The editor warns about
    /// the same thing much earlier, on the branch where it can still be fixed
    /// cheaply; this is the gate that stops it reaching production if nobody did.
    /// </para>
    /// </summary>
    private static IEnumerable<string> PrivateReferences(
        JobCatalog catalog, string notebookPath, ConnectionStore connections,
        IReadOnlyList<LanguageDescriptor> languages,
        IReadOnlyList<ConnectionProviderDescriptor> providers) {
        var path = Path.Combine(catalog.RootFor(GitService.TestBranch), notebookPath);
        if (connections == null || !File.Exists(path)) {
            return Array.Empty<string>();
        }
        return ConnectionReferences
            .In(File.ReadAllText(path), languages ?? Array.Empty<LanguageDescriptor>(),
                providers ?? Array.Empty<ConnectionProviderDescriptor>())
            .Where(name => connections.All.Any(c =>
                c.Scope == ConnectionScope.Private
                && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

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

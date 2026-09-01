using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClrKernel.Core.Primitives;
using ClrKernel.Core.Scripting;

namespace ClrKernel.Studio;

/// <summary>What the UI shows next to the Promote button.</summary>
public sealed class PromotionEligibility {
    public bool Eligible { get; init; }
    /// <summary>Every reason promotion is blocked — empty when eligible.</summary>
    public List<string> Reasons { get; init; } = new();
    /// <summary>The paths a promotion would carry (notebook + its jobs files).</summary>
    public List<string> Paths { get; init; } = new();
    /// <summary>The green runs serving as evidence, by job name.</summary>
    public Dictionary<string, Guid> EvidenceRuns { get; init; } = new();
    /// <summary>True when this removes something from prod rather than updating it.</summary>
    public bool IsDeletion { get; init; }
    /// <summary>
    /// Schedules this promotion switches off, so the confirmation can name each one
    /// and say when it would next have fired. Turning off a job somebody relies on
    /// deserves more than "Promote?".
    /// </summary>
    public List<UnscheduledJob> Unscheduling { get; init; } = new();
}

/// <summary>A job that will stop being scheduled, and when it would next have run.</summary>
public sealed class UnscheduledJob {
    public string Name { get; init; }
    public string Cron { get; init; }
    public DateTime? NextRun { get; init; }
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
        Project project, ProjectRegistry projects, IRunStore store, string path,
        ConnectionStore connections = null, IReadOnlyList<LanguageDescriptor> languages = null,
        IReadOnlyList<ConnectionProviderDescriptor> providers = null) {
        var catalog = projects.CatalogFor(project);
        var git = projects.GitFor(project);
        var reasons = new List<string>();
        var evidence = new Dictionary<string, Guid>();
        var catalogResult = catalog.Load();

        // Whichever half you asked about, both travel. A jobs file is named for its
        // notebook, so the pair is derivable from either — including when one of
        // them has already been deleted, which is the case that needs it most.
        if (PairFor(catalog, path) is not { } pair) {
            return Refused($"'{path}' is neither a notebook nor a jobs file.");
        }
        var (notebookPath, jobsPath) = pair;
        var paths = new List<string> { notebookPath, jobsPath };

        var testRoot = catalog.RootFor(GitService.TestBranch);
        var prodRoot = catalog.RootFor("prod");
        var testNotebook = File.Exists(Path.Combine(testRoot, notebookPath));
        var prodNotebook = File.Exists(Path.Combine(prodRoot, notebookPath));
        var testYaml = File.Exists(Path.Combine(testRoot, jobsPath));
        var prodYaml = File.Exists(Path.Combine(prodRoot, jobsPath));

        // Jobs are keyed on the yaml, not the notebook: it is the file that defines
        // them, and it is the one that may be gone.
        var testJobs = catalogResult.In(project.Slug, GitService.TestBranch)
            .Where(j => Same(j.SourceFileRelative, jobsPath)).ToList();
        var prodJobs = catalogResult.In(project.Slug, "prod")
            .Where(j => Same(j.SourceFileRelative, jobsPath)).ToList();

        if (!testNotebook && !testYaml && !prodNotebook && !prodYaml) {
            // The next step, not only the fact. Said to somebody who is very
            // likely looking at that file on their own branch, where "exists in
            // neither environment" is true and reads as a contradiction.
            return Refused(
                $"Nothing to promote: '{path}' exists in neither environment — push it to test first.");
        }

        // A schedule whose notebook is missing is the state this whole pairing rule
        // exists to prevent, so it is refused rather than promoted.
        if (testYaml && !testNotebook) {
            reasons.Add($"'{jobsPath}' has no notebook in test. Promote the pair once "
                + $"'{notebookPath}' is there, or delete the jobs file too.");
        }

        var deletingNotebook = !testNotebook && prodNotebook;
        var deletingYaml = !testYaml && prodYaml;
        var isDeletion = deletingNotebook || deletingYaml;
        var unscheduling = new List<UnscheduledJob>();

        if (isDeletion) {
            // Removing a schedule needs no green run: there is nothing left to
            // prove. It needs only that nothing is running, because promotion
            // rewrites the prod worktree underneath anything in flight.
            foreach (var job in prodJobs) {
                if (await store.HasActiveRunAsync(project.Slug, "prod", job.Name)) {
                    reasons.Add($"'{job.Name}' has a prod run in flight.");
                }
            }
            if (deletingYaml) {
                unscheduling.AddRange(prodJobs.Select(job => new UnscheduledJob {
                    Name = job.Name,
                    Cron = job.Cron,
                    NextRun = NextRun(job.Cron),
                }));
            }
        } else {
            if (testJobs.Count == 0 && testNotebook) {
                reasons.Add("No jobs are defined for this notebook in test — nothing proves it works.");
            }

            // Gate by what actually changed. A notebook edit changes what runs and
            // needs a green run at the current sha. A schedule edit changes when it
            // runs and needs only to be structurally sound — re-running a notebook
            // to prove a cron is valid proves nothing about the cron.
            //
            // Parameters are the exception, and deliberately: they are inputs to the
            // notebook, so changing them changes what runs even though only the yaml
            // moved. The gate already refuses a run that used ad-hoc overrides for
            // exactly this reason — the evidence has to be of the thing as written.
            var changed = git.NameStatus(paths.ToArray()).Select(c => c.Path).ToList();
            var notebookChanged = changed.Any(c => Same(c, notebookPath));
            var parametersChanged = ParametersDiffer(prodJobs, testJobs);
            var needsRun = notebookChanged || parametersChanged;

            if (needsRun) {
                foreach (var job in testJobs.Where(j => j.Enabled)) {
                    var latest = (await store.QueryRunsAsync(new RunQuery {
                        Projects = new[] { project.Slug },
                        Environment = GitService.TestBranch,
                        JobName = job.Name,
                        // The gate means the most recently *recorded* run, which is
                        // not quite "most recently started" for anything that queued.
                        Sort = RunSort.Created,
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
                            notebookPath, jobsPath)) {
                        reasons.Add($"'{job.Name}' files changed since its green run — run it again.");
                        continue;
                    }
                    evidence[job.Name] = latest.Id;
                }
            } else if (testYaml) {
                // Schedule-only: the file has to be sound, which is the same check
                // the push gate and the editor apply, so a promotion cannot carry
                // something they would have refused.
                foreach (var problem in JobsFileValidation.Check(
                             File.ReadAllText(Path.Combine(testRoot, jobsPath)), jobsPath)) {
                    reasons.Add($"{jobsPath} line {problem.Line}: {problem.Message}");
                }
                foreach (var error in catalogResult.Errors.Where(e => e.Contains(jobsPath))) {
                    reasons.Add(error);
                }
            }

            foreach (var job in testJobs) {
                if (await store.HasActiveRunAsync(project.Slug, GitService.TestBranch, job.Name)
                    || await store.HasActiveRunAsync(project.Slug, "prod", job.Name)) {
                    reasons.Add($"'{job.Name}' has a run in flight.");
                }
            }

            // Jobs that exist in prod and not in test are being switched off even
            // when the file itself survives.
            unscheduling.AddRange(prodJobs
                .Where(p => !testJobs.Any(t => Same(t.Name, p.Name)))
                .Select(job => new UnscheduledJob {
                    Name = job.Name,
                    Cron = job.Cron,
                    NextRun = NextRun(job.Cron),
                }));

            // The prod graph must still validate after the swap: prod jobs not from
            // this file + this file's test jobs.
            var simulated = catalogResult.In(project.Slug, "prod")
                .Where(j => !Same(j.SourceFileRelative, jobsPath))
                .Concat(testJobs)
                .ToList();
            foreach (var error in new JobGraph(simulated).Validate()) {
                reasons.Add($"Promotion would break prod: {error}");
            }
        }

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
            Unscheduling = unscheduling,
        };
    }

    private static PromotionEligibility Refused(string reason) =>
        new() { Eligible = false, Reasons = new List<string> { reason } };

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The notebook and jobs file for whichever half was asked about, as paths
    /// relative to the environment root. Neither is promised to exist — a deletion
    /// is exactly the case where one of them does not.
    /// </summary>
    private static (string Notebook, string Jobs)? PairFor(JobCatalog catalog, string path) {
        if (JobsPairing.IsJobsFile(path)) {
            var name = JobsPairing.BaseNameOfJobsFile(path);
            var directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;
            // The notebook could be any of four extensions, so look for the one
            // that is actually there — in test first, then prod for a deletion.
            foreach (var root in new[] { catalog.RootFor(GitService.TestBranch), catalog.RootFor("prod") }) {
                if (JobsPairing.NotebookFor(Path.Combine(root, path)) is { } found) {
                    return (Relative(root, found), path);
                }
            }
            // None on disk: name the default so a pair of deletions still resolves.
            return (Join(directory, name + ".nb.md"), path);
        }
        if (NotebookTree.IsNotebook(path)) {
            return (path, JobsPairing.JobsFileFor(path).Replace('\\', '/'));
        }
        return null;
    }

    private static string Join(string directory, string name) =>
        string.IsNullOrEmpty(directory) ? name : $"{directory}/{name}";

    private static string Relative(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/');

    /// <summary>
    /// Whether any job's effective parameters differ between the environments —
    /// defaults merged in, so a change to `defaults.parameters` counts.
    /// </summary>
    private static bool ParametersDiffer(
        IReadOnlyList<JobDefinition> prodJobs, IReadOnlyList<JobDefinition> testJobs) {
        foreach (var test in testJobs) {
            var prod = prodJobs.FirstOrDefault(p => Same(p.Name, test.Name));
            if (prod == null) {
                // A new job has never run; the evidence loop says so more clearly
                // than "parameters changed" would.
                continue;
            }
            if (prod.Parameters.Count != test.Parameters.Count
                || prod.Parameters.Any(kv => !test.Parameters.TryGetValue(kv.Key, out var value)
                    || !Equals($"{value}", $"{kv.Value}"))) {
                return true;
            }
        }
        return false;
    }

    /// <summary>When this cron next comes round, for a confirmation that can say so.</summary>
    private static DateTime? NextRun(string cron) {
        if (string.IsNullOrWhiteSpace(cron)) {
            return null;
        }
        try {
            return Cronos.CronExpression.Parse(cron).GetNextOccurrence(DateTime.UtcNow);
        } catch (Cronos.CronFormatException) {
            return null;
        }
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

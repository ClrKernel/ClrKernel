using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ClrKernel.Studio;

/// <summary>One run that is about to be started again, and what it will run.</summary>
public sealed class RerunTarget {
    /// <summary>The run being repeated. Becomes the new run's <c>CausedByRunId</c>.</summary>
    public Guid OriginalRunId { get; init; }
    public JobDefinition Job { get; init; }
    /// <summary>The commit this will run, for the confirmation and the log.</summary>
    public string Sha { get; init; }
    /// <summary>
    /// The detached checkout the job was read out of, for an exact-version rerun.
    /// Null for a rerun at branch HEAD. Whoever launches the run removes it when the
    /// run finishes.
    /// </summary>
    public string WorktreePath { get; init; }
}

/// <summary>A run that will not be started again, and the reason in words.</summary>
public sealed record RerunRefusal(Guid RunId, string Reason);

public sealed class RerunPlan {
    public List<RerunTarget> Targets { get; } = new();
    public List<RerunRefusal> Refused { get; } = new();
    /// <summary>The one project every target belongs to; null when the plan is empty.</summary>
    public string Project { get; set; }
    /// <summary>The one branch every target belongs to — what the confirmation names.</summary>
    public string Environment { get; set; }
    /// <summary>Set when the request itself is wrong, rather than individual runs.</summary>
    public string Error { get; set; }
}

/// <summary>
/// Deciding what "run that again" means.
/// <para>
/// Two different things wear the same button. <b>At branch HEAD</b> is what you want
/// after a fix — the whole point of fixing something is that the next run is not the
/// one that failed. <b>At the recorded commit</b> is for reproducing a failure, and
/// is a different, deliberate act. The spec is right that getting this wrong
/// silently is worse than either choice, so the caller says which, and this refuses
/// rather than approximates when the recorded version cannot honestly be reproduced.
/// </para>
/// </summary>
public static class Rerun {
    /// <summary>
    /// Why this run cannot be reproduced exactly, or null when it can.
    /// <para>
    /// Each of these would otherwise produce a run labelled "the exact failed
    /// version" that is nothing of the kind.
    /// </para>
    /// </summary>
    public static string CannotReproduce(Run run) {
        if (string.IsNullOrEmpty(run.CommitSha)) {
            // Non-git installs never capture one, and neither does a user branch.
            return "no commit was recorded for that run, so there is no exact version to go back to.";
        }
        if (run.WasDirty) {
            return "that run had uncommitted changes under it — the commit it recorded is not what ran.";
        }
        if (run.HadOverrides) {
            // The bool is recorded; the overrides themselves are not. Reproducing
            // without them would be a different run wearing the same label.
            return "that run used one-off parameter overrides, and they were not kept.";
        }
        return null;
    }

    /// <summary>
    /// Turns a set of recorded runs into the set of jobs to start.
    /// <para>
    /// Refuses a selection spanning more than one project or branch. The grid spans
    /// both by design, so a filtered selection mixes them easily — and a confirmation
    /// that has to name <em>the</em> branch cannot name three. It also keeps the
    /// production permission one check rather than one per row.
    /// </para>
    /// </summary>
    public static async Task<RerunPlan> PlanAsync(
        IReadOnlyList<Run> runs, bool exactVersion,
        JobCatalog catalog, GitService git, IRunStore store) {
        var plan = new RerunPlan();
        if (runs.Count == 0) {
            plan.Error = "Nothing was selected.";
            return plan;
        }

        var projects = runs.Select(r => r.Project ?? ProjectRegistry.DefaultSlug).Distinct().ToList();
        var environments = runs.Select(r => r.Environment).Distinct().ToList();
        if (projects.Count > 1 || environments.Count > 1) {
            plan.Error =
                "A rerun covers one project and one branch at a time. Filter the grid to one and try again.";
            return plan;
        }
        plan.Project = projects[0];
        plan.Environment = environments[0];

        if (exactVersion && runs.Count > 1) {
            // Not a limitation to work around later: after a fix you want HEAD, and
            // reproducing a specific failure is a single deliberate act. Fifty
            // simultaneous checkouts of the past would be a lot of disk for a
            // question nobody asks in bulk.
            plan.Error = "Rerunning the exact recorded version is one run at a time.";
            return plan;
        }

        var loaded = catalog.Load();

        // A job that failed nightly for a week is seven rows. Selecting all of them
        // must not be one start and six "already running" refusals — and the
        // confirmation's count has to be the number of runs it will actually make.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs.OrderByDescending(r => r.CreatedAt)) {
            if (!seen.Add(run.JobName ?? string.Empty)) {
                continue;
            }
            if (await store.HasActiveRunAsync(plan.Project, plan.Environment, run.JobName)) {
                plan.Refused.Add(new RerunRefusal(run.Id, $"'{run.JobName}' already has a run in flight."));
                continue;
            }

            if (!exactVersion) {
                var job = loaded.Find(plan.Project, plan.Environment, run.JobName);
                if (job == null) {
                    plan.Refused.Add(new RerunRefusal(run.Id,
                        $"'{run.JobName}' no longer exists in {plan.Environment}. " +
                        "Rerun the exact recorded version instead."));
                    continue;
                }
                plan.Targets.Add(new RerunTarget {
                    OriginalRunId = run.Id,
                    Job = job,
                    Sha = git?.HeadSha(plan.Environment),
                });
                continue;
            }

            if (CannotReproduce(run) is { } why) {
                plan.Refused.Add(new RerunRefusal(run.Id, $"Cannot reproduce that run: {why}"));
                continue;
            }
            if (git == null) {
                plan.Refused.Add(new RerunRefusal(run.Id, "This server is not using the git workflow."));
                continue;
            }

            string worktree = null;
            try {
                worktree = git.AddRerunWorktree(run.CommitSha);
                var job = JobAt(worktree, run);
                if (job == null) {
                    git.RemoveRerunWorktree(worktree);
                    plan.Refused.Add(new RerunRefusal(run.Id,
                        $"'{run.JobName}' is not in {run.CommitSha[..Math.Min(8, run.CommitSha.Length)]}."));
                    continue;
                }
                plan.Targets.Add(new RerunTarget {
                    OriginalRunId = run.Id,
                    Job = job,
                    Sha = run.CommitSha,
                    WorktreePath = worktree,
                });
            } catch (Exception e) {
                git.RemoveRerunWorktree(worktree);
                plan.Refused.Add(new RerunRefusal(run.Id, $"Could not check out that commit: {e.Message}"));
            }
        }

        return plan;
    }

    /// <summary>
    /// The job as it was, read out of a checkout of the past.
    /// <para>
    /// Straight to the paired jobs file rather than through a catalog scan: the pair
    /// is derived from the notebook's name, so there is exactly one file to read, and
    /// a whole-tree scan would also drag in every unrelated validation error the
    /// repository had that day.
    /// </para>
    /// </summary>
    private static JobDefinition JobAt(string root, Run run) {
        if (string.IsNullOrEmpty(run.NotebookPath)) {
            return null;
        }
        var yaml = Path.Combine(root, JobsPairing.JobsFileFor(run.NotebookPath));
        if (!File.Exists(yaml)) {
            return null;
        }
        var job = JobsFile.Load(yaml, root)
            .FirstOrDefault(j => string.Equals(j.Name, run.JobName, StringComparison.OrdinalIgnoreCase));
        if (job == null) {
            return null;
        }
        // The catalog stamps these; nothing scanned this tree, so it is done here.
        // They are the run store's keys — a rerun that recorded a different
        // environment would be a run of a job nobody could find again.
        job.Project = run.Project ?? ProjectRegistry.DefaultSlug;
        job.Environment = run.Environment;
        return job;
    }
}

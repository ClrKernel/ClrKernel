using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClrKernel.Jobs;

/// <summary>
/// Run-history persistence. Jobs themselves live in *.jobs.yaml files — the store
/// only records executions. Artifacts (executed .ipynb, run.log) always live on
/// disk; the store keeps their relative paths.
/// </summary>
public interface IRunStore {
    Task<Run> CreateRunAsync(Run run);
    Task UpdateRunAsync(Run run);
    Task<Run> GetRunAsync(Guid id);
    Task<IReadOnlyList<Run>> QueryRunsAsync(RunQuery query);
    /// <param name="projects">
    /// Limit the count to these projects; null counts every one. The dashboard is a
    /// number, and a number is as much of a leak as a list.
    /// </param>
    Task<RunStats> GetStatsAsync(TimeSpan window, IReadOnlyCollection<string> projects = null);

    Task SaveCellsAsync(Guid runId, IReadOnlyList<RunCell> cells);
    Task UpdateCellAsync(RunCell cell);
    Task<IReadOnlyList<RunCell>> GetCellsAsync(Guid runId);

    // Every lookup below is keyed by (project, environment, job): job names are
    // unique within one environment of one project and nowhere wider than that.

    /// <summary>The most recent Succeeded run of a job, or null (chain freshness + lineage).</summary>
    Task<Run> GetLastSuccessfulRunAsync(string project, string environment, string jobName);
    /// <summary>True when the job has a Pending or Running run (schedule overlap skip).</summary>
    Task<bool> HasActiveRunAsync(string project, string environment, string jobName);
    Task<DateTime?> GetLastTriggerAsync(string project, string environment, string jobName);
    Task SetLastTriggerAsync(string project, string environment, string jobName, DateTime triggeredAt);

    /// <summary>Marks rows stuck in Pending/Running (from a crash) as Failed. Returns the count.</summary>
    Task<int> MarkOrphansFailedAsync();
}

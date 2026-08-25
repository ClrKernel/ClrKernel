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

    // --- driving a notebook by hand in test or prod -------------------------
    //
    // Separate from runs on purpose: this is who did what, not what the schedule
    // did, and nothing here may ever answer "has this job run in test".

    Task StartManualRunAsync(ManualRun run);
    Task FinishManualRunAsync(Guid id, string outcome, string errorSummary, DateTime finishedAt);
    Task<IReadOnlyList<ManualRun>> QueryManualRunsAsync(ManualRunQuery query);

    /// <summary>Records a finished statement against a shared connection. Written
    /// after the fact rather than opened and closed like a run: a query is one round
    /// trip, and nobody polls it.</summary>
    Task RecordQueryAsync(QueryAudit audit);

    Task<IReadOnlyList<QueryAudit>> QueryAuditAsync(QueryAuditQuery query);

    /// <summary>Creates or replaces a saved query.</summary>
    Task SaveQueryAsync(SavedQuery query);

    /// <summary>The saved queries one person may see: every shared one, plus their
    /// own.</summary>
    Task<IReadOnlyList<SavedQuery>> SavedQueriesAsync(SavedQueryFilter filter);

    /// <summary>One saved query, or null when it does not exist or is not this
    /// person's to see — those two answer the same, deliberately.</summary>
    Task<SavedQuery> SavedQueryAsync(Guid id, Guid viewerId);

    Task<bool> DeleteSavedQueryAsync(Guid id);

    /// <summary>Marks rows stuck in Pending/Running (from a crash) as Failed. Returns the count.</summary>
    Task<int> MarkOrphansFailedAsync();
}

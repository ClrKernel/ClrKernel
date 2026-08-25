using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace ClrKernel.Jobs;

/// <summary>
/// <see cref="IRunStore"/> over EF Core. A fresh context per operation — the store is
/// long-lived and called from concurrent runs, so no shared change tracker.
/// </summary>
public sealed class EfRunStore : IRunStore {
    private readonly Func<RunsDbContext> _contextFactory;

    /// <param name="contextFactory">
    /// Creates a context for the configured provider. A fresh one per operation:
    /// the store is long-lived and called from concurrent runs.
    /// </param>
    public EfRunStore(Func<RunsDbContext> contextFactory) {
        _contextFactory = contextFactory;
    }

    /// <summary>A SQLite-backed store for a database file path (the default backend).</summary>
    public static EfRunStore Sqlite(string dbPath) {
        var options = new DbContextOptionsBuilder<SqliteRunsDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new EfRunStore(() => new SqliteRunsDbContext(options));
    }

    /// <summary>Applies pending migrations (creates the database when absent).</summary>
    public void Migrate() {
        using var db = _contextFactory();
        db.Database.Migrate();
    }

    /// <summary>
    /// Empties every table. Test support: the contract suite runs against a scratch
    /// database that must start clean, and each backend needs the same guarantee
    /// without the tests knowing which provider they are on.
    /// </summary>
    internal void ClearForTests() {
        using var db = _contextFactory();
        db.Database.ExecuteSqlRaw("DELETE FROM run_cells");
        db.Database.ExecuteSqlRaw("DELETE FROM runs");
        db.Database.ExecuteSqlRaw("DELETE FROM job_trigger_state");
    }

    public async Task<Run> CreateRunAsync(Run run) {
        using var db = _contextFactory();
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    public async Task UpdateRunAsync(Run run) {
        using var db = _contextFactory();
        db.Runs.Update(run);
        await db.SaveChangesAsync();
    }

    public async Task<Run> GetRunAsync(Guid id) {
        using var db = _contextFactory();
        return await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<IReadOnlyList<Run>> QueryRunsAsync(RunQuery query) {
        using var db = _contextFactory();
        var runs = db.Runs.AsNoTracking();
        if (!string.IsNullOrEmpty(query.Project)) {
            runs = runs.Where(r => r.Project == query.Project);
        }
        if (!string.IsNullOrEmpty(query.Environment)) {
            runs = runs.Where(r => r.Environment == query.Environment);
        }
        if (!string.IsNullOrEmpty(query.JobName)) {
            runs = runs.Where(r => r.JobName == query.JobName);
        }
        if (query.Status is { } status) {
            runs = runs.Where(r => r.Status == status);
        }
        return await runs.OrderByDescending(r => r.CreatedAt)
            .Skip(query.Offset).Take(query.Limit).ToListAsync();
    }

    public async Task<RunStats> GetStatsAsync(
        TimeSpan window, IReadOnlyCollection<string> projects = null) {
        using var db = _contextFactory();
        var since = DateTime.UtcNow - window;
        var runs = db.Runs.AsNoTracking().Where(r => r.CreatedAt >= since);
        if (projects != null) {
            runs = runs.Where(r => projects.Contains(r.Project));
        }
        var counts = await runs
            .GroupBy(r => r.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync();
        return new RunStats {
            Total = counts.Sum(c => c.Count),
            Succeeded = counts.Where(c => c.Key == RunStatus.Succeeded).Sum(c => c.Count),
            Failed = counts.Where(c => c.Key is RunStatus.Failed or RunStatus.TimedOut).Sum(c => c.Count),
            ByStatus = counts.ToDictionary(c => c.Key.ToString(), c => c.Count),
        };
    }

    public async Task SaveCellsAsync(Guid runId, IReadOnlyList<RunCell> cells) {
        using var db = _contextFactory();
        db.RunCells.AddRange(cells);
        await db.SaveChangesAsync();
    }

    public async Task UpdateCellAsync(RunCell cell) {
        using var db = _contextFactory();
        db.RunCells.Update(cell);
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<RunCell>> GetCellsAsync(Guid runId) {
        using var db = _contextFactory();
        return await db.RunCells.AsNoTracking()
            .Where(c => c.RunId == runId).OrderBy(c => c.CellIndex).ToListAsync();
    }

    public async Task StartManualRunAsync(ManualRun run) {
        using var db = _contextFactory();
        db.ManualRuns.Add(run);
        await db.SaveChangesAsync();
    }

    public async Task FinishManualRunAsync(
        Guid id, string outcome, string errorSummary, DateTime finishedAt) {
        using var db = _contextFactory();
        await db.ManualRuns.Where(r => r.Id == id).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.Outcome, outcome)
            .SetProperty(r => r.ErrorSummary, errorSummary)
            .SetProperty(r => r.FinishedAt, finishedAt));
    }

    public async Task<IReadOnlyList<ManualRun>> QueryManualRunsAsync(ManualRunQuery query) {
        using var db = _contextFactory();
        var runs = db.ManualRuns.AsNoTracking();
        if (!string.IsNullOrEmpty(query.Project)) {
            runs = runs.Where(r => r.Project == query.Project);
        }
        if (!string.IsNullOrEmpty(query.Environment)) {
            runs = runs.Where(r => r.Environment == query.Environment);
        }
        if (!string.IsNullOrEmpty(query.NotebookPath)) {
            runs = runs.Where(r => r.NotebookPath == query.NotebookPath);
        }
        return await runs.OrderByDescending(r => r.StartedAt).Take(query.Limit).ToListAsync();
    }

    public async Task RecordQueryAsync(QueryAudit audit) {
        using var db = _contextFactory();
        db.QueryAudits.Add(audit);
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<QueryAudit>> QueryAuditAsync(QueryAuditQuery query) {
        using var db = _contextFactory();
        var audits = db.QueryAudits.AsNoTracking();
        if (!string.IsNullOrEmpty(query.ConnectionId)) {
            audits = audits.Where(a => a.ConnectionId == query.ConnectionId);
        }
        // The visibility rule, in the one place a route cannot skip: a private
        // connection's rows are its actor's alone, and an admin reads everybody's
        // shared ones.
        audits = audits.Where(a =>
            a.ActorId == query.ViewerId
            || (a.Scope != "private" && query.ViewerIsAdmin));
        return await audits.OrderByDescending(a => a.StartedAt).Take(query.Limit).ToListAsync();
    }

    public async Task SaveQueryAsync(SavedQuery query) {
        using var db = _contextFactory();
        var existing = await db.SavedQueries.FindAsync(query.Id);
        if (existing == null) {
            db.SavedQueries.Add(query);
        } else {
            db.Entry(existing).CurrentValues.SetValues(query);
        }
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<SavedQuery>> SavedQueriesAsync(SavedQueryFilter filter) {
        using var db = _contextFactory();
        return await db.SavedQueries.AsNoTracking()
            .Where(q => q.Scope != "private" || q.OwnerId == filter.ViewerId)
            .OrderBy(q => q.Scope).ThenBy(q => q.Name)
            .Take(filter.Limit)
            .ToListAsync();
    }

    public async Task<SavedQuery> SavedQueryAsync(Guid id, Guid viewerId) {
        using var db = _contextFactory();
        return await db.SavedQueries.AsNoTracking().FirstOrDefaultAsync(q =>
            q.Id == id && (q.Scope != "private" || q.OwnerId == viewerId));
    }

    public async Task<bool> DeleteSavedQueryAsync(Guid id) {
        using var db = _contextFactory();
        return await db.SavedQueries.Where(q => q.Id == id).ExecuteDeleteAsync() > 0;
    }

    public async Task<Run> GetLastSuccessfulRunAsync(string project, string environment, string jobName) {
        using var db = _contextFactory();
        return await db.Runs.AsNoTracking()
            .Where(r => r.Project == project && r.Environment == environment && r.JobName == jobName
                && r.Status == RunStatus.Succeeded)
            .OrderByDescending(r => r.FinishedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<bool> HasActiveRunAsync(string project, string environment, string jobName) {
        using var db = _contextFactory();
        return await db.Runs.AsNoTracking().AnyAsync(r =>
            r.Project == project && r.Environment == environment && r.JobName == jobName
            && (r.Status == RunStatus.Pending || r.Status == RunStatus.Running));
    }

    public async Task<DateTime?> GetLastTriggerAsync(string project, string environment, string jobName) {
        using var db = _contextFactory();
        var state = await db.JobTriggerStates.AsNoTracking()
            .FirstOrDefaultAsync(s =>
                s.Project == project && s.Environment == environment && s.JobName == jobName);
        return state?.LastTriggerAt;
    }

    public async Task SetLastTriggerAsync(
        string project, string environment, string jobName, DateTime triggeredAt) {
        using var db = _contextFactory();
        var state = await db.JobTriggerStates
            .FirstOrDefaultAsync(s =>
                s.Project == project && s.Environment == environment && s.JobName == jobName);
        if (state == null) {
            db.JobTriggerStates.Add(new JobTriggerState {
                Project = project,
                Environment = environment,
                JobName = jobName,
                LastTriggerAt = triggeredAt,
            });
        } else {
            state.LastTriggerAt = triggeredAt;
        }
        await db.SaveChangesAsync();
    }

    public async Task<int> MarkOrphansFailedAsync() {
        using var db = _contextFactory();
        var orphans = await db.Runs
            .Where(r => r.Status == RunStatus.Pending || r.Status == RunStatus.Running)
            .ToListAsync();
        foreach (var run in orphans) {
            run.Status = RunStatus.Failed;
            run.ErrorSummary = "Orphaned by shutdown.";
            run.FinishedAt ??= DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        return orphans.Count;
    }
}

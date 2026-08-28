using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace ClrKernel.Studio;

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
        db.Database.ExecuteSqlRaw("DELETE FROM promotions");
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
        var projects = query.Projects;
        var runs = db.Runs.AsNoTracking().Where(r => projects.Contains(r.Project));
        if (!string.IsNullOrEmpty(query.Environment)) {
            runs = runs.Where(r => r.Environment == query.Environment);
        }
        if (!string.IsNullOrEmpty(query.JobName)) {
            runs = runs.Where(r => r.JobName == query.JobName);
        }
        if (!string.IsNullOrEmpty(query.NotebookPath)) {
            runs = runs.Where(r => r.NotebookPath == query.NotebookPath);
        }
        if (query.Status is { } status) {
            runs = runs.Where(r => r.Status == status);
        }
        if (query.Trigger is { } trigger) {
            runs = runs.Where(r => r.Trigger == trigger);
        }
        if (query.ActorId is { } actor) {
            runs = runs.Where(r => r.ActorId == actor);
        }
        // Against the same instant the grid sorts on, so "runs since 9am" and
        // "sorted by started" cannot disagree about which column they mean.
        if (query.Since is { } since) {
            runs = runs.Where(r => (r.StartedAt ?? r.CreatedAt) >= since);
        }
        if (query.Until is { } until) {
            runs = runs.Where(r => (r.StartedAt ?? r.CreatedAt) < until);
        }
        return await Ordered(runs, query).Skip(query.Offset).Take(query.Limit).ToListAsync();
    }

    /// <summary>
    /// Applies the sort, always ending on CreatedAt.
    /// <para>
    /// The tiebreaker is not decoration. Paging is Skip/Take over a fresh query per
    /// page, so any two rows the sort calls equal may come back in either order — and
    /// a row that moves between page 1 and page 2 is a row the reader never sees.
    /// </para>
    /// <para>
    /// Started coalesces to CreatedAt rather than sorting nulls, because where a NULL
    /// lands in an ORDER BY is a per-provider decision (PostgreSQL puts them last
    /// ascending, SQL Server puts them first) and a grid whose pending runs jump ends
    /// to end when you change database is a bug nobody would think to look for.
    /// </para>
    /// </summary>
    private static IOrderedQueryable<Run> Ordered(IQueryable<Run> runs, RunQuery query) {
        var ascending = query.Ascending;
        IOrderedQueryable<Run> sorted = query.Sort switch {
            RunSort.Created => By(runs, r => r.CreatedAt, ascending),
            RunSort.Project => By(runs, r => r.Project, ascending),
            RunSort.JobName => By(runs, r => r.JobName, ascending),
            RunSort.Environment => By(runs, r => r.Environment, ascending),
            RunSort.Status => By(runs, r => r.Status, ascending),
            RunSort.Trigger => By(runs, r => r.Trigger, ascending),
            _ => By(runs, r => r.StartedAt ?? r.CreatedAt, ascending),
        };
        return query.Sort == RunSort.Created
            ? sorted.ThenByDescending(r => r.Id)
            : sorted.ThenByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id);
    }

    private static IOrderedQueryable<Run> By<TKey>(
        IQueryable<Run> runs, System.Linq.Expressions.Expression<Func<Run, TKey>> key, bool ascending) =>
        ascending ? runs.OrderBy(key) : runs.OrderByDescending(key);

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
        // One more grouped pass rather than pulling every row back to count them
        // here: the window is a day or a year, and "a year" is the whole table.
        var perProject = await runs
            .GroupBy(r => new { r.Project, r.Status })
            .Select(g => new { g.Key.Project, g.Key.Status, Count = g.Count() })
            .ToListAsync();
        return new RunStats {
            Total = counts.Sum(c => c.Count),
            Succeeded = counts.Where(c => c.Key == RunStatus.Succeeded).Sum(c => c.Count),
            Failed = counts.Where(c => c.Key is RunStatus.Failed or RunStatus.TimedOut).Sum(c => c.Count),
            ByStatus = counts.ToDictionary(c => c.Key.ToString(), c => c.Count),
            ByProject = perProject
                .GroupBy(r => r.Project ?? ProjectRegistry.DefaultSlug)
                .Select(g => new ProjectRunStats {
                    Project = g.Key,
                    Total = g.Sum(r => r.Count),
                    Succeeded = g.Where(r => r.Status == RunStatus.Succeeded).Sum(r => r.Count),
                    Failed = g.Where(r => r.Status is RunStatus.Failed or RunStatus.TimedOut)
                        .Sum(r => r.Count),
                })
                .OrderByDescending(p => p.Total)
                .ToList(),
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

    public async Task RecordPromotionAsync(PromotionAudit audit) {
        using var db = _contextFactory();
        db.PromotionAudits.Add(audit);
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<PromotionAudit>> PromotionAuditAsync(PromotionAuditQuery query) {
        using var db = _contextFactory();
        var audits = db.PromotionAudits.AsNoTracking();
        if (!string.IsNullOrEmpty(query.Project)) {
            audits = audits.Where(a => a.Project == query.Project);
        }
        if (query.UnschedulesOnly) {
            audits = audits.Where(a => a.Unscheduled != null && a.Unscheduled != "");
        }
        return await audits
            .OrderByDescending(a => a.PromotedAt)
            .Take(Math.Clamp(query.Limit, 1, 500))
            .ToListAsync();
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

    public async Task<IReadOnlyList<string>> PurgeRunsAsync(DateTime before) {
        using var db = _contextFactory();
        // The newest run of each job, whatever its age. Read as ids rather than
        // filtered around in the delete query: three providers, and "not the max of
        // its group" is where a LINQ translation quietly becomes a table scan or a
        // NotSupportedException.
        var keep = (await db.Runs.AsNoTracking()
            .GroupBy(r => new { r.Project, r.Environment, r.JobName })
            .Select(g => g.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                .Select(r => r.Id).First())
            .ToListAsync()).ToHashSet();

        var stale = await db.Runs
            .Where(r => r.FinishedAt != null && r.FinishedAt < before)
            .ToListAsync();
        var going = stale.Where(r => !keep.Contains(r.Id)).ToList();
        if (going.Count == 0) {
            return Array.Empty<string>();
        }

        var ids = going.Select(r => r.Id).ToHashSet();
        db.RunCells.RemoveRange(await db.RunCells.Where(c => ids.Contains(c.RunId)).ToListAsync());
        db.Runs.RemoveRange(going);
        await db.SaveChangesAsync();
        return going.Select(r => r.ArtifactPath).Where(p => p != null).ToList();
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

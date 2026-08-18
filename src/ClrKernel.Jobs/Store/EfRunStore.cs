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
    private readonly DbContextOptions<RunsDbContext> _options;

    public EfRunStore(DbContextOptions<RunsDbContext> options) {
        _options = options;
    }

    /// <summary>SQLite options for a database file path (the default backend).</summary>
    public static DbContextOptions<RunsDbContext> SqliteOptions(string dbPath) =>
        new DbContextOptionsBuilder<RunsDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

    /// <summary>Applies pending migrations (creates the database when absent).</summary>
    public void Migrate() {
        using var db = new RunsDbContext(_options);
        db.Database.Migrate();
    }

    public async Task<Run> CreateRunAsync(Run run) {
        using var db = new RunsDbContext(_options);
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    public async Task UpdateRunAsync(Run run) {
        using var db = new RunsDbContext(_options);
        db.Runs.Update(run);
        await db.SaveChangesAsync();
    }

    public async Task<Run> GetRunAsync(Guid id) {
        using var db = new RunsDbContext(_options);
        return await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<IReadOnlyList<Run>> QueryRunsAsync(RunQuery query) {
        using var db = new RunsDbContext(_options);
        var runs = db.Runs.AsNoTracking();
        if (!string.IsNullOrEmpty(query.JobName)) {
            runs = runs.Where(r => r.JobName == query.JobName);
        }
        if (query.Status is { } status) {
            runs = runs.Where(r => r.Status == status);
        }
        return await runs.OrderByDescending(r => r.CreatedAt)
            .Skip(query.Offset).Take(query.Limit).ToListAsync();
    }

    public async Task<RunStats> GetStatsAsync(TimeSpan window) {
        using var db = new RunsDbContext(_options);
        var since = DateTime.UtcNow - window;
        var counts = await db.Runs.AsNoTracking()
            .Where(r => r.CreatedAt >= since)
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
        using var db = new RunsDbContext(_options);
        db.RunCells.AddRange(cells);
        await db.SaveChangesAsync();
    }

    public async Task UpdateCellAsync(RunCell cell) {
        using var db = new RunsDbContext(_options);
        db.RunCells.Update(cell);
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<RunCell>> GetCellsAsync(Guid runId) {
        using var db = new RunsDbContext(_options);
        return await db.RunCells.AsNoTracking()
            .Where(c => c.RunId == runId).OrderBy(c => c.CellIndex).ToListAsync();
    }

    public async Task<DateTime?> GetLastSuccessAsync(string jobName) {
        using var db = new RunsDbContext(_options);
        return await db.Runs.AsNoTracking()
            .Where(r => r.JobName == jobName && r.Status == RunStatus.Succeeded)
            .MaxAsync(r => (DateTime?)r.FinishedAt);
    }

    public async Task<DateTime?> GetLastTriggerAsync(string jobName) {
        using var db = new RunsDbContext(_options);
        var state = await db.JobTriggerStates.AsNoTracking().FirstOrDefaultAsync(s => s.JobName == jobName);
        return state?.LastTriggerAt;
    }

    public async Task SetLastTriggerAsync(string jobName, DateTime triggeredAt) {
        using var db = new RunsDbContext(_options);
        var state = await db.JobTriggerStates.FirstOrDefaultAsync(s => s.JobName == jobName);
        if (state == null) {
            db.JobTriggerStates.Add(new JobTriggerState { JobName = jobName, LastTriggerAt = triggeredAt });
        } else {
            state.LastTriggerAt = triggeredAt;
        }
        await db.SaveChangesAsync();
    }

    public async Task<int> MarkOrphansFailedAsync() {
        using var db = new RunsDbContext(_options);
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

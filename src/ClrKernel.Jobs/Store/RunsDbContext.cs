using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClrKernel.Jobs;

/// <summary>
/// The run-history EF model. Tables and columns are snake_case and enums are stored
/// as their names, because this history is meant to be queried directly —
/// <c>select job_name, status from runs</c> should work in any client without
/// quoting or a lookup table. Provider selection (sqlite/sqlserver/postgres) happens
/// where the options are built; per-provider migrations live under Store/Migrations/.
/// </summary>
public abstract class RunsDbContext : DbContext {
    protected RunsDbContext(DbContextOptions options) : base(options) { }

    public DbSet<Run> Runs => Set<Run>();
    public DbSet<RunCell> RunCells => Set<RunCell>();
    public DbSet<JobTriggerState> JobTriggerStates => Set<JobTriggerState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<Run>(run => {
            run.ToTable("runs");
            run.HasKey(r => r.Id);
            run.Property(r => r.Id).HasColumnName("id");
            run.Property(r => r.JobName).HasColumnName("job_name").IsRequired();
            run.Property(r => r.NotebookPath).HasColumnName("notebook_path");
            run.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
            // Not "trigger": that is a reserved word in T-SQL, so `select trigger`
            // would need bracketing — exactly what this naming is meant to avoid.
            run.Property(r => r.Trigger).HasColumnName("trigger_type").HasConversion<string>().HasMaxLength(16);
            run.Property(r => r.CausedByRunId).HasColumnName("caused_by_run_id");
            run.Property(r => r.Attempt).HasColumnName("attempt");
            run.Property(r => r.ScheduledFor).HasColumnName("scheduled_for");
            run.Property(r => r.CreatedAt).HasColumnName("created_at");
            run.Property(r => r.StartedAt).HasColumnName("started_at");
            run.Property(r => r.FinishedAt).HasColumnName("finished_at");
            run.Property(r => r.ErrorSummary).HasColumnName("error_summary");
            run.Property(r => r.ArtifactPath).HasColumnName("artifact_path");
            run.Property(r => r.LogPath).HasColumnName("log_path");
            run.HasIndex(r => r.JobName);
            run.HasIndex(r => r.CreatedAt);
        });

        modelBuilder.Entity<RunCell>(cell => {
            cell.ToTable("run_cells");
            cell.HasKey(c => new { c.RunId, c.CellIndex });
            cell.Property(c => c.RunId).HasColumnName("run_id");
            cell.Property(c => c.CellIndex).HasColumnName("cell_index");
            cell.Property(c => c.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
            cell.Property(c => c.SourcePreview).HasColumnName("source_preview");
            cell.Property(c => c.StartedAt).HasColumnName("started_at");
            cell.Property(c => c.FinishedAt).HasColumnName("finished_at");
            cell.Property(c => c.ErrorSummary).HasColumnName("error_summary");
        });

        modelBuilder.Entity<JobTriggerState>(state => {
            state.ToTable("job_trigger_state");
            state.HasKey(s => s.JobName);
            state.Property(s => s.JobName).HasColumnName("job_name");
            state.Property(s => s.LastTriggerAt).HasColumnName("last_trigger_at");
        });
    }
}

// One context per provider: EF ties a migration to the context that generated it,
// and the SQL differs per dialect. The model above is shared by all three.

public sealed class SqliteRunsDbContext : RunsDbContext {
    public SqliteRunsDbContext(DbContextOptions<SqliteRunsDbContext> options) : base(options) { }
}

public sealed class SqlServerRunsDbContext : RunsDbContext {
    public SqlServerRunsDbContext(DbContextOptions<SqlServerRunsDbContext> options) : base(options) { }
}

public sealed class PostgresRunsDbContext : RunsDbContext {
    public PostgresRunsDbContext(DbContextOptions<PostgresRunsDbContext> options) : base(options) { }
}

// Design-time factories so `dotnet ef migrations add` never boots the app host.
// The connection strings are placeholders — migrations only need the dialect.

public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteRunsDbContext> {
    public SqliteRunsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteRunsDbContext>()
            .UseSqlite("Data Source=design-time.db").Options);
}

public sealed class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<SqlServerRunsDbContext> {
    public SqlServerRunsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqlServerRunsDbContext>()
            .UseSqlServer("Server=design-time;Database=clrkernel_jobs;Trusted_Connection=True").Options);
}

public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresRunsDbContext> {
    public PostgresRunsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresRunsDbContext>()
            .UseNpgsql("Host=design-time;Database=clrkernel_jobs").Options);
}

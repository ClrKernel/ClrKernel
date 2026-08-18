using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClrKernel.Jobs;

/// <summary>
/// The run-history EF model. Enums are stored as strings so the tables read well in
/// any DB browser. Provider selection (sqlite/sqlserver/postgres) happens where the
/// options are built; per-provider migrations live under Store/Migrations/.
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
            run.Property(r => r.JobName).IsRequired();
            run.Property(r => r.Status).HasConversion<string>();
            run.Property(r => r.Trigger).HasConversion<string>();
            run.HasIndex(r => r.JobName);
            run.HasIndex(r => r.CreatedAt);
        });

        modelBuilder.Entity<RunCell>(cell => {
            cell.ToTable("run_cells");
            cell.HasKey(c => new { c.RunId, c.CellIndex });
            cell.Property(c => c.Status).HasConversion<string>();
        });

        modelBuilder.Entity<JobTriggerState>(state => {
            state.ToTable("job_trigger_state");
            state.HasKey(s => s.JobName);
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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClrKernel.Jobs;

/// <summary>
/// The run-history EF model. Enums are stored as strings so the tables read well in
/// any DB browser. Provider selection (sqlite/sqlserver/postgres) happens where the
/// options are built; per-provider migrations live under Store/Migrations/.
/// </summary>
public sealed class RunsDbContext : DbContext {
    public RunsDbContext(DbContextOptions<RunsDbContext> options) : base(options) { }

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

/// <summary>Design-time factory so `dotnet ef migrations` never boots the app host.</summary>
public sealed class RunsDbContextFactory : IDesignTimeDbContextFactory<RunsDbContext> {
    public RunsDbContext CreateDbContext(string[] args) {
        var builder = new DbContextOptionsBuilder<RunsDbContext>();
        builder.UseSqlite("Data Source=design-time.db");
        return new RunsDbContext(builder.Options);
    }
}

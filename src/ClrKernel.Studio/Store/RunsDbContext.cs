using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClrKernel.Studio;

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
    public DbSet<ManualRun> ManualRuns => Set<ManualRun>();
    public DbSet<QueryAudit> QueryAudits => Set<QueryAudit>();
    public DbSet<PromotionAudit> PromotionAudits => Set<PromotionAudit>();
    public DbSet<SavedQuery> SavedQueries => Set<SavedQuery>();

    // Accounts live here rather than in a file of their own so one backup covers
    // the whole server. The consequence is that `--store files` cannot host a
    // multi-user server; `serve` says so and names the fix.
    public DbSet<User> Users => Set<User>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<AuthSession> Sessions => Set<AuthSession>();
    public DbSet<ProjectMembership> ProjectMemberships => Set<ProjectMembership>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<Run>(run => {
            run.ToTable("runs");
            run.HasKey(r => r.Id);
            run.Property(r => r.Id).HasColumnName("id");
            run.Property(r => r.Project).HasColumnName("project").IsRequired().HasMaxLength(64);
            run.Property(r => r.Environment).HasColumnName("environment").IsRequired().HasMaxLength(16);
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
            run.Property(r => r.CommitSha).HasColumnName("commit_sha").HasMaxLength(64);
            run.Property(r => r.WasDirty).HasColumnName("was_dirty");
            run.Property(r => r.HadOverrides).HasColumnName("had_overrides");
            run.HasIndex(r => new { r.Project, r.Environment, r.JobName });
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

        modelBuilder.Entity<ManualRun>(run => {
            run.ToTable("manual_runs");
            run.HasKey(r => r.Id);
            run.Property(r => r.Id).HasColumnName("id");
            run.Property(r => r.Project).HasColumnName("project").IsRequired().HasMaxLength(64);
            run.Property(r => r.Environment).HasColumnName("environment").IsRequired().HasMaxLength(16);
            run.Property(r => r.NotebookPath).HasColumnName("notebook_path").IsRequired();
            run.Property(r => r.ActorId).HasColumnName("actor_id");
            run.Property(r => r.ActorName).HasColumnName("actor_name").HasMaxLength(120);
            run.Property(r => r.StartedAt).HasColumnName("started_at");
            run.Property(r => r.FinishedAt).HasColumnName("finished_at");
            run.Property(r => r.Cells).HasColumnName("cells");
            run.Property(r => r.CellCount).HasColumnName("cell_count");
            run.Property(r => r.Overrides).HasColumnName("overrides");
            run.Property(r => r.Outcome).HasColumnName("outcome").HasMaxLength(16);
            run.Property(r => r.ErrorSummary).HasColumnName("error_summary");
            run.HasIndex(r => new { r.Project, r.Environment, r.NotebookPath });
            run.HasIndex(r => r.StartedAt);
            // No foreign key to users: the point of an audit row is that it outlives
            // the account, and a cascade would delete exactly the evidence somebody
            // came looking for.
        });

        modelBuilder.Entity<QueryAudit>(audit => {
            audit.ToTable("connection_queries");
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Id).HasColumnName("id");
            audit.Property(a => a.ConnectionId).HasColumnName("connection_id").IsRequired().HasMaxLength(64);
            audit.Property(a => a.ConnectionName).HasColumnName("connection_name").HasMaxLength(200);
            audit.Property(a => a.ActorId).HasColumnName("actor_id");
            audit.Property(a => a.ActorName).HasColumnName("actor_name").HasMaxLength(120);
            audit.Property(a => a.StartedAt).HasColumnName("started_at");
            audit.Property(a => a.DurationMs).HasColumnName("duration_ms");
            audit.Property(a => a.Statement).HasColumnName("statement");
            audit.Property(a => a.LeastPrivilege).HasColumnName("least_privilege");
            audit.Property(a => a.Outcome).HasColumnName("outcome").HasMaxLength(16);
            audit.Property(a => a.RowsAffected).HasColumnName("rows_affected");
            audit.Property(a => a.ErrorSummary).HasColumnName("error_summary");
            audit.Property(a => a.Scope).HasColumnName("scope").HasMaxLength(16);
            audit.HasIndex(a => a.StartedAt);
            audit.HasIndex(a => a.ConnectionId);
            // No foreign key to users, for the same reason the manual-run audit has
            // none: the row has to outlive the account it is about.
        });

        modelBuilder.Entity<PromotionAudit>(audit => {
            audit.ToTable("promotions");
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Id).HasColumnName("id");
            audit.Property(a => a.Project).HasColumnName("project").HasMaxLength(120);
            audit.Property(a => a.Paths).HasColumnName("paths");
            audit.Property(a => a.ActorId).HasColumnName("actor_id");
            audit.Property(a => a.ActorName).HasColumnName("actor_name").HasMaxLength(120);
            audit.Property(a => a.PromotedAt).HasColumnName("promoted_at");
            audit.Property(a => a.IsDeletion).HasColumnName("is_deletion");
            audit.Property(a => a.CommitSha).HasColumnName("commit_sha").HasMaxLength(64);
            audit.Property(a => a.Unscheduled).HasColumnName("unscheduled");
            audit.Property(a => a.EvidenceRuns).HasColumnName("evidence_runs");
            audit.HasIndex(a => a.PromotedAt);
            audit.HasIndex(a => a.Project);
            // No foreign key to users, for the same reason the other audits have
            // none: the row has to outlive the account it is about.
        });

        modelBuilder.Entity<SavedQuery>(query => {
            query.ToTable("saved_queries");
            query.HasKey(q => q.Id);
            query.Property(q => q.Id).HasColumnName("id");
            query.Property(q => q.Name).HasColumnName("name").IsRequired().HasMaxLength(200);
            query.Property(q => q.Scope).HasColumnName("scope").IsRequired().HasMaxLength(16);
            query.Property(q => q.OwnerId).HasColumnName("owner_id");
            query.Property(q => q.ConnectionId).HasColumnName("connection_id").HasMaxLength(64);
            query.Property(q => q.ConnectionName).HasColumnName("connection_name").HasMaxLength(200);
            query.Property(q => q.Sql).HasColumnName("sql");
            query.Property(q => q.CreatedBy).HasColumnName("created_by");
            query.Property(q => q.CreatedByName).HasColumnName("created_by_name").HasMaxLength(120);
            query.Property(q => q.CreatedAt).HasColumnName("created_at");
            query.Property(q => q.UpdatedAt).HasColumnName("updated_at");
            query.HasIndex(q => new { q.Scope, q.OwnerId });
            // No foreign key to users or connections: a saved query outlives both,
            // and a cascade would delete the thing somebody came back for.
        });

        modelBuilder.Entity<JobTriggerState>(state => {
            state.ToTable("job_trigger_state");
            // Composite key: a test run must never advance prod's trigger clock, and
            // two projects with a job of the same name must not share one either.
            state.HasKey(s => new { s.Project, s.Environment, s.JobName });
            state.Property(s => s.Project).HasColumnName("project").HasMaxLength(64);
            state.Property(s => s.Environment).HasColumnName("environment").HasMaxLength(16);
            state.Property(s => s.JobName).HasColumnName("job_name");
            state.Property(s => s.LastTriggerAt).HasColumnName("last_trigger_at");
        });

        modelBuilder.Entity<User>(user => {
            user.ToTable("users");
            user.HasKey(u => u.Id);
            user.Property(u => u.Id).HasColumnName("id");
            user.Property(u => u.DisplayName).HasColumnName("display_name").IsRequired().HasMaxLength(120);
            user.Property(u => u.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(16);
            user.Property(u => u.CreatedAt).HasColumnName("created_at");
            user.Property(u => u.LastSeenAt).HasColumnName("last_seen_at");
            user.Property(u => u.Disabled).HasColumnName("disabled");
        });

        modelBuilder.Entity<Credential>(credential => {
            credential.ToTable("credentials");
            credential.HasKey(c => c.Id);
            // base64url, so it is safe in a URL and readable in a query result.
            credential.Property(c => c.Id).HasColumnName("id").HasMaxLength(512);
            credential.Property(c => c.UserId).HasColumnName("user_id");
            credential.Property(c => c.PublicKey).HasColumnName("public_key").IsRequired();
            credential.Property(c => c.SignCount).HasColumnName("sign_count");
            credential.Property(c => c.Transports).HasColumnName("transports").HasMaxLength(128);
            credential.Property(c => c.AaGuid).HasColumnName("aaguid");
            credential.Property(c => c.Name).HasColumnName("name").HasMaxLength(120);
            credential.Property(c => c.CreatedAt).HasColumnName("created_at");
            credential.Property(c => c.LastUsedAt).HasColumnName("last_used_at");
            // Removing a user takes their passkeys with them; leaving orphans would
            // leave credentials that authenticate as nobody.
            credential.HasOne(c => c.User).WithMany(u => u.Credentials)
                .HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            credential.HasIndex(c => c.UserId);
        });

        modelBuilder.Entity<Invite>(invite => {
            invite.ToTable("invites");
            invite.HasKey(i => i.Code);
            invite.Property(i => i.Code).HasColumnName("code").HasMaxLength(64);
            invite.Property(i => i.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(16);
            invite.Property(i => i.Label).HasColumnName("label").HasMaxLength(200);
            invite.Property(i => i.CreatedBy).HasColumnName("created_by");
            invite.Property(i => i.CreatedAt).HasColumnName("created_at");
            invite.Property(i => i.ExpiresAt).HasColumnName("expires_at");
            invite.Property(i => i.UsedAt).HasColumnName("used_at");
            invite.Property(i => i.UsedBy).HasColumnName("used_by");
            invite.Property(i => i.Revoked).HasColumnName("revoked");
        });

        modelBuilder.Entity<ProjectMembership>(member => {
            member.ToTable("project_members");
            // The project is a slug rather than a foreign key: projects live in
            // projects.json, not in this database, and a grant that outlives an
            // unregistered project is what makes re-registering it restore access.
            member.HasKey(m => new { m.ProjectSlug, m.UserId });
            member.Property(m => m.ProjectSlug).HasColumnName("project").HasMaxLength(64);
            member.Property(m => m.UserId).HasColumnName("user_id");
            member.Property(m => m.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(24);
            member.Property(m => m.CreatedAt).HasColumnName("created_at");
            member.HasIndex(m => m.UserId);
            // Deleting an account takes its grants with it; a grant naming nobody
            // would be a row that can never match a caller and never be cleaned up.
            member.HasOne<User>().WithMany()
                .HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthSession>(session => {
            session.ToTable("sessions");
            session.HasKey(s => s.Id);
            session.Property(s => s.Id).HasColumnName("id").HasMaxLength(64);
            session.Property(s => s.UserId).HasColumnName("user_id");
            session.Property(s => s.CreatedAt).HasColumnName("created_at");
            session.Property(s => s.ExpiresAt).HasColumnName("expires_at");
            session.Property(s => s.LastSeenAt).HasColumnName("last_seen_at");
            session.HasIndex(s => s.UserId);
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
            .UseSqlServer("Server=design-time;Database=clrkernel_studio;Trusted_Connection=True").Options);
}

public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresRunsDbContext> {
    public PostgresRunsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresRunsDbContext>()
            .UseNpgsql("Host=design-time;Database=clrkernel_studio").Options);
}

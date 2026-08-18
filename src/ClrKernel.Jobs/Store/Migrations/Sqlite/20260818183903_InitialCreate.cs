using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Jobs.Store.Migrations.Sqlite;
/// <inheritdoc />
public partial class InitialCreate : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "job_trigger_state",
            columns: table => new {
                JobName = table.Column<string>(type: "TEXT", nullable: false),
                LastTriggerAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => {
                table.PrimaryKey("PK_job_trigger_state", x => x.JobName);
            });

        migrationBuilder.CreateTable(
            name: "run_cells",
            columns: table => new {
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                CellIndex = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                SourcePreview = table.Column<string>(type: "TEXT", nullable: true),
                StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                ErrorSummary = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table => {
                table.PrimaryKey("PK_run_cells", x => new { x.RunId, x.CellIndex });
            });

        migrationBuilder.CreateTable(
            name: "runs",
            columns: table => new {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                JobName = table.Column<string>(type: "TEXT", nullable: false),
                NotebookPath = table.Column<string>(type: "TEXT", nullable: true),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                Trigger = table.Column<string>(type: "TEXT", nullable: false),
                CausedByRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                ScheduledFor = table.Column<DateTime>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                ErrorSummary = table.Column<string>(type: "TEXT", nullable: true),
                ArtifactPath = table.Column<string>(type: "TEXT", nullable: true),
                LogPath = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table => {
                table.PrimaryKey("PK_runs", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_runs_CreatedAt",
            table: "runs",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_runs_JobName",
            table: "runs",
            column: "JobName");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropTable(
            name: "job_trigger_state");

        migrationBuilder.DropTable(
            name: "run_cells");

        migrationBuilder.DropTable(
            name: "runs");
    }
}

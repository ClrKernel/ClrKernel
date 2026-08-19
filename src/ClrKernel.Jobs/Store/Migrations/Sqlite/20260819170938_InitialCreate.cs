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
                environment = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                job_name = table.Column<string>(type: "TEXT", nullable: false),
                last_trigger_at = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => {
                table.PrimaryKey("PK_job_trigger_state", x => new { x.environment, x.job_name });
            });

        migrationBuilder.CreateTable(
            name: "run_cells",
            columns: table => new {
                run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                cell_index = table.Column<int>(type: "INTEGER", nullable: false),
                status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                source_preview = table.Column<string>(type: "TEXT", nullable: true),
                started_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                finished_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                error_summary = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table => {
                table.PrimaryKey("PK_run_cells", x => new { x.run_id, x.cell_index });
            });

        migrationBuilder.CreateTable(
            name: "runs",
            columns: table => new {
                id = table.Column<Guid>(type: "TEXT", nullable: false),
                environment = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                job_name = table.Column<string>(type: "TEXT", nullable: false),
                notebook_path = table.Column<string>(type: "TEXT", nullable: true),
                status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                trigger_type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                caused_by_run_id = table.Column<Guid>(type: "TEXT", nullable: true),
                attempt = table.Column<int>(type: "INTEGER", nullable: false),
                scheduled_for = table.Column<DateTime>(type: "TEXT", nullable: true),
                created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                started_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                finished_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                error_summary = table.Column<string>(type: "TEXT", nullable: true),
                artifact_path = table.Column<string>(type: "TEXT", nullable: true),
                log_path = table.Column<string>(type: "TEXT", nullable: true),
                commit_sha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                was_dirty = table.Column<bool>(type: "INTEGER", nullable: false),
                had_overrides = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table => {
                table.PrimaryKey("PK_runs", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_runs_created_at",
            table: "runs",
            column: "created_at");

        migrationBuilder.CreateIndex(
            name: "IX_runs_environment_job_name",
            table: "runs",
            columns: new[] { "environment", "job_name" });
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

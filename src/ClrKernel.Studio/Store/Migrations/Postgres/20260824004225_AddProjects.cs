using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.Postgres;

/// <summary>
/// Notebooks and jobs now live in a named project, and run history keys on it: two
/// projects are each allowed a job called <c>nightly</c>, so the project is part of
/// the trigger state's primary key and of the lookup index on runs.
/// </summary>
public partial class AddProjects : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropIndex(
            name: "IX_runs_environment_job_name",
            table: "runs");

        migrationBuilder.DropPrimaryKey(
            name: "PK_job_trigger_state",
            table: "job_trigger_state");

        migrationBuilder.AddColumn<string>(
            name: "project",
            table: "runs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "project",
            table: "job_trigger_state",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddPrimaryKey(
            name: "PK_job_trigger_state",
            table: "job_trigger_state",
            columns: new[] { "project", "environment", "job_name" });

        migrationBuilder.CreateIndex(
            name: "IX_runs_project_environment_job_name",
            table: "runs",
            columns: new[] { "project", "environment", "job_name" });

        // Every row that predates projects belongs to the one project this
        // server was already running — whose slug is "default" for exactly this
        // reason. Promotability asks "has this job run in test?" and would find
        // nothing if the answer sat under a project name nobody uses.
        migrationBuilder.Sql("UPDATE runs SET project = 'default' WHERE project = ''");
        migrationBuilder.Sql("UPDATE job_trigger_state SET project = 'default' WHERE project = ''");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropIndex(
            name: "IX_runs_project_environment_job_name",
            table: "runs");

        migrationBuilder.DropPrimaryKey(
            name: "PK_job_trigger_state",
            table: "job_trigger_state");

        migrationBuilder.DropColumn(
            name: "project",
            table: "runs");

        migrationBuilder.DropColumn(
            name: "project",
            table: "job_trigger_state");

        migrationBuilder.AddPrimaryKey(
            name: "PK_job_trigger_state",
            table: "job_trigger_state",
            columns: new[] { "environment", "job_name" });

        migrationBuilder.CreateIndex(
            name: "IX_runs_environment_job_name",
            table: "runs",
            columns: new[] { "environment", "job_name" });
    }
}

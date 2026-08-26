using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.SqlServer;

/// <summary>
/// Who drove a notebook by hand in test or prod, and what happened.
/// <para>
/// Its own table rather than a row in <c>runs</c>: promotability asks for the
/// latest run of a named job, and an audit entry that could answer that question
/// would be a hole in the gate. No foreign key to users either — the point of an
/// audit row is that it outlives the account.
/// </para>
/// </summary>
public partial class AddManualRuns : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "manual_runs",
            columns: table => new {
                id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                project = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                environment = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                notebook_path = table.Column<string>(type: "nvarchar(450)", nullable: false),
                actor_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                actor_name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                started_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                finished_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                cells = table.Column<string>(type: "nvarchar(max)", nullable: true),
                cell_count = table.Column<int>(type: "int", nullable: false),
                overrides = table.Column<string>(type: "nvarchar(max)", nullable: true),
                outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                error_summary = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table => {
                table.PrimaryKey("PK_manual_runs", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_manual_runs_project_environment_notebook_path",
            table: "manual_runs",
            columns: new[] { "project", "environment", "notebook_path" });

        migrationBuilder.CreateIndex(
            name: "IX_manual_runs_started_at",
            table: "manual_runs",
            column: "started_at");

    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropTable(
            name: "manual_runs");
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.SqlServer;

/// <summary>
/// The operator's switch per jobs file: active, and paused until when. It applies
/// to every job in the file and is keyed by the file's path, which is what joins
/// it to runs by the notebook beside it. Kept here rather than in the file because
/// pausing a prod schedule is an operation, not an edit — it must not need a
/// commit and a promotion to take effect. A file with no row is active, not paused.
/// </summary>
public partial class JobSwitches : Migration {
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "jobs",
            columns: table => new {
                project = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                environment = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                path = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                active = table.Column<bool>(type: "bit", nullable: false),
                paused_until = table.Column<DateTime>(type: "datetime2", nullable: true),
                last_modified = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table => {
                table.PrimaryKey("PK_jobs", x => new { x.project, x.environment, x.path });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropTable(name: "jobs");
    }
}

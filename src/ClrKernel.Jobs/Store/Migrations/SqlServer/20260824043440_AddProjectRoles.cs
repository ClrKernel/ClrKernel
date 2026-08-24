using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Jobs.Store.Migrations.SqlServer;

/// <summary>
/// Per-project grants. A grant raises what someone may do on one project and never
/// lowers it — the effective role is the higher of this and what their server role
/// implies — so the baseline Server User, who has no implicit access anywhere, sees
/// only the projects named here.
/// </summary>
public partial class AddProjectRoles : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "project_members",
            columns: table => new {
                project = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                role = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                created_at = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table => {
                table.PrimaryKey("PK_project_members", x => new { x.project, x.user_id });
                table.ForeignKey(
                    name: "FK_project_members_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_project_members_user_id",
            table: "project_members",
            column: "user_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropTable(
            name: "project_members");
    }
}

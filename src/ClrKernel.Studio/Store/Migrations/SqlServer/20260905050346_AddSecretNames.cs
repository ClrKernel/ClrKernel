using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.SqlServer;

/// <summary>
/// The names of the secrets this server has been asked to hold, per branch.
///
/// <para>
/// Names only — the values live in whichever store the server was configured with.
/// The table exists because a secret store cannot be enumerated: no OS credential
/// store lists by service portably, so "which secrets does prod have?" has to be
/// answered from somewhere, and this is it.
/// </para>
/// </summary>
public partial class AddSecretNames : Migration {
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "secret_names",
            columns: table => new {
                id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                project = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                branch = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                created_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                created_by_name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                updated_at = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table => {
                table.PrimaryKey("PK_secret_names", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_secret_names_project_branch_name",
            table: "secret_names",
            columns: new[] { "project", "branch", "name" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropTable(
            name: "secret_names");
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.Sqlite;

/// <inheritdoc />
public partial class AddPromotionAudit : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "promotions",
            columns: table => new {
                id = table.Column<Guid>(type: "TEXT", nullable: false),
                project = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                paths = table.Column<string>(type: "TEXT", nullable: true),
                actor_id = table.Column<Guid>(type: "TEXT", nullable: false),
                actor_name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                promoted_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                is_deletion = table.Column<bool>(type: "INTEGER", nullable: false),
                commit_sha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                unscheduled = table.Column<string>(type: "TEXT", nullable: true),
                evidence_runs = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table => {
                table.PrimaryKey("PK_promotions", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_promotions_project",
            table: "promotions",
            column: "project");

        migrationBuilder.CreateIndex(
            name: "IX_promotions_promoted_at",
            table: "promotions",
            column: "promoted_at");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropTable(
            name: "promotions");
    }
}

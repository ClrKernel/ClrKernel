using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.Postgres;

/// <inheritdoc />
public partial class AddPromotionAudit : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "promotions",
            columns: table => new {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                project = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                paths = table.Column<string>(type: "text", nullable: true),
                actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                actor_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                promoted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                is_deletion = table.Column<bool>(type: "boolean", nullable: false),
                commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                unscheduled = table.Column<string>(type: "text", nullable: true),
                evidence_runs = table.Column<string>(type: "text", nullable: true)
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

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.Postgres;

/// <inheritdoc />
public partial class AddNotificationDeliveries : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "notifications",
            columns: table => new {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                project = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                channel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                subject = table.Column<string>(type: "text", nullable: true),
                run_id = table.Column<Guid>(type: "uuid", nullable: true),
                sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                error = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table => {
                table.PrimaryKey("PK_notifications", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_notifications_project",
            table: "notifications",
            column: "project");

        migrationBuilder.CreateIndex(
            name: "IX_notifications_sent_at",
            table: "notifications",
            column: "sent_at");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropTable(
            name: "notifications");
    }
}

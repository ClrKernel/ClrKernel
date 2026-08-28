using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.Sqlite;

/// <inheritdoc />
public partial class AddNotificationDeliveries : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "notifications",
            columns: table => new {
                id = table.Column<Guid>(type: "TEXT", nullable: false),
                project = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                environment = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                event_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                channel = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                subject = table.Column<string>(type: "TEXT", nullable: true),
                run_id = table.Column<Guid>(type: "TEXT", nullable: true),
                sent_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                error = table.Column<string>(type: "TEXT", nullable: true)
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

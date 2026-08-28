using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.SqlServer;

/// <inheritdoc />
public partial class AddNotificationDeliveries : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "notifications",
            columns: table => new {
                id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                project = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                environment = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                event_type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                channel = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                subject = table.Column<string>(type: "nvarchar(max)", nullable: true),
                run_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                sent_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                error = table.Column<string>(type: "nvarchar(max)", nullable: true)
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

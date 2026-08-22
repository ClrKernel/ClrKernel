using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Jobs.Store.Migrations.SqlServer;
/// <inheritdoc />
public partial class AddAuth : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "invites",
            columns: table => new {
                code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                created_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                used_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                used_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                revoked = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table => {
                table.PrimaryKey("PK_invites", x => x.code);
            });

        migrationBuilder.CreateTable(
            name: "sessions",
            columns: table => new {
                id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                last_seen_at = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table => {
                table.PrimaryKey("PK_sessions", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new {
                id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                display_name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                last_seen_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                disabled = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table => {
                table.PrimaryKey("PK_users", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "credentials",
            columns: table => new {
                id = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                public_key = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                sign_count = table.Column<long>(type: "bigint", nullable: false),
                transports = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                aaguid = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                last_used_at = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table => {
                table.PrimaryKey("PK_credentials", x => x.id);
                table.ForeignKey(
                    name: "FK_credentials_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_credentials_user_id",
            table: "credentials",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "IX_sessions_user_id",
            table: "sessions",
            column: "user_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropTable(
            name: "credentials");

        migrationBuilder.DropTable(
            name: "invites");

        migrationBuilder.DropTable(
            name: "sessions");

        migrationBuilder.DropTable(
            name: "users");
    }
}

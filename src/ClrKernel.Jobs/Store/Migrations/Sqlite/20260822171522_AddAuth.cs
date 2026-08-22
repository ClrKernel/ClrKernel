using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Jobs.Store.Migrations.Sqlite;
/// <inheritdoc />
public partial class AddAuth : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "invites",
            columns: table => new {
                code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                role = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                used_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                used_by = table.Column<Guid>(type: "TEXT", nullable: true),
                revoked = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table => {
                table.PrimaryKey("PK_invites", x => x.code);
            });

        migrationBuilder.CreateTable(
            name: "sessions",
            columns: table => new {
                id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                last_seen_at = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => {
                table.PrimaryKey("PK_sessions", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new {
                id = table.Column<Guid>(type: "TEXT", nullable: false),
                display_name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                role = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                last_seen_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                disabled = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table => {
                table.PrimaryKey("PK_users", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "credentials",
            columns: table => new {
                id = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                public_key = table.Column<byte[]>(type: "BLOB", nullable: false),
                sign_count = table.Column<long>(type: "INTEGER", nullable: false),
                transports = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                aaguid = table.Column<Guid>(type: "TEXT", nullable: false),
                name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                last_used_at = table.Column<DateTime>(type: "TEXT", nullable: true)
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

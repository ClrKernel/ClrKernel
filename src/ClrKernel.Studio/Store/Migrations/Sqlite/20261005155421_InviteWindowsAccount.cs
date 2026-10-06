using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.Sqlite;

/// <summary>
/// An invite can name the Windows account it is for: the SID it is matched on, and
/// the account name the admin typed, kept for showing. Both nullable — every
/// invite issued before this is an ordinary one, redeemed with a passkey or with
/// any Windows account.
/// </summary>
public partial class InviteWindowsAccount : Migration {
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.AddColumn<string>(
            name: "windows_account",
            table: "invites",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "windows_sid",
            table: "invites",
            type: "TEXT",
            maxLength: 184,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropColumn(
            name: "windows_account",
            table: "invites");

        migrationBuilder.DropColumn(
            name: "windows_sid",
            table: "invites");
    }
}

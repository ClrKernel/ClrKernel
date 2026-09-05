using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.Sqlite;

/// <summary>
/// An invite carries the name and the handle the account will be created with.
///
/// <para>
/// Both were typed by the invitee at redemption before this, and the handle was
/// derived from the name — which meant a collision surfaced while somebody was
/// holding a security key, with nowhere to put the error. Chosen by the admin on
/// a form, it is checked twice with a person looking at it.
/// </para>
/// <para>
/// Nullable, because invites issued before this exist and have neither. Those are
/// refused at redemption rather than falling back to the derived name: a second
/// naming path is one nobody would notice still firing.
/// </para>
/// </summary>
public partial class InviteNames : Migration {
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.AddColumn<string>(
            name: "display_name",
            table: "invites",
            type: "TEXT",
            maxLength: 120,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "username",
            table: "invites",
            type: "TEXT",
            maxLength: 39,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropColumn(
            name: "display_name",
            table: "invites");

        migrationBuilder.DropColumn(
            name: "username",
            table: "invites");
    }
}

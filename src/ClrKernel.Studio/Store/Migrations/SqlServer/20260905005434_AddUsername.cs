using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.SqlServer;

/// <summary>
/// Gives every account the handle git knows it by. Until now a personal branch was
/// <c>user/&lt;guid&gt;</c>, unreadable in `git branch`, in the worktree directory it
/// names, and in the author of every commit that person makes.
///
/// <para>
/// The backfill deliberately sets each username to the account's own id. A guid is a
/// valid handle by the rules in <c>UserName</c>, it is unique without consulting
/// anything, and it means this migration on its own changes nothing anybody can see:
/// the branches keep exactly the names they have. Turning those into readable names
/// is a separate idempotent pass at startup, which also has to move the branch and
/// the worktree and so cannot happen in SQL.
/// </para>
/// <para>
/// The backfill sits between the column and the index because the column's default
/// is one empty string per row and the index is unique — two accounts would collide
/// before the pass ever ran.
/// </para>
/// </summary>
public partial class AddUsername : Migration {
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.AddColumn<string>(
            name: "username",
            table: "users",
            type: "nvarchar(39)",
            maxLength: 39,
            nullable: false,
            defaultValue: "");

        migrationBuilder.Sql(
            "UPDATE users SET username = LOWER(CONVERT(varchar(36), id)) WHERE username = ''");

        migrationBuilder.CreateIndex(
            name: "IX_users_username",
            table: "users",
            column: "username",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropIndex(
            name: "IX_users_username",
            table: "users");

        migrationBuilder.DropColumn(
            name: "username",
            table: "users");
    }
}

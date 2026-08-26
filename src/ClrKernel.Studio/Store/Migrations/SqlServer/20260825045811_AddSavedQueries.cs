using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.SqlServer;

/// <summary>
/// Saved queries, and the scope column that decides who may read a history row.
/// <para>
/// Every execution is recorded now, private connections included — that is what
/// makes a personal history worth having. The scope is what keeps the promise that
/// went with it: a row about a private connection is its actor's alone, admins
/// included, and the store filters on this column rather than trusting a route to
/// remember.
/// </para>
/// </summary>
public partial class AddSavedQueries : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.AddColumn<string>(
            name: "scope",
            table: "connection_queries",
            type: "nvarchar(16)",
            maxLength: 16,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "saved_queries",
            columns: table => new {
                id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                scope = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                owner_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                connection_id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                connection_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                sql = table.Column<string>(type: "nvarchar(max)", nullable: true),
                created_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                created_by_name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                updated_at = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table => {
                table.PrimaryKey("PK_saved_queries", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_saved_queries_scope_owner_id",
            table: "saved_queries",
            columns: new[] { "scope", "owner_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropTable(
            name: "saved_queries");

        migrationBuilder.DropColumn(
            name: "scope",
            table: "connection_queries");
    }
}

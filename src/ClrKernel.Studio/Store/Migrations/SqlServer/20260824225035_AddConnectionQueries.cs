using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.SqlServer;

/// <summary>
/// Who ran what against a shared connection. Its own table rather than a row in
/// <c>manual_runs</c>: that audit is about notebooks in an environment, and a
/// connection belongs to no project at all, so every column it carries would be
/// empty here and the two would answer each other's queries wrongly.
/// </summary>
public partial class AddConnectionQueries : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "connection_queries",
            columns: table => new {
                id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                connection_id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                connection_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                actor_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                actor_name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                started_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                duration_ms = table.Column<double>(type: "float", nullable: false),
                statement = table.Column<string>(type: "nvarchar(max)", nullable: true),
                least_privilege = table.Column<bool>(type: "bit", nullable: false),
                outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                rows_affected = table.Column<int>(type: "int", nullable: false),
                error_summary = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table => {
                table.PrimaryKey("PK_connection_queries", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_connection_queries_connection_id",
            table: "connection_queries",
            column: "connection_id");

        migrationBuilder.CreateIndex(
            name: "IX_connection_queries_started_at",
            table: "connection_queries",
            column: "started_at");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropTable(
            name: "connection_queries");
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.Sqlite;

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
                id = table.Column<Guid>(type: "TEXT", nullable: false),
                connection_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                connection_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                actor_id = table.Column<Guid>(type: "TEXT", nullable: false),
                actor_name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                duration_ms = table.Column<double>(type: "REAL", nullable: false),
                statement = table.Column<string>(type: "TEXT", nullable: true),
                least_privilege = table.Column<bool>(type: "INTEGER", nullable: false),
                outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                rows_affected = table.Column<int>(type: "INTEGER", nullable: false),
                error_summary = table.Column<string>(type: "TEXT", nullable: true)
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

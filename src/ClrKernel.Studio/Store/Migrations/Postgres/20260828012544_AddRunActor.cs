using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.Postgres;

/// <inheritdoc />
public partial class AddRunActor : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.AddColumn<Guid>(
            name: "actor_id",
            table: "runs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "actor_name",
            table: "runs",
            type: "character varying(120)",
            maxLength: 120,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropColumn(
            name: "actor_id",
            table: "runs");

        migrationBuilder.DropColumn(
            name: "actor_name",
            table: "runs");
    }
}

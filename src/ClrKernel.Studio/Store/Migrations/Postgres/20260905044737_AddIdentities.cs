using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.Postgres;

/// <summary>
/// One row per way an account can prove who it is, so a person can hold more than
/// a passkey — a Windows account, an OIDC subject — without another schema change.
///
/// <para>
/// Every existing passkey is backfilled as an identity, because sign-in resolves
/// through this table from here on: without the backfill the first person to sign
/// in after upgrading is told their passkey belongs to nobody. The credential rows
/// stay where they are and keep the cryptography; these say who it is.
/// </para>
/// </summary>
public partial class AddIdentities : Migration {
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "identities",
            columns: table => new {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => {
                table.PrimaryKey("PK_identities", x => x.id);
                table.ForeignKey(
                    name: "FK_identities_users_user_id",
                    column: x => x.user_id,
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_identities_provider_subject",
            table: "identities",
            columns: new[] { "provider", "subject" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_identities_user_id",
            table: "identities",
            column: "user_id");
        migrationBuilder.Sql(
            "INSERT INTO identities (id, provider, subject, user_id, label, created_at) " +
            "SELECT gen_random_uuid(), 'passkey', c.id, c.user_id, c.name, c.created_at " +
            "FROM credentials c " +
            "WHERE NOT EXISTS (SELECT 1 FROM identities i " +
            "WHERE i.provider = 'passkey' AND i.subject = c.id)");
    }

    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropTable(
            name: "identities");
    }
}

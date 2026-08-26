using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClrKernel.Studio.Store.Migrations.Sqlite;

/// <summary>
/// 0.10 renamed the editable branch from <c>dev</c> to <c>test</c>. The rows have to
/// travel with it: promotability asks "has this job run in the editable environment?",
/// so history left under the old name would make every notebook silently
/// un-promotable with nothing on screen to explain it.
/// </summary>
public partial class RenameDevToTest : Migration {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) {
        // (environment, job_name) is job_trigger_state's primary key, so a stray
        // 'test' row would turn the update below into a key violation at startup.
        migrationBuilder.Sql(
            "DELETE FROM job_trigger_state WHERE environment = 'test' AND job_name IN " +
            "(SELECT job_name FROM job_trigger_state WHERE environment = 'dev')");
        migrationBuilder.Sql("UPDATE job_trigger_state SET environment = 'test' WHERE environment = 'dev'");
        migrationBuilder.Sql("UPDATE runs SET environment = 'test' WHERE environment = 'dev'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.Sql(
            "DELETE FROM job_trigger_state WHERE environment = 'dev' AND job_name IN " +
            "(SELECT job_name FROM job_trigger_state WHERE environment = 'test')");
        migrationBuilder.Sql("UPDATE job_trigger_state SET environment = 'dev' WHERE environment = 'test'");
        migrationBuilder.Sql("UPDATE runs SET environment = 'dev' WHERE environment = 'test'");
    }
}

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20260829230000_AddSubmissions")]
public sealed class AddSubmissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "semantic_role",
            table: "jobs",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "Legacy");

        migrationBuilder.AddColumn<Guid>(
            name: "submission_id",
            table: "jobs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "submissions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "TEXT", nullable: false),
                submitted_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                specification_schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                specification_json = table.Column<string>(type: "TEXT", nullable: false),
                rerun_of_submission_id = table.Column<Guid>(type: "TEXT", nullable: true),
                preview_id = table.Column<Guid>(type: "TEXT", nullable: true),
                artifact_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                revision = table.Column<long>(type: "INTEGER", nullable: false),
                archived_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_submissions", x => x.id);
                table.CheckConstraint("ck_submissions_revision", "revision >= 0");
                table.CheckConstraint("ck_submissions_specification_schema", "specification_schema_version > 0");
                table.ForeignKey(
                    name: "FK_submissions_submissions_rerun_of_submission_id",
                    column: x => x.rerun_of_submission_id,
                    principalTable: "submissions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_jobs_submission_id_display_id_id",
            table: "jobs",
            columns: new[] { "submission_id", "display_id", "id" });
        migrationBuilder.CreateIndex(
            name: "IX_submissions_archived_at_utc",
            table: "submissions",
            column: "archived_at_utc");
        migrationBuilder.CreateIndex(
            name: "IX_submissions_rerun_of_submission_id",
            table: "submissions",
            column: "rerun_of_submission_id");
        migrationBuilder.CreateIndex(
            name: "IX_submissions_submitted_at_utc_id",
            table: "submissions",
            columns: new[] { "submitted_at_utc", "id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "submissions");
        migrationBuilder.DropIndex(
            name: "IX_jobs_submission_id_display_id_id",
            table: "jobs");
        migrationBuilder.DropColumn(name: "semantic_role", table: "jobs");
        migrationBuilder.DropColumn(name: "submission_id", table: "jobs");
    }
}

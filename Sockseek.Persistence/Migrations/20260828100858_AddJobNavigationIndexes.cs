using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobNavigationIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_jobs_parent_job_id_display_id_id",
                table: "jobs",
                columns: new[] { "parent_job_id", "display_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_workflow_id_display_id_id",
                table: "jobs",
                columns: new[] { "workflow_id", "display_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_jobs_parent_job_id_display_id_id",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "IX_jobs_workflow_id_display_id_id",
                table: "jobs");
        }
    }
}

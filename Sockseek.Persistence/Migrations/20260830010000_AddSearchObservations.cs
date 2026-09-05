using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20260830010000_AddSearchObservations")]
public sealed class AddSearchObservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "observed_peer_count",
            table: "search_jobs",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);
        migrationBuilder.AddColumn<int>(
            name: "queue_length",
            table: "search_results",
            type: "INTEGER",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "visibility",
            table: "search_results",
            type: "TEXT",
            maxLength: 16,
            nullable: false,
            defaultValue: "Public");
        migrationBuilder.DropIndex(
            name: "IX_search_results_search_job_id_username_remote_filename",
            table: "search_results");
        migrationBuilder.CreateIndex(
            name: "IX_search_results_search_job_id_username_remote_filename_visibility",
            table: "search_results",
            columns: new[] { "search_job_id", "username", "remote_filename", "visibility" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_search_results_search_job_id_username_remote_filename_visibility",
            table: "search_results");
        migrationBuilder.DropColumn(name: "observed_peer_count", table: "search_jobs");
        migrationBuilder.DropColumn(name: "queue_length", table: "search_results");
        migrationBuilder.DropColumn(name: "visibility", table: "search_results");
        migrationBuilder.CreateIndex(
            name: "IX_search_results_search_job_id_username_remote_filename",
            table: "search_results",
            columns: new[] { "search_job_id", "username", "remote_filename" },
            unique: true);
    }
}

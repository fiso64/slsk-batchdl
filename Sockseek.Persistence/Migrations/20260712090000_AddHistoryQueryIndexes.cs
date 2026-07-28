using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20260712090000_AddHistoryQueryIndexes")]
public sealed class AddHistoryQueryIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_jobs_workflow_id_created_at_utc_id",
            table: "jobs",
            columns: new[] { "workflow_id", "created_at_utc", "id" });
        migrationBuilder.CreateIndex(
            name: "IX_transfers_direction_state_created_at_utc",
            table: "transfers",
            columns: new[] { "direction", "state", "created_at_utc" });
        migrationBuilder.CreateIndex(
            name: "IX_transfers_workflow_id_created_at_utc_id",
            table: "transfers",
            columns: new[] { "workflow_id", "created_at_utc", "id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_jobs_workflow_id_created_at_utc_id", table: "jobs");
        migrationBuilder.DropIndex(name: "IX_transfers_direction_state_created_at_utc", table: "transfers");
        migrationBuilder.DropIndex(name: "IX_transfers_workflow_id_created_at_utc_id", table: "transfers");
    }
}

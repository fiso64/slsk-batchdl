using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20260729120000_AddUploadTransferHistory")]
public sealed class AddUploadTransferHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "cancellation_source",
            table: "transfers",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "None");

        migrationBuilder.CreateIndex(
            name: "IX_transfers_direction_started_at_utc_id",
            table: "transfers",
            columns: new[] { "direction", "started_at_utc", "id" });

        migrationBuilder.CreateIndex(
            name: "IX_transfers_username_direction_started_at_utc_id",
            table: "transfers",
            columns: new[] { "username", "direction", "started_at_utc", "id" });

        migrationBuilder.CreateIndex(
            name: "IX_transfers_direction_terminal_outcome_completed_at_utc",
            table: "transfers",
            columns: new[] { "direction", "terminal_outcome", "completed_at_utc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_transfers_direction_started_at_utc_id",
            table: "transfers");

        migrationBuilder.DropIndex(
            name: "IX_transfers_username_direction_started_at_utc_id",
            table: "transfers");

        migrationBuilder.DropIndex(
            name: "IX_transfers_direction_terminal_outcome_completed_at_utc",
            table: "transfers");

        migrationBuilder.DropColumn(
            name: "cancellation_source",
            table: "transfers");
    }
}

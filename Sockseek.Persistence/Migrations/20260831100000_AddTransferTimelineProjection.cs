using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20260831100000_AddTransferTimelineProjection")]
public sealed class AddTransferTimelineProjection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>("archived_at_utc", "transfers", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<long>("bytes_per_second", "transfers", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<string>("file_attributes_json", "transfers", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<int>("file_bit_depth", "transfers", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<int>("file_bit_rate", "transfers", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<string>("file_extension", "transfers", type: "TEXT", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<int>("file_length", "transfers", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<string>("file_name", "transfers", type: "TEXT", maxLength: 1024, nullable: true);
        migrationBuilder.AddColumn<int>("file_sample_rate", "transfers", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<long>("file_size_bytes", "transfers", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<string>("group_ref", "transfers", type: "TEXT", maxLength: 4096, nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_transfers_archived_at_utc_created_at_utc_id",
            table: "transfers",
            columns: ["archived_at_utc", "created_at_utc", "id"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_transfers_archived_at_utc_created_at_utc_id", "transfers");
        migrationBuilder.DropColumn("archived_at_utc", "transfers");
        migrationBuilder.DropColumn("bytes_per_second", "transfers");
        migrationBuilder.DropColumn("file_attributes_json", "transfers");
        migrationBuilder.DropColumn("file_bit_depth", "transfers");
        migrationBuilder.DropColumn("file_bit_rate", "transfers");
        migrationBuilder.DropColumn("file_extension", "transfers");
        migrationBuilder.DropColumn("file_length", "transfers");
        migrationBuilder.DropColumn("file_name", "transfers");
        migrationBuilder.DropColumn("file_sample_rate", "transfers");
        migrationBuilder.DropColumn("file_size_bytes", "transfers");
        migrationBuilder.DropColumn("group_ref", "transfers");
    }
}

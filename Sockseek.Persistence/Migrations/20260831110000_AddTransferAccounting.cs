using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20260831110000_AddTransferAccounting")]
public sealed class AddTransferAccounting : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "group_display_path",
            table: "transfers",
            type: "TEXT",
            maxLength: 4096,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "transfer_accounting_state",
            columns: table => new
            {
                state_id = table.Column<int>(type: "INTEGER", nullable: false),
                complete_from_utc = table.Column<long>(type: "INTEGER", nullable: false),
                updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_transfer_accounting_state", x => x.state_id);
                table.CheckConstraint("ck_transfer_accounting_state_coverage", "complete_from_utc >= 0");
                table.CheckConstraint("ck_transfer_accounting_state_singleton", "state_id = 1");
            });

        migrationBuilder.CreateTable(
            name: "transfer_byte_buckets",
            columns: table => new
            {
                bucket_start_utc = table.Column<long>(type: "INTEGER", nullable: false),
                direction = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                bytes = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_transfer_byte_buckets", x => new { x.bucket_start_utc, x.direction, x.username });
                table.CheckConstraint("ck_transfer_byte_bucket_bytes", "bytes >= 0");
            });

        migrationBuilder.CreateTable(
            name: "transfer_accounting_checkpoints",
            columns: table => new
            {
                attempt_id = table.Column<Guid>(type: "TEXT", nullable: false),
                transfer_id = table.Column<Guid>(type: "TEXT", nullable: false),
                revision = table.Column<long>(type: "INTEGER", nullable: false),
                cumulative_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                last_observed_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_transfer_accounting_checkpoints", x => x.attempt_id);
                table.CheckConstraint("ck_transfer_accounting_checkpoint_bytes", "cumulative_bytes >= 0");
                table.CheckConstraint("ck_transfer_accounting_checkpoint_revision", "revision >= 0");
                table.ForeignKey(
                    name: "FK_transfer_accounting_checkpoints_transfers_transfer_id",
                    column: x => x.transfer_id,
                    principalTable: "transfers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_transfer_accounting_checkpoints_transfer_id",
            table: "transfer_accounting_checkpoints",
            column: "transfer_id");
        migrationBuilder.CreateIndex(
            name: "IX_transfer_byte_buckets_direction_bucket_start_utc",
            table: "transfer_byte_buckets",
            columns: ["direction", "bucket_start_utc"]);
        migrationBuilder.CreateIndex(
            name: "IX_transfer_byte_buckets_username_bucket_start_utc",
            table: "transfer_byte_buckets",
            columns: ["username", "bucket_start_utc"]);

        migrationBuilder.Sql(
            "INSERT INTO transfer_accounting_state (state_id, complete_from_utc, updated_at_utc) " +
            "VALUES (1, CAST(strftime('%s', 'now') AS INTEGER) * 1000, CAST(strftime('%s', 'now') AS INTEGER) * 1000)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("transfer_accounting_checkpoints");
        migrationBuilder.DropTable("transfer_byte_buckets");
        migrationBuilder.DropTable("transfer_accounting_state");
        migrationBuilder.DropColumn("group_display_path", "transfers");
    }
}

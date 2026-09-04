using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20260831120000_AddPeerRestrictions")]
public sealed class AddPeerRestrictions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "peer_restriction_overrides",
            columns: table => new
            {
                restriction_kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, collation: "BINARY"),
                override_state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_peer_restriction_overrides",
                    x => new { x.restriction_kind, x.username });
            });

        migrationBuilder.CreateIndex(
            name: "IX_peer_restriction_overrides_username_restriction_kind",
            table: "peer_restriction_overrides",
            columns: ["username", "restriction_kind"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable("peer_restriction_overrides");
}

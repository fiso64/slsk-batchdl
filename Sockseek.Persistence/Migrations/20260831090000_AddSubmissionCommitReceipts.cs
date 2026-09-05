using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20260831090000_AddSubmissionCommitReceipts")]
public sealed class AddSubmissionCommitReceipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "commit_fingerprint",
            table: "submissions",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "commit_receipt_json",
            table: "submissions",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "commit_fingerprint", table: "submissions");
        migrationBuilder.DropColumn(name: "commit_receipt_json", table: "submissions");
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferAttemptSourceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_path",
                table: "transfer_attempts",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_username",
                table: "transfer_attempts",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_path",
                table: "transfer_attempts");

            migrationBuilder.DropColumn(
                name: "source_username",
                table: "transfer_attempts");
        }
    }
}

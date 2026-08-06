using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatSequenceAllocator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_sequences",
                columns: table => new
                {
                    sequence_id = table.Column<int>(type: "INTEGER", nullable: false),
                    last_message_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    last_notification_sequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_sequences", x => x.sequence_id);
                    table.CheckConstraint("ck_chat_sequences_singleton", "sequence_id = 1");
                    table.CheckConstraint("ck_chat_sequences_values", "last_message_sequence >= 0 AND last_notification_sequence >= 0");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_sequences");
        }
    }
}

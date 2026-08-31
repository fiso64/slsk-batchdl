using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatsAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_conversations",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    local_account_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    peer_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    display_username = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    archived_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    last_read_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    last_message_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_conversations", x => x.conversation_id);
                    table.CheckConstraint("ck_chat_conversations_revision", "revision >= 0");
                    table.CheckConstraint("ck_chat_conversations_sequences", "last_read_sequence >= 0 AND last_message_sequence >= 0");
                });

            migrationBuilder.CreateTable(
                name: "chat_messages",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    local_account_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    target_kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    target_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    display_target = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    sender_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    display_sender = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    direction = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    occurred_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    recorded_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    send_state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    failure_reason = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    protocol_message_id = table.Column<int>(type: "INTEGER", nullable: true),
                    protocol_timestamp = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_messages", x => x.message_id);
                    table.CheckConstraint("ck_chat_messages_sequence", "sequence > 0");
                });

            migrationBuilder.CreateTable(
                name: "chat_room_subscriptions",
                columns: table => new
                {
                    room_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    local_account_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    room_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    runtime_desired = table.Column<bool>(type: "INTEGER", nullable: false),
                    room_kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    last_read_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    last_message_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_room_subscriptions", x => x.room_id);
                    table.CheckConstraint("ck_chat_room_subscriptions_revision", "revision >= 0");
                    table.CheckConstraint("ck_chat_room_subscriptions_sequences", "last_read_sequence >= 0 AND last_message_sequence >= 0");
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    notification_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    local_account_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    source_message_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    read_at_utc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.notification_id);
                    table.CheckConstraint("ck_notifications_sequence", "sequence > 0");
                    table.ForeignKey(
                        name: "FK_notifications_chat_messages_source_message_id",
                        column: x => x.source_message_id,
                        principalTable: "chat_messages",
                        principalColumn: "message_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_conversations_local_account_key_last_message_sequence_conversation_id",
                table: "chat_conversations",
                columns: new[] { "local_account_key", "last_message_sequence", "conversation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_conversations_local_account_key_peer_key",
                table: "chat_conversations",
                columns: new[] { "local_account_key", "peer_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_local_account_key_send_state",
                table: "chat_messages",
                columns: new[] { "local_account_key", "send_state" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_local_account_key_target_id_sequence_message_id",
                table: "chat_messages",
                columns: new[] { "local_account_key", "target_id", "sequence", "message_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_local_account_key_target_kind_target_key_protocol_message_id_protocol_timestamp",
                table: "chat_messages",
                columns: new[] { "local_account_key", "target_kind", "target_key", "protocol_message_id", "protocol_timestamp" },
                unique: true,
                filter: "protocol_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_sequence",
                table: "chat_messages",
                column: "sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_room_subscriptions_local_account_key_last_message_sequence_room_id",
                table: "chat_room_subscriptions",
                columns: new[] { "local_account_key", "last_message_sequence", "room_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_room_subscriptions_local_account_key_room_key",
                table: "chat_room_subscriptions",
                columns: new[] { "local_account_key", "room_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_room_subscriptions_local_account_key_runtime_desired",
                table: "chat_room_subscriptions",
                columns: new[] { "local_account_key", "runtime_desired" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_local_account_key_read_at_utc_sequence_notification_id",
                table: "notifications",
                columns: new[] { "local_account_key", "read_at_utc", "sequence", "notification_id" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_local_account_key_source_message_id_kind",
                table: "notifications",
                columns: new[] { "local_account_key", "source_message_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_sequence",
                table: "notifications",
                column: "sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_source_message_id",
                table: "notifications",
                column: "source_message_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_conversations");

            migrationBuilder.DropTable(
                name: "chat_room_subscriptions");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "chat_messages");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runtime_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    stopped_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    shutdown_kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workflow_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    parent_job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    source_job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    result_job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    last_runtime_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    display_id = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    lifecycle_state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    activity_phase = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    activity_until_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    terminal_outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    skip_reason = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    cancellation_source = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    failure_reason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    failure_message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    failure_detail = table.Column<string>(type: "TEXT", nullable: true),
                    item_name = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    query_text = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    updated_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    payload_schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_display_id", "display_id > 0");
                    table.CheckConstraint("ck_jobs_last_sequence", "last_sequence >= 0");
                    table.CheckConstraint("ck_jobs_revision", "revision >= 0");
                    table.CheckConstraint("ck_jobs_terminal_time", "(lifecycle_state = 'Terminal' AND completed_at_utc IS NOT NULL) OR (lifecycle_state <> 'Terminal' AND completed_at_utc IS NULL)");
                    table.ForeignKey(
                        name: "FK_jobs_jobs_parent_job_id",
                        column: x => x.parent_job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_jobs_jobs_result_job_id",
                        column: x => x.result_job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_jobs_jobs_source_job_id",
                        column: x => x.source_job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_jobs_runtime_sessions_last_runtime_id",
                        column: x => x.last_runtime_id,
                        principalTable: "runtime_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_jobs",
                columns: table => new
                {
                    job_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    query = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    result_count = table.Column<long>(type: "INTEGER", nullable: false),
                    locked_file_count = table.Column<long>(type: "INTEGER", nullable: false),
                    is_complete = table.Column<bool>(type: "INTEGER", nullable: false),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    result_persistence_state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    results_pruned_at_utc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_jobs", x => x.job_id);
                    table.CheckConstraint("ck_search_jobs_revision", "revision >= 0");
                    table.ForeignKey(
                        name: "FK_search_jobs_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    job_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    workflow_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    last_runtime_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    direction = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    remote_path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    local_path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    terminal_outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    total_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    transferred_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    last_progress_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    failure_reason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    failure_message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfers", x => x.id);
                    table.CheckConstraint("ck_transfers_bytes", "total_bytes >= 0 AND transferred_bytes >= 0");
                    table.CheckConstraint("ck_transfers_last_sequence", "last_sequence >= 0");
                    table.CheckConstraint("ck_transfers_revision", "revision >= 0");
                    table.CheckConstraint("ck_transfers_terminal_time", "(terminal_outcome = 'None' AND completed_at_utc IS NULL) OR (terminal_outcome <> 'None' AND completed_at_utc IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_transfers_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_transfers_runtime_sessions_last_runtime_id",
                        column: x => x.last_runtime_id,
                        principalTable: "runtime_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "search_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    search_job_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    remote_filename = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    bit_rate = table.Column<int>(type: "INTEGER", nullable: true),
                    bit_depth = table.Column<int>(type: "INTEGER", nullable: true),
                    response_file_count = table.Column<int>(type: "INTEGER", nullable: false),
                    sample_rate = table.Column<int>(type: "INTEGER", nullable: true),
                    duration_seconds = table.Column<int>(type: "INTEGER", nullable: true),
                    extension = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    upload_speed = table.Column<int>(type: "INTEGER", nullable: true),
                    has_free_upload_slot = table.Column<bool>(type: "INTEGER", nullable: true),
                    attributes_json = table.Column<string>(type: "TEXT", nullable: true),
                    observed_at_utc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_results", x => x.id);
                    table.CheckConstraint("ck_search_results_revision", "revision > 0");
                    table.CheckConstraint("ck_search_results_sequence", "sequence > 0");
                    table.ForeignKey(
                        name: "FK_search_results_search_jobs_search_job_id",
                        column: x => x.search_job_id,
                        principalTable: "search_jobs",
                        principalColumn: "job_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "transfer_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    transfer_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    last_runtime_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    last_sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    attempt_number = table.Column<int>(type: "INTEGER", nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    output_path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    started_at_utc = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at_utc = table.Column<long>(type: "INTEGER", nullable: true),
                    failure_reason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    failure_message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_attempts", x => x.id);
                    table.CheckConstraint("ck_transfer_attempts_last_sequence", "last_sequence >= 0");
                    table.CheckConstraint("ck_transfer_attempts_number", "attempt_number > 0");
                    table.CheckConstraint("ck_transfer_attempts_revision", "revision >= 0");
                    table.ForeignKey(
                        name: "FK_transfer_attempts_runtime_sessions_last_runtime_id",
                        column: x => x.last_runtime_id,
                        principalTable: "runtime_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_transfer_attempts_transfers_transfer_id",
                        column: x => x.transfer_id,
                        principalTable: "transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_created_at_utc_id",
                table: "jobs",
                columns: new[] { "created_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_display_id",
                table: "jobs",
                column: "display_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_last_runtime_id_lifecycle_state",
                table: "jobs",
                columns: new[] { "last_runtime_id", "lifecycle_state" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_lifecycle_state_completed_at_utc",
                table: "jobs",
                columns: new[] { "lifecycle_state", "completed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_parent_job_id",
                table: "jobs",
                column: "parent_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_result_job_id",
                table: "jobs",
                column: "result_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_jobs_source_job_id",
                table: "jobs",
                column: "source_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_sessions_started_at_utc",
                table: "runtime_sessions",
                column: "started_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_search_jobs_result_persistence_state_completed_at_utc",
                table: "search_jobs",
                columns: new[] { "result_persistence_state", "completed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_search_results_search_job_id_sequence",
                table: "search_results",
                columns: new[] { "search_job_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_search_results_search_job_id_username",
                table: "search_results",
                columns: new[] { "search_job_id", "username" });

            migrationBuilder.CreateIndex(
                name: "IX_search_results_search_job_id_username_remote_filename",
                table: "search_results",
                columns: new[] { "search_job_id", "username", "remote_filename" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transfer_attempts_last_runtime_id_completed_at_utc",
                table: "transfer_attempts",
                columns: new[] { "last_runtime_id", "completed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_transfer_attempts_transfer_id_attempt_number",
                table: "transfer_attempts",
                columns: new[] { "transfer_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transfer_attempts_transfer_id_started_at_utc",
                table: "transfer_attempts",
                columns: new[] { "transfer_id", "started_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_completed_at_utc",
                table: "transfers",
                column: "completed_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_transfers_job_id",
                table: "transfers",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "IX_transfers_last_runtime_id_terminal_outcome",
                table: "transfers",
                columns: new[] { "last_runtime_id", "terminal_outcome" });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "search_results");

            migrationBuilder.DropTable(
                name: "transfer_attempts");

            migrationBuilder.DropTable(
                name: "search_jobs");

            migrationBuilder.DropTable(
                name: "transfers");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "runtime_sessions");
        }
    }
}

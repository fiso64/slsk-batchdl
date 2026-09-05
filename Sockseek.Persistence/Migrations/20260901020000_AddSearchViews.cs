using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sockseek.Persistence.Migrations;

[DbContext(typeof(SockseekDbContext))]
[Migration("20260901020000_AddSearchViews")]
public sealed class AddSearchViews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            CREATE TABLE search_views (
                id TEXT PRIMARY KEY, source_job_id TEXT NOT NULL,
                projection_kind TEXT NOT NULL,
                definition_json TEXT NOT NULL,
                created_at_utc INTEGER NOT NULL, expires_at_utc INTEGER NOT NULL,
                revision INTEGER NOT NULL, source_revision INTEGER NOT NULL,
                consumed_sequence INTEGER NOT NULL, is_complete INTEGER NOT NULL,
                retention_state TEXT NOT NULL,
                public_file_count INTEGER NOT NULL, locked_file_count INTEGER NOT NULL,
                public_bytes INTEGER NOT NULL, locked_bytes INTEGER NOT NULL,
                observed_peer_count INTEGER NOT NULL, projected_file_count INTEGER NOT NULL,
                projected_public_file_count INTEGER NOT NULL,
                projected_locked_file_count INTEGER NOT NULL,
                preferred_file_count INTEGER NOT NULL, other_file_count INTEGER NOT NULL,
                top_level_item_count INTEGER NOT NULL,
                selectable_option_count INTEGER NOT NULL);
            CREATE INDEX ix_search_views_source
                ON search_views(source_job_id, created_at_utc);
            CREATE INDEX ix_search_views_expiry ON search_views(expires_at_utc);

            CREATE TABLE search_view_revisions (
                view_id TEXT NOT NULL, revision INTEGER NOT NULL,
                source_revision INTEGER NOT NULL, consumed_sequence INTEGER NOT NULL,
                is_complete INTEGER NOT NULL, retention_state TEXT NOT NULL,
                public_file_count INTEGER NOT NULL, locked_file_count INTEGER NOT NULL,
                public_bytes INTEGER NOT NULL, locked_bytes INTEGER NOT NULL,
                observed_peer_count INTEGER NOT NULL, projected_file_count INTEGER NOT NULL,
                projected_public_file_count INTEGER NOT NULL,
                projected_locked_file_count INTEGER NOT NULL,
                preferred_file_count INTEGER NOT NULL, other_file_count INTEGER NOT NULL,
                top_level_item_count INTEGER NOT NULL,
                selectable_option_count INTEGER NOT NULL,
                PRIMARY KEY (view_id, revision),
                FOREIGN KEY (view_id) REFERENCES search_views(id) ON DELETE CASCADE);

            CREATE TABLE search_view_files (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL,
                admitted_revision INTEGER NOT NULL, sequence INTEGER NOT NULL,
                source_row_revision INTEGER NOT NULL, username TEXT NOT NULL,
                response_file_count INTEGER NOT NULL, filename TEXT NOT NULL,
                size_bytes INTEGER NOT NULL, bit_rate INTEGER NULL,
                bit_depth INTEGER NULL, sample_rate INTEGER NULL,
                duration_seconds INTEGER NULL, extension TEXT NOT NULL,
                upload_speed INTEGER NULL, has_free_upload_slot INTEGER NULL,
                queue_length INTEGER NULL, attributes_json TEXT NULL,
                observed_at_utc INTEGER NOT NULL, visibility TEXT NOT NULL,
                preference_tier TEXT NOT NULL,
                necessary_conditions_satisfied INTEGER NOT NULL DEFAULT 1,
                condition_matches_json TEXT NOT NULL,
                configured_conditions_json TEXT NOT NULL DEFAULT '[]',
                sort_high INTEGER NOT NULL, sort_upload_fast INTEGER NOT NULL,
                sort_mid INTEGER NOT NULL, sort_inferred INTEGER NOT NULL,
                sort_upload_medium INTEGER NOT NULL, sort_bitrate INTEGER NOT NULL,
                sort_tie INTEGER NOT NULL,
                PRIMARY KEY (view_id, item_ref),
                UNIQUE (view_id, username, filename, visibility),
                FOREIGN KEY (view_id) REFERENCES search_views(id) ON DELETE CASCADE);
            CREATE INDEX ix_search_view_files_page ON search_view_files(
                view_id, admitted_revision, sort_high DESC, sort_upload_fast DESC,
                sort_mid DESC, sort_inferred DESC, sort_upload_medium DESC,
                sort_bitrate DESC, sort_tie DESC, sequence, item_ref);

            CREATE TABLE search_view_directories (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL,
                username TEXT NOT NULL, folder_path TEXT NOT NULL,
                PRIMARY KEY (view_id, item_ref),
                UNIQUE (view_id, username, folder_path),
                FOREIGN KEY (view_id) REFERENCES search_views(id) ON DELETE CASCADE);
            CREATE TABLE search_view_directory_versions (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL, revision INTEGER NOT NULL,
                best_file_ref TEXT NOT NULL,
                public_matching_count INTEGER NOT NULL,
                locked_matching_count INTEGER NOT NULL,
                public_matching_bytes INTEGER NOT NULL,
                locked_matching_bytes INTEGER NOT NULL,
                is_fully_retrieved INTEGER NOT NULL,
                retrieved_file_count INTEGER NULL, retrieved_bytes INTEGER NULL,
                is_removed INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (view_id, item_ref, revision),
                FOREIGN KEY (view_id, item_ref)
                    REFERENCES search_view_directories(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, best_file_ref)
                    REFERENCES search_view_files(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, revision)
                    REFERENCES search_view_revisions(view_id, revision) ON DELETE CASCADE);
            CREATE INDEX ix_search_view_directory_versions_revision
                ON search_view_directory_versions(view_id, revision, item_ref);
            CREATE TABLE search_view_directory_files (
                view_id TEXT NOT NULL, directory_ref TEXT NOT NULL,
                file_ref TEXT NOT NULL, admitted_revision INTEGER NOT NULL,
                relative_path TEXT NOT NULL, removed_revision INTEGER NULL,
                PRIMARY KEY (view_id, directory_ref, file_ref),
                FOREIGN KEY (view_id, directory_ref)
                    REFERENCES search_view_directories(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, file_ref)
                    REFERENCES search_view_files(view_id, item_ref) ON DELETE CASCADE);
            CREATE INDEX ix_search_view_directory_files_page
                ON search_view_directory_files(
                    view_id, directory_ref, admitted_revision,
                    relative_path COLLATE BINARY, file_ref);

            CREATE TABLE search_view_aggregate_tracks (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL,
                group_index INTEGER NOT NULL, query_json TEXT NOT NULL,
                PRIMARY KEY (view_id, item_ref),
                UNIQUE (view_id, group_index),
                FOREIGN KEY (view_id) REFERENCES search_views(id) ON DELETE CASCADE);
            CREATE TABLE search_view_aggregate_track_versions (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL, revision INTEGER NOT NULL,
                share_count INTEGER NOT NULL, selectable_option_count INTEGER NOT NULL,
                representative_file_ref TEXT NOT NULL,
                PRIMARY KEY (view_id, item_ref, revision),
                FOREIGN KEY (view_id, item_ref)
                    REFERENCES search_view_aggregate_tracks(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, representative_file_ref)
                    REFERENCES search_view_files(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, revision)
                    REFERENCES search_view_revisions(view_id, revision) ON DELETE CASCADE);
            CREATE INDEX ix_search_view_aggregate_track_versions_page
                ON search_view_aggregate_track_versions(
                    view_id, revision, share_count DESC, item_ref);
            CREATE TABLE search_view_aggregate_track_files (
                view_id TEXT NOT NULL, group_ref TEXT NOT NULL,
                file_ref TEXT NOT NULL, admitted_revision INTEGER NOT NULL,
                PRIMARY KEY (view_id, group_ref, file_ref),
                FOREIGN KEY (view_id, group_ref)
                    REFERENCES search_view_aggregate_tracks(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, file_ref)
                    REFERENCES search_view_files(view_id, item_ref) ON DELETE CASCADE);
            CREATE INDEX ix_search_view_aggregate_track_files_page
                ON search_view_aggregate_track_files(
                    view_id, group_ref, admitted_revision, file_ref);

            CREATE TABLE search_view_aggregate_albums (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL,
                stable_username TEXT NOT NULL, stable_folder_path TEXT NOT NULL,
                query_json TEXT NOT NULL,
                PRIMARY KEY (view_id, item_ref),
                UNIQUE (view_id, stable_username, stable_folder_path),
                FOREIGN KEY (view_id) REFERENCES search_views(id) ON DELETE CASCADE);
            CREATE TABLE search_view_aggregate_album_versions (
                view_id TEXT NOT NULL, item_ref TEXT NOT NULL, revision INTEGER NOT NULL,
                group_index INTEGER NOT NULL, share_count INTEGER NOT NULL,
                selectable_option_count INTEGER NOT NULL,
                representative_directory_ref TEXT NOT NULL,
                is_removed INTEGER NOT NULL,
                PRIMARY KEY (view_id, item_ref, revision),
                FOREIGN KEY (view_id, item_ref)
                    REFERENCES search_view_aggregate_albums(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, representative_directory_ref)
                    REFERENCES search_view_directories(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, revision)
                    REFERENCES search_view_revisions(view_id, revision) ON DELETE CASCADE);
            CREATE INDEX ix_search_view_aggregate_album_versions_page
                ON search_view_aggregate_album_versions(
                    view_id, revision, share_count DESC, group_index, item_ref);
            CREATE TABLE search_view_aggregate_album_directory_versions (
                view_id TEXT NOT NULL, group_ref TEXT NOT NULL,
                directory_ref TEXT NOT NULL, revision INTEGER NOT NULL,
                is_present INTEGER NOT NULL,
                PRIMARY KEY (view_id, group_ref, directory_ref, revision),
                FOREIGN KEY (view_id, group_ref)
                    REFERENCES search_view_aggregate_albums(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, directory_ref)
                    REFERENCES search_view_directories(view_id, item_ref) ON DELETE CASCADE,
                FOREIGN KEY (view_id, revision)
                    REFERENCES search_view_revisions(view_id, revision) ON DELETE CASCADE);
            CREATE INDEX ix_search_view_aggregate_album_directories_page
                ON search_view_aggregate_album_directory_versions(
                    view_id, group_ref, revision, directory_ref);

            CREATE TABLE search_view_peers (
                view_id TEXT NOT NULL, username TEXT NOT NULL,
                PRIMARY KEY (view_id, username),
                FOREIGN KEY (view_id) REFERENCES search_views(id) ON DELETE CASCADE);
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("search_view_aggregate_album_directory_versions");
        migrationBuilder.DropTable("search_view_aggregate_album_versions");
        migrationBuilder.DropTable("search_view_aggregate_track_files");
        migrationBuilder.DropTable("search_view_aggregate_track_versions");
        migrationBuilder.DropTable("search_view_directory_files");
        migrationBuilder.DropTable("search_view_directory_versions");
        migrationBuilder.DropTable("search_view_peers");
        migrationBuilder.DropTable("search_view_aggregate_albums");
        migrationBuilder.DropTable("search_view_aggregate_tracks");
        migrationBuilder.DropTable("search_view_directories");
        migrationBuilder.DropTable("search_view_files");
        migrationBuilder.DropTable("search_view_revisions");
        migrationBuilder.DropTable("search_views");
    }
}

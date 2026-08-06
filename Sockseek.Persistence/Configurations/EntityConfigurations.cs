using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sockseek.Persistence.Entities;

namespace Sockseek.Persistence.Configurations;

internal sealed class RuntimeSessionConfiguration : IEntityTypeConfiguration<RuntimeSessionEntity>
{
    public void Configure(EntityTypeBuilder<RuntimeSessionEntity> builder)
    {
        builder.ToTable("runtime_sessions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(x => x.StoppedAtUtc).HasColumnName("stopped_at_utc");
        builder.Property(x => x.ShutdownKind).HasColumnName("shutdown_kind").HasMaxLength(32);
        builder.Property(x => x.Version).HasColumnName("version").HasMaxLength(64);
        builder.HasIndex(x => x.StartedAtUtc);
    }
}

internal sealed class JobConfiguration : IEntityTypeConfiguration<JobEntity>
{
    public void Configure(EntityTypeBuilder<JobEntity> builder)
    {
        builder.ToTable("jobs", table =>
        {
            table.HasCheckConstraint("ck_jobs_revision", "revision >= 0");
            table.HasCheckConstraint("ck_jobs_last_sequence", "last_sequence >= 0");
            table.HasCheckConstraint("ck_jobs_display_id", "display_id > 0");
            table.HasCheckConstraint("ck_jobs_terminal_time", "(lifecycle_state = 'Terminal' AND completed_at_utc IS NOT NULL) OR (lifecycle_state <> 'Terminal' AND completed_at_utc IS NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.WorkflowId).HasColumnName("workflow_id");
        builder.Property(x => x.ParentJobId).HasColumnName("parent_job_id");
        builder.Property(x => x.SourceJobId).HasColumnName("source_job_id");
        builder.Property(x => x.ResultJobId).HasColumnName("result_job_id");
        builder.Property(x => x.LastRuntimeId).HasColumnName("last_runtime_id");
        builder.Property(x => x.LastSequence).HasColumnName("last_sequence");
        builder.Property(x => x.DisplayId).HasColumnName("display_id");
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(48);
        builder.Property(x => x.LifecycleState).HasColumnName("lifecycle_state").HasMaxLength(32);
        builder.Property(x => x.ActivityPhase).HasColumnName("activity_phase").HasMaxLength(48);
        builder.Property(x => x.ActivityUntilUtc).HasColumnName("activity_until_utc");
        builder.Property(x => x.TerminalOutcome).HasColumnName("terminal_outcome").HasMaxLength(32);
        builder.Property(x => x.SkipReason).HasColumnName("skip_reason").HasMaxLength(48);
        builder.Property(x => x.CancellationSource).HasColumnName("cancellation_source").HasMaxLength(48);
        builder.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(64);
        builder.Property(x => x.FailureMessage).HasColumnName("failure_message").HasMaxLength(2048);
        builder.Property(x => x.FailureDetail).HasColumnName("failure_detail");
        builder.Property(x => x.ItemName).HasColumnName("item_name").HasMaxLength(2048);
        builder.Property(x => x.QueryText).HasColumnName("query_text").HasMaxLength(4096);
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.Property(x => x.PayloadSchemaVersion).HasColumnName("payload_schema_version");
        builder.Property(x => x.PayloadJson).HasColumnName("payload_json");

        builder.HasOne<RuntimeSessionEntity>().WithMany().HasForeignKey(x => x.LastRuntimeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<JobEntity>().WithMany().HasForeignKey(x => x.ParentJobId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<JobEntity>().WithMany().HasForeignKey(x => x.SourceJobId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<JobEntity>().WithMany().HasForeignKey(x => x.ResultJobId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => x.DisplayId).IsUnique();
        builder.HasIndex(x => new { x.CreatedAtUtc, x.Id });
        builder.HasIndex(x => new { x.WorkflowId, x.CreatedAtUtc, x.Id });
        builder.HasIndex(x => new { x.LastRuntimeId, x.LifecycleState });
        builder.HasIndex(x => new { x.LifecycleState, x.CompletedAtUtc });
        builder.HasIndex(x => x.ParentJobId);
        builder.HasIndex(x => x.SourceJobId);
        builder.HasIndex(x => x.ResultJobId);
    }
}

internal sealed class SearchJobConfiguration : IEntityTypeConfiguration<SearchJobEntity>
{
    public void Configure(EntityTypeBuilder<SearchJobEntity> builder)
    {
        builder.ToTable("search_jobs", table => table.HasCheckConstraint("ck_search_jobs_revision", "revision >= 0"));
        builder.HasKey(x => x.JobId);
        builder.Property(x => x.JobId).HasColumnName("job_id");
        builder.Property(x => x.Query).HasColumnName("query").HasMaxLength(4096);
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.Property(x => x.ResultCount).HasColumnName("result_count");
        builder.Property(x => x.LockedFileCount).HasColumnName("locked_file_count");
        builder.Property(x => x.IsComplete).HasColumnName("is_complete");
        builder.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(x => x.ResultPersistenceState).HasColumnName("result_persistence_state").HasMaxLength(32);
        builder.Property(x => x.ResultsPrunedAtUtc).HasColumnName("results_pruned_at_utc");
        builder.HasOne<JobEntity>().WithOne().HasForeignKey<SearchJobEntity>(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.ResultPersistenceState, x.CompletedAtUtc });
    }
}

internal sealed class SearchResultConfiguration : IEntityTypeConfiguration<SearchResultEntity>
{
    public void Configure(EntityTypeBuilder<SearchResultEntity> builder)
    {
        builder.ToTable("search_results", table =>
        {
            table.HasCheckConstraint("ck_search_results_sequence", "sequence > 0");
            table.HasCheckConstraint("ck_search_results_revision", "revision > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.SearchJobId).HasColumnName("search_job_id");
        builder.Property(x => x.Sequence).HasColumnName("sequence");
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.Property(x => x.Username).HasColumnName("username").HasMaxLength(256);
        builder.Property(x => x.RemoteFilename).HasColumnName("remote_filename").HasMaxLength(4096);
        builder.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        builder.Property(x => x.BitRate).HasColumnName("bit_rate");
        builder.Property(x => x.BitDepth).HasColumnName("bit_depth");
        builder.Property(x => x.ResponseFileCount).HasColumnName("response_file_count");
        builder.Property(x => x.SampleRate).HasColumnName("sample_rate");
        builder.Property(x => x.DurationSeconds).HasColumnName("duration_seconds");
        builder.Property(x => x.Extension).HasColumnName("extension").HasMaxLength(32);
        builder.Property(x => x.UploadSpeed).HasColumnName("upload_speed");
        builder.Property(x => x.HasFreeUploadSlot).HasColumnName("has_free_upload_slot");
        builder.Property(x => x.AttributesJson).HasColumnName("attributes_json");
        builder.Property(x => x.ObservedAtUtc).HasColumnName("observed_at_utc");
        builder.HasOne<SearchJobEntity>().WithMany().HasForeignKey(x => x.SearchJobId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.SearchJobId, x.Sequence }).IsUnique();
        builder.HasIndex(x => new { x.SearchJobId, x.Username, x.RemoteFilename }).IsUnique();
        builder.HasIndex(x => new { x.SearchJobId, x.Username });
    }
}

internal sealed class TransferConfiguration : IEntityTypeConfiguration<TransferEntity>
{
    public void Configure(EntityTypeBuilder<TransferEntity> builder)
    {
        builder.ToTable("transfers", table =>
        {
            table.HasCheckConstraint("ck_transfers_revision", "revision >= 0");
            table.HasCheckConstraint("ck_transfers_last_sequence", "last_sequence >= 0");
            table.HasCheckConstraint("ck_transfers_bytes", "total_bytes >= 0 AND transferred_bytes >= 0");
            table.HasCheckConstraint("ck_transfers_terminal_time", "(terminal_outcome = 'None' AND completed_at_utc IS NULL) OR (terminal_outcome <> 'None' AND completed_at_utc IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.JobId).HasColumnName("job_id");
        builder.Property(x => x.WorkflowId).HasColumnName("workflow_id");
        builder.Property(x => x.LastRuntimeId).HasColumnName("last_runtime_id");
        builder.Property(x => x.LastSequence).HasColumnName("last_sequence");
        builder.Property(x => x.Direction).HasColumnName("direction").HasMaxLength(16);
        builder.Property(x => x.Source).HasColumnName("source").HasMaxLength(32);
        builder.Property(x => x.Username).HasColumnName("username").HasMaxLength(256);
        builder.Property(x => x.RemotePath).HasColumnName("remote_path").HasMaxLength(4096);
        builder.Property(x => x.LocalPath).HasColumnName("local_path").HasMaxLength(4096);
        builder.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
        builder.Property(x => x.TerminalOutcome).HasColumnName("terminal_outcome").HasMaxLength(32);
        builder.Property(x => x.TotalBytes).HasColumnName("total_bytes");
        builder.Property(x => x.TransferredBytes).HasColumnName("transferred_bytes");
        builder.Property(x => x.AttemptCount).HasColumnName("attempt_count");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(x => x.LastProgressAtUtc).HasColumnName("last_progress_at_utc");
        builder.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(64);
        builder.Property(x => x.FailureMessage).HasColumnName("failure_message").HasMaxLength(2048);
        builder.Property(x => x.CancellationSource).HasColumnName("cancellation_source").HasMaxLength(32);
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.HasOne<JobEntity>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<RuntimeSessionEntity>().WithMany().HasForeignKey(x => x.LastRuntimeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.JobId);
        builder.HasIndex(x => new { x.WorkflowId, x.CreatedAtUtc, x.Id });
        builder.HasIndex(x => new { x.Direction, x.State, x.CreatedAtUtc });
        builder.HasIndex(x => new { x.Direction, x.StartedAtUtc, x.Id });
        builder.HasIndex(x => new { x.Username, x.Direction, x.StartedAtUtc, x.Id });
        builder.HasIndex(x => new { x.Direction, x.TerminalOutcome, x.CompletedAtUtc });
        builder.HasIndex(x => new { x.LastRuntimeId, x.TerminalOutcome });
        builder.HasIndex(x => x.CompletedAtUtc);
    }
}

internal sealed class TransferAttemptConfiguration : IEntityTypeConfiguration<TransferAttemptEntity>
{
    public void Configure(EntityTypeBuilder<TransferAttemptEntity> builder)
    {
        builder.ToTable("transfer_attempts", table =>
        {
            table.HasCheckConstraint("ck_transfer_attempts_number", "attempt_number > 0");
            table.HasCheckConstraint("ck_transfer_attempts_revision", "revision >= 0");
            table.HasCheckConstraint("ck_transfer_attempts_last_sequence", "last_sequence >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.TransferId).HasColumnName("transfer_id");
        builder.Property(x => x.LastRuntimeId).HasColumnName("last_runtime_id");
        builder.Property(x => x.LastSequence).HasColumnName("last_sequence");
        builder.Property(x => x.AttemptNumber).HasColumnName("attempt_number");
        builder.Property(x => x.Source).HasColumnName("source").HasMaxLength(32);
        builder.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
        builder.Property(x => x.SourceUsername).HasColumnName("source_username").HasMaxLength(256);
        builder.Property(x => x.SourcePath).HasColumnName("source_path").HasMaxLength(4096);
        builder.Property(x => x.OutputPath).HasColumnName("output_path").HasMaxLength(4096);
        builder.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(64);
        builder.Property(x => x.FailureMessage).HasColumnName("failure_message").HasMaxLength(2048);
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.HasOne<TransferEntity>().WithMany().HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<RuntimeSessionEntity>().WithMany().HasForeignKey(x => x.LastRuntimeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.TransferId, x.AttemptNumber }).IsUnique();
        builder.HasIndex(x => new { x.TransferId, x.StartedAtUtc });
        builder.HasIndex(x => new { x.LastRuntimeId, x.CompletedAtUtc });
    }
}

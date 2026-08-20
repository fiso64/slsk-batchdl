using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Snapshots;

namespace Sockseek.Server;

/// <summary>
/// Projects only best-effort human-readable edges. Durable UI state is emitted by
/// <see cref="EngineStateStore"/> as typed deltas instead.
/// </summary>
public sealed class EngineActivityDtoAdapter
{
    private readonly EngineStateStore stateStore;
    private readonly Func<JobSnapshot, JobSummaryDto> getSummary;

    public EngineActivityDtoAdapter(
        EngineStateStore stateStore,
        Func<JobSnapshot, JobSummaryDto> getSummary)
    {
        this.stateStore = stateStore;
        this.getSummary = getSummary;
    }

    public void Attach(DownloadEvents events, SearchEvents searchEvents)
    {
        events.JobStatus += change =>
        {
            var summary = getSummary(change.Job);
            stateStore.PublishActivity(
                "job.status",
                new JobStatusActivityDto(summary.DisplayId, change.Status),
                summary.WorkflowId,
                summary.JobId,
                occurredAtUtc: change.OccurredAtUtc);
        };
        events.JobMessage += change =>
        {
            var summary = getSummary(change.Job);
            stateStore.PublishActivity(
                "job.message",
                new JobMessageActivityDto(
                    summary.DisplayId,
                    change.Level.ToString(),
                    change.Source,
                    change.Message),
                summary.WorkflowId,
                summary.JobId,
                occurredAtUtc: change.OccurredAtUtc);
        };
        events.WorkflowMessage += change => stateStore.PublishActivity(
            "workflow.message",
            new WorkflowMessageActivityDto(
                change.Level.ToString(),
                change.Source,
                change.Message),
            change.WorkflowId,
            occurredAtUtc: change.OccurredAtUtc);
        events.DownloadAttemptFailed += change =>
        {
            var summary = getSummary(change.Song);
            stateStore.PublishActivity(
                "download.attempt-failed",
                new DownloadAttemptFailedActivityDto(
                    summary.DisplayId,
                    change.Target.Identity.Username,
                    change.Target.Identity.Filename,
                    change.OutputPath,
                    change.Attempt,
                    change.MaxAttempts,
                    change.Exception.Type,
                    change.Exception.Message,
                    change.Exception.Detail),
                summary.WorkflowId,
                summary.JobId,
                change.TransferId,
                change.OccurredAtUtc);
        };
        events.TrackBatchResolved += change =>
        {
            var summary = getSummary(change.Owner);
            stateStore.PublishActivity(
                "track-batch.resolved",
                new TrackBatchResolvedActivityDto(
                    summary.DisplayId,
                    change.Owner.Kind == JobSnapshotKind.JobList,
                    change.Owner.PrintOption,
                    change.Pending.Count,
                    change.Existing.Count,
                    change.NotFound.Count),
                summary.WorkflowId,
                summary.JobId,
                occurredAtUtc: change.OccurredAtUtc);
        };
        events.JobStateChanged += OnJobStateChanged;
        searchEvents.SearchRateLimited += resetsAt => stateStore.UpdateSearchRateLimit(resetsAt);
        searchEvents.SearchResumed += () => stateStore.UpdateSearchRateLimit(null);
    }

    private void OnJobStateChanged(JobStateChangedChange change)
    {
        var summary = getSummary(change.Job);
        if (change.Job.Payload is ExtractJobSnapshotPayload extract
            && change.Job.ActivityPhase == JobActivityPhase.Extracting)
        {
            stateStore.PublishActivity(
                "extraction.started",
                new ExtractionStartedActivityDto(
                    summary.DisplayId,
                    extract.Input,
                    extract.InputType,
                    extract.InputType),
                summary.WorkflowId,
                summary.JobId,
                occurredAtUtc: change.OccurredAtUtc);
        }

        if (change.Job.TerminalOutcome != JobTerminalOutcome.Failed
            || string.IsNullOrWhiteSpace(change.Job.FailureDetail))
        {
            return;
        }

        stateStore.PublishActivity(
            "diagnostic.error",
            new DiagnosticActivityDto(
                summary.DisplayId,
                "job",
                change.Job.FailureMessage ?? "Job failed",
                ExceptionType(change.Job.FailureDetail),
                change.Job.FailureDetail,
                change.Job.Payload is ExtractJobSnapshotPayload failedExtract
                    ? failedExtract.InputType
                    : null),
            summary.WorkflowId,
            summary.JobId,
            occurredAtUtc: change.OccurredAtUtc);
    }

    private static string ExceptionType(string exceptionDetail)
    {
        var firstLine = exceptionDetail
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', 2)[0];
        var separatorIndex = firstLine.IndexOf(':');
        return separatorIndex > 0 ? firstLine[..separatorIndex] : firstLine;
    }
}

using Sockseek.Api;
using Sockseek.Core.Services;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Write;

namespace Sockseek.Server.Persistence;

public sealed record CombinedJobPage(IReadOnlyList<JobSummaryDto> Items, string? NextCursor);
public sealed record CombinedSearchRawPage(IReadOnlyList<SearchRawResultDto> Items, long? NextSequence);
public sealed record CombinedTransferAttemptPage(IReadOnlyList<TransferAttemptHistoryDto> Items, int? NextAttemptNumber);
public sealed record CombinedWorkflowPage(IReadOnlyList<WorkflowSummaryDto> Items, string? NextCursor);
public sealed record CombinedSubmissionPage(IReadOnlyList<SubmissionSummaryDto> Items, string? NextCursor);

public sealed class HistoricalQueryFacade(EngineStateStore live, EngineSupervisor supervisor, PersistenceCoordinator persistence)
{
    public async Task<CombinedSubmissionPage> GetSubmissionsAsync(
        string? cursor,
        int limit,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        if (persistence.Submissions == null)
            throw new NotSupportedException("Submission history is unavailable because persistence is disabled or not started.");
        var liveJobs = live.GetJobs(new JobQuery(
            null,
            null,
            null,
            null,
            IncludeAll: true,
            Archived: archived));
        await persistence.WaitForAllHandoffsAsync(cancellationToken).ConfigureAwait(false);
        var page = await persistence.Submissions.GetSubmissionsAsync(
            new SubmissionHistoryQuery(cursor, limit, archived),
            cancellationToken).ConfigureAwait(false);
        return new CombinedSubmissionPage(
            page.Items.Select(item => WithLiveSubmissionCounts(
                SubmissionDtoMapper.ToSummary(item),
                liveJobs.Where(job => job.SubmissionId == item.Id).ToArray())).ToArray(),
            page.NextCursor);
    }

    public async Task<SubmissionDetailDto?> GetSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        if (persistence.Submissions == null)
            throw new NotSupportedException("Submission history is unavailable because persistence is disabled or not started.");
        var liveJobs = live.GetJobs(new JobQuery(
            null,
            null,
            null,
            null,
            IncludeAll: true,
            SubmissionId: submissionId));
        await persistence.WaitForAllHandoffsAsync(cancellationToken).ConfigureAwait(false);
        var submission = await persistence.Submissions
            .GetSubmissionAsync(submissionId, cancellationToken)
            .ConfigureAwait(false);
        if (submission == null)
            return null;
        var detail = SubmissionDtoMapper.ToDetail(submission);
        return detail with { Summary = WithLiveSubmissionCounts(detail.Summary, liveJobs) };
    }

    private static SubmissionSummaryDto WithLiveSubmissionCounts(
        SubmissionSummaryDto summary,
        IReadOnlyList<JobSummaryDto> jobs)
    {
        if (jobs.Count == 0)
            return summary;
        return summary with
        {
            TotalJobCount = jobs.Count,
            UserRootJobCount = jobs.Count(job => job.Role == ServerJobRole.UserRoot),
            ActiveJobCount = jobs.Count(job => job.LifecycleState != ServerJobLifecycleState.Terminal),
            FailedJobCount = jobs.Count(job => job.TerminalOutcome == ServerJobTerminalOutcome.Failed),
        };
    }

    public async Task<TransferTimelinePageDto> GetTransfersAsync(
        string? cursor,
        int limit,
        Guid? jobId,
        Guid? workflowId,
        string? direction,
        string? source,
        string? state,
        string? terminalOutcome,
        string? username,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        bool archived = false,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > TransferHistoryReader.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
        TransferHistoryReader.TransferCursorValue? boundary =
            TransferHistoryReader.DecodeCursor(cursor);

        var retainedCoverage = RetainedTransferCoverage();
        PersistedTransferPage? retained = persistence.TransferHistory == null
            || retainedCoverage.State == TransferRetainedCoverageState.Unavailable
            ? null
            : await persistence.TransferHistory.GetTransfersAsync(
                new TransferHistoryQuery(
                    cursor, limit, jobId, workflowId, direction, source, state,
                    terminalOutcome, username, fromUtc, toUtc, archived),
                cancellationToken).ConfigureAwait(false);

        var retainedRows = (retained?.Items ?? [])
            .Select(ToTransfer)
            .ToArray();
        var liveRows = new List<TransferHistoryDto>();

        foreach (TransferStateDto transfer in archived
                     ? []
                     : live.GetActiveTransferSnapshot())
        {
            TransferHistoryDto row = ToTimelineTransfer(transfer);
            if (MatchesTransferFilter(
                    row, boundary, jobId, workflowId, direction, source, state,
                    terminalOutcome, username, fromUtc, toUtc))
                liveRows.Add(row);
        }

        bool queueHasMore = false;
        var queuedRows = new List<TransferHistoryDto>();
        if (!archived
            && CanIncludeQueuedUploads(direction, source, state, terminalOutcome)
            && supervisor?.Sharing is { } sharing)
        {
            DateTimeOffset? before = boundary is { } value
                ? DateTimeOffset.FromUnixTimeMilliseconds(value.CreatedAtUtc)
                : null;
            var queued = sharing.Uploads.GetNewestQueuePage(
                before,
                boundary?.Id,
                limit + 1,
                username,
                fromUtc,
                toUtc);
            queueHasMore = queued.NextTransferId != null;
            foreach (var queuedItem in queued.Items)
            {
                if (live.GetLiveTransfer(queuedItem.TransferId) is not { } transfer)
                    continue;
                TransferHistoryDto row = ToTimelineTransfer(transfer);
                if (MatchesTransferFilter(
                    row, boundary, jobId, workflowId, direction, source, state,
                    terminalOutcome, username, fromUtc, toUtc))
                    queuedRows.Add(row);
            }
        }

        return ComposeTransferTimeline(
            retainedRows,
            liveRows,
            queuedRows,
            limit,
            retained?.NextCursor != null,
            queueHasMore,
            retainedCoverage);
    }

    internal static TransferTimelinePageDto ComposeTransferTimeline(
        IReadOnlyList<TransferHistoryDto> retained,
        IReadOnlyList<TransferHistoryDto> liveRows,
        IReadOnlyList<TransferHistoryDto> queuedRows,
        int limit,
        bool retainedHasMore,
        bool queueHasMore,
        TransferRetainedCoverageDto retainedCoverage)
    {
        var candidates = retained.ToDictionary(item => item.TransferId);
        foreach (TransferHistoryDto row in liveRows)
            candidates[row.TransferId] = candidates.TryGetValue(row.TransferId, out TransferHistoryDto? retainedRow)
                ? OverlayLiveTransfer(retainedRow, row)
                : row;
        foreach (TransferHistoryDto row in queuedRows)
            candidates[row.TransferId] = candidates.TryGetValue(row.TransferId, out TransferHistoryDto? queuedRetainedRow)
                ? OverlayLiveTransfer(queuedRetainedRow, row)
                : row;

        var ordered = candidates.Values
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.TransferId)
            .ToList();
        bool hasMore = ordered.Count > limit
            || retainedHasMore
            || queueHasMore;
        if (ordered.Count > limit)
            ordered.RemoveRange(limit, ordered.Count - limit);
        string? next = hasMore && ordered.Count > 0
            ? TransferHistoryReader.EncodeCursor(
                ordered[^1].CreatedAtUtc.ToUnixTimeMilliseconds(),
                ordered[^1].TransferId)
            : null;
        return new TransferTimelinePageDto(ordered, next, retainedCoverage);
    }

    private static TransferHistoryDto OverlayLiveTransfer(
        TransferHistoryDto retained,
        TransferHistoryDto current)
        => current with
        {
            // Creation order is immutable once retained. Mutable live state
            // must never move an existing row to a different keyset page.
            CreatedAtUtc = retained.CreatedAtUtc,
            RequestedAtUtc = current.RequestedAtUtc ?? retained.RequestedAtUtc,
            StartedAtUtc = current.StartedAtUtc ?? retained.StartedAtUtc,
            LastProgressAtUtc = current.LastProgressAtUtc ?? retained.LastProgressAtUtc,
            CompletedAtUtc = current.CompletedAtUtc ?? retained.CompletedAtUtc,
            FailureMessage = current.FailureMessage ?? retained.FailureMessage,
            File = current.File ?? retained.File,
            ArchivedAtUtc = retained.ArchivedAtUtc,
            GroupRef = current.GroupRef ?? retained.GroupRef,
            GroupDisplayPath = current.GroupDisplayPath ?? retained.GroupDisplayPath,
        };

    public async Task<TransferCommandReceiptDto> SetTransfersArchivedAsync(
        TransferArchiveFilter filter,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        if (persistence.TransferHistory is null)
            throw new NotSupportedException(
                "Transfer archive is unavailable because persistence is disabled or not started.");
        TransferArchiveResult result = await persistence.TransferHistory
            .SetArchivedAsync(filter, archived, cancellationToken)
            .ConfigureAwait(false);

        if (archived)
        {
            foreach (TransferStateDto transfer in live.GetCancellableTransferSnapshot()
                         .Where(transfer =>
                             transfer.Status.IsTerminal
                             && transfer.Identity.Direction.Equals(
                                 "Upload", StringComparison.OrdinalIgnoreCase)
                             && MatchesArchiveFilter(transfer, filter)))
            {
                live.RemoveUploadTransfer(transfer.TransferId);
            }
        }

        return new TransferCommandReceiptDto(
            result.ResolvedCount,
            result.ChangedCount,
            result.NoOpCount,
            result.RejectedCount,
            FailedCount: 0,
            result.Reasons.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new TransferCommandReasonCountDto(pair.Key, pair.Value))
                .ToArray());
    }

    private static bool MatchesArchiveFilter(
        TransferStateDto transfer,
        TransferArchiveFilter filter)
    {
        DateTimeOffset created = transfer.Scheduling?.RequestedAtUtc
            ?? DateTimeOffset.UnixEpoch;
        return (!filter.TransferId.HasValue || transfer.TransferId == filter.TransferId.Value)
            && (filter.Direction is null || EqualsFilter(transfer.Identity.Direction, filter.Direction))
            && (filter.TerminalOutcome is null
                || EqualsFilter(transfer.Status.TerminalOutcome.ToString(), filter.TerminalOutcome))
            && (filter.Username is null
                || string.Equals(transfer.Identity.Username, filter.Username, StringComparison.Ordinal))
            && (!filter.FromUtc.HasValue || created >= filter.FromUtc.Value)
            && (!filter.ToUtc.HasValue || created <= filter.ToUtc.Value);
    }

    private TransferRetainedCoverageDto RetainedTransferCoverage()
    {
        if (!persistence.IsEnabled)
            return new(TransferRetainedCoverageState.Unavailable, "PersistenceDisabled");
        if (!persistence.IsStarted || persistence.TransferHistory == null)
            return new(TransferRetainedCoverageState.Unavailable, "PersistenceUnavailable");
        return persistence.HealthSnapshot?.State switch
        {
            PersistenceHealthState.Degraded => new(
                TransferRetainedCoverageState.Degraded,
                "PersistenceDegraded"),
            PersistenceHealthState.Unhealthy => new(
                TransferRetainedCoverageState.Unavailable,
                "PersistenceUnhealthy"),
            _ => new(TransferRetainedCoverageState.Available),
        };
    }

    private static bool CanIncludeQueuedUploads(
        string? direction,
        string? source,
        string? state,
        string? terminalOutcome)
        => (direction == null || EqualsFilter(direction, "Upload"))
            && (source == null || EqualsFilter(source, "SoulseekPeer"))
            && (state == null || EqualsFilter(state, "Queued"))
            && (terminalOutcome == null || EqualsFilter(terminalOutcome, "None"));

    private static bool MatchesTransferFilter(
        TransferHistoryDto row,
        TransferHistoryReader.TransferCursorValue? boundary,
        Guid? jobId,
        Guid? workflowId,
        string? direction,
        string? source,
        string? state,
        string? terminalOutcome,
        string? username,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc)
    {
        long created = row.CreatedAtUtc.ToUnixTimeMilliseconds();
        if (boundary is { } cursor
            && (created > cursor.CreatedAtUtc
                || created == cursor.CreatedAtUtc
                && row.TransferId.CompareTo(cursor.Id) >= 0))
            return false;
        return (!jobId.HasValue || row.JobId == jobId)
            && (!workflowId.HasValue || row.WorkflowId == workflowId)
            && (direction == null || EqualsFilter(row.Direction, direction))
            && (source == null || EqualsFilter(row.Source, source))
            && (state == null || EqualsFilter(row.State, state))
            && (terminalOutcome == null || EqualsFilter(row.TerminalOutcome, terminalOutcome))
            && (username == null || string.Equals(row.Username, username, StringComparison.Ordinal))
            && (!fromUtc.HasValue || row.CreatedAtUtc >= fromUtc.Value)
            && (!toUtc.HasValue || row.CreatedAtUtc <= toUtc.Value);
    }

    private static bool EqualsFilter(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public async Task<CombinedWorkflowPage> GetWorkflowsAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateJobPageLimit(limit);
        var decoded = JobHistoryReader.DecodeCursor(cursor);
        var liveRows = live.GetWorkflowPageCandidates(
            decoded?.DisplayId,
            decoded?.Id,
            limit + 1);
        await persistence.WaitForAllHandoffsAsync(cancellationToken).ConfigureAwait(false);
        PersistedWorkflowPage? persisted = persistence.JobHistory == null
            ? null
            : await persistence.JobHistory.GetWorkflowsAsync(cursor, limit, cancellationToken).ConfigureAwait(false);

        var merged = new Dictionary<Guid, (long FirstDisplayId, WorkflowSummaryDto Summary)>();
        foreach (var workflow in persisted?.Items ?? [])
            merged[workflow.WorkflowId] = (workflow.FirstDisplayId, ToWorkflowSummary(workflow));
        foreach (var workflow in liveRows)
            merged[workflow.Summary.WorkflowId] = (workflow.FirstDisplayId, workflow.Summary);

        var ordered = merged.Values
            .OrderBy(workflow => workflow.FirstDisplayId)
            .ThenBy(workflow => workflow.Summary.WorkflowId)
            .ToList();
        bool hasMore = ordered.Count > limit
            || liveRows.Count > limit
            || persisted?.NextCursor != null;
        if (ordered.Count > limit)
            ordered.RemoveRange(limit, ordered.Count - limit);
        string? next = hasMore && ordered.Count > 0
            ? JobHistoryReader.EncodeCursor(
                ordered[^1].FirstDisplayId,
                ordered[^1].Summary.WorkflowId)
            : null;
        return new CombinedWorkflowPage(ordered.Select(item => item.Summary).ToArray(), next);
    }

    public async Task<WorkflowDetailDto?> GetWorkflowAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var liveWorkflow = live.GetWorkflow(workflowId);
        if (liveWorkflow != null)
            return liveWorkflow;
        await persistence.WaitForWorkflowHandoffAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        if (persistence.JobHistory == null)
            return null;
        var historical = await persistence.JobHistory.GetWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false);
        return historical == null ? null : new WorkflowDetailDto(ToWorkflowSummary(historical));
    }

    public async Task<JobDetailDto?> GetJobByDisplayIdAsync(
        Guid workflowId,
        int displayId,
        CancellationToken cancellationToken = default)
    {
        var liveDetail = supervisor.GetJobDetailByDisplayId(workflowId, displayId);
        if (liveDetail != null)
            return liveDetail;
        await persistence.WaitForWorkflowHandoffAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);
        if (persistence.JobHistory == null)
            return null;
        var job = await persistence.JobHistory.GetJobByDisplayIdAsync(workflowId, displayId, cancellationToken).ConfigureAwait(false);
        if (job == null)
            return null;
        int childCount = await persistence.JobHistory.GetChildCountAsync(job.Id, cancellationToken).ConfigureAwait(false);
        return new JobDetailDto(HistoricalJobDtoMapper.ToSummary(job), HistoricalJobDtoMapper.ToPayload(job), childCount);
    }

    private async Task<(TransferHistoryDto Transfer, TransferAttemptHistoryDto? LatestAttempt)?> GetHistoricalTransferAsync(
        Guid transferId,
        CancellationToken cancellationToken = default)
    {
        if (persistence.TransferHistory == null)
            return null;
        var detail = await persistence.TransferHistory.GetTransferAsync(transferId, cancellationToken).ConfigureAwait(false);
        return detail == null
            ? null
            : (ToTransfer(detail.Transfer), detail.LatestAttempt == null ? null : ToAttempt(detail.LatestAttempt));
    }

    public async Task<TransferDetailDto?> GetTransferDetailAsync(
        Guid transferId,
        CancellationToken cancellationToken = default)
    {
        LiveTransferDetail? liveDetail = live.GetLiveTransferDetail(transferId);
        var historical = await GetHistoricalTransferAsync(
            transferId,
            cancellationToken).ConfigureAwait(false);
        if (liveDetail is null && historical is null)
            return null;

        TransferQueueEstimateDto? estimate = null;
        if (liveDetail is not null
            && liveDetail.Transfer.Status.State.Equals("Queued", StringComparison.OrdinalIgnoreCase)
            && supervisor.Sharing is { } sharing)
        {
            var value = sharing.Uploads.GetQueueEstimate(transferId);
            estimate = new TransferQueueEstimateDto(
                value.AheadCount,
                value.QueueRevision);
        }

        return new TransferDetailDto(
            liveDetail is not null && historical is not null
                ? TransferDetailSource.Merged
                : liveDetail is not null
                    ? TransferDetailSource.Live
                    : TransferDetailSource.Historical,
            liveDetail?.Transfer,
            estimate,
            historical?.Transfer,
            Math.Max(
                liveDetail?.Transfer.Status.AttemptCount ?? 0,
                historical?.Transfer.AttemptCount ?? 0),
            liveDetail?.LatestAttempt ?? historical?.LatestAttempt);
    }

    public async Task<CombinedTransferAttemptPage?> GetTransferAttemptsAsync(
        Guid transferId,
        int afterAttemptNumber,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (persistence.TransferHistory == null)
            return null;
        var page = await persistence.TransferHistory.GetAttemptsAsync(transferId, afterAttemptNumber, limit, cancellationToken).ConfigureAwait(false);
        return page == null
            ? null
            : new CombinedTransferAttemptPage(page.Items.Select(ToAttempt).ToArray(), page.NextAttemptNumber);
    }

    public async Task<CombinedJobPage> GetJobsAsync(
        JobQuery query,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateJobPageLimit(limit);
        var decoded = JobHistoryReader.DecodeCursor(cursor);
        var liveRows = live.GetJobPageCandidates(query, decoded?.DisplayId, decoded?.Id, limit + 1);
        if (query.WorkflowId is { } workflowId)
        {
            await persistence.WaitForWorkflowHandoffAsync(workflowId, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await persistence.WaitForAllHandoffsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        PersistedJobPage? persisted = persistence.JobHistory == null
            ? null
            : await persistence.JobHistory.GetJobsAsync(new JobHistoryQuery(
                cursor,
                limit,
                query.LifecycleState?.ToString(),
                query.TerminalOutcome?.ToString(),
                query.SkipReason?.ToString(),
                query.Kind?.ToString(),
                query.WorkflowId,
                query.IncludeAll,
                query.ParentJobId,
                query.SubmissionId,
                query.Role?.ToString(),
                query.Archived), cancellationToken).ConfigureAwait(false);

        var merged = persisted?.Items
            .Select(HistoricalJobDtoMapper.ToSummary)
            .ToDictionary(job => job.JobId)
            ?? new Dictionary<Guid, JobSummaryDto>();
        foreach (var job in liveRows)
            merged[job.JobId] = job;

        var ordered = merged.Values
            .OrderBy(job => job.DisplayId)
            .ThenBy(job => job.JobId)
            .ToList();
        bool hasMore = ordered.Count > limit
            || liveRows.Count > limit
            || persisted?.NextCursor != null;
        if (ordered.Count > limit)
            ordered.RemoveRange(limit, ordered.Count - limit);
        string? next = hasMore && ordered.Count > 0
            ? JobHistoryReader.EncodeCursor(ordered[^1].DisplayId, ordered[^1].JobId)
            : null;
        return new CombinedJobPage(ordered, next);
    }

    public async Task<JobDetailDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var liveDetail = live.GetJobDetail(jobId);
        if (liveDetail != null)
            return liveDetail;
        await persistence.WaitForJobHandoffAsync(jobId, cancellationToken)
            .ConfigureAwait(false);
        if (persistence.JobHistory == null)
            return null;

        var job = await persistence.JobHistory.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
            return null;
        int childCount = await persistence.JobHistory.GetChildCountAsync(jobId, cancellationToken).ConfigureAwait(false);
        return new JobDetailDto(
            HistoricalJobDtoMapper.ToSummary(job),
            HistoricalJobDtoMapper.ToPayload(job),
            childCount);
    }

    public async Task<CombinedSearchRawPage?> GetRawSearchResultsAsync(
        Guid jobId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > SearchHistoryReader.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Search result page size must be between 1 and {SearchHistoryReader.MaximumPageSize}.");
        var liveResults = supervisor.GetSearchRawResults(jobId, afterSequence);
        if (liveResults != null)
        {
            var items = liveResults.Take(limit + 1).ToList();
            bool hasMore = items.Count > limit;
            if (hasMore) items.RemoveAt(items.Count - 1);
            return new CombinedSearchRawPage(items, hasMore && items.Count > 0 ? items[^1].Sequence : null);
        }
        await persistence.WaitForJobHandoffAsync(jobId, cancellationToken)
            .ConfigureAwait(false);
        if (persistence.SearchHistory == null)
            return null;

        var page = await persistence.SearchHistory.GetRawResultsAsync(jobId, afterSequence, limit, cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        return new CombinedSearchRawPage(
            page.Items.Select(result => new SearchRawResultDto(
                result.Sequence,
                checked((int)result.Revision),
                result.Username,
                result.RemoteFilename,
                result.SizeBytes,
                result.BitRate,
                result.SampleRate,
                result.DurationSeconds,
                result.Visibility,
                result.QueueLength,
                result.ObservedAtUtc)).ToArray(),
            page.NextSequence);
    }

    private static TransferHistoryDto ToTransfer(PersistedTransfer transfer)
        => new(
            transfer.Id, transfer.JobId, transfer.WorkflowId, transfer.Direction, transfer.Source, transfer.Username,
            transfer.RemotePath, transfer.LocalPath, transfer.State, transfer.TerminalOutcome, transfer.TotalBytes,
            transfer.TransferredBytes, transfer.AttemptCount, transfer.CreatedAtUtc, transfer.CompletedAtUtc,
            transfer.FailureReason, transfer.FailureMessage,
            Enum.TryParse(
                transfer.CancellationSource,
                ignoreCase: true,
                out TransferCancellationSource cancellationSource)
                ? cancellationSource
                : TransferCancellationSource.None,
            transfer.Revision,
            RequestedAtUtc: transfer.CreatedAtUtc,
            transfer.StartedAtUtc,
            transfer.LastProgressAtUtc,
            transfer.BytesPerSecond,
            ToFileMetadata(transfer.File),
            AvailableActions: [],
            transfer.ArchivedAtUtc,
            transfer.GroupRef);

    private static TransferHistoryDto ToTimelineTransfer(TransferStateDto transfer)
    {
        DateTimeOffset created = transfer.Scheduling?.RequestedAtUtc
            ?? DateTimeOffset.UnixEpoch;
        created = DateTimeOffset.FromUnixTimeMilliseconds(
            created.ToUnixTimeMilliseconds());
        return new TransferHistoryDto(
            transfer.TransferId,
            transfer.Identity.JobId,
            transfer.Identity.WorkflowId,
            transfer.Identity.Direction,
            transfer.Identity.Source,
            transfer.Identity.Username,
            transfer.Identity.RemotePath,
            transfer.Status.LocalPath,
            transfer.Status.State,
            transfer.Status.TerminalOutcome.ToString(),
            transfer.Progress.TotalBytes < 0 ? null : transfer.Progress.TotalBytes,
            transfer.Progress.BytesTransferred,
            transfer.Status.AttemptCount,
            created,
            CompletedAtUtc: null,
            transfer.Status.FailureReason.ToString(),
            FailureMessage: null,
            transfer.Status.CancellationSource,
            transfer.Revision,
            transfer.Scheduling?.RequestedAtUtc,
            transfer.Scheduling?.StartedAtUtc,
            transfer.Progress.LastProgressAtUtc,
            transfer.Progress.BytesPerSecond,
            transfer.File,
            transfer.Status.AvailableActions,
            GroupRef: transfer.Identity.GroupRef,
            GroupDisplayPath: transfer.Identity.GroupDisplayPath);
    }

    private static FileMetadataDto? ToFileMetadata(
        Sockseek.Core.Snapshots.TransferFileMetadataSnapshot? file)
        => file is null
            ? null
            : new FileMetadataDto(
                file.Name,
                file.Size,
                file.Extension,
                file.BitRate,
                file.BitDepth,
                file.SampleRate,
                file.Length,
                file.Attributes?.Select(attribute => new FileAttributeDto(
                    attribute.Type,
                    attribute.Value)).ToArray());

    private static TransferAttemptHistoryDto ToAttempt(PersistedTransferAttempt attempt)
        => new(
            attempt.Id, attempt.TransferId, attempt.AttemptNumber, attempt.Source, attempt.State,
            attempt.SourceUsername, attempt.SourcePath, attempt.OutputPath,
            attempt.StartedAtUtc, attempt.CompletedAtUtc, attempt.FailureReason, attempt.FailureMessage, attempt.Revision);

    private static WorkflowSummaryDto ToWorkflowSummary(PersistedWorkflowSummary workflow)
        => new(
            workflow.WorkflowId,
            workflow.Title,
            Enum.TryParse(workflow.State, ignoreCase: true, out ServerWorkflowState state)
                ? state
                : ServerWorkflowState.Completed,
            workflow.RootJobCount,
            workflow.ActiveJobCount,
            workflow.FailedJobCount,
            workflow.CompletedJobCount);

    private static void ValidateJobPageLimit(int limit)
    {
        if (limit is < 1 or > JobHistoryReader.MaximumPageSize)
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"Page size must be between 1 and {JobHistoryReader.MaximumPageSize}.");
    }
}

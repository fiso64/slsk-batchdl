using Sockseek.Api;
using Sockseek.Persistence.Read;

namespace Sockseek.Server.Persistence;

public sealed record CombinedJobPage(IReadOnlyList<JobSummaryDto> Items, string? NextCursor);
public sealed record CombinedSearchRawPage(IReadOnlyList<SearchRawResultDto> Items, long? NextSequence);
public sealed record CombinedTransferPage(IReadOnlyList<TransferHistoryDto> Items, string? NextCursor);
public sealed record CombinedTransferAttemptPage(IReadOnlyList<TransferAttemptHistoryDto> Items, int? NextAttemptNumber);
public sealed record CombinedWorkflowPage(IReadOnlyList<WorkflowSummaryDto> Items, string? NextCursor);

public sealed class HistoricalQueryFacade(EngineStateStore live, EngineSupervisor supervisor, PersistenceCoordinator persistence)
{
    public async Task<CombinedTransferPage> GetTransfersAsync(
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
        CancellationToken cancellationToken = default)
    {
        if (persistence.TransferHistory == null)
            return new CombinedTransferPage([], null);
        var page = await persistence.TransferHistory.GetTransfersAsync(
            new TransferHistoryQuery(
                cursor, limit, jobId, workflowId, direction, source, state, terminalOutcome, username, fromUtc, toUtc),
            cancellationToken).ConfigureAwait(false);
        return new CombinedTransferPage(page.Items.Select(ToTransfer).ToArray(), page.NextCursor);
    }

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
                query.ParentJobId), cancellationToken).ConfigureAwait(false);

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
                result.DurationSeconds)).ToArray(),
            page.NextSequence);
    }

    public async Task<SearchResultSnapshotDto<FileCandidateDto>?> GetFileResultsAsync(
        Guid jobId,
        FileSearchProjectionRequestDto? request,
        CancellationToken cancellationToken = default)
    {
        var liveResults = supervisor.GetFileResults(jobId, request);
        if (liveResults != null)
            return liveResults;
        await persistence.WaitForJobHandoffAsync(jobId, cancellationToken)
            .ConfigureAwait(false);
        if (persistence.SearchHistory == null || persistence.JobHistory == null)
            return null;

        var metadata = await persistence.SearchHistory.GetMetadataAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (metadata == null)
            return null;
        var job = await persistence.JobHistory.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
            return null;
        if (metadata.ResultPersistenceState is "Pruned" or "NotPersisted")
            return new SearchResultSnapshotDto<FileCandidateDto>(
                checked((int)metadata.Revision), false, [], metadata.ResultPersistenceState, metadata.ResultsPrunedAtUtc);

        var projection = request
            ?? HistoricalJobDtoMapper.DefaultFileProjection(job)
            ?? new FileSearchProjectionRequestDto(new SongQueryDto(Title: metadata.Query));
        var inputs = new List<Sockseek.Core.Models.SearchProjectionInput>();
        await foreach (var input in persistence.SearchHistory
            .ReadProjectionInputsAsync(jobId, cancellationToken)
            .ConfigureAwait(false))
            inputs.Add(input);
        return supervisor.ProjectHistoricalFileResults(inputs, metadata, projection);
    }

    public async Task<SearchResultSnapshotDto<AlbumFolderDto>?> GetFolderResultsAsync(
        Guid jobId,
        FolderSearchProjectionRequestDto? request,
        bool includeFiles,
        CancellationToken cancellationToken = default)
    {
        var liveResults = request != null
            ? supervisor.GetFolderResults(jobId, request)
            : supervisor.GetFolderResults(jobId, includeFiles);
        if (liveResults != null)
            return liveResults;
        await persistence.WaitForJobHandoffAsync(jobId, cancellationToken)
            .ConfigureAwait(false);
        var source = await LoadHistoricalProjectionSourceAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (source == null)
            return null;
        var projection = request
            ?? HistoricalJobDtoMapper.DefaultFolderProjection(source.Value.Job)
            ?? throw new ArgumentException("Historical album-folder projection requires an album query.");
        return supervisor.ProjectHistoricalFolderResults(
            source.Value.Inputs, source.Value.Metadata, projection.AlbumQuery,
            request?.IncludeFiles ?? includeFiles);
    }

    public async Task<SearchResultSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackResultsAsync(
        Guid jobId,
        AggregateTrackProjectionRequestDto? request,
        CancellationToken cancellationToken = default)
    {
        var liveResults = supervisor.GetAggregateTrackResults(jobId, request);
        if (liveResults != null)
            return liveResults;
        await persistence.WaitForJobHandoffAsync(jobId, cancellationToken)
            .ConfigureAwait(false);
        var source = await LoadHistoricalProjectionSourceAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (source == null)
            return null;
        var defaultFile = HistoricalJobDtoMapper.DefaultFileProjection(source.Value.Job);
        var query = request?.SongQuery
            ?? defaultFile?.SongQuery
            ?? new SongQueryDto(Title: source.Value.Metadata.Query);
        return supervisor.ProjectHistoricalAggregateTracks(
            source.Value.Inputs, source.Value.Metadata, query, request?.IncludeCandidates ?? false);
    }

    public async Task<SearchResultSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumResultsAsync(
        Guid jobId,
        AggregateAlbumProjectionRequestDto? request,
        CancellationToken cancellationToken = default)
    {
        var liveResults = supervisor.GetAggregateAlbumResults(jobId, request);
        if (liveResults != null)
            return liveResults;
        await persistence.WaitForJobHandoffAsync(jobId, cancellationToken)
            .ConfigureAwait(false);
        var source = await LoadHistoricalProjectionSourceAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (source == null)
            return null;
        var defaultFolder = HistoricalJobDtoMapper.DefaultFolderProjection(source.Value.Job);
        var query = request?.AlbumQuery
            ?? defaultFolder?.AlbumQuery
            ?? throw new ArgumentException("Historical aggregate-album projection requires an album query.");
        return supervisor.ProjectHistoricalAggregateAlbums(
            source.Value.Inputs, source.Value.Metadata, query, request?.IncludeFolders ?? false);
    }

    private async Task<(PersistedSearchMetadata Metadata, PersistedJob Job, List<Sockseek.Core.Models.SearchProjectionInput> Inputs)?>
        LoadHistoricalProjectionSourceAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (persistence.SearchHistory == null || persistence.JobHistory == null)
            return null;
        var metadata = await persistence.SearchHistory.GetMetadataAsync(jobId, cancellationToken).ConfigureAwait(false);
        var job = await persistence.JobHistory.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (metadata == null || job == null)
            return null;
        var inputs = new List<Sockseek.Core.Models.SearchProjectionInput>();
        if (metadata.ResultPersistenceState is not ("Pruned" or "NotPersisted"))
        {
            await foreach (var input in persistence.SearchHistory
                .ReadProjectionInputsAsync(jobId, cancellationToken)
                .ConfigureAwait(false))
                inputs.Add(input);
        }
        return (metadata, job, inputs);
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
            transfer.Revision);

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

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
        if (persistence.JobHistory == null)
            return new CombinedWorkflowPage(live.GetWorkflows().Take(limit).ToArray(), null);
        var page = await persistence.JobHistory.GetWorkflowsAsync(cursor, limit, cancellationToken).ConfigureAwait(false);
        return new CombinedWorkflowPage(
            page.Items.Select(workflow =>
            {
                var summaries = MergeWorkflowSummaries(
                    workflow.Jobs.Select(HistoricalJobDtoMapper.ToSummary),
                    live.GetWorkflow(workflow.WorkflowId, includeAll: true)?.Jobs ?? []);
                return ToWorkflowSummary(workflow.WorkflowId, summaries);
            }).ToArray(),
            page.NextCursor);
    }

    public async Task<WorkflowDetailDto?> GetWorkflowAsync(
        Guid workflowId,
        bool includeAll,
        CancellationToken cancellationToken = default)
    {
        var jobs = await GetHistoricalWorkflowJobsAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var liveWorkflow = live.GetWorkflow(workflowId, includeAll: true);
        if (jobs.Count == 0 && liveWorkflow == null)
            return null;
        var summaries = MergeWorkflowSummaries(
            jobs.Select(HistoricalJobDtoMapper.ToSummary),
            liveWorkflow?.Jobs ?? []);
        return new WorkflowDetailDto(
            ToWorkflowSummary(workflowId, summaries),
            summaries.Where(job => includeAll || job.ParentJobId == null).ToArray());
    }

    public async Task<WorkflowTreeDto?> GetWorkflowTreeAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var jobs = await GetHistoricalWorkflowJobsAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var liveWorkflow = live.GetWorkflow(workflowId, includeAll: true);
        if (jobs.Count == 0 && liveWorkflow == null)
            return null;
        var summaries = MergeWorkflowSummaries(
            jobs.Select(HistoricalJobDtoMapper.ToSummary),
            liveWorkflow?.Jobs ?? []).ToDictionary(job => job.JobId);
        var children = summaries.Values
            .Where(job => job.ParentJobId.HasValue && summaries.ContainsKey(job.ParentJobId.Value))
            .GroupBy(job => job.ParentJobId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(job => job.DisplayId).ToArray());
        WorkflowJobNodeDto Build(JobSummaryDto job, HashSet<Guid> path)
        {
            if (!path.Add(job.JobId))
                return new WorkflowJobNodeDto(job, []);
            var nodes = children.GetValueOrDefault(job.JobId)?.Select(child => Build(child, path)).ToArray() ?? [];
            path.Remove(job.JobId);
            return new WorkflowJobNodeDto(job, nodes);
        }
        var roots = summaries.Values.Where(job => !job.ParentJobId.HasValue || !summaries.ContainsKey(job.ParentJobId.Value))
            .OrderBy(job => job.DisplayId)
            .Select(job => Build(job, []))
            .ToArray();
        return new WorkflowTreeDto(ToWorkflowSummary(workflowId, summaries.Values.ToArray()), roots);
    }

    public async Task<JobDetailDto?> GetJobByDisplayIdAsync(
        Guid workflowId,
        int displayId,
        CancellationToken cancellationToken = default)
    {
        var liveDetail = supervisor.GetJobDetailByDisplayId(workflowId, displayId);
        if (liveDetail != null)
            return liveDetail;
        if (persistence.JobHistory == null)
            return null;
        var job = await persistence.JobHistory.GetJobByDisplayIdAsync(workflowId, displayId, cancellationToken).ConfigureAwait(false);
        return job == null
            ? null
            : new JobDetailDto(HistoricalJobDtoMapper.ToSummary(job), HistoricalJobDtoMapper.ToPayload(job), []);
    }

    public async Task<TransferHistoryDetailDto?> GetTransferAsync(
        Guid transferId,
        int attemptLimit,
        CancellationToken cancellationToken = default)
    {
        if (persistence.TransferHistory == null)
            return null;
        var detail = await persistence.TransferHistory.GetTransferAsync(transferId, attemptLimit, cancellationToken).ConfigureAwait(false);
        return detail == null
            ? null
            : new TransferHistoryDetailDto(ToTransfer(detail.Transfer), detail.Attempts.Select(ToAttempt).ToArray());
    }

    public async Task<TransferDetailDto?> GetTransferDetailAsync(
        Guid transferId,
        int attemptLimit,
        CancellationToken cancellationToken = default)
    {
        TransferStateDto? liveTransfer = live.GetLiveTransfer(transferId);
        TransferHistoryDetailDto? historical = await GetTransferAsync(
            transferId,
            attemptLimit,
            cancellationToken).ConfigureAwait(false);
        if (liveTransfer is null && historical is null)
            return null;

        TransferQueueEstimateDto? estimate = null;
        if (liveTransfer is not null
            && liveTransfer.Status.State.Equals("Queued", StringComparison.OrdinalIgnoreCase)
            && supervisor.Sharing is { } sharing)
        {
            var value = sharing.Uploads.GetQueueEstimate(transferId);
            estimate = new TransferQueueEstimateDto(
                value.AheadCount,
                value.QueueRevision);
        }

        return new TransferDetailDto(
            liveTransfer is not null && historical is not null
                ? TransferDetailSource.Merged
                : liveTransfer is not null
                    ? TransferDetailSource.Live
                    : TransferDetailSource.Historical,
            liveTransfer,
            estimate,
            historical?.Transfer,
            historical?.Attempts ?? []);
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
        if (persistence.JobHistory == null)
            return new CombinedJobPage(live.GetJobs(query).Take(limit).ToArray(), null);

        var page = await persistence.JobHistory.GetJobsAsync(new JobHistoryQuery(
            cursor,
            limit,
            query.LifecycleState?.ToString(),
            query.TerminalOutcome?.ToString(),
            query.SkipReason?.ToString(),
            query.Kind?.ToString(),
            query.WorkflowId,
            query.IncludeAll), cancellationToken).ConfigureAwait(false);

        var liveById = live.GetJobs(query).ToDictionary(job => job.JobId);
        var items = page.Items
            .Select(job => liveById.GetValueOrDefault(job.Id) ?? HistoricalJobDtoMapper.ToSummary(job))
            .ToArray();
        return new CombinedJobPage(items, page.NextCursor);
    }

    public async Task<JobDetailDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var liveDetail = live.GetJobDetail(jobId);
        if (liveDetail != null)
            return liveDetail;
        if (persistence.JobHistory == null)
            return null;

        var job = await persistence.JobHistory.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
            return null;
        var children = await persistence.JobHistory.GetChildrenAsync(jobId, cancellationToken).ConfigureAwait(false);
        return new JobDetailDto(
            HistoricalJobDtoMapper.ToSummary(job),
            HistoricalJobDtoMapper.ToPayload(job),
            children.Select(HistoricalJobDtoMapper.ToSummary).ToArray());
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

    private async Task<IReadOnlyList<PersistedJob>> GetHistoricalWorkflowJobsAsync(Guid workflowId, CancellationToken cancellationToken)
        => persistence.JobHistory == null
            ? []
            : await persistence.JobHistory.GetWorkflowJobsAsync(workflowId, cancellationToken).ConfigureAwait(false);

    private static WorkflowSummaryDto ToWorkflowSummary(Guid workflowId, IReadOnlyList<PersistedJob> jobs)
        => ToWorkflowSummary(workflowId, jobs.Select(HistoricalJobDtoMapper.ToSummary).ToArray());

    private static WorkflowSummaryDto ToWorkflowSummary(Guid workflowId, IReadOnlyList<JobSummaryDto> jobs)
    {
        var ordered = jobs.OrderBy(job => job.DisplayId).ToArray();
        int active = ordered.Count(job => job.LifecycleState != ServerJobLifecycleState.Terminal);
        int failed = ordered.Count(job => job.TerminalOutcome == ServerJobTerminalOutcome.Failed);
        int completed = ordered.Count(job => job.LifecycleState == ServerJobLifecycleState.Terminal);
        var titleJob = ordered.FirstOrDefault(job => !string.IsNullOrWhiteSpace(job.ItemName)) ?? ordered[0];
        string title = titleJob.ItemName ?? ordered[0].QueryText ?? ordered[0].Kind.ToString();
        var state = active > 0 ? ServerWorkflowState.Active
            : failed > 0 ? ServerWorkflowState.Failed
            : ServerWorkflowState.Completed;
        return new WorkflowSummaryDto(
            workflowId,
            title,
            state,
            ordered.Where(job => job.ParentJobId == null).Select(job => job.JobId).ToArray(),
            active,
            failed,
            completed);
    }

    private static IReadOnlyList<JobSummaryDto> MergeWorkflowSummaries(
        IEnumerable<JobSummaryDto> historical,
        IEnumerable<JobSummaryDto> current)
    {
        var merged = historical.ToDictionary(job => job.JobId);
        foreach (var job in current)
            merged[job.JobId] = job;
        return merged.Values.OrderBy(job => job.DisplayId).ToArray();
    }
}

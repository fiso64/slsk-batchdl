using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;

namespace Sockseek.Server;

public static class ServerSnapshotMapper
{
    public static JobSummaryDto ToJobSummary(
        JobSnapshot job,
        Guid? parentJobId = null,
        Guid? resultJobId = null,
        Guid? sourceJobId = null,
        JobLifecycleState? lifecycleState = null,
        JobActivityPhase? activityPhase = null,
        DateTimeOffset? activityUntilUtc = null,
        JobTerminalOutcome? terminalOutcome = null)
    {
        var effectiveLifecycleState = lifecycleState ?? job.LifecycleState;
        var effectiveActivityPhase = activityPhase ?? job.ActivityPhase;
        var effectiveTerminalOutcome = terminalOutcome ?? job.TerminalOutcome;

        return new JobSummaryDto(
            job.Id,
            job.DisplayId,
            job.WorkflowId,
            ToServerJobKind(job.Kind),
            ToServerJobLifecycleState(effectiveLifecycleState),
            ToServerJobActivityPhase(effectiveActivityPhase),
            effectiveActivityPhase == JobActivityPhase.None ? null : activityUntilUtc ?? job.ActivityUntilUtc,
            ToServerJobTerminalOutcome(effectiveTerminalOutcome),
            ToServerJobSkipReason(job.SkipReason),
            job.ItemName,
            job.QueryText,
            ToServerFailureReason(job.FailureReason),
            job.FailureMessage,
            parentJobId,
            resultJobId,
            sourceJobId,
            job.Discovery?.RawResultCount,
            job.Discovery?.LockedFileCount,
            job.AppliedAutoProfiles,
            BuildActions(job),
            job.FailureDetail,
            ToServerJobCancellationSource(job.CancellationSource),
            job.PrintOption);
    }

    public static JobSummaryDto ToSubmittedJobSummary(Job job, Guid? sourceJobId = null)
        => ToJobSummary(CoreSnapshotFactory.CreateJob(job, revision: 0), sourceJobId: sourceJobId);

    public static JobPayloadDto ToJobPayload(
        JobSnapshot job,
        Func<Guid, string?> getTransferState,
        Func<Guid, ServerJobKind?, int> countDescendants)
        => job.Payload switch
        {
            ExtractJobSnapshotPayload extract => new ExtractJobPayloadDto(
                extract.Input,
                extract.InputType,
                extract.ResultJobId,
                extract.AutoProcessResult,
                ToJobDraft(extract.ResultDraft)),
            SearchJobSnapshotPayload search => new SearchJobPayloadDto(
                search.QueryText,
                search.DefaultFileProjection == null
                    ? null
                    : new FileSearchProjectionRequestDto(
                        ToSongQueryDto(search.DefaultFileProjection.Query),
                        search.DefaultFileProjection.IncludeFullResults),
                search.DefaultFolderProjection == null
                    ? null
                    : new FolderSearchProjectionRequestDto(
                        ToAlbumQueryDto(search.DefaultFolderProjection.Query),
                        search.DefaultFolderProjection.IncludeFiles),
                search.ResultCount,
                search.Revision,
                search.IsComplete),
            SongJobSnapshotPayload song => ToSongJobPayloadDto(job, song, getTransferState(job.Id)),
            AlbumJobSnapshotPayload album => new AlbumJobPayloadDto(
                ToAlbumQueryDto(album.Query),
                album.ResultCount,
                album.DownloadPath,
                album.ResolvedTarget?.Username,
                album.ResolvedTarget?.FolderPath,
                album.ResolvedTarget != null ? album.TrackJobs.Count : null,
                album.ResolvedTarget != null ? album.TrackJobs.Count(IsTerminalJob) : null,
                album.ResolvedTarget != null ? album.TrackJobs.Count(IsSuccessfulJob) : null,
                album.ResolvedTarget != null ? album.TrackJobs.Count(IsFailedOrSkippedJob) : null,
                null,
                null),
            AggregateJobSnapshotPayload aggregate => new AggregateJobPayloadDto(
                ToSongQueryDto(aggregate.Query),
                aggregate.Songs.Count,
                aggregate.Songs.Count(IsTerminalJob),
                aggregate.Songs.Count(IsSuccessfulJob),
                aggregate.Songs.Count(IsFailedOrSkippedJob),
                null),
            AlbumAggregateJobSnapshotPayload albumAggregate => new AlbumAggregateJobPayloadDto(
                ToAlbumQueryDto(albumAggregate.Query),
                albumAggregate.AlbumCount > 0
                    ? albumAggregate.AlbumCount
                    : countDescendants(job.Id, ServerJobKind.Album)),
            JobListSnapshotPayload list => new JobListPayloadDto(
                list.Count,
                list.Jobs.Count(IsActiveJob),
                list.Jobs.Count(IsTerminalJob),
                list.Jobs.Count(IsSuccessfulJob),
                list.Jobs.Count(IsFailedOrSkippedJob),
                null),
            RetrieveFolderJobSnapshotPayload retrieve => new RetrieveFolderJobPayloadDto(
                retrieve.TargetFolder.FolderPath,
                retrieve.TargetFolder.Username,
                retrieve.NewFilesFoundCount,
                ToServerFolderRetrievalOutcome(retrieve.RetrievalOutcome),
                retrieve.RetrievalCancelled,
                ToAlbumFolderDto(retrieve.TargetFolder, includeFiles: true)),
            GenericJobSnapshotPayload generic => new GenericJobPayloadDto(generic.Text),
            _ => new GenericJobPayloadDto(job.QueryText ?? ToServerJobKind(job.Kind).ToWireString()),
        };

    public static SongJobPayloadDto ToSongJobPayloadDto(JobSnapshot job, string? transferState = null)
        => job.Payload is SongJobSnapshotPayload song
            ? ToSongJobPayloadDto(job, song, transferState)
            : throw new ArgumentException($"Job {job.Id} is not a song snapshot.", nameof(job));

    public static SongJobPayloadDto ToSongJobPayloadDto(
        JobSnapshot job,
        SongJobSnapshotPayload song,
        string? transferState = null)
    {
        long? totalBytes = song.FileSize > 0 ? song.FileSize : song.ResolvedTarget?.Size;
        double? progressPercent = totalBytes > 0
            ? Math.Round((double)song.BytesTransferred / totalBytes.Value * 100, 2)
            : null;

        return new SongJobPayloadDto(
            ToSongQueryDto(song.Query),
            song.CandidateCount,
            song.DownloadPath,
            song.ResolvedTarget?.Username,
            song.ResolvedTarget?.Filename,
            song.ResolvedTarget?.Peer.HasFreeUploadSlot,
            song.ResolvedTarget?.Peer.UploadSpeed,
            song.ResolvedTarget?.Size,
            song.ResolvedTarget?.SampleRate,
            song.ResolvedTarget?.Extension,
            song.ResolvedTarget?.Attributes?.Select(ToFileAttributeDto).ToList(),
            job.Id,
            job.DisplayId,
            null,
            ToServerJobLifecycleState(job.LifecycleState),
            ToServerJobActivityPhase(job.ActivityPhase),
            job.ActivityUntilUtc,
            ToServerJobTerminalOutcome(job.TerminalOutcome),
            ToServerJobSkipReason(job.SkipReason),
            ToServerFailureReason(job.FailureReason),
            job.FailureMessage,
            song.BytesTransferred,
            totalBytes,
            progressPercent,
            BuildActions(job),
            transferState,
            ToServerJobCancellationSource(job.CancellationSource),
            ToServerSongDownloadSource(song.DownloadSource));
    }

    public static FileCandidateDto ToFileCandidateDto(FileCandidateSnapshot candidate)
        => new(
            new FileCandidateRefDto(candidate.Username, candidate.Filename),
            candidate.Username,
            candidate.Filename,
            new PeerInfoDto(candidate.Peer.Username, candidate.Peer.HasFreeUploadSlot, candidate.Peer.UploadSpeed),
            candidate.Size,
            candidate.BitRate,
            candidate.SampleRate,
            candidate.Length,
            candidate.Extension,
            candidate.Attributes?.Select(ToFileAttributeDto).ToList());

    public static AlbumFolderDto ToAlbumFolderDto(AlbumFolderSnapshot folder, bool includeFiles)
        => new(
            new AlbumFolderRefDto(folder.Username, folder.FolderPath),
            folder.Username,
            folder.FolderPath,
            new PeerInfoDto(
                folder.Peer.Username,
                folder.Peer.HasFreeUploadSlot,
                folder.Peer.UploadSpeed),
            folder.SearchFileCount,
            folder.SearchAudioFileCount,
            includeFiles
                ? folder.Files.Select(file => ToFileCandidateDto(file.Candidate)).ToList()
                : null,
            folder.IsFullyRetrieved);

    public static SongQueryDto ToSongQueryDto(SongQuerySnapshot query)
        => new(query.Artist, query.Title, query.Album, query.URI, query.Length, query.ArtistMaybeWrong);

    public static AlbumQueryDto ToAlbumQueryDto(AlbumQuerySnapshot query)
        => new(query.Artist, query.Album, query.SearchHint, query.URI, query.ArtistMaybeWrong);

    public static ServerJobKind ToServerJobKind(JobSnapshotKind kind)
        => kind switch
        {
            JobSnapshotKind.Extract => ServerJobKind.Extract,
            JobSnapshotKind.Search => ServerJobKind.Search,
            JobSnapshotKind.Song => ServerJobKind.Song,
            JobSnapshotKind.Album => ServerJobKind.Album,
            JobSnapshotKind.Aggregate => ServerJobKind.Aggregate,
            JobSnapshotKind.AlbumAggregate => ServerJobKind.AlbumAggregate,
            JobSnapshotKind.JobList => ServerJobKind.JobList,
            JobSnapshotKind.RetrieveFolder => ServerJobKind.RetrieveFolder,
            _ => ServerJobKind.Generic,
        };

    public static ServerJobKind ToServerJobKind(Job job) => job switch
    {
        ExtractJob => ServerJobKind.Extract,
        SearchJob => ServerJobKind.Search,
        SongJob => ServerJobKind.Song,
        AlbumJob => ServerJobKind.Album,
        JobList => ServerJobKind.JobList,
        RetrieveFolderJob => ServerJobKind.RetrieveFolder,
        AggregateJob => ServerJobKind.Aggregate,
        AlbumAggregateJob => ServerJobKind.AlbumAggregate,
        _ => ServerJobKind.Generic,
    };

    public static ServerJobLifecycleState ToServerJobLifecycleState(JobLifecycleState state)
        => Enum.Parse<ServerJobLifecycleState>(state.ToString());

    public static ServerJobActivityPhase ToServerJobActivityPhase(JobActivityPhase phase)
        => Enum.Parse<ServerJobActivityPhase>(phase.ToString());

    public static ServerJobTerminalOutcome ToServerJobTerminalOutcome(JobTerminalOutcome outcome)
        => Enum.Parse<ServerJobTerminalOutcome>(outcome.ToString());

    public static ServerSongDownloadSource ToServerSongDownloadSource(SongDownloadSource source)
        => Enum.Parse<ServerSongDownloadSource>(source.ToString());

    public static ServerJobSkipReason ToServerJobSkipReason(JobSkipReason reason)
        => Enum.Parse<ServerJobSkipReason>(reason.ToString());

    public static ServerJobFailureReason? ToServerFailureReason(JobFailureReason reason)
        => reason == JobFailureReason.None
            ? null
            : Enum.Parse<ServerJobFailureReason>(reason.ToString());

    public static ServerJobCancellationSource ToServerJobCancellationSource(JobCancellationSource source)
        => Enum.Parse<ServerJobCancellationSource>(source.ToString());

    public static ServerFolderRetrievalOutcome ToServerFolderRetrievalOutcome(FolderRetrievalOutcome outcome)
        => Enum.Parse<ServerFolderRetrievalOutcome>(outcome.ToString());

    public static bool ContainsNestedJob(JobSnapshot container, Guid jobId)
        => container.Payload switch
        {
            AlbumJobSnapshotPayload album => album.TrackJobs.Any(song => song.Id == jobId),
            AggregateJobSnapshotPayload aggregate => aggregate.Songs.Any(song => song.Id == jobId),
            JobListSnapshotPayload list => list.Jobs.Any(job => job.Id == jobId || ContainsNestedJob(job, jobId)),
            _ => false,
        };

    public static bool IsRunningOrPending(JobSnapshot job)
        => job.LifecycleState is JobLifecycleState.Pending or JobLifecycleState.Running;

    public static bool IsActiveJob(JobSnapshot job)
        => job.LifecycleState != JobLifecycleState.Terminal;

    public static bool IsTerminalJob(JobSnapshot job)
        => job.LifecycleState == JobLifecycleState.Terminal;

    public static bool IsSuccessfulJob(JobSnapshot job)
        => job.TerminalOutcome == JobTerminalOutcome.Succeeded
            || (job.TerminalOutcome == JobTerminalOutcome.Skipped && job.SkipReason == JobSkipReason.AlreadyExists);

    public static bool IsFailedOrSkippedJob(JobSnapshot job)
        => job.TerminalOutcome is JobTerminalOutcome.Failed
                or JobTerminalOutcome.Cancelled
                or JobTerminalOutcome.PartialSuccess
            || (job.TerminalOutcome == JobTerminalOutcome.Skipped && job.SkipReason != JobSkipReason.AlreadyExists);

    private static IReadOnlyList<ResourceActionDto> BuildActions(JobSnapshot job)
        => job.CanCancel
            ? [CancelAction(job.Id)]
            : [];

    private static ResourceActionDto CancelAction(Guid jobId)
        => new(ServerResourceActionKind.Cancel, "POST", $"/api/jobs/{jobId}/cancel");

    private static FileAttributeDto ToFileAttributeDto(FileAttributeSnapshot attribute)
        => new(attribute.Type, attribute.Value);

    private static JobDraftDto? ToJobDraft(JobDraftSnapshot? draft)
        => draft switch
        {
            null => null,
            ExtractJobDraftSnapshot extract => new ExtractJobDraftDto(
                extract.Input,
                extract.InputType,
                extract.AutoProcessResult,
                DownloadSettings: null,
                extract.ResultDownloadBehavior == null ? null : ToDownloadBehaviorPolicyDto(extract.ResultDownloadBehavior),
                ToProvenanceDto(extract.Provenance)),
            AlbumSearchJobDraftSnapshot search => new AlbumSearchJobDraftDto(
                ToAlbumQueryDto(search.AlbumQuery),
                DownloadSettings: null,
                ToProvenanceDto(search.Provenance)),
            TrackSearchJobDraftSnapshot search => new TrackSearchJobDraftDto(
                ToSongQueryDto(search.SongQuery),
                search.IncludeFullResults,
                DownloadSettings: null,
                ToProvenanceDto(search.Provenance)),
            SongJobDraftSnapshot song => new SongJobDraftDto(
                ToSongQueryDto(song.SongQuery),
                song.DownloadBehavior == null ? null : ToDownloadBehaviorPolicyDto(song.DownloadBehavior),
                DownloadSettings: null,
                ToProvenanceDto(song.Provenance)),
            AlbumJobDraftSnapshot album => new AlbumJobDraftDto(
                ToAlbumQueryDto(album.AlbumQuery),
                album.DownloadBehavior == null ? null : ToDownloadBehaviorPolicyDto(album.DownloadBehavior),
                DownloadSettings: null,
                ToProvenanceDto(album.Provenance)),
            AggregateJobDraftSnapshot aggregate => new AggregateJobDraftDto(
                ToSongQueryDto(aggregate.SongQuery),
                aggregate.DownloadBehavior == null ? null : ToDownloadBehaviorPolicyDto(aggregate.DownloadBehavior),
                DownloadSettings: null,
                ToProvenanceDto(aggregate.Provenance)),
            AlbumAggregateJobDraftSnapshot aggregate => new AlbumAggregateJobDraftDto(
                ToAlbumQueryDto(aggregate.AlbumQuery),
                aggregate.DownloadBehavior == null ? null : ToDownloadBehaviorPolicyDto(aggregate.DownloadBehavior),
                DownloadSettings: null,
                ToProvenanceDto(aggregate.Provenance)),
            JobListDraftSnapshot list => new JobListJobDraftDto(
                list.Name,
                list.Jobs.Select(ToJobDraft).OfType<JobDraftDto>().ToList(),
                DownloadSettings: null,
                ToProvenanceDto(list.Provenance)),
            _ => null,
        };

    private static JobProvenanceDto? ToProvenanceDto(JobProvenanceSnapshot? provenance)
        => provenance == null
            ? null
            : new JobProvenanceDto(
                provenance.ItemNumber,
                provenance.LineNumber,
                ToSourceMutationDto(provenance.SourceMutation));

    private static SourceMutationDto? ToSourceMutationDto(SourceMutationSnapshot? mutation)
        => mutation == null
            ? null
            : new SourceMutationDto(
                mutation.Kind.ToString(),
                mutation.Source,
                mutation.LineNumber,
                mutation.ItemNumber,
                mutation.CsvColumnCount,
                mutation.TrackUri);

    private static DownloadBehaviorPolicyDto ToDownloadBehaviorPolicyDto(DownloadBehaviorPolicySnapshot policy)
        => new(policy.Default, policy.Song, policy.Album, policy.Aggregate, policy.AlbumAggregate);
}

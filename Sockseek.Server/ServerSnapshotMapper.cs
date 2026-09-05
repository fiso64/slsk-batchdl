using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Planning;
using Sockseek.Core.Services;

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
            job.PrintOption.ToServer(),
            job.SubmissionId,
            ToServerJobRole(job.SemanticRole),
            job.CreatedAtUtc,
            DiscoveryPublicFileCount: job.Discovery?.RawResultCount,
            DiscoveryObservedPeerCount: job.Discovery?.ObservedPeerCount);
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
                extract.ResultJobId),
            SearchJobSnapshotPayload search => new SearchJobPayloadDto(
                search.QueryText,
                search.DefaultFolderProjection != null
                    ? ServerSearchDefaultProjectionKind.Album
                    : search.DefaultFileProjection != null
                        ? ServerSearchDefaultProjectionKind.Track
                        : ServerSearchDefaultProjectionKind.GenericFile,
                search.DefaultFileProjection == null
                    ? null
                    : ToSongQueryDto(search.DefaultFileProjection.Query),
                search.DefaultFolderProjection == null
                    ? null
                    : ToAlbumQueryDto(search.DefaultFolderProjection.Query),
                search.DefaultFileProjection?.IncludeFullResults ?? false,
                search.ResultCount,
                search.Revision,
                search.IsComplete),
            SongJobSnapshotPayload song => ToSongJobPayloadDto(job, song, getTransferState(job.Id)),
            AlbumJobSnapshotPayload album => new AlbumJobPayloadDto(
                ToAlbumQueryDto(album.Query),
                album.ResultCount,
                ToDirectoryDownloadStateDto(album.Directory),
                album.ResolvedTarget?.Username,
                album.ResolvedTarget?.FolderPath),
            RemoteFileJobSnapshotPayload remoteFile => new RemoteFileJobPayloadDto(
                ToPeerFileTargetDto(remoteFile.Target),
                remoteFile.OutputPath.Components,
                ToFileDownloadStateDto(remoteFile.File)),
            RemoteDirectoryJobSnapshotPayload remoteDirectory => new RemoteDirectoryJobPayloadDto(
                remoteDirectory.SourceKind == RemoteDirectorySourceSnapshotKind.PeerDirectory
                    ? RemoteDirectorySourceKindDto.PeerDirectory
                    : RemoteDirectorySourceKindDto.Resolved,
                remoteDirectory.DirectorySource?.Username,
                remoteDirectory.DirectorySource?.FolderPath,
                ToDirectoryDownloadStateDto(remoteDirectory.Directory)),
            AggregateJobSnapshotPayload aggregate => new AggregateJobPayloadDto(
                ToSongQueryDto(aggregate.Query),
                aggregate.Songs.Count,
                aggregate.Songs.Count(IsTerminalJob),
                aggregate.Songs.Count(IsSuccessfulJob),
                aggregate.Songs.Count(IsFailedOrSkippedJob)),
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
                list.Jobs.Count(IsFailedOrSkippedJob)),
            RetrieveFolderJobSnapshotPayload retrieve => new RetrieveFolderJobPayloadDto(
                retrieve.Directory.FolderPath,
                retrieve.Directory.Username,
                retrieve.NewFilesFoundCount,
                ToServerFolderRetrievalOutcome(retrieve.RetrievalOutcome),
                retrieve.RetrievalCancelled),
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
        long? totalBytes = song.File.FileSize > 0
            ? song.File.FileSize
            : song.ResolvedTarget?.Size ?? song.ExactTarget?.Size;
        double? progressPercent = totalBytes > 0
            ? Math.Round((double)song.File.BytesTransferred / totalBytes.Value * 100, 2)
            : null;

        return new SongJobPayloadDto(
            ToSongQueryDto(song.Query),
            song.CandidateCount,
            new FileDownloadStateDto(
                song.File.DownloadPath,
                song.File.BytesTransferred,
                totalBytes,
                progressPercent),
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
            ToServerJobLifecycleState(job.LifecycleState),
            ToServerJobActivityPhase(job.ActivityPhase),
            job.ActivityUntilUtc,
            ToServerJobTerminalOutcome(job.TerminalOutcome),
            ToServerJobSkipReason(job.SkipReason),
            ToServerFailureReason(job.FailureReason),
            job.FailureMessage,
            BuildActions(job),
            transferState,
            ToServerJobCancellationSource(job.CancellationSource),
            ToServerSongDownloadSource(song.DownloadSource),
            song.ExactTarget == null ? null : ToPeerFileTargetDto(song.ExactTarget));
    }

    public static SongQueryDto ToSongQueryDto(SongQuerySnapshot query)
        => new(query.Artist, query.Title, query.Album, query.URI, query.Length, query.ArtistMaybeWrong);

    public static SongQueryDto ToSongQueryDto(SongQuery query)
        => new(
            Optional(query.Artist),
            Optional(query.Title),
            Optional(query.Album),
            Optional(query.URI),
            Optional(query.Length),
            query.ArtistMaybeWrong);

    public static SongQueryDto ToSongQueryDto(SongQueryDefinition query)
        => new(
            Optional(query.Artist),
            Optional(query.Title),
            Optional(query.Album),
            Optional(query.Uri),
            Optional(query.Length),
            query.ArtistMaybeWrong);

    public static AlbumQueryDto ToAlbumQueryDto(AlbumQuerySnapshot query)
        => new(query.Artist, query.Album, query.SearchHint, query.URI, query.ArtistMaybeWrong);

    public static AlbumQueryDto ToAlbumQueryDto(AlbumQuery query)
        => new(
            Optional(query.Artist),
            Optional(query.Album),
            Optional(query.SearchHint),
            Optional(query.URI),
            query.ArtistMaybeWrong);

    public static AlbumQueryDto ToAlbumQueryDto(AlbumQueryDefinition query)
        => new(
            Optional(query.Artist),
            Optional(query.Album),
            Optional(query.SearchHint),
            Optional(query.Uri),
            query.ArtistMaybeWrong);

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
            JobSnapshotKind.RemoteFile => ServerJobKind.RemoteFile,
            JobSnapshotKind.RemoteDirectory => ServerJobKind.RemoteDirectory,
            _ => ServerJobKind.Generic,
        };

    private static string? Optional(string value)
        => value.Length == 0 ? null : value;

    private static int? Optional(int value)
        => value < 0 ? null : value;

    public static ServerJobKind ToServerJobKind(Job job) => job switch
    {
        ExtractJob => ServerJobKind.Extract,
        SearchJob => ServerJobKind.Search,
        SongJob => ServerJobKind.Song,
        AlbumJob => ServerJobKind.Album,
        RemoteFileJob => ServerJobKind.RemoteFile,
        RemoteDirectoryJob => ServerJobKind.RemoteDirectory,
        JobList => ServerJobKind.JobList,
        RetrieveFolderJob => ServerJobKind.RetrieveFolder,
        AggregateJob => ServerJobKind.Aggregate,
        AlbumAggregateJob => ServerJobKind.AlbumAggregate,
        _ => ServerJobKind.Generic,
    };

    public static ServerJobRole ToServerJobRole(JobSemanticRole role)
        => Enum.Parse<ServerJobRole>(role.ToString());

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

    public static IEnumerable<Guid> NestedJobIds(JobSnapshot container)
        => DirectNestedJobs(container).SelectMany(EnumerateSubtreeIds);

    private static IEnumerable<JobSnapshot> DirectNestedJobs(JobSnapshot container)
        => container.Payload switch
        {
            AlbumJobSnapshotPayload album => album.TrackJobs,
            RemoteDirectoryJobSnapshotPayload directory => directory.FileJobs,
            AggregateJobSnapshotPayload aggregate => aggregate.Songs,
            JobListSnapshotPayload list => list.Jobs,
            _ => [],
        };

    private static IEnumerable<Guid> EnumerateSubtreeIds(JobSnapshot job)
    {
        yield return job.Id;
        foreach (JobSnapshot child in DirectNestedJobs(job))
        {
            foreach (Guid id in EnumerateSubtreeIds(child))
                yield return id;
        }
    }

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

    private static PeerFileTargetDto ToPeerFileTargetDto(PeerFileTargetSnapshot target)
        => new(
            target.Identity.Username,
            target.Identity.Filename,
            target.Size,
            target.Extension,
            target.BitRate,
            target.BitDepth,
            target.SampleRate,
            target.Length,
            target.Attributes?.Select(ToFileAttributeDto).ToList());

    private static FileDownloadStateDto ToFileDownloadStateDto(FileDownloadStateSnapshot file)
        => new(
            file.DownloadPath,
            file.BytesTransferred,
            file.FileSize,
            file.FileSize > 0
                ? Math.Round((double)file.BytesTransferred / file.FileSize.Value * 100, 2)
                : null);

    private static DirectoryDownloadStateDto ToDirectoryDownloadStateDto(DirectoryDownloadStateSnapshot directory)
        => new(
            directory.Phase,
            directory.AttemptNumber,
            directory.DownloadPath,
            directory.FileCount,
            directory.TerminalFileCount,
            directory.SuccessfulFileCount,
            directory.FailedFileCount,
            directory.BytesTransferred,
            directory.TotalKnownBytes,
            directory.TotalKnownBytes > 0
                ? Math.Round((double)directory.BytesTransferred / directory.TotalKnownBytes * 100, 2)
                : null);
}

using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Sockseek.Core;
using Sockseek.Core.Events;

namespace Sockseek.Core.Snapshots;

public static class CoreSnapshotFactory
{
    public static JobSnapshot CreateJob(Job job, long revision)
        => CreateJob(job, revision, []);

    public static FileCandidateSnapshot CreateFileCandidate(FileCandidate candidate)
        => new(
            candidate.Username,
            candidate.Filename,
            new PeerSnapshot(
                candidate.Username,
                candidate.HasFreeUploadSlot,
                candidate.UploadSpeed),
            candidate.Size,
            candidate.BitRate,
            candidate.BitDepth,
            candidate.SampleRate,
            candidate.Length,
            candidate.Extension,
            candidate.Attributes);

    public static PeerFileTargetSnapshot CreatePeerFileTarget(PeerFileTarget target)
        => new(
            new PeerFileIdentitySnapshot(target.Username, target.Filename),
            target.Size,
            target.Extension,
            target.BitRate,
            target.BitDepth,
            target.SampleRate,
            target.Length,
            target.Attributes);

    public static PeerDirectoryResultSnapshot CreatePeerDirectory(PeerDirectorySnapshot directory)
        => new(
            new PeerDirectoryIdentitySnapshot(
                directory.Identity.Username,
                directory.Identity.FolderPath),
            SnapshotCollections.Freeze(directory.Files.Select(CreatePeerFileTarget)),
            directory.IsComplete);

    public static DirectoryTransferPlanSnapshot CreateDirectoryTransferPlan(DirectoryTransferPlan plan)
        => new(
            plan.DisplayRoot,
            SnapshotCollections.Freeze(plan.Entries.Select(entry =>
                new DirectoryTransferEntrySnapshot(
                    CreatePeerFileTarget(entry.Target),
                    SnapshotCollections.Freeze(entry.RelativeDirectoryComponents)))),
            plan.TotalKnownBytes);

    public static SearchResultSnapshot CreateSearchResult(SearchRawResult result)
        => new(
            result.Sequence,
            result.Revision,
            result.Username,
            result.Filename,
            result.Size,
            result.BitRate,
            result.BitDepth,
            result.ResponseFileCount,
            result.SampleRate,
            result.Length,
            result.Extension,
            result.UploadSpeed,
            result.HasFreeUploadSlot,
            result.Attributes,
            result.ObservedAtUtc);

    public static TransferSnapshot CreateDownloadTransfer(
        Guid transferId,
        SongJob song,
        FileCandidate candidate,
        string outputPath,
        long revision,
        string? state,
        long bytesTransferred,
        long totalBytes,
        int attemptCount)
    {
        var candidateSnapshot = CreateFileCandidate(candidate);
        return new TransferSnapshot(
            transferId,
            TransferSnapshotDirection.Download,
            TransferSnapshotSource.SoulseekPeer,
            song.Id,
            song.WorkflowId,
            revision,
            candidate.Username,
            candidate.Filename,
            outputPath,
            CandidateKey(candidateSnapshot),
            State: state,
            BytesTransferred: bytesTransferred,
            TotalBytes: totalBytes,
            AttemptCount: attemptCount,
            candidateSnapshot,
            CreatePeerFileTarget(candidate.Target));
    }

    public static TransferSnapshot CreateDownloadTransfer(
        Guid transferId,
        FileDownloadJob owner,
        PeerFileTarget target,
        string outputPath,
        long revision,
        string? state,
        long bytesTransferred,
        long totalBytes,
        int attemptCount)
        => new(
            transferId,
            TransferSnapshotDirection.Download,
            TransferSnapshotSource.SoulseekPeer,
            owner.Id,
            owner.WorkflowId,
            revision,
            target.Username,
            target.Filename,
            outputPath,
            CandidateKey(target.Identity),
            State: state,
            BytesTransferred: bytesTransferred,
            TotalBytes: totalBytes,
            AttemptCount: attemptCount,
            Candidate: null,
            Target: CreatePeerFileTarget(target));

    public static TransferSnapshot CreateFallbackTransfer(
        Guid transferId,
        SongJob song,
        string? sourceReference,
        string? outputPath,
        long revision,
        string? state,
        long bytesTransferred,
        long totalBytes,
        int attemptCount)
        => new(
            transferId,
            TransferSnapshotDirection.Download,
            TransferSnapshotSource.Fallback,
            song.Id,
            song.WorkflowId,
            revision,
            Username: null,
            RemotePath: sourceReference,
            LocalPath: outputPath,
            CandidateKey: null,
            state,
            bytesTransferred,
            totalBytes,
            attemptCount,
            Candidate: null,
            Target: null);

    public static AlbumFolderSnapshot CreateAlbumFolder(AlbumFolder folder, bool includeFiles)
    {
        var first = folder.Files.FirstOrDefault()?.Candidate;
        return new AlbumFolderSnapshot(
            folder.Username,
            folder.FolderPath,
            new PeerSnapshot(
                folder.Username,
                first?.HasFreeUploadSlot,
                first?.UploadSpeed),
            folder.SearchFileCount,
            folder.SearchAudioFileCount,
            includeFiles
                ? SnapshotCollections.Freeze(folder.Files.Select(CreateAlbumFile))
                : SnapshotCollections.Empty<AlbumFileSnapshot>(),
            folder.IsFullyRetrieved);
    }

    public static ExceptionSnapshot CreateException(Exception exception)
        => new(exception.GetType().Name, Diagnostics.ExceptionText.Summary(exception), Diagnostics.ExceptionText.Detail(exception));

    private static JobSnapshot CreateJob(Job job, long revision, HashSet<Guid> visited)
    {
        if (!visited.Add(job.Id))
        {
            return new JobSnapshot(
                job.Id,
                job.DisplayId,
                job.WorkflowId,
                GetJobKind(job),
                revision,
                job.LifecycleState,
                job.ActivityPhase,
                job.ActivityUntilUtc,
                job.TerminalOutcome,
                job.SkipReason,
                job.CancellationSource,
                job.FailureReason,
                job.FailureMessage,
                job.FailureDetail,
                job.ItemName,
                job.ToString(noInfo: true),
                CreateDiscovery(job.Discovery),
                SnapshotCollections.Freeze(job.Config?.AppliedAutoProfiles?.OrderBy(x => x) ?? Enumerable.Empty<string>()),
                job.Config?.PrintOption ?? PrintOption.None,
                CanCancel(job),
                new GenericJobSnapshotPayload(job.ToString(noInfo: true)));
        }

        try
        {
            return new JobSnapshot(
                job.Id,
                job.DisplayId,
                job.WorkflowId,
                GetJobKind(job),
                revision,
                job.LifecycleState,
                job.ActivityPhase,
                job.ActivityUntilUtc,
                job.TerminalOutcome,
                job.SkipReason,
                job.CancellationSource,
                job.FailureReason,
                job.FailureMessage,
                job.FailureDetail,
                job.ItemName,
                job.ToString(noInfo: true),
                CreateDiscovery(job.Discovery),
                SnapshotCollections.Freeze(job.Config?.AppliedAutoProfiles?.OrderBy(x => x) ?? Enumerable.Empty<string>()),
                job.Config?.PrintOption ?? PrintOption.None,
                CanCancel(job),
                CreatePayload(job, visited));
        }
        finally
        {
            visited.Remove(job.Id);
        }
    }

    private static JobSnapshotPayload CreatePayload(Job job, HashSet<Guid> visited)
        => job switch
        {
            ExtractJob extract => new ExtractJobSnapshotPayload(
                extract.Input,
                extract.InputType?.ToString(),
                extract.Result?.Id,
                extract.AutoProcessResult,
                BuildExtractResultDraft(extract)),
            SearchJob search => new SearchJobSnapshotPayload(
                search.QueryText,
                search.DefaultFileProjection == null
                    ? null
                    : new FileSearchProjectionSnapshot(
                        CreateSongQuery(search.DefaultFileProjection.Query),
                        search.DefaultFileProjection.IncludeFullResults),
                search.DefaultFolderProjection == null
                    ? null
                    : new FolderSearchProjectionSnapshot(
                        CreateAlbumQuery(search.DefaultFolderProjection.Query),
                        search.DefaultFolderProjection.IncludeFiles),
                search.ResultCount,
                search.Revision,
                search.IsComplete),
            SongJob song => new SongJobSnapshotPayload(
                CreateSongQuery(song.Query),
                song.Candidates?.Count,
                song.ResolvedTarget == null ? null : CreateFileCandidate(song.ResolvedTarget),
                song.ExactTarget == null ? null : CreatePeerFileTarget(song.ExactTarget),
                song.DownloadSource,
                CreateFileDownloadState(song)),
            AlbumJob album => new AlbumJobSnapshotPayload(
                CreateAlbumQuery(album.Query),
                album.Results.Count,
                album.ResolvedTarget == null ? null : CreateAlbumFolder(album.ResolvedTarget, includeFiles: false),
                SnapshotCollections.Freeze(album.TrackJobs.Select(child => CreateJob(child, 0, visited))),
                CreateDirectoryDownloadState(album)),
            RemoteFileJob remoteFile => new RemoteFileJobSnapshotPayload(
                CreatePeerFileTarget(remoteFile.Target),
                new RelativeOutputPathSnapshot(SnapshotCollections.Freeze(remoteFile.OutputPath.Components)),
                CreateFileDownloadState(remoteFile)),
            RemoteDirectoryJob remoteDirectory => CreateRemoteDirectoryPayload(remoteDirectory, visited),
            AggregateJob aggregate => new AggregateJobSnapshotPayload(
                CreateSongQuery(aggregate.Query),
                SnapshotCollections.Freeze(aggregate.Songs.Select(song => CreateJob(song, 0, visited)))),
            AlbumAggregateJob albumAggregate => new AlbumAggregateJobSnapshotPayload(
                CreateAlbumQuery(albumAggregate.Query),
                albumAggregate.Albums.Count),
            JobList list => new JobListSnapshotPayload(
                list.Count,
                SnapshotCollections.Freeze(list.Jobs.Select(child => CreateJob(child, 0, visited)))),
            RetrieveFolderJob retrieve => new RetrieveFolderJobSnapshotPayload(
                new PeerDirectoryIdentitySnapshot(
                    retrieve.Directory.Username,
                    retrieve.Directory.FolderPath),
                retrieve.Result == null ? null : CreatePeerDirectory(retrieve.Result),
                retrieve.NewFilesFoundCount,
                retrieve.RetrievalOutcome,
                retrieve.RetrievalCancelled),
            _ => new GenericJobSnapshotPayload(job.ToString(noInfo: true)),
        };

    private static FileDownloadStateSnapshot CreateFileDownloadState(FileDownloadJob job)
        => new(job.DownloadPath, job.BytesTransferred, job.FileSize);

    private static DirectoryDownloadStateSnapshot CreateDirectoryDownloadState(DirectoryDownloadJob job)
        => new(
            job.DirectoryState switch
            {
                DirectoryExecutionState.Unresolved => "unresolved",
                DirectoryExecutionState.Resolving => "resolving",
                DirectoryExecutionState.Planned => "planned",
                DirectoryExecutionState.Transferring => "transferring",
                _ => "unknown",
            },
            job.ActiveAttempt?.AttemptNumber,
            job.DownloadPath,
            job.FileJobs.Count,
            job.FileJobs.Count(child => child.IsTerminal),
            job.FileJobs.Count(child => child.IsSuccessfulTerminal),
            job.FileJobs.Count(child => child.IsUnsuccessfulTerminal),
            job.BytesTransferred,
            job.TotalKnownBytes);

    private static RemoteDirectoryJobSnapshotPayload CreateRemoteDirectoryPayload(
        RemoteDirectoryJob job,
        HashSet<Guid> visited)
    {
        var sourceKind = job.Source is RemoteDirectorySource.PeerDirectory
            ? RemoteDirectorySourceSnapshotKind.PeerDirectory
            : RemoteDirectorySourceSnapshotKind.Resolved;
        var directorySource = job.Source is RemoteDirectorySource.PeerDirectory peer
            ? new PeerDirectoryIdentitySnapshot(peer.Directory.Username, peer.Directory.FolderPath)
            : null;
        var resolvedPlan = job.Source is RemoteDirectorySource.Resolved resolved
            ? CreateDirectoryTransferPlan(resolved.Plan)
            : null;

        return new RemoteDirectoryJobSnapshotPayload(
            sourceKind,
            directorySource,
            resolvedPlan,
            job.ResolvedDirectory == null ? null : CreatePeerDirectory(job.ResolvedDirectory),
            job.ActiveAttempt == null || sourceKind == RemoteDirectorySourceSnapshotKind.Resolved
                ? null
                : CreateDirectoryTransferPlan(job.ActiveAttempt.Plan),
            SnapshotCollections.Freeze(job.FileJobs.Select(child => CreateJob(child, 0, visited))),
            CreateDirectoryDownloadState(job));
    }

    private static AlbumFileSnapshot CreateAlbumFile(AlbumFile file)
        => new(CreateSongQuery(file.Query), CreateFileCandidate(file.Candidate));

    private static DiscoverySnapshot? CreateDiscovery(DiscoverySummary? discovery)
        => discovery == null
            ? null
            : new DiscoverySnapshot(discovery.RawResultCount, discovery.LockedFileCount);

    private static bool CanCancel(Job job)
        => job.LifecycleState != JobLifecycleState.Terminal
            && job.Cts != null
            && !job.Cts.IsCancellationRequested;

    private static JobSnapshotKind GetJobKind(Job job)
        => job switch
        {
            ExtractJob => JobSnapshotKind.Extract,
            SearchJob => JobSnapshotKind.Search,
            SongJob => JobSnapshotKind.Song,
            AlbumJob => JobSnapshotKind.Album,
            RemoteFileJob => JobSnapshotKind.RemoteFile,
            RemoteDirectoryJob => JobSnapshotKind.RemoteDirectory,
            AggregateJob => JobSnapshotKind.Aggregate,
            AlbumAggregateJob => JobSnapshotKind.AlbumAggregate,
            JobList => JobSnapshotKind.JobList,
            RetrieveFolderJob => JobSnapshotKind.RetrieveFolder,
            _ => JobSnapshotKind.Generic,
        };

    private static SongQuerySnapshot CreateSongQuery(SongQuery query)
        => new(
            Optional(query.Artist),
            Optional(query.Title),
            Optional(query.Album),
            Optional(query.URI),
            Optional(query.Length),
            query.ArtistMaybeWrong);

    private static AlbumQuerySnapshot CreateAlbumQuery(AlbumQuery query)
        => new(
            Optional(query.Artist),
            Optional(query.Album),
            Optional(query.SearchHint),
            Optional(query.URI),
            query.ArtistMaybeWrong);

    private static string? Optional(string value)
        => value.Length > 0 ? value : null;

    private static int? Optional(int value)
        => value >= 0 ? value : null;

    private static JobDraftSnapshot? BuildExtractResultDraft(ExtractJob extract)
        => extract is { AutoProcessResult: false, Result: not null }
            ? ToJobDraft(extract.Result)
            : null;

    private static JobDraftSnapshot? ToJobDraft(Job? job)
        => job switch
        {
            null => null,
            ExtractJob extract => new ExtractJobDraftSnapshot(
                extract.Input,
                extract.InputType?.ToString(),
                extract.AutoProcessResult,
                extract.ResultDownloadBehaviorPolicy == null ? null : CreateDownloadBehaviorPolicy(extract.ResultDownloadBehaviorPolicy),
                CreateProvenance(extract)),
            SearchJob search when search.DefaultFolderProjection != null =>
                new AlbumSearchJobDraftSnapshot(
                    CreateAlbumQuery(search.DefaultFolderProjection.Query),
                    CreateProvenance(search)),
            SearchJob search when search.DefaultFileProjection != null =>
                new TrackSearchJobDraftSnapshot(
                    CreateSongQuery(search.DefaultFileProjection.Query),
                    search.DefaultFileProjection.IncludeFullResults,
                    CreateProvenance(search)),
            SongJob song => new SongJobDraftSnapshot(
                CreateSongQuery(song.Query),
                CreateDownloadBehaviorPolicy(song.DownloadBehaviorPolicy),
                CreateProvenance(song)),
            AlbumJob album => new AlbumJobDraftSnapshot(
                CreateAlbumQuery(album.Query),
                CreateDownloadBehaviorPolicy(album.DownloadBehaviorPolicy),
                CreateProvenance(album)),
            RemoteFileJob remoteFile => new RemoteFileJobDraftSnapshot(
                CreatePeerFileTarget(remoteFile.Target),
                new RelativeOutputPathSnapshot(SnapshotCollections.Freeze(remoteFile.OutputPath.Components)),
                CreateProvenance(remoteFile)),
            RemoteDirectoryJob remoteDirectory => remoteDirectory.Source switch
            {
                RemoteDirectorySource.PeerDirectory peer => new RemoteDirectoryJobDraftSnapshot(
                    new PeerDirectoryIdentitySnapshot(peer.Directory.Username, peer.Directory.FolderPath),
                    null,
                    CreateProvenance(remoteDirectory)),
                RemoteDirectorySource.Resolved resolved => new RemoteDirectoryJobDraftSnapshot(
                    null,
                    CreateDirectoryTransferPlan(resolved.Plan),
                    CreateProvenance(remoteDirectory)),
                _ => throw new InvalidOperationException("Unsupported remote directory source."),
            },
            AggregateJob aggregate => new AggregateJobDraftSnapshot(
                CreateSongQuery(aggregate.Query),
                CreateDownloadBehaviorPolicy(aggregate.DownloadBehaviorPolicy),
                CreateProvenance(aggregate)),
            AlbumAggregateJob aggregate => new AlbumAggregateJobDraftSnapshot(
                CreateAlbumQuery(aggregate.Query),
                CreateDownloadBehaviorPolicy(aggregate.DownloadBehaviorPolicy),
                CreateProvenance(aggregate)),
            JobList list => new JobListDraftSnapshot(
                list.ItemName,
                SnapshotCollections.Freeze(list.Jobs.Select(ToJobDraft).OfType<JobDraftSnapshot>()),
                CreateProvenance(list)),
            _ => null,
        };

    private static DownloadBehaviorPolicySnapshot CreateDownloadBehaviorPolicy(DownloadBehaviorPolicy policy)
        => new(policy.Default, policy.Song, policy.Album, policy.Aggregate, policy.AlbumAggregate);

    private static JobProvenanceSnapshot? CreateProvenance(Job job)
        => job.SourceMutation == null && job.LineNumber == 0 && job.ItemNumber == 1
            ? null
            : new JobProvenanceSnapshot(job.ItemNumber, job.LineNumber, CreateSourceMutation(job.SourceMutation));

    private static SourceMutationSnapshot? CreateSourceMutation(SourceMutation? mutation)
        => mutation == null
            ? null
            : new SourceMutationSnapshot(
                mutation.Kind,
                mutation.Source,
                mutation.LineNumber,
                mutation.ItemNumber,
                mutation.CsvColumnCount,
                mutation.TrackUri);

    private static string CandidateKey(FileCandidateSnapshot candidate)
        => CandidateKey(new PeerFileIdentity(candidate.Username, candidate.Filename));

    private static string CandidateKey(PeerFileIdentity identity)
        => string.Join('\0', identity.Username, identity.Filename);
}

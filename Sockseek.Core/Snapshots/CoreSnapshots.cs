using Sockseek.Core.Jobs;
using Sockseek.Core.Models;

namespace Sockseek.Core.Snapshots;

public enum JobSnapshotKind
{
    Generic,
    Extract,
    Search,
    Song,
    Album,
    Aggregate,
    AlbumAggregate,
    JobList,
    RetrieveFolder,
}

public sealed record DiscoverySnapshot(int RawResultCount, int LockedFileCount);

public sealed record SongQuerySnapshot(
    string? Artist,
    string? Title,
    string? Album,
    string? URI,
    int? Length,
    bool ArtistMaybeWrong);

public sealed record AlbumQuerySnapshot(
    string? Artist,
    string? Album,
    string? SearchHint,
    string? URI,
    bool ArtistMaybeWrong);

public sealed record FileAttributeSnapshot(string Type, int Value, int StableCode = 0);

public sealed record PeerSnapshot(string Username, bool? HasFreeUploadSlot, int? UploadSpeed);

public sealed record SearchResultSnapshot(
    long Sequence,
    int Revision,
    string Username,
    string Filename,
    long Size,
    int? BitRate,
    int? BitDepth,
    int ResponseFileCount,
    int? SampleRate,
    int? Length,
    string Extension,
    int? UploadSpeed,
    bool? HasFreeUploadSlot,
    IReadOnlyList<FileAttributeSnapshot>? Attributes,
    DateTimeOffset ObservedAtUtc);

public sealed record FileCandidateSnapshot(
    string Username,
    string Filename,
    PeerSnapshot Peer,
    long Size,
    int? BitRate,
    int? SampleRate,
    int? Length,
    string Extension,
    IReadOnlyList<FileAttributeSnapshot>? Attributes);

public sealed record AlbumFileSnapshot(SongQuerySnapshot Query, FileCandidateSnapshot Candidate)
{
    public string Filename => Candidate.Filename;
}

public sealed record AlbumFolderSnapshot(
    string Username,
    string FolderPath,
    PeerSnapshot Peer,
    int SearchFileCount,
    int SearchAudioFileCount,
    IReadOnlyList<AlbumFileSnapshot> Files,
    bool IsFullyRetrieved);

public sealed record SourceMutationSnapshot(
    SourceMutationKind Kind,
    string Source,
    int LineNumber,
    int ItemNumber,
    int CsvColumnCount,
    string? TrackUri);

public enum TransferSnapshotDirection
{
    Download,
    Upload,
}

public enum TransferSnapshotSource
{
    SoulseekPeer,
    Fallback,
}

public sealed record TransferSnapshot(
    Guid Id,
    TransferSnapshotDirection Direction,
    TransferSnapshotSource Source,
    Guid JobId,
    Guid WorkflowId,
    long Revision,
    string? Username,
    string? RemotePath,
    string? LocalPath,
    string? CandidateKey,
    string? State,
    long BytesTransferred,
    long TotalBytes,
    int AttemptCount,
    FileCandidateSnapshot? Candidate);

public sealed record JobProvenanceSnapshot(
    int ItemNumber,
    int LineNumber,
    SourceMutationSnapshot? SourceMutation);

public sealed record DownloadBehaviorPolicySnapshot(
    DownloadBehavior Default,
    DownloadBehavior? Song,
    DownloadBehavior? Album,
    DownloadBehavior? Aggregate,
    DownloadBehavior? AlbumAggregate);

public sealed record FileSearchProjectionSnapshot(SongQuerySnapshot Query, bool IncludeFullResults);

public sealed record FolderSearchProjectionSnapshot(AlbumQuerySnapshot Query, bool IncludeFiles);

public abstract record JobDraftSnapshot(JobProvenanceSnapshot? Provenance);

public sealed record ExtractJobDraftSnapshot(
    string Input,
    string? InputType,
    bool AutoProcessResult,
    DownloadBehaviorPolicySnapshot? ResultDownloadBehavior,
    JobProvenanceSnapshot? Provenance) : JobDraftSnapshot(Provenance);

public sealed record TrackSearchJobDraftSnapshot(
    SongQuerySnapshot SongQuery,
    bool IncludeFullResults,
    JobProvenanceSnapshot? Provenance) : JobDraftSnapshot(Provenance);

public sealed record AlbumSearchJobDraftSnapshot(
    AlbumQuerySnapshot AlbumQuery,
    JobProvenanceSnapshot? Provenance) : JobDraftSnapshot(Provenance);

public sealed record SongJobDraftSnapshot(
    SongQuerySnapshot SongQuery,
    DownloadBehaviorPolicySnapshot? DownloadBehavior,
    JobProvenanceSnapshot? Provenance) : JobDraftSnapshot(Provenance);

public sealed record AlbumJobDraftSnapshot(
    AlbumQuerySnapshot AlbumQuery,
    DownloadBehaviorPolicySnapshot? DownloadBehavior,
    JobProvenanceSnapshot? Provenance) : JobDraftSnapshot(Provenance);

public sealed record AggregateJobDraftSnapshot(
    SongQuerySnapshot SongQuery,
    DownloadBehaviorPolicySnapshot? DownloadBehavior,
    JobProvenanceSnapshot? Provenance) : JobDraftSnapshot(Provenance);

public sealed record AlbumAggregateJobDraftSnapshot(
    AlbumQuerySnapshot AlbumQuery,
    DownloadBehaviorPolicySnapshot? DownloadBehavior,
    JobProvenanceSnapshot? Provenance) : JobDraftSnapshot(Provenance);

public sealed record JobListDraftSnapshot(
    string? Name,
    IReadOnlyList<JobDraftSnapshot> Jobs,
    JobProvenanceSnapshot? Provenance) : JobDraftSnapshot(Provenance);

public abstract record JobSnapshotPayload;

public sealed record ExtractJobSnapshotPayload(
    string Input,
    string? InputType,
    Guid? ResultJobId,
    bool AutoProcessResult,
    JobDraftSnapshot? ResultDraft) : JobSnapshotPayload;

public sealed record SearchJobSnapshotPayload(
    string QueryText,
    FileSearchProjectionSnapshot? DefaultFileProjection,
    FolderSearchProjectionSnapshot? DefaultFolderProjection,
    int ResultCount,
    int Revision,
    bool IsComplete) : JobSnapshotPayload;

public sealed record SongJobSnapshotPayload(
    SongQuerySnapshot Query,
    int? CandidateCount,
    string? DownloadPath,
    FileCandidateSnapshot? ResolvedTarget,
    long BytesTransferred,
    long FileSize,
    SongDownloadSource DownloadSource) : JobSnapshotPayload;

public sealed record AlbumJobSnapshotPayload(
    AlbumQuerySnapshot Query,
    int ResultCount,
    string? DownloadPath,
    AlbumFolderSnapshot? ResolvedTarget,
    IReadOnlyList<JobSnapshot> TrackJobs) : JobSnapshotPayload;

public sealed record AggregateJobSnapshotPayload(
    SongQuerySnapshot Query,
    IReadOnlyList<JobSnapshot> Songs) : JobSnapshotPayload;

public sealed record AlbumAggregateJobSnapshotPayload(
    AlbumQuerySnapshot Query,
    int AlbumCount) : JobSnapshotPayload;

public sealed record JobListSnapshotPayload(
    int Count,
    IReadOnlyList<JobSnapshot> Jobs) : JobSnapshotPayload;

public sealed record RetrieveFolderJobSnapshotPayload(
    AlbumFolderSnapshot TargetFolder,
    int NewFilesFoundCount,
    FolderRetrievalOutcome RetrievalOutcome,
    bool RetrievalCancelled) : JobSnapshotPayload;

public sealed record GenericJobSnapshotPayload(string Text) : JobSnapshotPayload;

public sealed record JobSnapshot(
    Guid Id,
    int DisplayId,
    Guid WorkflowId,
    JobSnapshotKind Kind,
    long Revision,
    JobLifecycleState LifecycleState,
    JobActivityPhase ActivityPhase,
    DateTimeOffset? ActivityUntilUtc,
    JobTerminalOutcome TerminalOutcome,
    JobSkipReason SkipReason,
    JobCancellationSource CancellationSource,
    JobFailureReason FailureReason,
    string? FailureMessage,
    string? FailureDetail,
    string? ItemName,
    string? QueryText,
    DiscoverySnapshot? Discovery,
    IReadOnlyList<string> AppliedAutoProfiles,
    PrintOption PrintOption,
    bool CanCancel,
    JobSnapshotPayload Payload);

internal static class SnapshotCollections
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> source)
        => Array.AsReadOnly(source.ToArray());

    public static IReadOnlyList<T> Empty<T>() => Array.AsReadOnly(Array.Empty<T>());
}

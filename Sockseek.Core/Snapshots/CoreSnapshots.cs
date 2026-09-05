using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Planning;
using Sockseek.Core.Services;
using Sockseek.Core.Events;

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
    RemoteFile,
    RemoteDirectory,
}

public sealed record DiscoverySnapshot(
    int RawResultCount,
    int LockedFileCount,
    int ObservedPeerCount = 0);

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

public sealed record PeerFileIdentitySnapshot(string Username, string Filename);

public sealed record PeerFileTargetSnapshot(
    PeerFileIdentitySnapshot Identity,
    long? Size,
    string? Extension,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    IReadOnlyList<FileAttributeSnapshot>? Attributes);

public sealed record PeerDirectoryIdentitySnapshot(string Username, string FolderPath);

public sealed record PeerDirectoryResultSnapshot(
    PeerDirectoryIdentitySnapshot Identity,
    IReadOnlyList<PeerFileTargetSnapshot> Files,
    bool IsComplete);

public sealed record RelativeOutputPathSnapshot(IReadOnlyList<string> Components);

public sealed record DirectoryTransferEntrySnapshot(
    PeerFileTargetSnapshot Target,
    IReadOnlyList<string> RelativeDirectoryComponents);

public sealed record DirectoryTransferPlanSnapshot(
    string DisplayRoot,
    IReadOnlyList<DirectoryTransferEntrySnapshot> Entries,
    long TotalKnownBytes);

public sealed record PeerSnapshot(
    string Username,
    bool? HasFreeUploadSlot,
    int? UploadSpeed,
    int? QueueLength = null,
    DateTimeOffset? ObservedAtUtc = null);

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
    DateTimeOffset ObservedAtUtc,
    int? QueueLength = null,
    SearchResultVisibility Visibility = SearchResultVisibility.Public);

public sealed record FileCandidateSnapshot(
    string Username,
    string Filename,
    PeerSnapshot Peer,
    long Size,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    string Extension,
    IReadOnlyList<FileAttributeSnapshot>? Attributes,
    SearchResultVisibility Visibility = SearchResultVisibility.Public);

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

public enum TransferSnapshotTerminalOutcome
{
    None,
    Succeeded,
    Cancelled,
    Failed,
    Interrupted,
}

/// <summary>
/// Reusable presentation-safe metadata for a transfer's exact file. Transfer
/// identity remains in the surrounding snapshot; fallback or genuinely
/// unknown files leave this value null.
/// </summary>
public sealed record TransferFileMetadataSnapshot(
    string Name,
    long Size,
    string? Extension,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    IReadOnlyList<FileAttributeSnapshot>? Attributes = null);

public sealed record TransferSnapshot(
    Guid Id,
    TransferSnapshotDirection Direction,
    TransferSnapshotSource Source,
    Guid? JobId,
    Guid? WorkflowId,
    long Revision,
    string? Username,
    string? RemotePath,
    string? LocalPath,
    string? CandidateKey,
    string? State,
    long BytesTransferred,
    long TotalBytes,
    int AttemptCount,
    FileCandidateSnapshot? Candidate,
    PeerFileTargetSnapshot? Target = null,
    DateTimeOffset? RequestedAtUtc = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? LastProgressAtUtc = null,
    long? BytesPerSecond = null,
    TransferSnapshotTerminalOutcome TerminalOutcome = TransferSnapshotTerminalOutcome.None,
    TransferFailureReason? FailureReason = null,
    TransferCancellationReason? CancellationReason = null,
    TransferFileMetadataSnapshot? File = null);

public sealed record FileSearchProjectionSnapshot(SongQuerySnapshot Query, bool IncludeFullResults);

public sealed record FolderSearchProjectionSnapshot(AlbumQuerySnapshot Query, bool IncludeFiles);

public abstract record JobSnapshotPayload;

public sealed record FileDownloadStateSnapshot(
    string? DownloadPath,
    long BytesTransferred,
    long? FileSize);

public sealed record DirectoryDownloadStateSnapshot(
    string Phase,
    int? AttemptNumber,
    string? DownloadPath,
    int FileCount,
    int TerminalFileCount,
    int SuccessfulFileCount,
    int FailedFileCount,
    long BytesTransferred,
    long TotalKnownBytes);

public sealed record ExtractJobSnapshotPayload(
    string Input,
    string? InputType,
    Guid? ResultJobId,
    bool AutoProcessResult) : JobSnapshotPayload;

public sealed record SearchJobSnapshotPayload(
    string QueryText,
    FileSearchProjectionSnapshot? DefaultFileProjection,
    FolderSearchProjectionSnapshot? DefaultFolderProjection,
    int ResultCount,
    int Revision,
    bool IsComplete,
    SearchDefinition? Definition) : JobSnapshotPayload;

public sealed record SongJobSnapshotPayload(
    SongQuerySnapshot Query,
    int? CandidateCount,
    FileCandidateSnapshot? ResolvedTarget,
    PeerFileTargetSnapshot? ExactTarget,
    SongDownloadSource DownloadSource,
    FileDownloadStateSnapshot File,
    SearchDefinition? Definition) : JobSnapshotPayload;

public sealed record AlbumJobSnapshotPayload(
    AlbumQuerySnapshot Query,
    int ResultCount,
    AlbumFolderSnapshot? ResolvedTarget,
    IReadOnlyList<JobSnapshot> TrackJobs,
    DirectoryDownloadStateSnapshot Directory,
    SearchDefinition? Definition) : JobSnapshotPayload;

public sealed record RemoteFileJobSnapshotPayload(
    PeerFileTargetSnapshot Target,
    RelativeOutputPathSnapshot OutputPath,
    FileDownloadStateSnapshot File) : JobSnapshotPayload;

public enum RemoteDirectorySourceSnapshotKind
{
    PeerDirectory,
    Resolved,
}

public sealed record RemoteDirectoryJobSnapshotPayload(
    RemoteDirectorySourceSnapshotKind SourceKind,
    PeerDirectoryIdentitySnapshot? DirectorySource,
    DirectoryTransferPlanSnapshot? ResolvedPlanSource,
    PeerDirectoryResultSnapshot? ResolvedDirectory,
    DirectoryTransferPlanSnapshot? ActivePlan,
    IReadOnlyList<JobSnapshot> FileJobs,
    DirectoryDownloadStateSnapshot Directory) : JobSnapshotPayload;

public sealed record AggregateJobSnapshotPayload(
    SongQuerySnapshot Query,
    IReadOnlyList<JobSnapshot> Songs,
    SearchDefinition? Definition) : JobSnapshotPayload;

public sealed record AlbumAggregateJobSnapshotPayload(
    AlbumQuerySnapshot Query,
    int AlbumCount,
    SearchDefinition? Definition) : JobSnapshotPayload;

public sealed record JobListSnapshotPayload(
    int Count,
    IReadOnlyList<JobSnapshot> Jobs) : JobSnapshotPayload;

public sealed record RetrieveFolderJobSnapshotPayload(
    PeerDirectoryIdentitySnapshot Directory,
    PeerDirectoryResultSnapshot? Result,
    int NewFilesFoundCount,
    FolderRetrievalOutcome RetrievalOutcome,
    bool RetrievalCancelled) : JobSnapshotPayload;

public sealed record GenericJobSnapshotPayload(string Text) : JobSnapshotPayload;

public sealed record JobSnapshot(
    Guid Id,
    int DisplayId,
    Guid WorkflowId,
    Guid? SubmissionId,
    JobSemanticRole SemanticRole,
    DateTimeOffset? CreatedAtUtc,
    string? SubmissionSpecificationJson,
    Guid? RerunOfSubmissionId,
    Guid? PreviewId,
    string? ArtifactId,
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

namespace Sockseek.Core.PeerBrowsing;

public enum PeerBrowseState
{
    Queued,
    Running,
    Complete,
    Failed,
    Cancelled,
}

public enum PeerBrowsePhase
{
    WaitingForPeer,
    Receiving,
    Indexing,
    Ready,
}

public enum PeerBrowseEntryVisibility
{
    Public,
    Locked,
    Mixed,
}

public sealed record PeerBrowseFailure(string Code, string Message);

public sealed record PeerBrowseResource(
    Guid BrowseId,
    string LocalAccount,
    string Username,
    PeerBrowseState State,
    PeerBrowsePhase Phase,
    long CompressedBytesReceived,
    long? CompressedBytesExpected,
    long DirectoryCount,
    long FileCount,
    long TotalFileBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset ExpiresAt,
    PeerBrowseFailure? Failure,
    long Revision);

public sealed record PeerBrowseDirectoryEntry(
    long DirectoryId,
    long? ParentId,
    string Name,
    string DisplayPath,
    PeerBrowseEntryVisibility Visibility,
    bool IsSynthetic,
    long DirectDirectoryCount,
    long DirectFileCount,
    long RecursiveFileCount,
    long RecursiveFileBytes,
    long LockedDescendantCount,
    bool HasChildren);

public sealed record PeerBrowseFileAttribute(int Type, int Value);

public sealed record PeerBrowseFileEntry(
    long FileId,
    long DirectoryId,
    PeerBrowseEntryVisibility Visibility,
    string Name,
    long Size,
    string? Extension,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    IReadOnlyList<PeerBrowseFileAttribute>? Attributes);

public sealed record PeerBrowsePage<T>(
    IReadOnlyList<T> Items,
    string? NextSortKey,
    long? NextId);

public sealed record PeerBrowseResourcePage(
    IReadOnlyList<PeerBrowseResource> Items,
    DateTimeOffset? NextCreatedAt,
    Guid? NextBrowseId);

public sealed class PeerBrowseSelectionException(string message)
    : ArgumentException(message);

/// <summary>
/// Immutable ordinary-transfer plans resolved entirely from one browse artifact.
/// Compact artifact IDs never escape into the transfer model.
/// </summary>
public sealed record PeerBrowseDownloadResolution(
    IReadOnlyList<Sockseek.Core.Models.DirectoryTransferPlan> Plans,
    int CanonicalDirectoryRoots,
    int StandaloneFiles,
    long TotalPublicFiles,
    long TotalPublicBytes,
    int RedundantSelectionsRemoved,
    long LockedBranchesSkipped);

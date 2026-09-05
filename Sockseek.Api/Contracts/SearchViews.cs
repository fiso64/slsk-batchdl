using System.Text.Json.Serialization;

namespace Sockseek.Api;

[JsonConverter(typeof(JsonStringEnumConverter<SearchViewRetentionState>))]
public enum SearchViewRetentionState
{
    Live,
    Complete,
    Incomplete,
    Interrupted,
    Pruned,
}

public sealed record CreateSearchViewRequestDto(
    ServerSearchViewProjectionKind? Kind = null,
    SongQueryDto? SongQuery = null,
    AlbumQueryDto? AlbumQuery = null,
    bool IncludeFullResults = false);

public sealed record SearchViewCountersDto(
    long PublicFileCount,
    long LockedFileCount,
    long PublicBytes,
    long LockedBytes,
    int ObservedPeerCount,
    long ProjectedFileCount,
    long ProjectedPublicFileCount,
    long ProjectedLockedFileCount,
    long PreferredFileCount,
    long OtherFileCount,
    long TopLevelItemCount,
    long SelectableOptionCount);

public sealed record SearchViewSummaryDto(
    Guid ViewId,
    Guid SourceJobId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long Revision,
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    SearchViewRetentionState RetentionState,
    SearchViewCountersDto Counters);

public sealed record SearchViewRevisionDto(
    Guid ViewId,
    long Revision,
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    SearchViewRetentionState RetentionState,
    SearchViewCountersDto Counters);

public sealed record SearchViewFileDto(
    string Ref,
    ServerSearchResultVisibility Visibility,
    ServerSearchPreferenceTier PreferenceTier,
    bool NecessaryConditionsSatisfied,
    IReadOnlyList<ServerSearchPreferenceCondition> SatisfiedPreferredConditions,
    IReadOnlyList<ServerSearchPreferenceCondition> UnsatisfiedPreferredConditions,
    string RemoteFilename,
    PeerInfoDto Peer,
    FileMetadataDto File);

public sealed record SearchViewFilePageDto(
    SearchViewRevisionDto Revision,
    IReadOnlyList<SearchViewFileDto> Items,
    string? NextCursor);

[JsonConverter(typeof(JsonStringEnumConverter<SearchViewDirectoryVisibility>))]
public enum SearchViewDirectoryVisibility
{
    Public,
    Locked,
    Mixed,
}

[JsonConverter(typeof(JsonStringEnumConverter<SearchViewDirectoryRetrievalState>))]
public enum SearchViewDirectoryRetrievalState
{
    SearchResultsOnly,
    Complete,
    Incomplete,
}

/// <summary>
/// View-scoped opaque reference composed with the exact peer directory
/// identity it resolves to in the bound revision.
/// </summary>
public sealed record PeerDirectoryRefDto(
    string Ref,
    string Username,
    string FolderPath);

public sealed record SearchViewDirectoryDto(
    PeerDirectoryRefDto Ref,
    SearchViewDirectoryVisibility Visibility,
    ServerSearchPreferenceTier PreferenceTier,
    IReadOnlyList<ServerSearchPreferenceCondition> SatisfiedPreferredConditions,
    long PublicMatchingFileCount,
    long LockedMatchingFileCount,
    long PublicMatchingBytes,
    long LockedMatchingBytes,
    long? RetrievedFileCount,
    long? RetrievedBytes,
    SearchViewDirectoryRetrievalState RetrievalState,
    PeerInfoDto Peer,
    SearchViewFileDto BestChild);

public sealed record SearchViewDirectoryPageDto(
    SearchViewRevisionDto Revision,
    IReadOnlyList<SearchViewDirectoryDto> Items,
    string? NextCursor);

public sealed record SearchViewDirectoryFileDto(
    string Ref,
    string RelativePath,
    SearchViewFileDto File);

public sealed record SearchViewDirectoryFilePageDto(
    SearchViewRevisionDto Revision,
    PeerDirectoryRefDto Directory,
    IReadOnlyList<SearchViewDirectoryFileDto> Items,
    string? NextCursor);

public sealed record RetrieveSearchViewDirectoryRequestDto(
    long Revision,
    PeerDirectoryRefDto Directory);

public sealed record SearchViewAggregateTrackGroupDto(
    string Ref,
    SongQueryDto Query,
    int ShareCount,
    long SelectableOptionCount,
    SearchViewFileDto Representative);

public sealed record SearchViewAggregateTrackPageDto(
    SearchViewRevisionDto Revision,
    IReadOnlyList<SearchViewAggregateTrackGroupDto> Items,
    string? NextCursor);

public sealed record SearchViewAggregateTrackOptionPageDto(
    SearchViewRevisionDto Revision,
    string GroupRef,
    IReadOnlyList<SearchViewFileDto> Items,
    string? NextCursor);

public sealed record SearchViewAggregateAlbumGroupDto(
    string Ref,
    AlbumQueryDto Query,
    int ShareCount,
    long SelectableOptionCount,
    SearchViewDirectoryDto Representative);

public sealed record SearchViewAggregateAlbumPageDto(
    SearchViewRevisionDto Revision,
    IReadOnlyList<SearchViewAggregateAlbumGroupDto> Items,
    string? NextCursor);

public sealed record SearchViewAggregateAlbumOptionPageDto(
    SearchViewRevisionDto Revision,
    string GroupRef,
    IReadOnlyList<SearchViewDirectoryDto> Items,
    string? NextCursor);

public sealed record SearchViewUpdateDto(
    SearchViewSummaryDto Summary,
    bool HasNewRevision);

public sealed record CommitSearchViewSelectionRequestDto(
    long Revision,
    RefSelectionExpressionDto Selection,
    Guid IdempotencyKey);

public sealed record CommitSearchViewSelectionResponseDto(
    Guid ViewId,
    long ViewRevision,
    Guid? SubmissionId,
    Guid? WorkflowId,
    long RequestedCount,
    long ResolvedCount,
    long SubmittedCount,
    long SkippedCount,
    long RejectedCount,
    IReadOnlyList<SubmissionReasonCountDto> RejectionReasons);

using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;

namespace Sockseek.Persistence.Read;

public sealed record PersistedSearchMetadata(
    Guid JobId,
    string Query,
    long Revision,
    long ResultCount,
    long LockedFileCount,
    long ObservedPeerCount,
    bool IsComplete,
    DateTimeOffset? CompletedAtUtc,
    string ResultPersistenceState,
    DateTimeOffset? ResultsPrunedAtUtc);

public sealed record PersistedSearchResult(
    Guid Id,
    Guid SearchJobId,
    long Sequence,
    long Revision,
    string Username,
    string RemoteFilename,
    long SizeBytes,
    int? BitRate,
    int? BitDepth,
    int ResponseFileCount,
    int? SampleRate,
    int? DurationSeconds,
    string Extension,
    int? UploadSpeed,
    bool? HasFreeUploadSlot,
    string? AttributesJson,
    DateTimeOffset ObservedAtUtc,
    int? QueueLength,
    SearchResultVisibility Visibility)
{
    public SearchProjectionInput ToProjectionInput()
        => new(
            Sequence, checked((int)Revision), Username, ResponseFileCount, RemoteFilename, SizeBytes,
            BitRate, BitDepth, SampleRate, DurationSeconds, Extension, UploadSpeed, HasFreeUploadSlot,
            DeserializeAttributes(AttributesJson), ObservedAtUtc, QueueLength, Visibility);

    private static IReadOnlyList<FileAttributeSnapshot>? DeserializeAttributes(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<PersistedAttribute[]>(json)?
                .Select(attribute => new FileAttributeSnapshot(attribute.Name, attribute.Value, attribute.Code))
                .ToArray();

    private sealed record PersistedAttribute(int Code, string Name, int Value);
}

public sealed record PersistedSearchResultPage(
    PersistedSearchMetadata Metadata,
    IReadOnlyList<PersistedSearchResult> Items,
    long? NextSequence);

public sealed record PersistedSearchResultLookup(
    PersistedSearchMetadata Metadata,
    PersistedSearchResult? Result);

public interface ISearchHistoryReader
{
    Task<PersistedSearchResultPage?> GetRawResultsAsync(Guid searchJobId, long afterSequence, int limit, CancellationToken cancellationToken = default);
    Task<PersistedSearchResultLookup?> GetResultAsync(Guid searchJobId, string username, string remoteFilename, CancellationToken cancellationToken = default);
    Task<PersistedSearchMetadata?> GetMetadataAsync(Guid searchJobId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SearchProjectionInput> ReadProjectionInputsAsync(Guid searchJobId, CancellationToken cancellationToken = default);
}

public sealed class SearchHistoryReader(IDbContextFactory<SockseekDbContext> contextFactory) : ISearchHistoryReader
{
    public const int MaximumPageSize = 500;

    public async Task<PersistedSearchResultPage?> GetRawResultsAsync(
        Guid searchJobId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Search result page size must be between 1 and {MaximumPageSize}.");

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var metadata = await context.SearchJobs.AsNoTracking()
            .SingleOrDefaultAsync(search => search.JobId == searchJobId, cancellationToken)
            .ConfigureAwait(false);
        if (metadata == null)
            return null;

        var rows = await context.SearchResults.AsNoTracking()
            .Where(result => result.SearchJobId == searchJobId && result.Sequence > afterSequence)
            .OrderBy(result => result.Sequence)
            .Take(limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        var page = new PersistedSearchResultPage(
            MapMetadata(metadata),
            rows.Select(MapResult).ToArray(),
            hasMore && rows.Count > 0 ? rows[^1].Sequence : null);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return page;
    }

    public async Task<PersistedSearchResultLookup?> GetResultAsync(
        Guid searchJobId,
        string username,
        string remoteFilename,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteFilename);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var metadata = await context.SearchJobs.AsNoTracking()
            .SingleOrDefaultAsync(search => search.JobId == searchJobId, cancellationToken)
            .ConfigureAwait(false);
        if (metadata == null)
            return null;

        var result = await context.SearchResults.AsNoTracking()
            .SingleOrDefaultAsync(row => row.SearchJobId == searchJobId
                && row.Username == username
                && row.RemoteFilename == remoteFilename
                && row.Visibility == SearchResultVisibility.Public.ToString(), cancellationToken)
            .ConfigureAwait(false);

        var lookup = new PersistedSearchResultLookup(MapMetadata(metadata), result == null ? null : MapResult(result));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return lookup;
    }

    public async Task<PersistedSearchMetadata?> GetMetadataAsync(Guid searchJobId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var metadata = await context.SearchJobs.AsNoTracking()
            .SingleOrDefaultAsync(search => search.JobId == searchJobId, cancellationToken)
            .ConfigureAwait(false);
        return metadata == null ? null : MapMetadata(metadata);
    }

    public async IAsyncEnumerable<SearchProjectionInput> ReadProjectionInputsAsync(
        Guid searchJobId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var row in context.SearchResults.AsNoTracking()
            .Where(result => result.SearchJobId == searchJobId)
            .OrderBy(result => result.Sequence)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return MapResult(row).ToProjectionInput();
        }
    }

    private static PersistedSearchMetadata MapMetadata(Entities.SearchJobEntity metadata)
        => new(
            metadata.JobId,
            metadata.Query,
            metadata.Revision,
            metadata.ResultCount,
            metadata.LockedFileCount,
            metadata.ObservedPeerCount,
            metadata.IsComplete,
            FromUnix(metadata.CompletedAtUtc),
            metadata.ResultPersistenceState,
            FromUnix(metadata.ResultsPrunedAtUtc));

    private static PersistedSearchResult MapResult(Entities.SearchResultEntity result)
        => new(
            result.Id,
            result.SearchJobId,
            result.Sequence,
            result.Revision,
            result.Username,
            result.RemoteFilename,
            result.SizeBytes,
            result.BitRate,
            result.BitDepth,
            result.ResponseFileCount,
            result.SampleRate,
            result.DurationSeconds,
            result.Extension,
            result.UploadSpeed,
            result.HasFreeUploadSlot,
            result.AttributesJson,
            DateTimeOffset.FromUnixTimeMilliseconds(result.ObservedAtUtc),
            result.QueueLength,
            Enum.TryParse<SearchResultVisibility>(result.Visibility, out var visibility)
                ? visibility
                : SearchResultVisibility.Public);

    private static DateTimeOffset? FromUnix(long? value)
        => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null;
}

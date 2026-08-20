using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Sockseek.Api;

namespace Sockseek.Server;

public sealed class IdempotencyConflictException : InvalidOperationException;

/// <summary>
/// Bounded daemon-local single-flight/idempotency memory for browse submissions.
/// Eviction never rejects new work; it only ends the guarantee for the oldest key.
/// </summary>
public sealed class UserShareSubmissionStore
{
    public const int MaximumRetainedRequests = 4_096;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<Guid, Entry> entries = [];
    private readonly Func<DateTimeOffset> utcNow;
    private readonly int maximumRetainedRequests;
    private readonly TimeSpan retention;

    public UserShareSubmissionStore(
        Func<DateTimeOffset>? utcNow = null,
        int maximumRetainedRequests = MaximumRetainedRequests,
        TimeSpan? retention = null)
    {
        if (maximumRetainedRequests < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedRequests));
        if (retention is { } value && value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention));

        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.maximumRetainedRequests = maximumRetainedRequests;
        this.retention = retention ?? Retention;
    }

    public static string Fingerprint(Guid browseId, StartUserShareDownloadsRequestDto request)
    {
        if (browseId == Guid.Empty)
            throw new ArgumentException("BrowseId cannot be empty.", nameof(browseId));
        ArgumentNullException.ThrowIfNull(request);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            request,
            SockseekApiJsonContext.Default.StartUserShareDownloadsRequestDto);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(browseId.ToByteArray());
        hash.AppendData(json);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public async Task<StartUserShareDownloadsResponseDto> ExecuteAsync(
        Guid requestId,
        string fingerprint,
        Func<Task<StartUserShareDownloadsResponseDto>> submit,
        CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty)
            throw new ArgumentException("RequestId cannot be empty.", nameof(requestId));
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        ArgumentNullException.ThrowIfNull(submit);

        Evict();
        var candidate = new Entry(
            fingerprint,
            utcNow(),
            new Lazy<Task<StartUserShareDownloadsResponseDto>>(
                submit,
                LazyThreadSafetyMode.ExecutionAndPublication));
        Entry entry = entries.GetOrAdd(requestId, candidate);
        if (!StringComparer.Ordinal.Equals(entry.Fingerprint, fingerprint))
            throw new IdempotencyConflictException();

        try
        {
            StartUserShareDownloadsResponseDto response =
                await entry.Response.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            Evict();
            return response;
        }
        catch
        {
            // A caller cancelling its wait does not own or cancel the shared
            // submission. Only a terminally failed/cancelled submission is
            // forgotten so a later retry can make a fresh attempt.
            if (entry.Response.IsValueCreated
                && entry.Response.Value.IsCompleted
                && !entry.Response.Value.IsCompletedSuccessfully)
            {
                entries.TryRemove(new KeyValuePair<Guid, Entry>(requestId, entry));
            }
            throw;
        }
    }

    private void Evict()
    {
        DateTimeOffset cutoff = utcNow() - retention;
        foreach ((Guid key, Entry value) in entries)
        {
            if (value.CreatedAt < cutoff
                && (!value.Response.IsValueCreated || value.Response.Value.IsCompleted))
            {
                entries.TryRemove(new KeyValuePair<Guid, Entry>(key, value));
            }
        }

        int excess = entries.Count - maximumRetainedRequests;
        if (excess <= 0)
            return;
        foreach ((Guid key, Entry value) in entries
                     .Where(pair => !pair.Value.Response.IsValueCreated
                                    || pair.Value.Response.Value.IsCompleted)
                     .OrderBy(pair => pair.Value.CreatedAt)
                     .Take(excess))
        {
            entries.TryRemove(new KeyValuePair<Guid, Entry>(key, value));
        }
    }

    private sealed record Entry(
        string Fingerprint,
        DateTimeOffset CreatedAt,
        Lazy<Task<StartUserShareDownloadsResponseDto>> Response);
}

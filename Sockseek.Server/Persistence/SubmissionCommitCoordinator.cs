using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sockseek.Api;
using Sockseek.Persistence.Read;

namespace Sockseek.Server.Persistence;

public sealed record SubmissionCommitExecution<T>(T Receipt, Guid? SubmissionId);

/// <summary>
/// Submission-owned single-flight and durable receipt lookup for mutation
/// retries. Client selection state never enters this owner.
/// </summary>
public sealed class SubmissionCommitCoordinator(
    PersistenceCoordinator persistence,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan MemoryRetention = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<Guid, Entry> entries = [];
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<T> ExecuteAsync<T>(
        Guid idempotencyKey,
        string fingerprint,
        Func<CancellationToken, Task<SubmissionCommitExecution<T>>> submit,
        CancellationToken cancellationToken = default)
    {
        if (idempotencyKey == Guid.Empty)
            throw new ArgumentException("A non-empty idempotency key is required.", nameof(idempotencyKey));
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        ArgumentNullException.ThrowIfNull(submit);

        EvictCompleted();
        var candidate = new Entry(
            fingerprint,
            clock.GetUtcNow(),
            new Lazy<Task<string>>(
                () => ExecuteCoreAsync(
                    idempotencyKey,
                    fingerprint,
                    submit),
                LazyThreadSafetyMode.ExecutionAndPublication));
        Entry entry = entries.GetOrAdd(idempotencyKey, candidate);
        if (!StringComparer.Ordinal.Equals(entry.Fingerprint, fingerprint))
            throw new IdempotencyConflictException();

        Task<string> receiptTask = entry.Receipt.Value;
        try
        {
            string json = await receiptTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(json, SockseekApiJson.CreateSerializerOptions())
                ?? throw new InvalidDataException("The retained submission receipt is invalid.");
        }
        catch
        {
            if (receiptTask.IsCompleted && !receiptTask.IsCompletedSuccessfully)
                entries.TryRemove(new KeyValuePair<Guid, Entry>(idempotencyKey, entry));
            throw;
        }
    }

    public static string Fingerprint(
        string scope,
        Guid resourceId,
        long revision,
        RefSelectionExpressionDto selection)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(selection.Refs);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, scope);
        hash.AppendData(resourceId.ToByteArray());
        hash.AppendData(BitConverter.GetBytes(revision));
        Append(hash, selection.Mode.ToString());
        foreach (string itemRef in selection.Refs
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            Append(hash, itemRef);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task<string> ExecuteCoreAsync<T>(
        Guid idempotencyKey,
        string fingerprint,
        Func<CancellationToken, Task<SubmissionCommitExecution<T>>> submit)
    {
        ISubmissionStore? store = persistence.Submissions;
        if (store != null)
        {
            PersistedSubmission? existing = await store.GetSubmissionAsync(
                idempotencyKey,
                CancellationToken.None).ConfigureAwait(false);
            if (existing != null)
            {
                if (!StringComparer.Ordinal.Equals(existing.CommitFingerprint, fingerprint))
                    throw new IdempotencyConflictException();
                return existing.CommitReceiptJson
                    ?? throw new InvalidOperationException(
                        "The submission was accepted but its commit receipt is not yet available.");
            }
        }

        SubmissionCommitExecution<T> completed = await submit(CancellationToken.None)
            .ConfigureAwait(false);
        string receiptJson = JsonSerializer.Serialize(
            completed.Receipt,
            SockseekApiJson.CreateSerializerOptions());
        if (completed.SubmissionId is Guid submissionId && store != null)
        {
            await store.SetCommitReceiptAsync(
                submissionId,
                fingerprint,
                receiptJson,
                CancellationToken.None).ConfigureAwait(false);
        }
        return receiptJson;
    }

    private void EvictCompleted()
    {
        DateTimeOffset cutoff = clock.GetUtcNow() - MemoryRetention;
        foreach ((Guid key, Entry entry) in entries)
        {
            if (entry.CreatedAtUtc < cutoff
                && (!entry.Receipt.IsValueCreated || entry.Receipt.Value.IsCompleted))
            {
                entries.TryRemove(new KeyValuePair<Guid, Entry>(key, entry));
            }
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private sealed record Entry(
        string Fingerprint,
        DateTimeOffset CreatedAtUtc,
        Lazy<Task<string>> Receipt);
}

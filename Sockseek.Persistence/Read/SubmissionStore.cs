using System.Text;
using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Read;

public sealed record SubmissionHistoryQuery(
    string? Cursor = null,
    int Limit = 100,
    bool Archived = false);

public sealed record PersistedSubmission(
    Guid Id,
    DateTimeOffset SubmittedAtUtc,
    int SpecificationSchemaVersion,
    string SpecificationJson,
    Guid? RerunOfSubmissionId,
    Guid? PreviewId,
    string? ArtifactId,
    string? CommitFingerprint,
    string? CommitReceiptJson,
    long Revision,
    DateTimeOffset? ArchivedAtUtc,
    int TotalJobCount,
    int UserRootJobCount,
    int ActiveJobCount,
    int FailedJobCount);

public sealed record PersistedSubmissionPage(
    IReadOnlyList<PersistedSubmission> Items,
    string? NextCursor);

public sealed record SubmissionArchiveResult(
    Guid SubmissionId,
    bool Archived,
    int AffectedSubmissionCount,
    int AffectedJobCount,
    int RejectedSubmissionCount,
    string? RejectionReason);

public sealed record SubmissionRegistration(
    Guid SubmissionId,
    DateTimeOffset SubmittedAtUtc,
    int SpecificationSchemaVersion,
    string SpecificationJson,
    Guid? RerunOfSubmissionId,
    Guid? PreviewId,
    string? ArtifactId,
    string? CommitFingerprint = null);

public interface ISubmissionStore
{
    Task CreateAsync(
        SubmissionRegistration registration,
        CancellationToken cancellationToken = default);
    Task<PersistedSubmissionPage> GetSubmissionsAsync(
        SubmissionHistoryQuery query,
        CancellationToken cancellationToken = default);
    Task<PersistedSubmission?> GetSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default);
    Task SetCommitReceiptAsync(
        Guid submissionId,
        string fingerprint,
        string receiptJson,
        CancellationToken cancellationToken = default);
    Task<SubmissionArchiveResult> SetArchivedAsync(
        Guid submissionId,
        bool archived,
        CancellationToken cancellationToken = default);
}

public sealed class SubmissionStore(
    IDbContextFactory<SockseekDbContext> contextFactory,
    PersistenceInbox inbox,
    TimeProvider? timeProvider = null) : ISubmissionStore
{
    public const int MaximumPageSize = 200;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task CreateAsync(
        SubmissionRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.SubmissionId == Guid.Empty)
            throw new ArgumentException("Submission ID cannot be empty.", nameof(registration));
        if (registration.SpecificationSchemaVersion <= 0
            || string.IsNullOrWhiteSpace(registration.SpecificationJson))
            throw new ArgumentException("Submission specification is required.", nameof(registration));

        var command = new AwaitablePersistenceCommand<bool>(async (context, ct) =>
        {
            var existing = await context.Submissions
                .SingleOrDefaultAsync(item => item.Id == registration.SubmissionId, ct)
                .ConfigureAwait(false);
            if (existing != null)
            {
                if (existing.SpecificationSchemaVersion != registration.SpecificationSchemaVersion
                    || !string.Equals(
                        existing.SpecificationJson,
                        registration.SpecificationJson,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        existing.CommitFingerprint,
                        registration.CommitFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Submission {registration.SubmissionId:N} is already registered with a different specification.");
                }
                return false;
            }

            context.Submissions.Add(new SubmissionEntity
            {
                Id = registration.SubmissionId,
                SubmittedAtUtc = registration.SubmittedAtUtc
                    .ToUniversalTime().ToUnixTimeMilliseconds(),
                SpecificationSchemaVersion = registration.SpecificationSchemaVersion,
                SpecificationJson = registration.SpecificationJson,
                RerunOfSubmissionId = registration.RerunOfSubmissionId,
                PreviewId = registration.PreviewId,
                ArtifactId = registration.ArtifactId,
                CommitFingerprint = registration.CommitFingerprint,
                Revision = 1,
            });
            return true;
        });
        await inbox.EnqueueCommandAsync(command, cancellationToken).ConfigureAwait(false);
        await command.Task.ConfigureAwait(false);
    }

    public async Task<PersistedSubmissionPage> GetSubmissionsAsync(
        SubmissionHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(query), $"Submission page size must be between 1 and {MaximumPageSize}.");
        CursorValue? cursor = DecodeCursor(query.Cursor);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var submissions = context.Submissions.AsNoTracking()
            .Where(item => query.Archived
                ? item.ArchivedAtUtc != null
                : item.ArchivedAtUtc == null);
        if (cursor != null)
        {
            submissions = submissions.Where(item =>
                item.SubmittedAtUtc < cursor.SubmittedAtUtc
                || item.SubmittedAtUtc == cursor.SubmittedAtUtc && item.Id.CompareTo(cursor.Id) < 0);
        }

        var rows = await submissions
            .OrderByDescending(item => item.SubmittedAtUtc)
            .ThenByDescending(item => item.Id)
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasMore = rows.Count > query.Limit;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);
        var items = await MapAsync(context, rows, cancellationToken).ConfigureAwait(false);
        string? next = hasMore && rows.Count > 0
            ? EncodeCursor(rows[^1].SubmittedAtUtc, rows[^1].Id)
            : null;
        return new PersistedSubmissionPage(items, next);
    }

    public async Task<PersistedSubmission?> GetSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await context.Submissions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == submissionId, cancellationToken)
            .ConfigureAwait(false);
        if (row == null)
            return null;
        return (await MapAsync(context, [row], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task SetCommitReceiptAsync(
        Guid submissionId,
        string fingerprint,
        string receiptJson,
        CancellationToken cancellationToken = default)
    {
        if (submissionId == Guid.Empty)
            throw new ArgumentException("Submission ID cannot be empty.", nameof(submissionId));
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        ArgumentException.ThrowIfNullOrEmpty(receiptJson);
        var command = new AwaitablePersistenceCommand<bool>(async (context, ct) =>
        {
            SubmissionEntity submission = await context.Submissions
                .SingleOrDefaultAsync(item => item.Id == submissionId, ct)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The submission was not found.");
            if (!string.Equals(
                    submission.CommitFingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The idempotency key belongs to a different submission request.");
            if (submission.CommitReceiptJson != null)
            {
                if (!string.Equals(
                        submission.CommitReceiptJson,
                        receiptJson,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The submission already has a different commit receipt.");
                return false;
            }
            submission.CommitReceiptJson = receiptJson;
            submission.Revision = checked(submission.Revision + 1);
            return true;
        });
        await inbox.EnqueueCommandAsync(command, cancellationToken).ConfigureAwait(false);
        await command.Task.ConfigureAwait(false);
    }

    public async Task<SubmissionArchiveResult> SetArchivedAsync(
        Guid submissionId,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        var command = new AwaitablePersistenceCommand<SubmissionArchiveResult>(async (context, ct) =>
        {
            var submission = await context.Submissions
                .SingleOrDefaultAsync(item => item.Id == submissionId, ct)
                .ConfigureAwait(false);
            if (submission == null)
                return new(submissionId, archived, 0, 0, 1, "not-found");

            int totalJobs = await context.Jobs
                .CountAsync(job => job.SubmissionId == submissionId, ct)
                .ConfigureAwait(false);
            if (archived)
            {
                int activeJobs = await context.Jobs
                    .CountAsync(
                        job => job.SubmissionId == submissionId
                            && job.LifecycleState != "Terminal",
                        ct)
                    .ConfigureAwait(false);
                if (activeJobs > 0)
                    return new(submissionId, true, 0, 0, 1, "nonterminal-jobs");
            }

            bool changed = archived
                ? submission.ArchivedAtUtc == null
                : submission.ArchivedAtUtc != null;
            if (changed)
            {
                submission.ArchivedAtUtc = archived
                    ? clock.GetUtcNow().ToUnixTimeMilliseconds()
                    : null;
                submission.Revision = checked(submission.Revision + 1);
            }
            return new(submissionId, archived, changed ? 1 : 0, changed ? totalJobs : 0, 0, null);
        });
        await inbox.EnqueueCommandAsync(command, cancellationToken).ConfigureAwait(false);
        // Once admitted, archive/unarchive is durable intent owned by the writer.
        return await command.Task.ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<PersistedSubmission>> MapAsync(
        SockseekDbContext context,
        IReadOnlyList<SubmissionEntity> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return [];
        Guid[] ids = rows.Select(row => row.Id).ToArray();
        var counts = await context.Jobs.AsNoTracking()
            .Where(job => job.SubmissionId != null && ids.Contains(job.SubmissionId.Value))
            .GroupBy(job => job.SubmissionId!.Value)
            .Select(group => new
            {
                SubmissionId = group.Key,
                Total = group.Count(),
                UserRoots = group.Count(job => job.SemanticRole == "UserRoot"),
                Active = group.Count(job => job.LifecycleState != "Terminal"),
                Failed = group.Count(job => job.TerminalOutcome == "Failed"),
            })
            .ToDictionaryAsync(item => item.SubmissionId, cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(row =>
        {
            counts.TryGetValue(row.Id, out var count);
            return new PersistedSubmission(
                row.Id,
                DateTimeOffset.FromUnixTimeMilliseconds(row.SubmittedAtUtc),
                row.SpecificationSchemaVersion,
                row.SpecificationJson,
                row.RerunOfSubmissionId,
                row.PreviewId,
                row.ArtifactId,
                row.CommitFingerprint,
                row.CommitReceiptJson,
                row.Revision,
                row.ArchivedAtUtc.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(row.ArchivedAtUtc.Value)
                    : null,
                count?.Total ?? 0,
                count?.UserRoots ?? 0,
                count?.Active ?? 0,
                count?.Failed ?? 0);
        }).ToArray();
    }

    private static string EncodeCursor(long submittedAtUtc, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{submittedAtUtc}:{id:D}"));

    private static CursorValue? DecodeCursor(string? cursor)
    {
        if (cursor == null)
            return null;
        if (cursor.Length > 128)
            throw new ArgumentException("The submission cursor is malformed.", nameof(cursor));
        try
        {
            string value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            int separator = value.IndexOf(':');
            if (separator <= 0
                || !long.TryParse(value[..separator], out long submittedAtUtc)
                || !Guid.TryParse(value[(separator + 1)..], out Guid id))
                throw new FormatException();
            return new CursorValue(submittedAtUtc, id);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ArgumentException("The submission cursor is malformed.", nameof(cursor), ex);
        }
    }

    private sealed record CursorValue(long SubmittedAtUtc, Guid Id);
}

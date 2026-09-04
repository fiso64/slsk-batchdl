using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using Sockseek.Core.Snapshots;

namespace Sockseek.Persistence.Read;

public sealed record PersistedTransfer(
    Guid Id,
    Guid? JobId,
    Guid? WorkflowId,
    string Direction,
    string Source,
    string? Username,
    string? RemotePath,
    string? LocalPath,
    string State,
    string TerminalOutcome,
    long? TotalBytes,
    long TransferredBytes,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string FailureReason,
    string? FailureMessage,
    string CancellationSource,
    long Revision,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? LastProgressAtUtc = null,
    long? BytesPerSecond = null,
    TransferFileMetadataSnapshot? File = null,
    string? GroupRef = null,
    string? GroupDisplayPath = null,
    DateTimeOffset? ArchivedAtUtc = null);

public sealed record PersistedTransferAttempt(
    Guid Id,
    Guid TransferId,
    int AttemptNumber,
    string Source,
    string State,
    string? SourceUsername,
    string? SourcePath,
    string? OutputPath,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string FailureReason,
    string? FailureMessage,
    long Revision);

public sealed record PersistedTransferDetail(PersistedTransfer Transfer, PersistedTransferAttempt? LatestAttempt);
public sealed record TransferHistoryQuery(
    string? Cursor = null,
    int Limit = 100,
    Guid? JobId = null,
    Guid? WorkflowId = null,
    string? Direction = null,
    string? Source = null,
    string? State = null,
    string? TerminalOutcome = null,
    string? Username = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    bool Archived = false);
public sealed record PersistedTransferPage(IReadOnlyList<PersistedTransfer> Items, string? NextCursor);
public sealed record PersistedTransferAttemptPage(IReadOnlyList<PersistedTransferAttempt> Items, int? NextAttemptNumber);
public sealed record TransferArchiveFilter(
    Guid? TransferId = null,
    string? Direction = null,
    string? TerminalOutcome = null,
    string? Username = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null);
public sealed record TransferArchiveResult(
    int ResolvedCount,
    int ChangedCount,
    int NoOpCount,
    int RejectedCount,
    IReadOnlyDictionary<string, int> Reasons);

public interface ITransferHistoryReader
{
    Task<PersistedTransferPage> GetTransfersAsync(TransferHistoryQuery query, CancellationToken cancellationToken = default);
    Task<PersistedTransferDetail?> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default);
    Task<PersistedTransferAttemptPage?> GetAttemptsAsync(Guid transferId, int afterAttemptNumber, int limit, CancellationToken cancellationToken = default);
    Task<TransferArchiveResult> SetArchivedAsync(
        TransferArchiveFilter filter,
        bool archived,
        CancellationToken cancellationToken = default);
}

public sealed class TransferHistoryReader(
    IDbContextFactory<SockseekDbContext> contextFactory,
    Persistence.Write.PersistenceInbox? inbox = null,
    TimeProvider? timeProvider = null) : ITransferHistoryReader
{
    public const int MaximumPageSize = 200;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<PersistedTransferPage> GetTransfersAsync(TransferHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(query));
        var cursor = DecodeCursor(query.Cursor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var transfers = context.Transfers.AsNoTracking().AsQueryable();
        transfers = transfers.Where(row => query.Archived
            ? row.ArchivedAtUtc != null
            : row.ArchivedAtUtc == null);
        if (query.JobId.HasValue) transfers = transfers.Where(row => row.JobId == query.JobId);
        if (query.WorkflowId.HasValue) transfers = transfers.Where(row => row.WorkflowId == query.WorkflowId);
        if (query.Direction != null) transfers = transfers.Where(row => EF.Functions.Collate(row.Direction, "NOCASE") == query.Direction);
        if (query.Source != null) transfers = transfers.Where(row => EF.Functions.Collate(row.Source, "NOCASE") == query.Source);
        if (query.State != null) transfers = transfers.Where(row => EF.Functions.Collate(row.State, "NOCASE") == query.State);
        if (query.TerminalOutcome != null) transfers = transfers.Where(row => EF.Functions.Collate(row.TerminalOutcome, "NOCASE") == query.TerminalOutcome);
        if (query.Username != null) transfers = transfers.Where(row => row.Username == query.Username);
        if (query.FromUtc.HasValue)
        {
            long from = query.FromUtc.Value.ToUniversalTime().ToUnixTimeMilliseconds();
            transfers = transfers.Where(row => row.CreatedAtUtc >= from);
        }
        if (query.ToUtc.HasValue)
        {
            long to = query.ToUtc.Value.ToUniversalTime().ToUnixTimeMilliseconds();
            transfers = transfers.Where(row => row.CreatedAtUtc <= to);
        }
        if (cursor.HasValue)
            transfers = transfers.Where(row => row.CreatedAtUtc < cursor.Value.CreatedAtUtc
                || row.CreatedAtUtc == cursor.Value.CreatedAtUtc && row.Id.CompareTo(cursor.Value.Id) < 0);
        var rows = await transfers.OrderByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.Id)
            .Take(query.Limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        bool hasMore = rows.Count > query.Limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new PersistedTransferPage(
            rows.Select(MapTransfer).ToArray(),
            hasMore && rows.Count > 0 ? EncodeCursor(rows[^1].CreatedAtUtc, rows[^1].Id) : null);
    }

    public async Task<PersistedTransferDetail?> GetTransferAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transfer = await context.Transfers.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == transferId, cancellationToken)
            .ConfigureAwait(false);
        if (transfer == null) return null;
        var latestAttempt = await context.TransferAttempts.AsNoTracking()
            .Where(attempt => attempt.TransferId == transferId)
            .OrderByDescending(attempt => attempt.AttemptNumber)
            .ThenByDescending(attempt => attempt.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var detail = new PersistedTransferDetail(
            MapTransfer(transfer),
            latestAttempt == null ? null : MapAttempt(latestAttempt));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return detail;
    }

    public async Task<PersistedTransferAttemptPage?> GetAttemptsAsync(
        Guid transferId,
        int afterAttemptNumber,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (afterAttemptNumber < 0) throw new ArgumentOutOfRangeException(nameof(afterAttemptNumber));
        if (limit is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await context.Transfers.AsNoTracking().AnyAsync(row => row.Id == transferId, cancellationToken).ConfigureAwait(false))
            return null;
        var rows = await context.TransferAttempts.AsNoTracking()
            .Where(row => row.TransferId == transferId && row.AttemptNumber > afterAttemptNumber)
            .OrderBy(row => row.AttemptNumber).Take(limit + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        bool hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var page = new PersistedTransferAttemptPage(
            rows.Select(MapAttempt).ToArray(),
            hasMore && rows.Count > 0 ? rows[^1].AttemptNumber : null);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return page;
    }

    public async Task<TransferArchiveResult> SetArchivedAsync(
        TransferArchiveFilter filter,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (inbox is null)
            throw new NotSupportedException("Transfer archive requires the daemon persistence writer.");

        var command = new Persistence.Write.AwaitablePersistenceCommand<TransferArchiveResult>(
            async (context, ct) =>
            {
                IQueryable<Entities.TransferEntity> matching = ApplyArchiveFilter(
                    context.Transfers,
                    filter);
                int resolved = await matching.CountAsync(ct).ConfigureAwait(false);
                if (resolved == 0)
                {
                    return filter.TransferId.HasValue
                        ? new(0, 0, 0, 1, new Dictionary<string, int>
                        {
                            ["not-found"] = 1,
                        })
                        : new(0, 0, 0, 0, new Dictionary<string, int>());
                }

                int rejected = archived
                    ? await matching.CountAsync(
                        transfer => transfer.TerminalOutcome == "None",
                        ct).ConfigureAwait(false)
                    : 0;
                IQueryable<Entities.TransferEntity> terminal = matching
                    .Where(transfer => transfer.TerminalOutcome != "None");
                IQueryable<Entities.TransferEntity> changes = archived
                    ? terminal.Where(transfer => transfer.ArchivedAtUtc == null)
                    : terminal.Where(transfer => transfer.ArchivedAtUtc != null);
                long? archiveTime = archived
                    ? clock.GetUtcNow().ToUnixTimeMilliseconds()
                    : null;
                int changed = await changes.ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        transfer => transfer.ArchivedAtUtc,
                        archiveTime),
                    ct).ConfigureAwait(false);
                int noOp = resolved - rejected - changed;
                var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
                if (rejected > 0)
                    reasons["nonterminal"] = rejected;
                if (noOp > 0)
                    reasons[archived ? "already-archived" : "already-restored"] = noOp;
                return new(resolved, changed, noOp, rejected, reasons);
            });
        await inbox.EnqueueCommandAsync(command, cancellationToken).ConfigureAwait(false);
        return await command.Task.ConfigureAwait(false);
    }

    private static IQueryable<Entities.TransferEntity> ApplyArchiveFilter(
        IQueryable<Entities.TransferEntity> transfers,
        TransferArchiveFilter filter)
    {
        if (filter.TransferId.HasValue)
            transfers = transfers.Where(row => row.Id == filter.TransferId.Value);
        if (filter.Direction is not null)
            transfers = transfers.Where(row => EF.Functions.Collate(row.Direction, "NOCASE") == filter.Direction);
        if (filter.TerminalOutcome is not null)
            transfers = transfers.Where(row => EF.Functions.Collate(row.TerminalOutcome, "NOCASE") == filter.TerminalOutcome);
        if (filter.Username is not null)
            transfers = transfers.Where(row => row.Username == filter.Username);
        if (filter.FromUtc.HasValue)
        {
            long from = filter.FromUtc.Value.ToUniversalTime().ToUnixTimeMilliseconds();
            transfers = transfers.Where(row => row.CreatedAtUtc >= from);
        }
        if (filter.ToUtc.HasValue)
        {
            long to = filter.ToUtc.Value.ToUniversalTime().ToUnixTimeMilliseconds();
            transfers = transfers.Where(row => row.CreatedAtUtc <= to);
        }
        return transfers;
    }

    private static PersistedTransfer MapTransfer(Entities.TransferEntity transfer)
        => new(
            transfer.Id, transfer.JobId, transfer.WorkflowId, transfer.Direction, transfer.Source,
            transfer.Username, transfer.RemotePath, transfer.LocalPath, transfer.State, transfer.TerminalOutcome,
            transfer.TotalBytes == long.MaxValue ? null : transfer.TotalBytes,
            transfer.TransferredBytes, transfer.AttemptCount,
            DateTimeOffset.FromUnixTimeMilliseconds(transfer.CreatedAtUtc), FromUnix(transfer.CompletedAtUtc),
            transfer.FailureReason, transfer.FailureMessage, transfer.CancellationSource, transfer.Revision,
            FromUnix(transfer.StartedAtUtc),
            FromUnix(transfer.LastProgressAtUtc),
            transfer.BytesPerSecond,
            MapFile(transfer),
            transfer.GroupRef,
            transfer.GroupDisplayPath,
            FromUnix(transfer.ArchivedAtUtc));

    private static TransferFileMetadataSnapshot? MapFile(
        Entities.TransferEntity transfer)
    {
        if (transfer.FileName is null || transfer.FileSizeBytes is null)
            return null;
        IReadOnlyList<FileAttributeSnapshot>? attributes = transfer.FileAttributesJson is null
            ? null
            : JsonSerializer.Deserialize<FileAttributeSnapshot[]>(transfer.FileAttributesJson);
        return new TransferFileMetadataSnapshot(
            transfer.FileName,
            transfer.FileSizeBytes.Value,
            transfer.FileExtension,
            transfer.FileBitRate,
            transfer.FileBitDepth,
            transfer.FileSampleRate,
            transfer.FileLength,
            attributes);
    }

    private static PersistedTransferAttempt MapAttempt(Entities.TransferAttemptEntity attempt)
        => new(
            attempt.Id, attempt.TransferId, attempt.AttemptNumber, attempt.Source, attempt.State,
            attempt.SourceUsername, attempt.SourcePath, attempt.OutputPath,
            DateTimeOffset.FromUnixTimeMilliseconds(attempt.StartedAtUtc), FromUnix(attempt.CompletedAtUtc),
            attempt.FailureReason, attempt.FailureMessage, attempt.Revision);

    public static string EncodeCursor(long createdAtUtc, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{createdAtUtc}:{id:N}"));

    public static TransferCursorValue? DecodeCursor(string? cursor)
    {
        if (cursor is null) return null;
        if (cursor.Length > 128)
            throw new ArgumentException("The transfer cursor is malformed.", nameof(cursor));
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            int separator = decoded.IndexOf(':');
            if (separator <= 0
                || !long.TryParse(decoded[..separator], System.Globalization.CultureInfo.InvariantCulture, out long created)
                || !Guid.TryParseExact(decoded[(separator + 1)..], "N", out Guid id))
                throw new FormatException();
            return new TransferCursorValue(created, id);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ArgumentException("The transfer cursor is malformed.", nameof(cursor), ex);
        }
    }

    private static DateTimeOffset? FromUnix(long? value)
        => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null;

    public readonly record struct TransferCursorValue(long CreatedAtUtc, Guid Id);
}

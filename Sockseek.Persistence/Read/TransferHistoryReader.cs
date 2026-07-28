using Microsoft.EntityFrameworkCore;
using System.Text;

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
    long Revision);

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

public sealed record PersistedTransferDetail(PersistedTransfer Transfer, IReadOnlyList<PersistedTransferAttempt> Attempts);
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
    DateTimeOffset? ToUtc = null);
public sealed record PersistedTransferPage(IReadOnlyList<PersistedTransfer> Items, string? NextCursor);
public sealed record PersistedTransferAttemptPage(IReadOnlyList<PersistedTransferAttempt> Items, int? NextAttemptNumber);

public interface ITransferHistoryReader
{
    Task<PersistedTransferPage> GetTransfersAsync(TransferHistoryQuery query, CancellationToken cancellationToken = default);
    Task<PersistedTransferDetail?> GetTransferAsync(Guid transferId, int attemptLimit = 200, CancellationToken cancellationToken = default);
    Task<PersistedTransferAttemptPage?> GetAttemptsAsync(Guid transferId, int afterAttemptNumber, int limit, CancellationToken cancellationToken = default);
}

public sealed class TransferHistoryReader(IDbContextFactory<SockseekDbContext> contextFactory) : ITransferHistoryReader
{
    public const int MaximumPageSize = 200;

    public async Task<PersistedTransferPage> GetTransfersAsync(TransferHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(query));
        var cursor = DecodeCursor(query.Cursor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var transfers = context.Transfers.AsNoTracking().AsQueryable();
        if (query.JobId.HasValue) transfers = transfers.Where(row => row.JobId == query.JobId);
        if (query.WorkflowId.HasValue) transfers = transfers.Where(row => row.WorkflowId == query.WorkflowId);
        if (query.Direction != null) transfers = transfers.Where(row => row.Direction == query.Direction);
        if (query.Source != null) transfers = transfers.Where(row => row.Source == query.Source);
        if (query.State != null) transfers = transfers.Where(row => row.State == query.State);
        if (query.TerminalOutcome != null) transfers = transfers.Where(row => row.TerminalOutcome == query.TerminalOutcome);
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
            transfers = transfers.Where(row => row.CreatedAtUtc > cursor.Value.CreatedAtUtc
                || row.CreatedAtUtc == cursor.Value.CreatedAtUtc && row.Id.CompareTo(cursor.Value.Id) > 0);
        var rows = await transfers.OrderBy(row => row.CreatedAtUtc).ThenBy(row => row.Id)
            .Take(query.Limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        bool hasMore = rows.Count > query.Limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new PersistedTransferPage(
            rows.Select(MapTransfer).ToArray(),
            hasMore && rows.Count > 0 ? EncodeCursor(rows[^1].CreatedAtUtc, rows[^1].Id) : null);
    }

    public async Task<PersistedTransferDetail?> GetTransferAsync(Guid transferId, int attemptLimit = 200, CancellationToken cancellationToken = default)
    {
        if (attemptLimit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(attemptLimit));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var transfer = await context.Transfers.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == transferId, cancellationToken)
            .ConfigureAwait(false);
        if (transfer == null) return null;
        var attempts = await context.TransferAttempts.AsNoTracking()
            .Where(attempt => attempt.TransferId == transferId)
            .OrderBy(attempt => attempt.AttemptNumber)
            .Take(attemptLimit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PersistedTransferDetail(
            MapTransfer(transfer),
            attempts.Select(MapAttempt).ToArray());
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
        if (!await context.Transfers.AsNoTracking().AnyAsync(row => row.Id == transferId, cancellationToken).ConfigureAwait(false))
            return null;
        var rows = await context.TransferAttempts.AsNoTracking()
            .Where(row => row.TransferId == transferId && row.AttemptNumber > afterAttemptNumber)
            .OrderBy(row => row.AttemptNumber).Take(limit + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        bool hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new PersistedTransferAttemptPage(
            rows.Select(MapAttempt).ToArray(),
            hasMore && rows.Count > 0 ? rows[^1].AttemptNumber : null);
    }

    private static PersistedTransfer MapTransfer(Entities.TransferEntity transfer)
        => new(
            transfer.Id, transfer.JobId, transfer.WorkflowId, transfer.Direction, transfer.Source,
            transfer.Username, transfer.RemotePath, transfer.LocalPath, transfer.State, transfer.TerminalOutcome,
            transfer.TotalBytes == long.MaxValue ? null : transfer.TotalBytes,
            transfer.TransferredBytes, transfer.AttemptCount,
            DateTimeOffset.FromUnixTimeMilliseconds(transfer.CreatedAtUtc), FromUnix(transfer.CompletedAtUtc),
            transfer.FailureReason, transfer.FailureMessage, transfer.Revision);

    private static PersistedTransferAttempt MapAttempt(Entities.TransferAttemptEntity attempt)
        => new(
            attempt.Id, attempt.TransferId, attempt.AttemptNumber, attempt.Source, attempt.State,
            attempt.SourceUsername, attempt.SourcePath, attempt.OutputPath,
            DateTimeOffset.FromUnixTimeMilliseconds(attempt.StartedAtUtc), FromUnix(attempt.CompletedAtUtc),
            attempt.FailureReason, attempt.FailureMessage, attempt.Revision);

    private static string EncodeCursor(long createdAtUtc, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{createdAtUtc}:{id:N}"));

    private static (long CreatedAtUtc, Guid Id)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            int separator = decoded.IndexOf(':');
            if (separator <= 0
                || !long.TryParse(decoded[..separator], System.Globalization.CultureInfo.InvariantCulture, out long created)
                || !Guid.TryParseExact(decoded[(separator + 1)..], "N", out Guid id))
                throw new FormatException();
            return (created, id);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ArgumentException("The transfer cursor is malformed.", nameof(cursor), ex);
        }
    }

    private static DateTimeOffset? FromUnix(long? value)
        => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null;
}

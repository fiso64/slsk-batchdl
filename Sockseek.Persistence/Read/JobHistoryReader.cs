using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Sockseek.Persistence.Read;

public sealed record JobHistoryQuery(
    string? Cursor = null,
    int Limit = 100,
    string? LifecycleState = null,
    string? TerminalOutcome = null,
    string? SkipReason = null,
    string? Kind = null,
    Guid? WorkflowId = null,
    bool IncludeAll = false);

public sealed record PersistedJob(
    Guid Id,
    long DisplayId,
    Guid WorkflowId,
    Guid? ParentJobId,
    Guid? SourceJobId,
    Guid? ResultJobId,
    string Kind,
    string LifecycleState,
    string ActivityPhase,
    DateTimeOffset? ActivityUntilUtc,
    string TerminalOutcome,
    string SkipReason,
    string CancellationSource,
    string FailureReason,
    string? FailureMessage,
    string? FailureDetail,
    string? ItemName,
    string? QueryText,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long Revision,
    int PayloadSchemaVersion,
    string? PayloadJson);

public sealed record PersistedJobPage(IReadOnlyList<PersistedJob> Items, string? NextCursor);
public sealed record PersistedWorkflow(Guid WorkflowId, IReadOnlyList<PersistedJob> Jobs);
public sealed record PersistedWorkflowPage(IReadOnlyList<PersistedWorkflow> Items, string? NextCursor);

public interface IJobHistoryReader
{
    Task<PersistedJobPage> GetJobsAsync(JobHistoryQuery query, CancellationToken cancellationToken = default);
    Task<PersistedJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PersistedJob>> GetChildrenAsync(Guid parentJobId, CancellationToken cancellationToken = default);
    Task<PersistedWorkflowPage> GetWorkflowsAsync(string? cursor = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PersistedJob>> GetWorkflowJobsAsync(Guid workflowId, CancellationToken cancellationToken = default);
    Task<PersistedJob?> GetJobByDisplayIdAsync(Guid workflowId, long displayId, CancellationToken cancellationToken = default);
}

public sealed class JobHistoryReader(IDbContextFactory<SockseekDbContext> contextFactory) : IJobHistoryReader
{
    public const int MaximumPageSize = 200;

    public async Task<PersistedJobPage> GetJobsAsync(JobHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(query), $"Job page size must be between 1 and {MaximumPageSize}.");
        var cursor = DecodeCursor(query.Cursor);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var jobs = context.Jobs.AsNoTracking().AsQueryable();
        if (!query.IncludeAll) jobs = jobs.Where(job => job.ParentJobId == null);
        if (query.LifecycleState != null) jobs = jobs.Where(job => job.LifecycleState == query.LifecycleState);
        if (query.TerminalOutcome != null) jobs = jobs.Where(job => job.TerminalOutcome == query.TerminalOutcome);
        if (query.SkipReason != null) jobs = jobs.Where(job => job.SkipReason == query.SkipReason);
        if (query.Kind != null) jobs = jobs.Where(job => job.Kind == query.Kind);
        if (query.WorkflowId != null) jobs = jobs.Where(job => job.WorkflowId == query.WorkflowId);
        if (cursor != null)
            jobs = jobs.Where(job => job.CreatedAtUtc > cursor.CreatedAtUtc
                || job.CreatedAtUtc == cursor.CreatedAtUtc && job.Id.CompareTo(cursor.Id) > 0);

        var rows = await jobs
            .OrderBy(job => job.CreatedAtUtc)
            .ThenBy(job => job.Id)
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasMore = rows.Count > query.Limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var items = rows.Select(Map).ToArray();
        string? next = hasMore && rows.Count > 0 ? EncodeCursor(rows[^1].CreatedAtUtc, rows[^1].Id) : null;
        return new PersistedJobPage(items, next);
    }

    public async Task<PersistedJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.Jobs.AsNoTracking().SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken).ConfigureAwait(false);
        return entity == null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<PersistedJob>> GetChildrenAsync(Guid parentJobId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await context.Jobs.AsNoTracking()
            .Where(job => job.ParentJobId == parentJobId)
            .OrderBy(job => job.DisplayId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(Map).ToArray();
    }

    public async Task<PersistedWorkflowPage> GetWorkflowsAsync(
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
        var decoded = DecodeCursor(cursor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var groups = context.Jobs.AsNoTracking()
            .GroupBy(job => job.WorkflowId)
            .Select(group => new { WorkflowId = group.Key, FirstCreatedAtUtc = group.Min(job => job.CreatedAtUtc) });
        if (decoded != null)
            groups = groups.Where(group => group.FirstCreatedAtUtc > decoded.CreatedAtUtc
                || group.FirstCreatedAtUtc == decoded.CreatedAtUtc && group.WorkflowId.CompareTo(decoded.Id) > 0);
        var workflowRows = await groups
            .OrderBy(group => group.FirstCreatedAtUtc)
            .ThenBy(group => group.WorkflowId)
            .Take(limit + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        bool hasMore = workflowRows.Count > limit;
        if (hasMore) workflowRows.RemoveAt(workflowRows.Count - 1);
        var ids = workflowRows.Select(row => row.WorkflowId).ToArray();
        var jobs = await context.Jobs.AsNoTracking()
            .Where(job => ids.Contains(job.WorkflowId))
            .OrderBy(job => job.DisplayId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byWorkflow = jobs.GroupBy(job => job.WorkflowId).ToDictionary(group => group.Key);
        return new PersistedWorkflowPage(
            workflowRows.Select(row => new PersistedWorkflow(
                row.WorkflowId,
                byWorkflow.GetValueOrDefault(row.WorkflowId)?.Select(Map).ToArray() ?? [])).ToArray(),
            hasMore && workflowRows.Count > 0
                ? EncodeCursor(workflowRows[^1].FirstCreatedAtUtc, workflowRows[^1].WorkflowId)
                : null);
    }

    public async Task<IReadOnlyList<PersistedJob>> GetWorkflowJobsAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var jobs = await context.Jobs.AsNoTracking()
            .Where(job => job.WorkflowId == workflowId)
            .OrderBy(job => job.DisplayId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return jobs.Select(Map).ToArray();
    }

    public async Task<PersistedJob?> GetJobByDisplayIdAsync(
        Guid workflowId,
        long displayId,
        CancellationToken cancellationToken = default)
    {
        if (displayId <= 0) throw new ArgumentOutOfRangeException(nameof(displayId));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await context.Jobs.AsNoTracking()
            .SingleOrDefaultAsync(row => row.WorkflowId == workflowId && row.DisplayId == displayId, cancellationToken)
            .ConfigureAwait(false);
        return job == null ? null : Map(job);
    }

    private static PersistedJob Map(Entities.JobEntity job)
        => new(
            job.Id,
            job.DisplayId,
            job.WorkflowId,
            job.ParentJobId,
            job.SourceJobId,
            job.ResultJobId,
            job.Kind,
            job.LifecycleState,
            job.ActivityPhase,
            FromUnixMilliseconds(job.ActivityUntilUtc),
            job.TerminalOutcome,
            job.SkipReason,
            job.CancellationSource,
            job.FailureReason,
            job.FailureMessage,
            job.FailureDetail,
            job.ItemName,
            job.QueryText,
            DateTimeOffset.FromUnixTimeMilliseconds(job.CreatedAtUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(job.UpdatedAtUtc),
            FromUnixMilliseconds(job.CompletedAtUtc),
            job.Revision,
            job.PayloadSchemaVersion,
            job.PayloadJson);

    private static DateTimeOffset? FromUnixMilliseconds(long? value)
        => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null;

    private static CursorValue? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return null;
        try
        {
            string padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            string value = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            string[] parts = value.Split(':', 2);
            return new CursorValue(long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), Guid.ParseExact(parts[1], "N"));
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
        {
            throw new ArgumentException("The job cursor is malformed.", nameof(cursor), ex);
        }
    }

    private static string EncodeCursor(long createdAtUtc, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{createdAtUtc}:{id:N}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record CursorValue(long CreatedAtUtc, Guid Id);
}

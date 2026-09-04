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
    bool IncludeAll = false,
    Guid? ParentJobId = null,
    Guid? SubmissionId = null,
    string? SemanticRole = null,
    bool Archived = false);

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
    string? PayloadJson,
    Guid? SubmissionId = null,
    string SemanticRole = "Legacy",
    long? DiscoveryPublicFileCount = null,
    long? DiscoveryLockedFileCount = null,
    long? DiscoveryObservedPeerCount = null);

public sealed record PersistedJobPage(IReadOnlyList<PersistedJob> Items, string? NextCursor);
public sealed record PersistedWorkflowSummary(
    Guid WorkflowId,
    long FirstDisplayId,
    string Title,
    string State,
    int RootJobCount,
    int ActiveJobCount,
    int FailedJobCount,
    int CompletedJobCount);
public sealed record PersistedWorkflowPage(IReadOnlyList<PersistedWorkflowSummary> Items, string? NextCursor);

public interface IJobHistoryReader
{
    Task<PersistedJobPage> GetJobsAsync(JobHistoryQuery query, CancellationToken cancellationToken = default);
    Task<PersistedJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<int> GetChildCountAsync(Guid parentJobId, CancellationToken cancellationToken = default);
    Task<PersistedWorkflowPage> GetWorkflowsAsync(string? cursor = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<PersistedWorkflowSummary?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default);
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
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var jobs = context.Jobs.AsNoTracking().AsQueryable();
        jobs = query.Archived
            ? jobs.Where(job => job.SubmissionId != null
                && context.Submissions.Any(submission =>
                    submission.Id == job.SubmissionId
                    && submission.ArchivedAtUtc != null))
            : jobs.Where(job => job.SubmissionId == null
                || !context.Submissions.Any(submission =>
                    submission.Id == job.SubmissionId
                    && submission.ArchivedAtUtc != null));
        if (query.ParentJobId != null)
            jobs = jobs.Where(job => job.ParentJobId == query.ParentJobId);
        else if (!query.IncludeAll)
            jobs = jobs.Where(job => job.ParentJobId == null);
        if (query.LifecycleState != null) jobs = jobs.Where(job => job.LifecycleState == query.LifecycleState);
        if (query.TerminalOutcome != null) jobs = jobs.Where(job => job.TerminalOutcome == query.TerminalOutcome);
        if (query.SkipReason != null) jobs = jobs.Where(job => job.SkipReason == query.SkipReason);
        if (query.Kind != null) jobs = jobs.Where(job => job.Kind == query.Kind);
        if (query.WorkflowId != null) jobs = jobs.Where(job => job.WorkflowId == query.WorkflowId);
        if (query.SubmissionId != null) jobs = jobs.Where(job => job.SubmissionId == query.SubmissionId);
        if (query.SemanticRole != null) jobs = jobs.Where(job => job.SemanticRole == query.SemanticRole);
        if (cursor != null)
            jobs = jobs.Where(job => job.DisplayId > cursor.DisplayId
                || job.DisplayId == cursor.DisplayId && job.Id.CompareTo(cursor.Id) > 0);

        var rows = await jobs
            .OrderBy(job => job.DisplayId)
            .ThenBy(job => job.Id)
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasMore = rows.Count > query.Limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var searchMetadata = await LoadSearchMetadataAsync(
            context,
            rows,
            cancellationToken).ConfigureAwait(false);
        var items = rows.Select(row => Map(
            row,
            searchMetadata.GetValueOrDefault(row.Id))).ToArray();
        string? next = hasMore && rows.Count > 0 ? EncodeCursor(rows[^1].DisplayId, rows[^1].Id) : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PersistedJobPage(items, next);
    }

    public async Task<PersistedJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.Jobs.AsNoTracking().SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken).ConfigureAwait(false);
        if (entity == null)
            return null;
        var search = await LoadSearchMetadataAsync(
            context,
            entity,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Map(entity, search);
    }

    public async Task<int> GetChildCountAsync(Guid parentJobId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Jobs.AsNoTracking()
            .CountAsync(job => job.ParentJobId == parentJobId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PersistedWorkflowPage> GetWorkflowsAsync(
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
        var decoded = DecodeCursor(cursor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var groups = WorkflowAggregates(context);
        if (decoded != null)
            groups = groups.Where(group => group.WorkflowId.CompareTo(decoded.Id) > 0);
        var aggregateRows = await groups
            .OrderBy(group => group.WorkflowId)
            .Take(limit + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        bool hasMore = aggregateRows.Count > limit;
        if (hasMore) aggregateRows.RemoveAt(aggregateRows.Count - 1);
        var workflowRows = await LoadWorkflowSummariesAsync(context, aggregateRows, cancellationToken).ConfigureAwait(false);
        var page = new PersistedWorkflowPage(
            workflowRows,
            hasMore && workflowRows.Count > 0
                ? EncodeCursor(0, workflowRows[^1].WorkflowId)
                : null);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return page;
    }

    public async Task<PersistedWorkflowSummary?> GetWorkflowAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var aggregate = await WorkflowAggregates(context)
            .SingleOrDefaultAsync(workflow => workflow.WorkflowId == workflowId, cancellationToken)
            .ConfigureAwait(false);
        if (aggregate == null)
            return null;
        var rows = await LoadWorkflowSummariesAsync(context, [aggregate], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows[0];
    }

    public async Task<PersistedJob?> GetJobByDisplayIdAsync(
        Guid workflowId,
        long displayId,
        CancellationToken cancellationToken = default)
    {
        if (displayId <= 0) throw new ArgumentOutOfRangeException(nameof(displayId));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var job = await context.Jobs.AsNoTracking()
            .SingleOrDefaultAsync(row => row.WorkflowId == workflowId && row.DisplayId == displayId, cancellationToken)
            .ConfigureAwait(false);
        if (job == null)
            return null;
        var search = await LoadSearchMetadataAsync(
            context,
            job,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Map(job, search);
    }

    private static IQueryable<WorkflowAggregate> WorkflowAggregates(SockseekDbContext context)
        => context.Jobs.AsNoTracking()
            .GroupBy(job => job.WorkflowId)
            .Select(group => new WorkflowAggregate
            {
                WorkflowId = group.Key,
                FirstDisplayId = group.Min(job => job.DisplayId),
                RootJobCount = group.Count(job => job.ParentJobId == null),
                ActiveJobCount = group.Count(job => job.LifecycleState != "Terminal"),
                FailedJobCount = group.Count(job => job.TerminalOutcome == "Failed"
                    || job.TerminalOutcome == "Cancelled"
                    || job.TerminalOutcome == "PartialSuccess"
                    || job.TerminalOutcome == "Skipped" && job.SkipReason != "AlreadyExists"),
                CompletedJobCount = group.Count(job => job.LifecycleState == "Terminal"),
            });

    private static async Task<IReadOnlyList<PersistedWorkflowSummary>> LoadWorkflowSummariesAsync(
        SockseekDbContext context,
        IReadOnlyList<WorkflowAggregate> aggregates,
        CancellationToken cancellationToken)
    {
        if (aggregates.Count == 0)
            return [];

        long[] firstDisplayIds = aggregates.Select(row => row.FirstDisplayId).ToArray();
        var firstJobs = await context.Jobs.AsNoTracking()
            .Where(job => firstDisplayIds.Contains(job.DisplayId))
            .Select(job => new { job.WorkflowId, job.QueryText, job.Kind })
            .ToDictionaryAsync(job => job.WorkflowId, cancellationToken)
            .ConfigureAwait(false);

        Guid[] workflowIds = aggregates.Select(row => row.WorkflowId).ToArray();
        var firstItemIndexes = await context.Jobs.AsNoTracking()
            .Where(job => workflowIds.Contains(job.WorkflowId)
                && job.ItemName != null
                && job.ItemName != "")
            .GroupBy(job => job.WorkflowId)
            .Select(group => new
            {
                WorkflowId = group.Key,
                DisplayId = group.Min(job => job.DisplayId),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        long[] firstItemDisplayIds = firstItemIndexes.Select(row => row.DisplayId).ToArray();
        var firstItemNames = firstItemDisplayIds.Length == 0
            ? new Dictionary<Guid, string?>()
            : await context.Jobs.AsNoTracking()
                .Where(job => firstItemDisplayIds.Contains(job.DisplayId))
                .Select(job => new { job.WorkflowId, job.ItemName })
                .ToDictionaryAsync(job => job.WorkflowId, job => job.ItemName, cancellationToken)
                .ConfigureAwait(false);

        return aggregates.Select(aggregate =>
        {
            var first = firstJobs[aggregate.WorkflowId];
            firstItemNames.TryGetValue(aggregate.WorkflowId, out string? itemName);
            return new PersistedWorkflowSummary(
                aggregate.WorkflowId,
                aggregate.FirstDisplayId,
                itemName ?? first.QueryText ?? first.Kind,
                aggregate.ActiveJobCount > 0
                    ? "Active"
                    : aggregate.FailedJobCount > 0
                        ? "Failed"
                        : "Completed",
                aggregate.RootJobCount,
                aggregate.ActiveJobCount,
                aggregate.FailedJobCount,
                aggregate.CompletedJobCount);
        }).ToArray();
    }

    private static async Task<Dictionary<Guid, Entities.SearchJobEntity>> LoadSearchMetadataAsync(
        SockseekDbContext context,
        IReadOnlyList<Entities.JobEntity> jobs,
        CancellationToken cancellationToken)
    {
        Guid[] ids = jobs
            .Where(job => string.Equals(job.Kind, "Search", StringComparison.Ordinal))
            .Select(job => job.Id)
            .ToArray();
        if (ids.Length == 0)
            return [];
        return await context.SearchJobs.AsNoTracking()
            .Where(search => ids.Contains(search.JobId))
            .ToDictionaryAsync(search => search.JobId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task<Entities.SearchJobEntity?> LoadSearchMetadataAsync(
        SockseekDbContext context,
        Entities.JobEntity job,
        CancellationToken cancellationToken)
        => !string.Equals(job.Kind, "Search", StringComparison.Ordinal)
            ? Task.FromResult<Entities.SearchJobEntity?>(null)
            : context.SearchJobs.AsNoTracking()
                .SingleOrDefaultAsync(search => search.JobId == job.Id, cancellationToken);

    private static PersistedJob Map(
        Entities.JobEntity job,
        Entities.SearchJobEntity? search = null)
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
            job.PayloadJson,
            job.SubmissionId,
            job.SemanticRole,
            search?.ResultCount,
            search?.LockedFileCount,
            search?.ObservedPeerCount);

    private static DateTimeOffset? FromUnixMilliseconds(long? value)
        => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null;

    public static CursorValue? DecodeCursor(string? cursor)
    {
        if (cursor is null)
            return null;
        if (cursor.Length > 128)
            throw new ArgumentException("The job cursor is malformed.", nameof(cursor));
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

    public static string EncodeCursor(long displayId, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{displayId}:{id:N}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public sealed record CursorValue(long DisplayId, Guid Id);

    private sealed class WorkflowAggregate
    {
        public Guid WorkflowId { get; init; }
        public long FirstDisplayId { get; init; }
        public int RootJobCount { get; init; }
        public int ActiveJobCount { get; init; }
        public int FailedJobCount { get; init; }
        public int CompletedJobCount { get; init; }
    }
}

using Microsoft.EntityFrameworkCore;

namespace Sockseek.Persistence.Read;

public sealed record PersistenceRetentionOptions
{
    public TimeSpan? CompletedJobHistoryAge { get; init; } = TimeSpan.FromDays(90);
    public TimeSpan? UnsuccessfulJobHistoryAge { get; init; } = TimeSpan.FromDays(180);
    public int? MaximumRetainedJobs { get; init; } = 100_000;
    public TimeSpan? SearchResultAge { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan? TransferHistoryAge { get; init; } = TimeSpan.FromDays(90);
    public int BatchSize { get; init; } = 500;

    public void Validate()
    {
        ValidateAge(CompletedJobHistoryAge, nameof(CompletedJobHistoryAge));
        ValidateAge(UnsuccessfulJobHistoryAge, nameof(UnsuccessfulJobHistoryAge));
        if (MaximumRetainedJobs <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumRetainedJobs));
        ValidateAge(SearchResultAge, nameof(SearchResultAge));
        ValidateAge(TransferHistoryAge, nameof(TransferHistoryAge));
        if (BatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(BatchSize));
    }

    private static void ValidateAge(TimeSpan? age, string name)
    {
        if (age <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(name);
    }
}

public sealed record RetentionResult(
    int PrunedJobs,
    int PrunedSearchResults,
    int SearchesMarkedPruned,
    int PrunedTransfers,
    int PrunedTransferAttempts);

public sealed class RetentionService(
    IDbContextFactory<SockseekDbContext> contextFactory,
    PersistenceRetentionOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<RetentionResult> RunBatchAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        long? completedJobCutoff = Cutoff(now, options.CompletedJobHistoryAge);
        long? unsuccessfulJobCutoff = Cutoff(now, options.UnsuccessfulJobHistoryAge);
        long? searchCutoff = Cutoff(now, options.SearchResultAge);
        long? transferCutoff = Cutoff(now, options.TransferHistoryAge);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var agedJobs = context.Jobs.AsNoTracking().Where(job => job.LifecycleState == "Terminal");
        if (completedJobCutoff.HasValue || unsuccessfulJobCutoff.HasValue)
            agedJobs = agedJobs.Where(job =>
                completedJobCutoff.HasValue && job.TerminalOutcome == "Succeeded" && job.CompletedAtUtc < completedJobCutoff
                || unsuccessfulJobCutoff.HasValue && job.TerminalOutcome != "Succeeded" && job.CompletedAtUtc < unsuccessfulJobCutoff);
        else
            agedJobs = agedJobs.Where(_ => false);
        var agedJobIds = await agedJobs
            .OrderBy(job => job.CompletedAtUtc)
            .Select(job => job.Id)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int retainedCount = await context.Jobs.CountAsync(cancellationToken).ConfigureAwait(false);
        int excess = options.MaximumRetainedJobs.HasValue
            ? Math.Max(0, retainedCount - options.MaximumRetainedJobs.Value)
            : 0;
        if (agedJobIds.Count < options.BatchSize && excess > 0)
        {
            var countIds = await context.Jobs.AsNoTracking()
                .Where(job => job.LifecycleState == "Terminal" && !agedJobIds.Contains(job.Id))
                .OrderBy(job => job.CompletedAtUtc)
                .Select(job => job.Id)
                .Take(Math.Min(options.BatchSize - agedJobIds.Count, excess))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            agedJobIds.AddRange(countIds);
        }

        int prunedJobs = 0;
        if (agedJobIds.Count > 0)
            prunedJobs = await context.Jobs.Where(job => agedJobIds.Contains(job.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        var searchesQuery = context.SearchJobs
            .Where(search => search.ResultPersistenceState != "Pruned");
        searchesQuery = searchCutoff.HasValue
            ? searchesQuery.Where(search => context.Jobs.Any(job => job.Id == search.JobId && job.CompletedAtUtc < searchCutoff))
            : searchesQuery.Where(_ => false);
        var searchesToPrune = await searchesQuery
            .OrderBy(search => search.CompletedAtUtc)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        int prunedResults = 0;
        foreach (var search in searchesToPrune)
        {
            prunedResults += await context.SearchResults
                .Where(result => result.SearchJobId == search.JobId)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            search.ResultPersistenceState = "Pruned";
            search.ResultsPrunedAtUtc = now;
        }

        int prunedAttempts = 0;
        int prunedTransfers = 0;
        if (transferCutoff.HasValue)
        {
            var transferIds = await context.Transfers.AsNoTracking()
                .Where(transfer => transfer.TerminalOutcome != "None" && transfer.CompletedAtUtc < transferCutoff)
                .OrderBy(transfer => transfer.CompletedAtUtc)
                .Select(transfer => transfer.Id)
                .Take(options.BatchSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (transferIds.Count > 0)
            {
                prunedAttempts = await context.TransferAttempts
                    .Where(attempt => transferIds.Contains(attempt.TransferId))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                prunedTransfers = await context.Transfers
                    .Where(transfer => transferIds.Contains(transfer.Id))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RetentionResult(prunedJobs, prunedResults, searchesToPrune.Count, prunedTransfers, prunedAttempts);
    }

    private static long? Cutoff(long now, TimeSpan? age)
        => age.HasValue ? now - (long)age.Value.TotalMilliseconds : null;
}

using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Entities;

namespace Sockseek.Persistence.Runtime;

public sealed record PersistenceRuntimeInfo(Guid RuntimeId, DateTimeOffset StartedAtUtc, string Version);

public sealed record StartupReconciliationResult(
    PersistenceRuntimeInfo Runtime,
    int UnfinishedRuntimeCount,
    int InterruptedJobCount,
    int InterruptedTransferCount,
    int InterruptedAttemptCount,
    int InterruptedSearchCount);

public sealed class PersistenceRuntimeSession(
    IDbContextFactory<SockseekDbContext> contextFactory,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private PersistenceRuntimeInfo? current;

    public PersistenceRuntimeInfo? Current => Volatile.Read(ref current);

    public async Task<StartupReconciliationResult> StartAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        if (Current != null)
            throw new InvalidOperationException("The persistence runtime session has already started.");

        var startedAt = clock.GetUtcNow();
        long now = ToUnixMilliseconds(startedAt);
        var runtime = new PersistenceRuntimeInfo(Guid.NewGuid(), startedAt, version);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var unfinishedSessions = await context.RuntimeSessions
            .Where(session => session.StoppedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var unfinishedIds = unfinishedSessions.Select(session => session.Id).ToHashSet();

        context.RuntimeSessions.Add(new RuntimeSessionEntity
        {
            Id = runtime.RuntimeId,
            StartedAtUtc = now,
            Version = version,
        });

        foreach (var unfinished in unfinishedSessions)
        {
            unfinished.StoppedAtUtc = now;
            unfinished.ShutdownKind = "Unclean";
        }

        int interruptedJobs = 0;
        int interruptedTransfers = 0;
        int interruptedAttempts = 0;
        int interruptedSearches = 0;

        if (unfinishedIds.Count > 0)
        {
            var jobs = await context.Jobs
                .Where(job => unfinishedIds.Contains(job.LastRuntimeId) && job.LifecycleState != "Terminal")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var job in jobs)
            {
                job.LastRuntimeId = runtime.RuntimeId;
                job.LastSequence = 0;
                job.LifecycleState = "Terminal";
                job.ActivityPhase = "None";
                job.ActivityUntilUtc = null;
                job.TerminalOutcome = "Failed";
                job.FailureReason = "Interrupted";
                job.FailureMessage = "Interrupted by an unclean daemon shutdown.";
                job.UpdatedAtUtc = Math.Max(job.UpdatedAtUtc, now);
                job.CompletedAtUtc = now;
                job.Revision = checked(job.Revision + 1);
            }
            interruptedJobs = jobs.Count;

            var searches = await context.SearchJobs
                .Where(search => !search.IsComplete && jobs.Select(job => job.Id).Contains(search.JobId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var search in searches)
            {
                search.IsComplete = true;
                search.CompletedAtUtc = now;
                search.ResultPersistenceState = "Interrupted";
                search.Revision = checked(search.Revision + 1);
            }
            interruptedSearches = searches.Count;

            var transfers = await context.Transfers
                .Where(transfer => unfinishedIds.Contains(transfer.LastRuntimeId) && transfer.TerminalOutcome == "None")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var transfer in transfers)
            {
                transfer.LastRuntimeId = runtime.RuntimeId;
                transfer.LastSequence = 0;
                transfer.State = "Interrupted";
                transfer.TerminalOutcome = "Failed";
                transfer.FailureReason = "Interrupted";
                transfer.FailureMessage = "Interrupted by an unclean daemon shutdown.";
                transfer.CompletedAtUtc = now;
                transfer.Revision = checked(transfer.Revision + 1);
            }
            interruptedTransfers = transfers.Count;

            var attempts = await context.TransferAttempts
                .Where(attempt => unfinishedIds.Contains(attempt.LastRuntimeId) && attempt.CompletedAtUtc == null)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var attempt in attempts)
            {
                attempt.LastRuntimeId = runtime.RuntimeId;
                attempt.LastSequence = 0;
                attempt.State = "Interrupted";
                attempt.FailureReason = "Interrupted";
                attempt.FailureMessage = "Interrupted by an unclean daemon shutdown.";
                attempt.CompletedAtUtc = now;
                attempt.Revision = checked(attempt.Revision + 1);
            }
            interruptedAttempts = attempts.Count;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref current, runtime);

        return new StartupReconciliationResult(
            runtime,
            unfinishedSessions.Count,
            interruptedJobs,
            interruptedTransfers,
            interruptedAttempts,
            interruptedSearches);
    }

    public async Task StopAsync(string shutdownKind = "Clean", CancellationToken cancellationToken = default)
    {
        var runtime = Current ?? throw new InvalidOperationException("The persistence runtime session has not started.");
        long now = ToUnixMilliseconds(clock.GetUtcNow());

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.RuntimeSessions.SingleAsync(
            session => session.Id == runtime.RuntimeId,
            cancellationToken).ConfigureAwait(false);
        if (entity.StoppedAtUtc != null)
            throw new InvalidOperationException("The persistence runtime session is already stopped.");

        entity.StoppedAtUtc = now;
        entity.ShutdownKind = shutdownKind;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref current, null);
    }

    internal static long ToUnixMilliseconds(DateTimeOffset value)
        => value.ToUniversalTime().ToUnixTimeMilliseconds();
}

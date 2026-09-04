using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Entities;
using Sockseek.Core.Planning;
using Sockseek.Core.Models;
using System.Diagnostics;
using System.Text.Json;

namespace Sockseek.Persistence.Write;

public sealed class PersistenceWriter(
    IDbContextFactory<SockseekDbContext> contextFactory,
    PersistenceInbox inbox,
    PersistenceHealth health,
    PersistenceWriterOptions options,
    TimeProvider? timeProvider = null,
    IPersistenceMutationObserver? mutationObserver = null)
{
    private const int MaximumConsecutiveCommands = 16;
    internal const long AccountingBucketMilliseconds = 5 * 60 * 1000;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset nextProgressFlush = clock.GetUtcNow() + options.TransferProgressFlushInterval;
            int consecutiveRecoveryAttempts = 0;
            int consecutiveCommands = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                await inbox.WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
                if (consecutiveCommands < MaximumConsecutiveCommands
                    && inbox.TryDequeueCommand(out var command))
                {
                    await WriteCommandAsync(command!, cancellationToken).ConfigureAwait(false);
                    consecutiveCommands++;
                    if (IsDrained())
                        return;
                    continue;
                }

                consecutiveCommands = 0;
                DateTimeOffset now = clock.GetUtcNow();
                bool flushProgress = inbox.IsCompleted || now >= nextProgressFlush;
                var batch = inbox.DrainBatch(flushProgress);
                if (flushProgress)
                    nextProgressFlush = now + options.TransferProgressFlushInterval;
                if (batch.Count == 0)
                {
                    if (IsDrained())
                        return;
                    continue;
                }

                var (result, failure) = await TryWriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                if (result == WriteBatchResult.RecoverableFailure)
                {
                    consecutiveRecoveryAttempts++;
                    if (consecutiveRecoveryAttempts >= options.MaximumRecoveryAttempts)
                    {
                        var exhausted = new InvalidOperationException(
                            $"Persistence recovery exhausted {options.MaximumRecoveryAttempts} attempts; dropping {batch.Count} retained mutations.",
                            failure);
                        health.RecordOperationalFailure(clock.GetUtcNow(), exhausted);
                        health.RecordPermanentlyFailedMutations(batch.Count);
                        mutationObserver?.PermanentlyFailed(batch, exhausted);
                        if (IsDrained())
                            return;
                        continue;
                    }
                    inbox.RequeueAfterFailure(batch);
                    await Task.Delay(options.FailureRetryDelay, clock, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (result == WriteBatchResult.PermanentFailure)
                {
                    consecutiveRecoveryAttempts = 0;
                    health.RecordPermanentlyFailedMutations(batch.Count);
                    mutationObserver?.PermanentlyFailed(
                        batch,
                        failure ?? new InvalidOperationException("Persistence mutation batch failed permanently."));
                    if (IsDrained())
                        return;
                    continue;
                }

                consecutiveRecoveryAttempts = 0;

                if (IsDrained())
                    return;
            }
        }
        finally
        {
            inbox.FailPendingCommands(new OperationCanceledException("Persistence writer stopped."));
        }
    }

    private bool IsDrained()
        => inbox.IsCompleted
            && inbox.CriticalDepth == 0
            && inbox.OrdinaryDepth == 0
            && inbox.ProgressCount == 0
            && inbox.DegradedCount == 0
            && inbox.SearchDepth == 0;

    private async Task WriteCommandAsync(
        AwaitablePersistenceCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            Exception? lastFailure = null;
            for (int recoveryAttempt = 0; recoveryAttempt < options.MaximumRecoveryAttempts; recoveryAttempt++)
            {
                var (result, exception) = await TryWriteCommandAsync(command, cancellationToken).ConfigureAwait(false);
                if (result == WriteBatchResult.Success)
                    return;
                lastFailure = exception;
                if (result == WriteBatchResult.PermanentFailure)
                    break;
                await Task.Delay(options.FailureRetryDelay, clock, cancellationToken).ConfigureAwait(false);
            }

            var failure = lastFailure ?? new InvalidOperationException("Persistence command failed.");
            health.RecordPermanentlyFailedMutations(1);
            command.Fail(failure);
        }
        catch (Exception ex)
        {
            // The command has already left the inbox, so the writer's final
            // pending-command sweep cannot see it.
            command.Fail(ex);
            throw;
        }
    }

    private async Task<(WriteBatchResult Result, Exception? Exception)> TryWriteCommandAsync(
        AwaitablePersistenceCommand command,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await command.ApplyAsync(context, cancellationToken).ConfigureAwait(false);
                int rows = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                health.RecordCommit(clock.GetUtcNow(), rows, stopwatch.Elapsed, 1,
                    reconciliationComplete: inbox.DegradedCount == 0);
                command.Complete();
                return (WriteBatchResult.Success, null);
            }
            catch (Exception ex) when (IsBusy(ex) && attempt < options.BusyRetryCount)
            {
                health.RecordBusyRetry();
                health.RecordFailure(clock.GetUtcNow(), ex, transient: true);
                await Task.Delay(options.BusyRetryDelay, clock, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                bool recoverable = IsRecoverable(ex);
                health.RecordFailure(clock.GetUtcNow(), ex, transient: recoverable);
                return (recoverable ? WriteBatchResult.RecoverableFailure : WriteBatchResult.PermanentFailure, ex);
            }
        }
    }

    private async Task<(WriteBatchResult Result, Exception? Exception)> TryWriteBatchAsync(
        IReadOnlyList<PersistenceMutation> batch,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                IReadOnlyList<PersistenceMutation> normalized = NormalizeBatch(batch);
                int rows = 0;
                foreach (var mutation in normalized
                    .OrderBy(DependencyOrder)
                    .ThenBy(item => item.Sequence))
                {
                    rows += await ApplyAsync(context, mutation, cancellationToken).ConfigureAwait(false);
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                health.RecordCommit(
                    clock.GetUtcNow(), rows, stopwatch.Elapsed, batch.Count,
                    reconciliationComplete: inbox.DegradedCount == 0);
                mutationObserver?.Committed(normalized);
                return (WriteBatchResult.Success, null);
            }
            catch (Exception ex) when (IsBusy(ex) && attempt < options.BusyRetryCount)
            {
                health.RecordBusyRetry();
                health.RecordFailure(clock.GetUtcNow(), WithBatchContext(ex, batch), transient: true);
                await Task.Delay(options.BusyRetryDelay, clock, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                bool recoverable = IsRecoverable(ex);
                health.RecordFailure(clock.GetUtcNow(), WithBatchContext(ex, batch), transient: recoverable);
                return (
                    recoverable ? WriteBatchResult.RecoverableFailure : WriteBatchResult.PermanentFailure,
                    ex);
            }
        }
    }

    private async Task<int> ApplyAsync(
        SockseekDbContext context,
        PersistenceMutation mutation,
        CancellationToken cancellationToken)
        => mutation switch
        {
            JobPersistenceMutation job => await ApplyJobAsync(context, job, cancellationToken).ConfigureAwait(false),
            TransferPersistenceMutation transfer => await ApplyTransferAsync(context, transfer, cancellationToken).ConfigureAwait(false),
            TransferAttemptPersistenceMutation attempt => await ApplyAttemptAsync(context, attempt, cancellationToken).ConfigureAwait(false),
            SearchResultsPersistenceMutation results => await ApplySearchResultsAsync(context, results, cancellationToken).ConfigureAwait(false),
            SearchCompletionPersistenceMutation completion => await ApplySearchCompletionAsync(context, completion, cancellationToken).ConfigureAwait(false),
            SearchIncompletePersistenceMutation incomplete => await ApplySearchIncompleteAsync(context, incomplete, cancellationToken).ConfigureAwait(false),
            SearchTerminalPersistenceMutation terminalSearch => await ApplySearchTerminalAsync(context, terminalSearch, cancellationToken).ConfigureAwait(false),
            TransferTerminalPersistenceMutation terminal => await ApplyTransferTerminalAsync(context, terminal, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported persistence mutation type {mutation.GetType().FullName}."),
        };

    private async Task<int> ApplyJobAsync(SockseekDbContext context, JobPersistenceMutation mutation, CancellationToken cancellationToken)
    {
        var entity = await context.Jobs.FindAsync([mutation.JobId], cancellationToken).ConfigureAwait(false);
        if (entity != null && (entity.Revision >= mutation.Revision || entity.LifecycleState == "Terminal" && mutation.LifecycleState != "Terminal"))
            return 0;

        long occurredAt = mutation.OccurredAtUnixMilliseconds;
        if (mutation.SubmissionId is Guid submissionId
            && !string.IsNullOrWhiteSpace(mutation.SubmissionSpecificationJson))
        {
            var submission = await context.Submissions.FindAsync(
                [submissionId],
                cancellationToken).ConfigureAwait(false);
            if (submission == null)
            {
                submission = new SubmissionEntity
                {
                    Id = submissionId,
                    SubmittedAtUtc = (mutation.RegisteredAtUtc ?? mutation.OccurredAtUtc)
                        .ToUniversalTime().ToUnixTimeMilliseconds(),
                    SpecificationSchemaVersion = SubmissionSpecification.CurrentSchemaVersion,
                    SpecificationJson = mutation.SubmissionSpecificationJson,
                    RerunOfSubmissionId = mutation.RerunOfSubmissionId,
                    PreviewId = mutation.PreviewId,
                    ArtifactId = mutation.ArtifactId,
                    Revision = 1,
                };
                context.Submissions.Add(submission);
            }
        }
        if (entity == null)
        {
            entity = new JobEntity
            {
                Id = mutation.JobId,
                CreatedAtUtc = (mutation.RegisteredAtUtc ?? mutation.OccurredAtUtc)
                    .ToUniversalTime().ToUnixTimeMilliseconds(),
                StartedAtUtc = mutation.LifecycleState == "Pending" ? null : occurredAt,
            };
            context.Jobs.Add(entity);
        }

        entity.WorkflowId = mutation.WorkflowId;
        entity.SubmissionId = mutation.SubmissionId;
        entity.SemanticRole = mutation.SemanticRole;
        entity.ParentJobId = mutation.ParentJobId;
        entity.SourceJobId = mutation.SourceJobId;
        entity.ResultJobId = mutation.ResultJobId;
        entity.LastRuntimeId = mutation.RuntimeId;
        entity.LastSequence = mutation.Sequence;
        entity.DisplayId = mutation.DisplayId;
        entity.Kind = mutation.Kind;
        entity.LifecycleState = mutation.LifecycleState;
        entity.ActivityPhase = mutation.ActivityPhase;
        entity.ActivityUntilUtc = mutation.ActivityUntilUtc?.ToUniversalTime().ToUnixTimeMilliseconds();
        entity.TerminalOutcome = mutation.TerminalOutcome;
        entity.SkipReason = mutation.SkipReason;
        entity.CancellationSource = mutation.CancellationSource;
        entity.FailureReason = mutation.FailureReason;
        entity.FailureMessage = Limit(mutation.FailureMessage);
        entity.FailureDetail = Limit(mutation.FailureDetail);
        entity.ItemName = mutation.ItemName;
        entity.QueryText = mutation.QueryText;
        entity.UpdatedAtUtc = Math.Max(entity.UpdatedAtUtc, occurredAt);
        if (entity.StartedAtUtc == null && mutation.LifecycleState != "Pending")
            entity.StartedAtUtc = occurredAt;
        if (mutation.LifecycleState == "Terminal" && entity.CompletedAtUtc == null)
            entity.CompletedAtUtc = occurredAt;
        entity.Revision = mutation.Revision;
        entity.PayloadSchemaVersion = mutation.PayloadSchemaVersion;
        entity.PayloadJson = mutation.PayloadJson;

        if (mutation.Kind == "Search" && await context.SearchJobs.FindAsync([mutation.JobId], cancellationToken).ConfigureAwait(false) == null)
        {
            context.SearchJobs.Add(new SearchJobEntity
            {
                JobId = mutation.JobId,
                Query = mutation.QueryText ?? "",
                Revision = 0,
                ResultPersistenceState = "NotPersisted",
            });
        }
        return 1;
    }

    private async Task<int> ApplyTransferAsync(SockseekDbContext context, TransferPersistenceMutation mutation, CancellationToken cancellationToken)
    {
        int accountingRows = await ApplyAccountingAsync(
            context,
            mutation.TransferId,
            mutation.Direction,
            mutation.Username,
            mutation.AccountingObservations,
            cancellationToken).ConfigureAwait(false);
        var entity = await context.Transfers.FindAsync([mutation.TransferId], cancellationToken).ConfigureAwait(false);
        if (entity != null && (entity.Revision >= mutation.Revision || entity.TerminalOutcome != "None" && mutation.TerminalOutcome == "None"))
            return accountingRows;

        long occurredAt = mutation.OccurredAtUnixMilliseconds;
        if (entity == null)
        {
            entity = new TransferEntity
            {
                Id = mutation.TransferId,
                CreatedAtUtc = mutation.RequestedAtUtc?.ToUniversalTime().ToUnixTimeMilliseconds()
                    ?? occurredAt,
            };
            context.Transfers.Add(entity);
        }

        entity.JobId = mutation.JobId;
        entity.WorkflowId = mutation.WorkflowId;
        entity.LastRuntimeId = mutation.RuntimeId;
        entity.LastSequence = mutation.Sequence;
        entity.Direction = mutation.Direction;
        entity.Source = mutation.Source;
        entity.Username = mutation.Username;
        entity.RemotePath = mutation.RemotePath;
        entity.LocalPath = mutation.LocalPath;
        entity.State = mutation.State;
        entity.TerminalOutcome = mutation.TerminalOutcome;
        // The v1 schema constrains total_bytes to nonnegative values. Preserve
        // unknown (-1 in the live protocol) without conflating it with zero.
        entity.TotalBytes = mutation.TotalBytes < 0 ? long.MaxValue : mutation.TotalBytes;
        entity.TransferredBytes = Math.Max(0, mutation.TransferredBytes);
        entity.AttemptCount = mutation.AttemptCount;
        // Admission alone is not an attempt. A queued transfer may terminalize
        // without ever starting (for example, user or daemon cancellation), in
        // which case started_at must remain null.
        if (entity.StartedAtUtc == null && mutation.StartedAtUtc is { } startedAt)
        {
            entity.StartedAtUtc = startedAt.ToUniversalTime().ToUnixTimeMilliseconds();
        }
        else if (entity.StartedAtUtc == null && mutation.AttemptCount > 0)
        {
            entity.StartedAtUtc = occurredAt;
        }
        if (mutation.LastProgressAtUtc is { } progressAt)
        {
            entity.LastProgressAtUtc = Math.Max(
                entity.LastProgressAtUtc ?? 0,
                progressAt.ToUniversalTime().ToUnixTimeMilliseconds());
        }
        else if (mutation.Priority == PersistenceMutationPriority.Progress)
        {
            entity.LastProgressAtUtc = Math.Max(entity.LastProgressAtUtc ?? 0, occurredAt);
        }
        if (mutation.BytesPerSecond.HasValue)
            entity.BytesPerSecond = Math.Max(0, mutation.BytesPerSecond.Value);
        if (mutation.TerminalOutcome != "None")
            entity.CompletedAtUtc ??= occurredAt;
        entity.FailureReason = mutation.FailureReason;
        entity.FailureMessage = Limit(mutation.FailureMessage);
        entity.CancellationSource = mutation.CancellationSource;
        if (mutation.File is { } file)
        {
            entity.FileName = Limit(file.Name, 1024);
            entity.FileSizeBytes = Math.Max(0, file.Size);
            entity.FileExtension = Limit(file.Extension, 32);
            entity.FileBitRate = file.BitRate;
            entity.FileBitDepth = file.BitDepth;
            entity.FileSampleRate = file.SampleRate;
            entity.FileLength = file.Length;
            entity.FileAttributesJson = file.Attributes is null
                ? null
                : JsonSerializer.Serialize(file.Attributes);
        }
        if (mutation.GroupRef is not null)
            entity.GroupRef = Limit(mutation.GroupRef, 4096);
        if (mutation.GroupDisplayPath is not null)
            entity.GroupDisplayPath = Limit(mutation.GroupDisplayPath, 4096);
        entity.Revision = mutation.Revision;
        return accountingRows + 1;
    }

    private async Task<int> ApplyAttemptAsync(SockseekDbContext context, TransferAttemptPersistenceMutation mutation, CancellationToken cancellationToken)
    {
        int accountingRows = await ApplyAccountingAsync(
            context,
            mutation.TransferId,
            mutation.Direction,
            mutation.SourceUsername,
            mutation.AccountingObservations,
            cancellationToken).ConfigureAwait(false);
        var entity = await context.TransferAttempts.FindAsync([mutation.AttemptId], cancellationToken).ConfigureAwait(false);
        if (entity != null && entity.Revision >= mutation.Revision)
            return accountingRows;

        long occurredAt = mutation.OccurredAtUnixMilliseconds;
        if (entity == null)
        {
            entity = new TransferAttemptEntity
            {
                Id = mutation.AttemptId,
                TransferId = mutation.TransferId,
                StartedAtUtc = occurredAt,
            };
            context.TransferAttempts.Add(entity);
        }

        entity.LastRuntimeId = mutation.RuntimeId;
        entity.LastSequence = mutation.Sequence;
        entity.AttemptNumber = mutation.AttemptNumber;
        entity.Source = mutation.Source;
        entity.State = mutation.State;
        entity.SourceUsername = mutation.SourceUsername;
        entity.SourcePath = mutation.SourcePath;
        entity.OutputPath = mutation.OutputPath;
        if (mutation.State is "Completed" or "Failed" or "Cancelled" or "Interrupted")
            entity.CompletedAtUtc ??= occurredAt;
        entity.FailureReason = mutation.FailureReason;
        entity.FailureMessage = Limit(mutation.FailureMessage);
        entity.Revision = mutation.Revision;
        return accountingRows + 1;
    }

    private static async Task<int> ApplyAccountingAsync(
        SockseekDbContext context,
        Guid transferId,
        string direction,
        string? username,
        IReadOnlyList<TransferAccountingObservation>? observations,
        CancellationToken cancellationToken)
    {
        if (observations is not { Count: > 0 })
            return 0;

        int changed = 0;
        string peer = username ?? "";
        foreach (TransferAccountingObservation observation in observations
            .OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item.Revision))
        {
            var checkpoint = await context.TransferAccountingCheckpoints.FindAsync(
                [observation.AttemptId],
                cancellationToken).ConfigureAwait(false);
            if (checkpoint is not null && checkpoint.Revision >= observation.Revision)
                continue;

            long cumulative = Math.Max(0, observation.CumulativeBytes);
            long delta = checkpoint is null
                ? cumulative
                : cumulative >= checkpoint.CumulativeBytes
                    ? cumulative - checkpoint.CumulativeBytes
                    : cumulative;
            long observedAt = observation.OccurredAtUtc.ToUniversalTime().ToUnixTimeMilliseconds();
            if (checkpoint is null)
            {
                checkpoint = new TransferAccountingCheckpointEntity
                {
                    AttemptId = observation.AttemptId,
                    TransferId = transferId,
                };
                context.TransferAccountingCheckpoints.Add(checkpoint);
                changed++;
            }
            checkpoint.Revision = observation.Revision;
            checkpoint.CumulativeBytes = cumulative;
            checkpoint.LastObservedAtUtc = observedAt;

            if (delta <= 0)
                continue;
            Math.DivRem(observedAt, AccountingBucketMilliseconds, out long remainder);
            long bucketStart = observedAt - remainder;
            var bucket = await context.TransferByteBuckets.FindAsync(
                [bucketStart, direction, peer],
                cancellationToken).ConfigureAwait(false);
            if (bucket is null)
            {
                context.TransferByteBuckets.Add(new TransferByteBucketEntity
                {
                    BucketStartUtc = bucketStart,
                    Direction = direction,
                    Username = peer,
                    Bytes = delta,
                });
                changed++;
            }
            else
            {
                bucket.Bytes = checked(bucket.Bytes + delta);
                changed++;
            }
        }

        var state = await context.TransferAccountingStates.FindAsync(
            [1],
            cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            long first = observations.Min(item => item.OccurredAtUtc.ToUniversalTime().ToUnixTimeMilliseconds());
            context.TransferAccountingStates.Add(new TransferAccountingStateEntity
            {
                Id = 1,
                CompleteFromUtc = first,
                UpdatedAtUtc = observations.Max(item => item.OccurredAtUtc.ToUniversalTime().ToUnixTimeMilliseconds()),
            });
            changed++;
        }
        else
        {
            state.UpdatedAtUtc = Math.Max(
                state.UpdatedAtUtc,
                observations.Max(item => item.OccurredAtUtc.ToUniversalTime().ToUnixTimeMilliseconds()));
        }
        return changed;
    }

    private async Task<int> ApplySearchResultsAsync(SockseekDbContext context, SearchResultsPersistenceMutation mutation, CancellationToken cancellationToken)
    {
        var search = await GetOrCreateSearchAsync(context, mutation.SearchJobId, cancellationToken).ConfigureAwait(false);
        if (search.ResultPersistenceState is "Pruned" or "Interrupted")
            return 0;

        var sequences = mutation.Results.Select(result => result.Sequence).Distinct().ToArray();
        var usernames = mutation.Results.Select(result => result.Username).Distinct().ToArray();
        var remoteFilenames = mutation.Results.Select(result => result.RemoteFilename).Distinct().ToArray();
        var visibilities = mutation.Results.Select(result => result.Visibility).Distinct().ToArray();
        var existingRows = await context.SearchResults
            .Where(row => row.SearchJobId == mutation.SearchJobId
                && (sequences.Contains(row.Sequence)
                    || usernames.Contains(row.Username)
                        && remoteFilenames.Contains(row.RemoteFilename)
                        && visibilities.Contains(row.Visibility)))
            .Select(row => new { row.Sequence, row.Username, row.RemoteFilename, row.Visibility })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var seenSequences = existingRows.Select(row => row.Sequence)
            .Concat(context.SearchResults.Local
                .Where(row => row.SearchJobId == mutation.SearchJobId)
                .Select(row => row.Sequence))
            .ToHashSet();
        var seenCandidates = existingRows
            .Select(row => (row.Username, row.RemoteFilename, row.Visibility))
            .Concat(context.SearchResults.Local
                .Where(row => row.SearchJobId == mutation.SearchJobId)
                .Select(row => (row.Username, row.RemoteFilename, row.Visibility)))
            .ToHashSet();

        int added = 0;
        int publicAdded = 0;
        int lockedAdded = 0;
        foreach (var result in mutation.Results)
        {
            if (!seenSequences.Add(result.Sequence)
                || !seenCandidates.Add((result.Username, result.RemoteFilename, result.Visibility)))
                continue;

            context.SearchResults.Add(new SearchResultEntity
            {
                Id = result.Id,
                SearchJobId = mutation.SearchJobId,
                Sequence = result.Sequence,
                Revision = result.Revision,
                Username = result.Username,
                RemoteFilename = result.RemoteFilename,
                SizeBytes = result.SizeBytes,
                BitRate = result.BitRate,
                BitDepth = result.BitDepth,
                ResponseFileCount = result.ResponseFileCount,
                SampleRate = result.SampleRate,
                DurationSeconds = result.DurationSeconds,
                Extension = result.Extension,
                UploadSpeed = result.UploadSpeed,
                HasFreeUploadSlot = result.HasFreeUploadSlot,
                QueueLength = result.QueueLength,
                Visibility = result.Visibility,
                AttributesJson = result.AttributesJson,
                ObservedAtUtc = result.ObservedAtUtc.ToUniversalTime().ToUnixTimeMilliseconds(),
            });
            added++;
            if (result.Visibility == SearchResultVisibility.Locked.ToString())
                lockedAdded++;
            else
                publicAdded++;
        }
        search.ResultCount = checked(search.ResultCount + publicAdded);
        search.LockedFileCount = checked(search.LockedFileCount + lockedAdded);
        search.Revision = Math.Max(search.Revision, mutation.Revision);
        if (search.ResultPersistenceState == "NotPersisted")
            search.ResultPersistenceState = "Incomplete";
        return added;
    }

    private async Task<int> ApplySearchCompletionAsync(SockseekDbContext context, SearchCompletionPersistenceMutation mutation, CancellationToken cancellationToken)
    {
        var search = await GetOrCreateSearchAsync(context, mutation.SearchJobId, cancellationToken).ConfigureAwait(false);
        if (search.Revision > mutation.Revision || search.ResultPersistenceState is "Pruned" or "Interrupted")
            return 0;
        if (search.IsComplete
            && search.ResultPersistenceState == "Incomplete"
            && mutation.ResultPersistenceState == "Complete")
            return 0;

        search.Query = mutation.Query;
        search.Revision = mutation.Revision;
        search.ResultCount = mutation.ResultCount;
        search.LockedFileCount = mutation.LockedFileCount;
        search.ObservedPeerCount = mutation.ObservedPeerCount;
        search.IsComplete = true;
        search.CompletedAtUtc ??= mutation.OccurredAtUnixMilliseconds;
        search.ResultPersistenceState = mutation.ResultPersistenceState;
        return 1;
    }

    private async Task<int> ApplySearchIncompleteAsync(SockseekDbContext context, SearchIncompletePersistenceMutation mutation, CancellationToken cancellationToken)
    {
        var search = await context.SearchJobs.FindAsync([mutation.SearchJobId], cancellationToken).ConfigureAwait(false);
        if (search == null || search.ResultPersistenceState is "Pruned" or "Interrupted")
            return 0;
        search.ResultPersistenceState = "Incomplete";
        search.Revision = Math.Max(search.Revision, mutation.Revision);
        return 1;
    }

    private async Task<int> ApplyTransferTerminalAsync(SockseekDbContext context, TransferTerminalPersistenceMutation mutation, CancellationToken cancellationToken)
    {
        int rows = 0;
        if (mutation.OwningJob != null)
            rows += await ApplyJobAsync(context, mutation.OwningJob, cancellationToken).ConfigureAwait(false);
        rows += await ApplyTransferAsync(context, mutation.Transfer, cancellationToken).ConfigureAwait(false);
        if (mutation.FinalAttempt != null)
            rows += await ApplyAttemptAsync(context, mutation.FinalAttempt, cancellationToken).ConfigureAwait(false);
        return rows;
    }

    private async Task<int> ApplySearchTerminalAsync(SockseekDbContext context, SearchTerminalPersistenceMutation mutation, CancellationToken cancellationToken)
    {
        int rows = 0;
        if (mutation.PendingResultBatches.Count > 0)
        {
            SearchResultsPersistenceMutation[] ordered = mutation.PendingResultBatches
                .OrderBy(batch => batch.Sequence)
                .ToArray();
            SearchResultsPersistenceMutation last = ordered[^1];
            var combined = last with
            {
                Revision = ordered.Max(batch => batch.Revision),
                Results = ordered.SelectMany(batch => batch.Results).ToArray(),
            };
            rows += await ApplySearchResultsAsync(context, combined, cancellationToken).ConfigureAwait(false);
        }
        rows += await ApplySearchCompletionAsync(context, mutation.Completion, cancellationToken).ConfigureAwait(false);
        return rows;
    }

    private static async Task<SearchJobEntity> GetOrCreateSearchAsync(
        SockseekDbContext context,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var search = await context.SearchJobs.FindAsync([jobId], cancellationToken).ConfigureAwait(false);
        if (search != null)
            return search;
        bool jobExists = context.Jobs.Local.Any(job => job.Id == jobId)
            || await context.Jobs.AnyAsync(job => job.Id == jobId, cancellationToken).ConfigureAwait(false);
        if (!jobExists)
            throw new InvalidOperationException($"Job {jobId} was not persisted before its search data.");
        search = new SearchJobEntity
        {
            JobId = jobId,
            Query = "",
            ResultPersistenceState = "NotPersisted",
        };
        context.SearchJobs.Add(search);
        return search;
    }

    private string? Limit(string? value)
        => value == null || value.Length <= options.MaximumFailureTextLength
            ? value
            : value[..options.MaximumFailureTextLength];

    private static string? Limit(string? value, int maximumLength)
        => value == null || value.Length <= maximumLength
            ? value
            : value[..maximumLength];

    private static bool IsBusy(Exception exception)
        => exception is SqliteException { SqliteErrorCode: 5 or 6 }
            || exception.InnerException != null && IsBusy(exception.InnerException);

    private static Exception WithBatchContext(Exception exception, IReadOnlyList<PersistenceMutation> batch)
    {
        string items = string.Join(", ", batch.Take(3).Select(mutation =>
            $"{mutation.GetType().Name}:{mutation.EntityId:N}@r{mutation.Revision}"));
        if (batch.Count > 3)
            items += $", +{batch.Count - 3} more";
        return new InvalidOperationException($"Persistence batch [{items}] failed: {exception.Message}", exception);
    }

    private static bool IsRecoverable(Exception exception)
    {
        if (exception is DbUpdateException { InnerException: not null } update)
            return IsRecoverable(update.InnerException!);
        if (exception is SqliteException sqlite)
            return sqlite.SqliteErrorCode != 19;
        if (exception is IOException or UnauthorizedAccessException)
            return true;
        return exception.InnerException != null && IsRecoverable(exception.InnerException);
    }

    private static IReadOnlyList<PersistenceMutation> NormalizeBatch(IReadOnlyList<PersistenceMutation> batch)
    {
        var coalesced = new Dictionary<string, PersistenceMutation>();
        var appendOnly = new List<PersistenceMutation>();
        foreach (var mutation in batch)
        {
            string? key = mutation switch
            {
                JobPersistenceMutation job => $"job:{job.JobId}",
                TransferPersistenceMutation transfer => $"transfer:{transfer.TransferId}",
                TransferTerminalPersistenceMutation terminal => $"transfer:{terminal.Transfer.TransferId}",
                TransferAttemptPersistenceMutation attempt => $"attempt:{attempt.AttemptId}",
                _ => null,
            };
            if (key == null)
            {
                appendOnly.Add(mutation);
                continue;
            }

            if (!coalesced.TryGetValue(key, out var current))
            {
                coalesced[key] = mutation;
                continue;
            }

            if (key.StartsWith("transfer:", StringComparison.Ordinal))
            {
                coalesced[key] = MergeTransferMutations(current, mutation);
                continue;
            }

            if (mutation.Revision > current.Revision
                || mutation.Revision == current.Revision && mutation.Priority > current.Priority)
                coalesced[key] = mutation;
        }

        appendOnly.AddRange(coalesced.Values);
        return appendOnly;
    }

    private static PersistenceMutation MergeTransferMutations(
        PersistenceMutation first,
        PersistenceMutation second)
    {
        TransferPersistenceMutation firstTransfer = first switch
        {
            TransferPersistenceMutation transfer => transfer,
            TransferTerminalPersistenceMutation terminal => terminal.Transfer,
            _ => throw new InvalidOperationException("Expected a transfer mutation."),
        };
        TransferPersistenceMutation secondTransfer = second switch
        {
            TransferPersistenceMutation transfer => transfer,
            TransferTerminalPersistenceMutation terminal => terminal.Transfer,
            _ => throw new InvalidOperationException("Expected a transfer mutation."),
        };
        bool secondWins = secondTransfer.Revision > firstTransfer.Revision
            || secondTransfer.Revision == firstTransfer.Revision
                && secondTransfer.Priority >= firstTransfer.Priority;
        TransferPersistenceMutation merged = secondWins
            ? PersistenceInbox.MergeTransfer(firstTransfer, secondTransfer)
            : PersistenceInbox.MergeTransfer(secondTransfer, firstTransfer);
        TransferTerminalPersistenceMutation? terminalMutation = second as TransferTerminalPersistenceMutation
            ?? first as TransferTerminalPersistenceMutation;
        return terminalMutation is null
            ? merged
            : terminalMutation with { Transfer = merged };
    }

    private static int DependencyOrder(PersistenceMutation mutation)
        => mutation switch
        {
            JobPersistenceMutation => 0,
            TransferTerminalPersistenceMutation => 1,
            TransferPersistenceMutation => 1,
            TransferAttemptPersistenceMutation => 2,
            SearchResultsPersistenceMutation => 3,
            SearchIncompletePersistenceMutation => 4,
            SearchTerminalPersistenceMutation => 5,
            SearchCompletionPersistenceMutation => 5,
            _ => 6,
        };

    private enum WriteBatchResult
    {
        Success,
        RecoverableFailure,
        PermanentFailure,
    }
}

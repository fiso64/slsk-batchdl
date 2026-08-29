using System.Threading.Channels;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence;
using Sockseek.Persistence.Runtime;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class PersistenceWriterTests
{
    [TestMethod]
    [TestCategory("Load")]
    public async Task OneHundredThousandProgressCallbacks_ProduceOneLatestValueWrite()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("progress-load-test")).Runtime;
        var options = new PersistenceWriterOptions { TransferProgressFlushInterval = TimeSpan.FromSeconds(3) };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid transferId = Guid.NewGuid();

        for (int revision = 1; revision <= 100_000; revision++)
            Assert.IsTrue(inbox.TryEnqueue(Transfer(runtime.RuntimeId, transferId, revision)));

        Assert.AreEqual(1, inbox.ProgressCount);
        inbox.Complete();
        await new PersistenceWriter(database.Factory, inbox, health, options)
            .RunAsync(CancellationToken.None);

        await using var verify = await database.Factory.CreateDbContextAsync();
        var transfer = await verify.Transfers.SingleAsync(item => item.Id == transferId);
        Assert.AreEqual(100_000L, transfer.Revision);
        Assert.AreEqual(100_000L, transfer.TransferredBytes);
        Assert.AreEqual(1L, health.Snapshot(inbox).SuccessfulCommitCount);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public void TransferTerminalAdmission_RemovesOlderBufferedAndDegradedProgress()
    {
        var options = new PersistenceWriterOptions
        {
            CriticalQueueCapacity = 1,
            DegradedProjectionCapacity = 2,
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid runtimeId = Guid.NewGuid();
        Guid transferId = Guid.NewGuid();
        Assert.IsTrue(inbox.TryEnqueue(Transfer(runtimeId, transferId, revision: 1)));

        var terminalTransfer = Transfer(runtimeId, transferId, revision: 2) with
        {
            Priority = PersistenceMutationPriority.Terminal,
            State = "Completed",
            TerminalOutcome = "Succeeded",
            TransferredBytes = 100,
        };
        Assert.IsTrue(inbox.TryEnqueue(new TransferTerminalPersistenceMutation(terminalTransfer, null, null)));

        Assert.AreEqual(0, inbox.ProgressCount);
        var batch = inbox.DrainBatch(includeProgress: false);
        Assert.AreEqual(1, batch.Count);
        Assert.IsInstanceOfType<TransferTerminalPersistenceMutation>(batch[0]);
    }

    [TestMethod]
    public async Task UnknownTransferTotal_RoundTripsAsExplicitNullHistoryValue()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("unknown-total-test")).Runtime;
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid transferId = Guid.NewGuid();
        var mutation = Transfer(runtime.RuntimeId, transferId, revision: 1) with
        {
            TotalBytes = -1,
            TransferredBytes = 42,
        };
        Assert.IsTrue(inbox.TryEnqueue(mutation));
        inbox.Complete();
        await new PersistenceWriter(database.Factory, inbox, health, options).RunAsync(CancellationToken.None);

        var detail = await new TransferHistoryReader(database.Factory).GetTransferAsync(transferId);
        Assert.IsNotNull(detail);
        Assert.IsNull(detail.Transfer.TotalBytes);
        Assert.AreEqual(42L, detail.Transfer.TransferredBytes);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task CompositeTransferTerminal_RollsBackCompletely_ThenRetriesAndReplaysIdempotently()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("composite-terminal-test")).Runtime;
        Guid existingJobId = Guid.NewGuid();
        Guid owningJobId = Guid.NewGuid();
        Guid transferId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();

        await RunCompletedWriterAsync(database, [Job(runtime.RuntimeId, existingJobId, 1,
            PersistenceMutationPriority.Structural) with { DisplayId = 700 }]);

        var terminalTransfer = Transfer(runtime.RuntimeId, transferId, 2) with
        {
            Priority = PersistenceMutationPriority.Terminal,
            JobId = owningJobId,
            State = "Completed",
            TerminalOutcome = "Succeeded",
            TotalBytes = 100,
            TransferredBytes = 100,
        };
        var finalAttempt = new TransferAttemptPersistenceMutation(
            runtime.RuntimeId, 3, DateTimeOffset.UtcNow, attemptId, 2,
            PersistenceMutationPriority.Terminal, transferId, 1, "SoulseekPeer", "Completed",
            "user", "remote", "local", "None", null);
        var conflictingJob = Job(runtime.RuntimeId, owningJobId, 2,
            PersistenceMutationPriority.Terminal, terminal: true) with { DisplayId = 700 };
        var badComposite = new TransferTerminalPersistenceMutation(terminalTransfer, finalAttempt, conflictingJob);

        var failedHealth = await RunCompletedWriterAsync(database, [badComposite]);
        Assert.AreEqual(1L, failedHealth.PermanentlyFailedMutationCount);
        await using (var verifyRollback = await database.Factory.CreateDbContextAsync())
        {
            Assert.IsFalse(await verifyRollback.Jobs.AnyAsync(job => job.Id == owningJobId));
            Assert.IsFalse(await verifyRollback.Transfers.AnyAsync(transfer => transfer.Id == transferId));
            Assert.IsFalse(await verifyRollback.TransferAttempts.AnyAsync(attempt => attempt.Id == attemptId));
        }

        var validComposite = badComposite with
        {
            OwningJob = conflictingJob with { DisplayId = 701 },
        };
        await RunCompletedWriterAsync(database, [validComposite]);
        await RunCompletedWriterAsync(database, [validComposite]);

        await using (var verifyReplay = await database.Factory.CreateDbContextAsync())
        {
            Assert.AreEqual(1, await verifyReplay.Jobs.CountAsync(job => job.Id == owningJobId));
            Assert.AreEqual(1, await verifyReplay.Transfers.CountAsync(transfer => transfer.Id == transferId));
            Assert.AreEqual(1, await verifyReplay.TransferAttempts.CountAsync(attempt => attempt.Id == attemptId));
        }
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task SearchCompletion_RepresentsGenuineZeroResults_AndCannotPromoteIncompleteHistory()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("search-state-test")).Runtime;
        Guid zeroSearchId = Guid.NewGuid();
        Guid incompleteSearchId = Guid.NewGuid();

        await RunCompletedWriterAsync(database,
        [
            Job(runtime.RuntimeId, zeroSearchId, 1, PersistenceMutationPriority.Structural, kind: "Search"),
            new SearchCompletionPersistenceMutation(
                runtime.RuntimeId, 2, DateTimeOffset.UtcNow, zeroSearchId, 2,
                "zero", 0, 0, "Complete"),
            Job(runtime.RuntimeId, incompleteSearchId, 1, PersistenceMutationPriority.Structural, kind: "Search"),
            new SearchCompletionPersistenceMutation(
                runtime.RuntimeId, 3, DateTimeOffset.UtcNow, incompleteSearchId, 2,
                "incomplete", 7, 0, "Incomplete"),
        ]);
        await RunCompletedWriterAsync(database,
        [
            new SearchCompletionPersistenceMutation(
                runtime.RuntimeId, 4, DateTimeOffset.UtcNow, incompleteSearchId, 3,
                "incomplete", 7, 0, "Complete"),
        ]);

        var reader = new SearchHistoryReader(database.Factory);
        var zero = await reader.GetMetadataAsync(zeroSearchId);
        Assert.IsNotNull(zero);
        Assert.AreEqual("Complete", zero.ResultPersistenceState);
        Assert.AreEqual(0L, zero.ResultCount);
        Assert.AreEqual(0, (await reader.GetRawResultsAsync(zeroSearchId, 0, 10))!.Items.Count);

        var incomplete = await reader.GetMetadataAsync(incompleteSearchId);
        Assert.IsNotNull(incomplete);
        Assert.AreEqual("Incomplete", incomplete.ResultPersistenceState);
        Assert.AreEqual(7L, incomplete.ResultCount);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    [TestCategory("Load")]
    public async Task LargeSearchCompletion_BatchesTenThousandRowsIntoOneTerminalTransaction()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("large-search-batch-test")).Runtime;
        Guid searchId = Guid.NewGuid();
        var options = new PersistenceWriterOptions
        {
            SearchResultCapacityPerSearch = 10_000,
            SearchResultGlobalCapacity = 10_000,
            SearchResultFlushCount = 200,
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, searchId, 1,
            PersistenceMutationPriority.Structural, kind: "Search")));
        for (int offset = 0; offset < 10_000; offset += 200)
        {
            var results = Enumerable.Range(offset + 1, 200).Select(value => Result(value)).ToArray();
            Assert.IsTrue(inbox.TryEnqueue(new SearchResultsPersistenceMutation(
                runtime.RuntimeId, offset + 2L, DateTimeOffset.UtcNow, searchId, offset + 201L, results)));
        }
        Assert.IsTrue(inbox.TryEnqueue(new SearchCompletionPersistenceMutation(
            runtime.RuntimeId, 20_000, DateTimeOffset.UtcNow, searchId, 10_002,
            "large", 10_000, 0, "Complete")));
        inbox.Complete();

        await new PersistenceWriter(database.Factory, inbox, health, options)
            .RunAsync(CancellationToken.None);

        await using var verify = await database.Factory.CreateDbContextAsync();
        Assert.AreEqual(10_000, await verify.SearchResults.CountAsync(row => row.SearchJobId == searchId));
        Assert.AreEqual("Complete", (await verify.SearchJobs.SingleAsync(row => row.JobId == searchId)).ResultPersistenceState);
        Assert.IsTrue(health.Snapshot(inbox).SuccessfulCommitCount <= 2,
            "Search persistence must commit batches/barriers, not one transaction per result.");
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task SearchDuplicatePolicy_FirstSequenceAndFirstPeerPathWinConsistently()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("duplicate-search-test")).Runtime;
        Guid searchId = Guid.NewGuid();
        var first = Result(1);
        var duplicatePeerPath = first with { Id = Guid.NewGuid(), Sequence = 2, Revision = 2, SizeBytes = 999 };
        var duplicateSequence = Result(1) with
        {
            Id = Guid.NewGuid(),
            Username = "another-peer",
            RemoteFilename = "another.mp3",
            SizeBytes = 888,
        };
        await RunCompletedWriterAsync(database,
        [
            Job(runtime.RuntimeId, searchId, 1, PersistenceMutationPriority.Structural, kind: "Search"),
            new SearchTerminalPersistenceMutation(
                new SearchCompletionPersistenceMutation(
                    runtime.RuntimeId, 4, DateTimeOffset.UtcNow, searchId, 3, "duplicates", 1, 0, "Complete"),
                [new SearchResultsPersistenceMutation(
                    runtime.RuntimeId, 2, DateTimeOffset.UtcNow, searchId, 2,
                    [first, duplicatePeerPath, duplicateSequence])]),
        ]);

        await using var verify = await database.Factory.CreateDbContextAsync();
        var stored = await verify.SearchResults.SingleAsync(row => row.SearchJobId == searchId);
        Assert.AreEqual(first.Id, stored.Id);
        Assert.AreEqual(first.SizeBytes, stored.SizeBytes);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public void Inbox_RemainsBounded_AndMarksSearchLossVisible()
    {
        var options = new PersistenceWriterOptions
        {
            CriticalQueueCapacity = 1,
            OrdinaryQueueCapacity = 1,
            ProgressEntityCapacity = 1,
            DegradedProjectionCapacity = 1,
            SearchResultCapacityPerSearch = 1,
            SearchResultGlobalCapacity = 1,
            MaximumBatchSize = 1,
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid runtimeId = Guid.NewGuid();

        Assert.IsTrue(inbox.TryEnqueue(Job(runtimeId, Guid.NewGuid(), revision: 1, PersistenceMutationPriority.Terminal)));
        Assert.IsFalse(inbox.TryEnqueue(Job(runtimeId, Guid.NewGuid(), revision: 1, PersistenceMutationPriority.Terminal)));
        Assert.IsFalse(inbox.TryEnqueue(Job(runtimeId, Guid.NewGuid(), revision: 1, PersistenceMutationPriority.Terminal)));

        Guid transferId = Guid.NewGuid();
        Assert.IsTrue(inbox.TryEnqueue(Transfer(runtimeId, transferId, revision: 1)));
        Assert.IsTrue(inbox.TryEnqueue(Transfer(runtimeId, transferId, revision: 2)));
        Assert.IsFalse(inbox.TryEnqueue(Transfer(runtimeId, Guid.NewGuid(), revision: 1)));

        Guid searchJobId = Guid.NewGuid();
        var tooLargeSearchBatch = new SearchResultsPersistenceMutation(
            runtimeId,
            10,
            DateTimeOffset.UtcNow,
            searchJobId,
            2,
            [Result(1), Result(2)]);
        Assert.IsFalse(inbox.TryEnqueue(tooLargeSearchBatch));

        var snapshot = health.Snapshot(inbox);
        Assert.IsTrue(inbox.CriticalDepth <= options.CriticalQueueCapacity);
        Assert.IsTrue(inbox.DegradedCount <= options.DegradedProjectionCapacity);
        Assert.IsTrue(inbox.ProgressCount <= options.ProgressEntityCapacity);
        Assert.IsTrue(inbox.BufferedSearchResultCount <= options.SearchResultGlobalCapacity);
        Assert.AreEqual(1L, snapshot.EvictedTerminalProjectionCount);
        Assert.AreEqual(1L, snapshot.DroppedProgressCount);
        Assert.AreEqual(2L, snapshot.DroppedSearchResultCount);
        Assert.AreEqual(1L, snapshot.IncompleteSearchCount);
        Assert.AreEqual(PersistenceHealthState.Degraded, snapshot.State);
    }

    [TestMethod]
    public void IncompleteSearchTrackingOverflow_DoesNotPoisonUnrelatedCompletions()
    {
        var options = new PersistenceWriterOptions
        {
            SearchResultCapacityPerSearch = 1,
            SearchResultGlobalCapacity = 1,
            IncompleteSearchTrackingCapacity = 1,
        };
        var inbox = new PersistenceInbox(options, new PersistenceHealth());
        Guid runtimeId = Guid.NewGuid();

        foreach (Guid searchId in new[] { Guid.NewGuid(), Guid.NewGuid() })
        {
            Assert.IsFalse(inbox.TryEnqueue(new SearchResultsPersistenceMutation(
                runtimeId,
                1,
                DateTimeOffset.UtcNow,
                searchId,
                1,
                [Result(1), Result(2)])));
        }

        Guid unaffectedSearchId = Guid.NewGuid();
        Assert.IsTrue(inbox.TryEnqueue(new SearchCompletionPersistenceMutation(
            runtimeId,
            2,
            DateTimeOffset.UtcNow,
            unaffectedSearchId,
            2,
            "unaffected",
            0,
            0,
            "Complete")));

        PersistenceMutation mutation = inbox.DrainBatch().Single();
        Assert.IsInstanceOfType<SearchTerminalPersistenceMutation>(mutation);
        var terminal = (SearchTerminalPersistenceMutation)mutation;
        Assert.AreEqual("Complete", terminal.Completion.ResultPersistenceState);
    }

    [TestMethod]
    public void SimulatedWeekLongOutage_LossyBuffersRemainBoundedWithoutDiscardingIncompleteSearchIds()
    {
        var options = new PersistenceWriterOptions
        {
            CriticalQueueCapacity = 8,
            OrdinaryQueueCapacity = 8,
            ProgressEntityCapacity = 16,
            DegradedProjectionCapacity = 32,
            SearchResultCapacityPerSearch = 8,
            SearchResultGlobalCapacity = 128,
            IncompleteSearchTrackingCapacity = 64,
            MaximumBatchSize = 16,
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid runtimeId = Guid.NewGuid();

        for (int day = 0; day < 7; day++)
        {
            for (int entity = 0; entity < 2_000; entity++)
            {
                inbox.TryEnqueue(Job(runtimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Terminal, terminal: true));
                inbox.TryEnqueue(Transfer(runtimeId, Guid.NewGuid(), 1));
                Guid searchId = Guid.NewGuid();
                inbox.TryEnqueue(new SearchResultsPersistenceMutation(
                    runtimeId, entity + 1L, DateTimeOffset.UtcNow, searchId, 1, [Result(1)]));
            }
        }

        var snapshot = health.Snapshot(inbox);
        Assert.IsTrue(inbox.CriticalDepth <= options.CriticalQueueCapacity);
        Assert.IsTrue(inbox.OrdinaryDepth <= options.OrdinaryQueueCapacity);
        Assert.IsTrue(inbox.ProgressCount <= options.ProgressEntityCapacity);
        Assert.IsTrue(inbox.DegradedCount <= options.DegradedProjectionCapacity);
        Assert.IsTrue(inbox.BufferedSearchResultCount <= options.SearchResultGlobalCapacity);
        Assert.IsTrue(inbox.IncompleteSearchTrackingCount > options.IncompleteSearchTrackingCapacity);
        Assert.IsTrue(inbox.IncompleteSearchTrackingOverflowed);
        Assert.IsTrue(snapshot.DroppedProgressCount > 0);
        Assert.IsTrue(snapshot.DroppedSearchResultCount > 0);
        Assert.IsTrue(snapshot.EvictedTerminalProjectionCount > 0);
        Assert.AreEqual(PersistenceHealthState.Degraded, snapshot.State);
    }

    [TestMethod]
    public async Task Writer_CoalescesByRevision_AndCannotRegressTerminalJob()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("writer-test")).Runtime;
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var commits = Channel.CreateUnbounded<bool>();
        health.CommitCompleted += () => commits.Writer.TryWrite(true);
        var writer = new PersistenceWriter(database.Factory, inbox, health, options);
        using var stop = new CancellationTokenSource();
        var runTask = writer.RunAsync(stop.Token);
        Guid jobId = Guid.NewGuid();

        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, revision: 1, PersistenceMutationPriority.Structural)));
        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, revision: 2, PersistenceMutationPriority.Terminal, terminal: true)));
        await commits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            var persisted = await context.Jobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
            Assert.AreEqual(2L, persisted.Revision);
            Assert.AreEqual("Terminal", persisted.LifecycleState);
        }

        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, revision: 1, PersistenceMutationPriority.Structural)));
        await commits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            var persisted = await context.Jobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
            Assert.AreEqual(2L, persisted.Revision);
            Assert.AreEqual("Terminal", persisted.LifecycleState);
        }

        stop.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => runTask);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task WalReadersRemainAvailableWhileSingleWriterCommits()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("concurrent-reader-test")).Runtime;
        var options = new PersistenceWriterOptions { CriticalQueueCapacity = 2_000 };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var writerTask = new PersistenceWriter(database.Factory, inbox, health, options).RunAsync(CancellationToken.None);
        var reader = new JobHistoryReader(database.Factory);
        var readerErrors = new List<Exception>();

        var reading = Task.Run(async () =>
        {
            for (int iteration = 0; iteration < 100; iteration++)
            {
                try
                {
                    var page = await reader.GetJobsAsync(new JobHistoryQuery(Limit: 50, IncludeAll: true));
                    Assert.IsTrue(page.Items.Count <= 50);
                }
                catch (Exception ex)
                {
                    lock (readerErrors) readerErrors.Add(ex);
                }
            }
        });

        for (int index = 1; index <= 1_000; index++)
        {
            var mutation = Job(runtime.RuntimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Structural)
                with { DisplayId = index };
            Assert.IsTrue(inbox.TryEnqueue(mutation));
        }
        inbox.Complete();
        await Task.WhenAll(reading, writerTask);

        Assert.AreEqual(0, readerErrors.Count,
            readerErrors.Count > 0 ? readerErrors[0].ToString() : null);
        await using (var verify = await database.Factory.CreateDbContextAsync())
            Assert.AreEqual(1_000, await verify.Jobs.CountAsync());
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task UnavailableWriterEntersUnhealthyWithoutBlockingMutationProducers()
    {
        var options = new PersistenceWriterOptions
        {
            MaximumRecoveryAttempts = 2,
            FailureRetryDelay = TimeSpan.FromMilliseconds(1),
            ProgressEntityCapacity = 8,
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var runTask = new PersistenceWriter(new UnavailableContextFactory(), inbox, health, options)
            .RunAsync(CancellationToken.None);
        Guid runtimeId = Guid.NewGuid();
        Assert.IsTrue(inbox.TryEnqueue(Job(runtimeId, Guid.NewGuid(), 1, PersistenceMutationPriority.Structural)));

        var stopwatch = Stopwatch.StartNew();
        for (int index = 0; index < 10_000; index++)
            inbox.TryEnqueue(Transfer(runtimeId, Guid.NewGuid(), index + 1L));
        stopwatch.Stop();
        inbox.Complete();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.AreEqual(PersistenceHealthState.Unhealthy, health.Snapshot(inbox).State);
        Assert.IsTrue(health.Snapshot(inbox).PermanentlyFailedMutationCount > 0);
    }

    [TestMethod]
    public async Task SearchCompletion_CommitsPendingResultsAndMetadataInOneBarrier()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("search-writer-test")).Runtime;
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        health.CommitCompleted += () => committed.TrySetResult();
        var writer = new PersistenceWriter(database.Factory, inbox, health, options);
        using var stop = new CancellationTokenSource();
        var runTask = writer.RunAsync(stop.Token);
        Guid jobId = Guid.NewGuid();

        inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural, kind: "Search"));
        inbox.TryEnqueue(new SearchResultsPersistenceMutation(
            runtime.RuntimeId,
            2,
            DateTimeOffset.UtcNow,
            jobId,
            2,
            [Result(1), Result(2)]));
        inbox.TryEnqueue(new SearchCompletionPersistenceMutation(
            runtime.RuntimeId,
            3,
            DateTimeOffset.UtcNow,
            jobId,
            3,
            "query",
            2,
            4,
            "Complete"));

        await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            var search = await context.SearchJobs.AsNoTracking().SingleAsync(row => row.JobId == jobId);
            var results = await context.SearchResults.AsNoTracking().OrderBy(row => row.Sequence).ToListAsync();
            Assert.IsTrue(search.IsComplete);
            Assert.AreEqual("Complete", search.ResultPersistenceState);
            Assert.AreEqual(2L, search.ResultCount);
            Assert.AreEqual(4L, search.LockedFileCount);
            Assert.AreEqual(2, results.Count);
            CollectionAssert.AreEqual(new long[] { 1, 2 }, results.Select(result => result.Sequence).ToArray());
        }

        var historical = await new SearchHistoryReader(database.Factory)
            .GetResultAsync(jobId, "user", "file-1.mp3");
        Assert.IsNotNull(historical?.Result);
        var neutral = historical.Result.ToProjectionInput();
        Assert.AreEqual("user", neutral.Username);
        Assert.AreEqual("file-1.mp3", neutral.Filename);
        Assert.AreEqual(1, neutral.ResponseFileCount);
        Assert.AreEqual(320, neutral.BitRate);

        stop.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => runTask);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task JobHistory_UsesStableCursorAndEnforcesLimits()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("reader-test")).Runtime;
        Guid[] ids = [Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"), Guid.Parse("00000000-0000-0000-0000-000000000003")];
        Guid[] workflowIds = [Guid.Parse("10000000-0000-0000-0000-000000000001"), Guid.Parse("10000000-0000-0000-0000-000000000002"), Guid.Parse("10000000-0000-0000-0000-000000000003")];
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            for (int i = 0; i < ids.Length; i++)
            {
                context.Jobs.Add(new JobEntity
                {
                    Id = ids[i],
                    WorkflowId = workflowIds[i],
                    LastRuntimeId = runtime.RuntimeId,
                    LastSequence = i + 1,
                    DisplayId = i + 1,
                    Kind = "Song",
                    LifecycleState = "Terminal",
                    ActivityPhase = "None",
                    TerminalOutcome = "Succeeded",
                    SkipReason = "None",
                    CancellationSource = "None",
                    FailureReason = "None",
                    CreatedAtUtc = 100,
                    UpdatedAtUtc = 200,
                    CompletedAtUtc = 200,
                    Revision = 2,
                    PayloadSchemaVersion = 1,
                });
            }
            await context.SaveChangesAsync();
        }

        var reader = new JobHistoryReader(database.Factory);
        var first = await reader.GetJobsAsync(new JobHistoryQuery(Limit: 2));
        Assert.AreEqual(2, first.Items.Count);
        Assert.IsNotNull(first.NextCursor);
        var second = await reader.GetJobsAsync(new JobHistoryQuery(Cursor: first.NextCursor, Limit: 2));
        CollectionAssert.AreEqual(ids.Take(2).ToArray(), first.Items.Select(job => job.Id).ToArray());
        CollectionAssert.AreEqual(ids.Skip(2).ToArray(), second.Items.Select(job => job.Id).ToArray());
        Assert.IsNull(second.NextCursor);
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => reader.GetJobsAsync(new JobHistoryQuery(Cursor: "bad", Limit: 2)));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => reader.GetJobsAsync(new JobHistoryQuery(
            Cursor: first.NextCursor + new string(' ', 128), Limit: 2)));
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() => reader.GetJobsAsync(new JobHistoryQuery(Limit: JobHistoryReader.MaximumPageSize + 1)));

        var workflowPage1 = await reader.GetWorkflowsAsync(limit: 2);
        var workflowPage2 = await reader.GetWorkflowsAsync(workflowPage1.NextCursor, limit: 2);
        CollectionAssert.AreEqual(workflowIds.Take(2).ToArray(), workflowPage1.Items.Select(item => item.WorkflowId).ToArray());
        CollectionAssert.AreEqual(workflowIds.Skip(2).ToArray(), workflowPage2.Items.Select(item => item.WorkflowId).ToArray());
        Assert.IsNull(workflowPage2.NextCursor);
        Assert.AreEqual(ids[1], (await reader.GetJobByDisplayIdAsync(workflowIds[1], 2))?.Id);
        Assert.AreEqual(1, (await reader.GetWorkflowAsync(workflowIds[0]))?.RootJobCount);
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => reader.GetWorkflowsAsync("bad", 2));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => reader.GetWorkflowsAsync(
            workflowPage1.NextCursor + new string(' ', 128), 2));
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task LargeWorkflow_PagesRootsDirectChildrenAndAllJobsInDisplayOrder()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("large-workflow-reader-test")).Runtime;
        Guid workflowId = Guid.NewGuid();
        Guid rootId = Guid.NewGuid();
        Guid firstChildId = Guid.NewGuid();
        var jobs = new List<JobEntity>(230);

        jobs.Add(CreateJob(rootId, displayId: 1, parentJobId: null, itemName: null));
        for (int displayId = 2; displayId <= 151; displayId++)
        {
            Guid id = displayId == 2 ? firstChildId : Guid.NewGuid();
            jobs.Add(CreateJob(
                id,
                displayId,
                rootId,
                itemName: displayId == 11 ? "Large workflow" : null));
        }
        for (int displayId = 152; displayId <= 230; displayId++)
            jobs.Add(CreateJob(Guid.NewGuid(), displayId, firstChildId, itemName: null));

        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            context.Jobs.AddRange(jobs);
            await context.SaveChangesAsync();
        }

        var reader = new JobHistoryReader(database.Factory);
        var summary = await reader.GetWorkflowAsync(workflowId);
        Assert.IsNotNull(summary);
        Assert.AreEqual("Large workflow", summary.Title);
        Assert.AreEqual(1, summary.RootJobCount);
        Assert.AreEqual(230, summary.CompletedJobCount);
        Assert.AreEqual(1, summary.FailedJobCount);

        var roots = await reader.GetJobsAsync(new JobHistoryQuery(
            Limit: 100,
            WorkflowId: workflowId,
            IncludeAll: false));
        CollectionAssert.AreEqual(new[] { rootId }, roots.Items.Select(job => job.Id).ToArray());

        var directPage1 = await reader.GetJobsAsync(new JobHistoryQuery(
            Limit: 100,
            WorkflowId: workflowId,
            IncludeAll: true,
            ParentJobId: rootId));
        var directPage2 = await reader.GetJobsAsync(new JobHistoryQuery(
            Cursor: directPage1.NextCursor,
            Limit: 100,
            WorkflowId: workflowId,
            IncludeAll: true,
            ParentJobId: rootId));
        Assert.AreEqual(150, directPage1.Items.Count + directPage2.Items.Count);
        Assert.IsNull(directPage2.NextCursor);
        CollectionAssert.AreEqual(
            Enumerable.Range(2, 150).Select(value => (long)value).ToArray(),
            directPage1.Items.Concat(directPage2.Items).Select(job => job.DisplayId).ToArray());
        Assert.AreEqual(150, await reader.GetChildCountAsync(rootId));

        var all = new List<PersistedJob>();
        string? cursor = null;
        do
        {
            var page = await reader.GetJobsAsync(new JobHistoryQuery(
                Cursor: cursor,
                Limit: 100,
                WorkflowId: workflowId,
                IncludeAll: true));
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor != null);
        Assert.AreEqual(230, all.Count);
        Assert.AreEqual(230, all.Select(job => job.Id).Distinct().Count());
        CollectionAssert.AreEqual(
            Enumerable.Range(1, 230).Select(value => (long)value).ToArray(),
            all.Select(job => job.DisplayId).ToArray());

        await runtimeSession.StopAsync();

        JobEntity CreateJob(Guid id, int displayId, Guid? parentJobId, string? itemName)
            => new()
            {
                Id = id,
                WorkflowId = workflowId,
                ParentJobId = parentJobId,
                LastRuntimeId = runtime.RuntimeId,
                LastSequence = displayId,
                DisplayId = displayId,
                Kind = displayId == 1 ? "JobList" : "Song",
                LifecycleState = "Terminal",
                ActivityPhase = "None",
                TerminalOutcome = displayId == 225 ? "Failed" : "Succeeded",
                SkipReason = "None",
                CancellationSource = "None",
                FailureReason = displayId == 225 ? "Other" : "None",
                ItemName = itemName,
                QueryText = displayId == 1 ? "fallback title" : null,
                CreatedAtUtc = displayId,
                UpdatedAtUtc = displayId,
                CompletedAtUtc = displayId,
                Revision = 1,
                PayloadSchemaVersion = 1,
            };
    }

    [TestMethod]
    public async Task TransferHistory_PagesTransfersAndAttemptsWithoutLoadingAllRows()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("transfer-reader-test")).Runtime;
        Guid workflowId = Guid.NewGuid();
        Guid[] ids =
        [
            Guid.Parse("00000000-0000-0000-0000-000000000011"),
            Guid.Parse("00000000-0000-0000-0000-000000000012"),
            Guid.Parse("00000000-0000-0000-0000-000000000013"),
        ];
        await using (var context = await database.Factory.CreateDbContextAsync())
        {
            foreach (var (id, index) in ids.Select((id, index) => (id, index)))
            {
                context.Transfers.Add(new TransferEntity
                {
                    Id = id,
                    WorkflowId = workflowId,
                    LastRuntimeId = runtime.RuntimeId,
                    LastSequence = index + 1,
                    Direction = "Download",
                    Source = index == 1 ? "YtDlpFallback" : "SoulseekPeer",
                    Username = index == 2 ? "special-user" : "user",
                    State = index == 0 ? "Failed" : "Completed",
                    TerminalOutcome = "Succeeded",
                    TotalBytes = 100,
                    TransferredBytes = 100,
                    AttemptCount = index == 0 ? 225 : 0,
                    CreatedAtUtc = 100,
                    CompletedAtUtc = 200,
                    FailureReason = "None",
                    Revision = 2,
                });
            }
            for (int attempt = 1; attempt <= 225; attempt++)
            {
                context.TransferAttempts.Add(new TransferAttemptEntity
                {
                    Id = Guid.NewGuid(),
                    TransferId = ids[0],
                    LastRuntimeId = runtime.RuntimeId,
                    LastSequence = attempt,
                    AttemptNumber = attempt,
                    Source = "SoulseekPeer",
                    State = "Completed",
                    StartedAtUtc = 100 + attempt,
                    CompletedAtUtc = 200 + attempt,
                    FailureReason = "None",
                    Revision = 2,
                });
            }
            await context.SaveChangesAsync();
        }

        var reader = new TransferHistoryReader(database.Factory);
        var first = await reader.GetTransfersAsync(new TransferHistoryQuery(Limit: 2, WorkflowId: workflowId));
        var second = await reader.GetTransfersAsync(new TransferHistoryQuery(first.NextCursor, 2, WorkflowId: workflowId));
        CollectionAssert.AreEqual(ids.Take(2).ToArray(), first.Items.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(ids.Skip(2).ToArray(), second.Items.Select(item => item.Id).ToArray());
        Assert.IsNull(second.NextCursor);
        Assert.AreEqual(1, (await reader.GetTransfersAsync(new TransferHistoryQuery(
            Limit: 10, WorkflowId: workflowId, Source: "YtDlpFallback"))).Items.Count);
        Assert.AreEqual(ids[2], (await reader.GetTransfersAsync(new TransferHistoryQuery(
            Limit: 10, Username: "special-user", TerminalOutcome: "Succeeded"))).Items.Single().Id);

        var detail = await reader.GetTransferAsync(ids[0]);
        Assert.IsNotNull(detail);
        Assert.AreEqual(225, detail.Transfer.AttemptCount);
        Assert.AreEqual(225, detail.LatestAttempt?.AttemptNumber);

        var attemptNumbers = new List<int>();
        int afterAttemptNumber = 0;
        int? nextAttemptNumber;
        do
        {
            var page = await reader.GetAttemptsAsync(ids[0], afterAttemptNumber, 100);
            Assert.IsNotNull(page);
            attemptNumbers.AddRange(page.Items.Select(item => item.AttemptNumber));
            nextAttemptNumber = page.NextAttemptNumber;
            afterAttemptNumber = nextAttemptNumber ?? 0;
        }
        while (nextAttemptNumber != null);
        CollectionAssert.AreEqual(Enumerable.Range(1, 225).ToArray(), attemptNumbers.ToArray());

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => reader.GetTransfersAsync(new TransferHistoryQuery("bad", 2)));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => reader.GetTransfersAsync(new TransferHistoryQuery(
            first.NextCursor + new string(' ', 129), 2)));
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() => reader.GetAttemptsAsync(ids[0], 0, TransferHistoryReader.MaximumPageSize + 1));
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task Writer_RecoversAfterExhaustedSqliteBusy_WithoutStoppingProducer()
    {
        await using var database = new WriterDatabase(
            defaultTimeoutSeconds: 1,
            busyTimeoutMilliseconds: 25);
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("busy-recovery-test")).Runtime;
        var options = new PersistenceWriterOptions
        {
            BusyRetryCount = 0,
            FailureRetryDelay = TimeSpan.FromMilliseconds(25),
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        health.FailureRecorded += () => failure.TrySetResult();
        health.CommitCompleted += () => committed.TrySetResult();
        var writer = new PersistenceWriter(database.Factory, inbox, health, options);
        var runTask = writer.RunAsync(CancellationToken.None);

        await using var lockingContext = await database.Factory.CreateDbContextAsync();
        await lockingContext.Database.OpenConnectionAsync();
        await using var lockCommand = lockingContext.Database.GetDbConnection().CreateCommand();
        lockCommand.CommandText = "BEGIN IMMEDIATE;";
        await lockCommand.ExecuteNonQueryAsync();

        Guid jobId = Guid.NewGuid();
        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural)));
        await failure.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsFalse(runTask.IsCompleted);
        await using (var release = lockingContext.Database.GetDbConnection().CreateCommand())
        {
            release.CommandText = "ROLLBACK;";
            await release.ExecuteNonQueryAsync();
        }
        await lockingContext.Database.CloseConnectionAsync();

        await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        inbox.Complete();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var verify = await database.Factory.CreateDbContextAsync())
            Assert.IsTrue(await verify.Jobs.AnyAsync(job => job.Id == jobId));
        Assert.AreEqual(PersistenceHealthState.Healthy, health.Snapshot(inbox).State);
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task Writer_StopsRetryingRecoverableFailure_AfterConfiguredRecoveryLimit()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("bounded-recovery-test")).Runtime;
        var options = new PersistenceWriterOptions
        {
            BusyRetryCount = 0,
            FailureRetryDelay = TimeSpan.FromMilliseconds(10),
            MaximumRecoveryAttempts = 2,
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        Guid jobId = Guid.NewGuid();

        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, jobId, 1, PersistenceMutationPriority.Structural)));
        inbox.Complete();

        await new PersistenceWriter(new UnavailableContextFactory(), inbox, health, options)
            .RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = health.Snapshot(inbox);
        Assert.AreEqual(PersistenceHealthState.Unhealthy, snapshot.State);
        Assert.AreEqual(1L, snapshot.PermanentlyFailedMutationCount);
        await using (var verify = await database.Factory.CreateDbContextAsync())
            Assert.IsFalse(await verify.Jobs.AnyAsync(job => job.Id == jobId));
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task Writer_DropsPermanentMutationOnce_AndContinuesWithLaterWork()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var runtimeSession = new PersistenceRuntimeSession(database.Factory);
        var runtime = (await runtimeSession.StartAsync("permanent-failure-test")).Runtime;
        var options = new PersistenceWriterOptions { FailureRetryDelay = TimeSpan.FromMilliseconds(25) };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        health.FailureRecorded += () => failed.TrySetResult();
        health.CommitCompleted += () => committed.TrySetResult();
        var writer = new PersistenceWriter(database.Factory, inbox, health, options);
        var runTask = writer.RunAsync(CancellationToken.None);

        Assert.IsTrue(inbox.TryEnqueue(new SearchCompletionPersistenceMutation(
            runtime.RuntimeId, 1, DateTimeOffset.UtcNow, Guid.NewGuid(), 1,
            "missing job", 0, 0, "Complete")));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Guid validJobId = Guid.NewGuid();
        Assert.IsTrue(inbox.TryEnqueue(Job(runtime.RuntimeId, validJobId, 2, PersistenceMutationPriority.Structural)));
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        inbox.Complete();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = health.Snapshot(inbox);
        Assert.AreEqual(1L, snapshot.PermanentlyFailedMutationCount);
        await using (var verify = await database.Factory.CreateDbContextAsync())
            Assert.IsTrue(await verify.Jobs.AnyAsync(job => job.Id == validJobId));
        await runtimeSession.StopAsync();
    }

    [TestMethod]
    public async Task Writer_DrainsAwaitableCommandsBeyondFairnessBurstOnShutdown()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        AwaitablePersistenceCommand<int>[] commands = Enumerable.Range(1, 40)
            .Select(value => new AwaitablePersistenceCommand<int>(
                (_, _) => Task.FromResult(value)))
            .ToArray();
        foreach (AwaitablePersistenceCommand<int> command in commands)
            await inbox.EnqueueCommandAsync(command, CancellationToken.None);
        inbox.Complete();

        await new PersistenceWriter(database.Factory, inbox, health, options)
            .RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(
            Enumerable.Range(1, 40).ToArray(),
            await Task.WhenAll(commands.Select(command => command.Task)));
        Assert.AreEqual(0, inbox.CommandDepth);
    }

    [TestMethod]
    public async Task Writer_CancellationCompletesInFlightCommandWaiter()
    {
        await using var database = new WriterDatabase();
        await database.Initializer.InitializeAsync();
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AwaitablePersistenceCommand<int>(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 1;
        });
        await inbox.EnqueueCommandAsync(command, CancellationToken.None);
        using var stop = new CancellationTokenSource();
        Task writer = new PersistenceWriter(database.Factory, inbox, health, options)
            .RunAsync(stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            () => writer.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            () => command.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static JobPersistenceMutation Job(
        Guid runtimeId,
        Guid jobId,
        long revision,
        PersistenceMutationPriority priority,
        bool terminal = false,
        string kind = "Song")
        => new(
            runtimeId,
            revision,
            DateTimeOffset.UtcNow,
            jobId,
            revision,
            priority,
            Guid.NewGuid(),
            null,
            null,
            null,
            DisplayId: Math.Abs(jobId.GetHashCode()) + 1L,
            kind,
            terminal ? "Terminal" : "Pending",
            "None",
            null,
            terminal ? "Succeeded" : "None",
            "None",
            "None",
            "None",
            null,
            null,
            null,
            kind == "Search" ? "query" : null,
            1,
            null);

    private static TransferPersistenceMutation Transfer(Guid runtimeId, Guid transferId, long revision)
        => new(
            runtimeId,
            revision,
            DateTimeOffset.UtcNow,
            transferId,
            revision,
            PersistenceMutationPriority.Progress,
            null,
            null,
            "Download",
            "SoulseekPeer",
            "user",
            "remote",
            "local",
            "InProgress",
            "None",
            100,
            revision,
            1,
            "None",
            null);

    private static SearchResultPersistenceRecord Result(long sequence)
        => new(
            Guid.NewGuid(),
            sequence,
            sequence,
            "user",
            $"file-{sequence}.mp3",
            100,
            320,
            null,
            1,
            44_100,
            180,
            ".mp3",
            1_000,
            true,
            null,
            DateTimeOffset.UtcNow);

    private static async Task<PersistenceHealthSnapshot> RunCompletedWriterAsync(
        WriterDatabase database,
        IReadOnlyList<PersistenceMutation> mutations)
    {
        var options = new PersistenceWriterOptions();
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        foreach (var mutation in mutations)
            Assert.IsTrue(inbox.TryEnqueue(mutation));
        inbox.Complete();
        await new PersistenceWriter(database.Factory, inbox, health, options).RunAsync(CancellationToken.None);
        return health.Snapshot(inbox);
    }

    private sealed class WriterDatabase : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "sockseek-writer-tests", Guid.NewGuid().ToString("N"));
        private readonly SqliteDatabaseOwner owner;

        public WriterDatabase(
            int defaultTimeoutSeconds = 5,
            int busyTimeoutMilliseconds = 5_000)
        {
            Directory.CreateDirectory(directory);
            var sqliteOptions = new SockseekSqliteOptions(
                Path.Combine(directory, "sockseek.db"),
                DefaultTimeoutSeconds: defaultTimeoutSeconds,
                BusyTimeoutMilliseconds: busyTimeoutMilliseconds);
            owner = SqliteDatabaseOwner.Acquire(sqliteOptions);
            Factory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(sqliteOptions));
            Initializer = new SqliteInitializer(Factory, sqliteOptions, owner);
        }

        public SockseekDbContextFactory Factory { get; }
        public SqliteInitializer Initializer { get; }

        public ValueTask DisposeAsync()
        {
            owner.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnavailableContextFactory : IDbContextFactory<SockseekDbContext>
    {
        public SockseekDbContext CreateDbContext()
            => throw new IOException("Simulated unavailable persistence volume.");

        public Task<SockseekDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromException<SockseekDbContext>(new IOException("Simulated unavailable persistence volume."));
    }
}

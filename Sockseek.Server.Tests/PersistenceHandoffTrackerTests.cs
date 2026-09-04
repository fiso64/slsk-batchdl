using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence.Write;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class PersistenceHandoffTrackerTests
{
    [TestMethod]
    public async Task RetirementWaitsForExactTerminalJobAndSearchRevisions()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        tracker.RegisterJob(workflowId, jobId);
        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [jobId] = 5 },
            new Dictionary<Guid, long> { [jobId] = 3 });

        Task handoff = tracker.WaitForJobAsync(jobId, CancellationToken.None);
        tracker.Committed([JobMutation(workflowId, jobId, revision: 5)]);
        Assert.IsFalse(handoff.IsCompleted, "The job row alone must not expose incomplete search history.");

        tracker.Committed([SearchCompletion(jobId, revision: 2)]);
        Assert.IsFalse(handoff.IsCompleted, "An older search completion must not satisfy the handoff.");

        tracker.Committed([SearchCompletion(jobId, revision: 3)]);
        await handoff;
    }

    [TestMethod]
    public async Task CommitBeforeRetirementSatisfiesTheLaterHandoff()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid firstJobId = Guid.NewGuid();
        tracker.RegisterJob(workflowId, firstJobId);
        tracker.Committed([JobMutation(workflowId, firstJobId, revision: 4)]);
        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [firstJobId] = 4 },
            new Dictionary<Guid, long>());
        await tracker.WaitForJobAsync(firstJobId, CancellationToken.None);
    }

    [TestMethod]
    public async Task CancellingOneWaiterDoesNotCancelTheSharedHandoff()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        tracker.RegisterJob(workflowId, jobId);
        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [jobId] = 2 },
            new Dictionary<Guid, long>());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        OperationCanceledException? cancellation = null;
        try
        {
            await tracker.WaitForJobAsync(jobId, cancelled.Token);
        }
        catch (OperationCanceledException exception)
        {
            cancellation = exception;
        }
        Assert.IsNotNull(cancellation);

        Task survivingWaiter = tracker.WaitForJobAsync(jobId, CancellationToken.None);
        Assert.IsFalse(survivingWaiter.IsCompleted);
        tracker.Committed([JobMutation(workflowId, jobId, revision: 2)]);
        await survivingWaiter;
    }

    [TestMethod]
    public async Task ReusedWorkflowIdDoesNotMixPendingAndActiveGenerations()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid firstJobId = Guid.NewGuid();
        tracker.RegisterJob(workflowId, firstJobId);
        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [firstJobId] = 4 },
            new Dictionary<Guid, long>());

        Guid secondJobId = Guid.NewGuid();
        tracker.RegisterJob(workflowId, secondJobId);
        Task firstHandoff = tracker.WaitForWorkflowAsync(workflowId, CancellationToken.None);
        Task allHandoffs = tracker.WaitForAllAsync(CancellationToken.None);
        Assert.IsFalse(firstHandoff.IsCompleted);
        Assert.IsFalse(allHandoffs.IsCompleted);
        tracker.Committed([JobMutation(workflowId, firstJobId, revision: 4)]);
        await firstHandoff;
        await allHandoffs;

        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [secondJobId] = 7 },
            new Dictionary<Guid, long>());
        Task secondHandoff = tracker.WaitForWorkflowAsync(workflowId, CancellationToken.None);
        Assert.IsFalse(secondHandoff.IsCompleted);
        tracker.Committed([JobMutation(workflowId, secondJobId, revision: 7)]);
        await secondHandoff;
    }

    [TestMethod]
    public async Task PermanentMutationLossFailsOnlyTheAffectedWorkflowHandoff()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        Guid otherWorkflowId = Guid.NewGuid();
        Guid otherJobId = Guid.NewGuid();
        var mutation = JobMutation(workflowId, jobId, revision: 2);
        tracker.RegisterJob(workflowId, jobId);
        tracker.RegisterJob(otherWorkflowId, otherJobId);
        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [jobId] = 2 },
            new Dictionary<Guid, long>());
        tracker.BeginRetirement(
            otherWorkflowId,
            new Dictionary<Guid, long> { [otherJobId] = 3 },
            new Dictionary<Guid, long>());

        tracker.PermanentlyFailed([mutation], new IOException("disk unavailable"));
        tracker.Committed([JobMutation(otherWorkflowId, otherJobId, revision: 3)]);

        await Assert.ThrowsExactlyAsync<PersistenceHandoffException>(
            () => tracker.WaitForJobAsync(jobId, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<PersistenceHandoffException>(
            () => tracker.WaitForWorkflowAsync(workflowId, CancellationToken.None));
        await tracker.WaitForJobAsync(otherJobId, CancellationToken.None);
        await tracker.WaitForWorkflowAsync(otherWorkflowId, CancellationToken.None);
        await tracker.WaitForWorkflowAsync(Guid.NewGuid(), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<PersistenceHandoffException>(
            () => tracker.WaitForAllAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task SupersededTerminalMutationLossDoesNotFailTheFinalHandoff()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid workflowId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        tracker.RegisterJob(workflowId, jobId);
        tracker.PermanentlyFailed(
            [JobMutation(workflowId, jobId, revision: 2)],
            new IOException("temporary disk failure"));
        tracker.Committed([JobMutation(workflowId, jobId, revision: 3)]);

        tracker.BeginRetirement(
            workflowId,
            new Dictionary<Guid, long> { [jobId] = 3 },
            new Dictionary<Guid, long>());

        await tracker.WaitForJobAsync(jobId, CancellationToken.None);
        await tracker.WaitForWorkflowAsync(workflowId, CancellationToken.None);
    }

    [TestMethod]
    public async Task UploadRetirementWaitsForExactTerminalTransferCommitWithoutPolling()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid transferId = Guid.NewGuid();
        tracker.BeginTransferTerminal(transferId, revision: 3);

        Task handoff = tracker.WaitForTransferAsync(
            transferId,
            revision: 3,
            CancellationToken.None);
        tracker.Committed([TransferTerminal(transferId, revision: 2)]);
        Assert.IsFalse(handoff.IsCompleted);

        tracker.Committed([TransferTerminal(transferId, revision: 3)]);
        await handoff;
    }

    [TestMethod]
    public async Task TransferCommitBeforeWaitStillSatisfiesRetirement()
    {
        var tracker = new PersistenceHandoffTracker();
        Guid transferId = Guid.NewGuid();
        tracker.BeginTransferTerminal(transferId, revision: 4);
        tracker.Committed([TransferTerminal(transferId, revision: 4)]);

        await tracker.WaitForTransferAsync(
            transferId,
            revision: 4,
            CancellationToken.None);
    }

    private static JobPersistenceMutation JobMutation(
        Guid workflowId,
        Guid jobId,
        long revision)
        => new(
            Guid.NewGuid(),
            Sequence: revision,
            DateTimeOffset.UtcNow,
            jobId,
            revision,
            PersistenceMutationPriority.Terminal,
            workflowId,
            ParentJobId: null,
            SourceJobId: null,
            ResultJobId: null,
            DisplayId: 1,
            Kind: "Search",
            LifecycleState: "Terminal",
            ActivityPhase: "None",
            ActivityUntilUtc: null,
            TerminalOutcome: "Succeeded",
            SkipReason: "None",
            CancellationSource: "None",
            FailureReason: "None",
            FailureMessage: null,
            FailureDetail: null,
            ItemName: null,
            QueryText: "query",
            PayloadSchemaVersion: 1,
            PayloadJson: null);

    private static SearchCompletionPersistenceMutation SearchCompletion(Guid jobId, long revision)
        => new(
            Guid.NewGuid(),
            Sequence: revision,
            DateTimeOffset.UtcNow,
            jobId,
            revision,
            Query: "query",
            ResultCount: 1,
            LockedFileCount: 0,
            ResultPersistenceState: "Complete");

    private static TransferTerminalPersistenceMutation TransferTerminal(
        Guid transferId,
        long revision)
        => new(
            new TransferPersistenceMutation(
                Guid.NewGuid(),
                Sequence: revision,
                DateTimeOffset.UtcNow,
                transferId,
                revision,
                PersistenceMutationPriority.Terminal,
                JobId: null,
                WorkflowId: null,
                Direction: "Upload",
                Source: "SoulseekPeer",
                Username: "peer",
                RemotePath: "file",
                LocalPath: null,
                State: "Completed",
                TerminalOutcome: "Succeeded",
                TotalBytes: 100,
                TransferredBytes: 100,
                AttemptCount: 1,
                FailureReason: "None",
                FailureMessage: null),
            FinalAttempt: null,
            OwningJob: null);
}

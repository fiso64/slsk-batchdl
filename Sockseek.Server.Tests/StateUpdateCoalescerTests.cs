using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Sharing;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class StateUpdateCoalescerTests
{
    [TestMethod]
    public void Flush_FoldsLaterComponentsIntoUnflushedEntityAdd()
    {
        var published = new List<StateUpdateBatchDto>();
        using var coalescer = new StateUpdateCoalescer(
            batches => published.AddRange(batches),
            TimeSpan.FromHours(1));
        var epoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var transfer = Transfer(transferId, jobId, workflowId, 1, 10, terminal: false);

        coalescer.Publish(Batch(
            epoch,
            0,
            1,
            workflowId,
            StateDeltaDto.Empty with
            {
                Transfers = [new TransferDeltaDto(transferId, 1, Added: transfer)],
            }));
        coalescer.Publish(Batch(
            epoch,
            1,
            2,
            workflowId,
            StateDeltaDto.Empty with
            {
                Transfers =
                [
                    new TransferDeltaDto(
                        transferId,
                        2,
                        Progress: new TransferProgressFieldsDto(80, 100),
                        Scheduling: new TransferSchedulingFieldsDto(
                            DateTimeOffset.UnixEpoch,
                            DateTimeOffset.UnixEpoch.AddSeconds(1))),
                ],
            }));
        coalescer.Flush();

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual(0, published[0].PreviousSequence);
        Assert.AreEqual(2, published[0].Sequence);
        var added = published[0].State.Transfers.Single().Added;
        Assert.IsNotNull(added);
        Assert.AreEqual(2, added.Revision);
        Assert.AreEqual(80, added.Progress.BytesTransferred);
        Assert.AreEqual(
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            added.Scheduling?.StartedAtUtc);
    }

    [TestMethod]
    public void TerminalTransferFlushesFinalProgressAndRemovalTogether()
    {
        var published = new List<StateUpdateBatchDto>();
        using var coalescer = new StateUpdateCoalescer(
            batches => published.AddRange(batches),
            TimeSpan.FromHours(1));
        var epoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var transferId = Guid.NewGuid();

        coalescer.Publish(Batch(
            epoch,
            0,
            1,
            workflowId,
            StateDeltaDto.Empty with
            {
                Transfers =
                [
                    new TransferDeltaDto(
                        transferId,
                        3,
                        Status: new TransferStatusFieldsDto("Completed", "track.mp3", 1, true),
                        Progress: new TransferProgressFieldsDto(100, 100)),
                ],
                RemovedTransferIds = [transferId],
            }));

        Assert.AreEqual(1, published.Count);
        var delta = published[0].State.Transfers.Single();
        Assert.AreEqual(100, delta.Progress?.BytesTransferred);
        Assert.IsTrue(delta.Status?.IsTerminal);
        CollectionAssert.Contains(published[0].State.RemovedTransferIds.ToList(), transferId);
    }

    [TestMethod]
    public void Flush_PreservesActivityOrderAfterState()
    {
        var published = new List<StateUpdateBatchDto>();
        using var coalescer = new StateUpdateCoalescer(
            batches => published.AddRange(batches),
            TimeSpan.FromHours(1));
        var epoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();

        coalescer.Publish(new StateUpdateBatchDto(
            StateStreamScopeDto.Workflow(workflowId),
            epoch,
            0,
            1,
            DateTimeOffset.UtcNow,
            StateDeltaDto.Empty,
            [Activity(1, workflowId, "first")]));
        coalescer.Publish(new StateUpdateBatchDto(
            StateStreamScopeDto.Workflow(workflowId),
            epoch,
            1,
            2,
            DateTimeOffset.UtcNow,
            StateDeltaDto.Empty,
            [Activity(2, workflowId, "second")]));
        coalescer.Flush();

        CollectionAssert.AreEqual(
            new[] { "first", "second" },
            published.Single().Activity.Select(activity => activity.Type).ToArray());
    }

    [TestMethod]
    public void QueueSummaryBurst_PublishesOneLatestDaemonDelta()
    {
        const int mutations = 100_000;
        var published = new List<StateUpdateBatchDto>();
        using var coalescer = new StateUpdateCoalescer(
            batches => published.AddRange(batches),
            TimeSpan.FromHours(1));
        var epoch = Guid.NewGuid();

        for (int sequence = 1; sequence <= mutations; sequence++)
        {
            coalescer.Publish(new StateUpdateBatchDto(
                StateStreamScopeDto.Daemon,
                epoch,
                sequence - 1,
                sequence,
                DateTimeOffset.UnixEpoch,
                StateDeltaDto.Empty with
                {
                    Daemon = DaemonState(sequence),
                },
                []));
        }
        coalescer.Flush();

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual(0, published[0].PreviousSequence);
        Assert.AreEqual(mutations, published[0].Sequence);
        Assert.AreEqual(
            mutations,
            published[0].State.Daemon?.Uploads.QueueRevision);
    }

    private static StateUpdateBatchDto Batch(
        Guid epoch,
        long previous,
        long sequence,
        Guid workflowId,
        StateDeltaDto state)
        => new(
            StateStreamScopeDto.Workflow(workflowId),
            epoch,
            previous,
            sequence,
            DateTimeOffset.UtcNow,
            state,
            []);

    private static ActivityEventDto Activity(long sequence, Guid workflowId, string type)
        => new(
            sequence,
            DateTimeOffset.UtcNow,
            type,
            workflowId,
            null,
            null,
            new WorkflowMessageActivityDto("Information", null, type));

    private static DaemonStateDto DaemonState(long revision)
        => new(
            revision,
            new SoulseekClientStatusDto("Connected", ["Connected", "LoggedIn"], true),
            0,
            null,
            new SharingStateDto(
                SharingHealthState.Disabled,
                "NotConfigured",
                [],
                0,
                0,
                new ShareCatalogStateDto(
                    null,
                    0,
                    0,
                    0,
                    false,
                    null,
                    null),
                null,
                null),
            new UploadRuntimeStateDto(
                SharingHealthState.Disabled,
                "NotConfigured",
                false,
                10,
                0,
                checked((int)Math.Min(revision, int.MaxValue)),
                revision,
                revision,
                null));

    private static TransferStateDto Transfer(
        Guid transferId,
        Guid jobId,
        Guid workflowId,
        long revision,
        long bytes,
        bool terminal)
        => new(
            transferId,
            revision,
            new TransferIdentityFieldsDto(
                jobId,
                workflowId,
                "Download",
                "SoulseekPeer",
                "user",
                "track.mp3",
                "candidate"),
            new TransferStatusFieldsDto(
                terminal ? "Completed" : "InProgress",
                "track.mp3",
                1,
                terminal),
            new TransferProgressFieldsDto(bytes, 100));
}

using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Write;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class HistoricalQueryFacadeTests
{
    [TestMethod]
    public async Task TransferDetail_MergesRetainedMetadataWithAuthoritativeLiveAttempt()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-combined-transfer-detail",
            Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = dataDirectory,
                RetentionEnabled = false,
            },
        });
        var persistence = new PersistenceCoordinator(options);
        await persistence.StartAsync(CancellationToken.None);

        try
        {
            Guid transferId = Guid.NewGuid();
            Guid retainedAttemptId = Guid.NewGuid();
            DateTimeOffset retainedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            Guid runtimeId = persistence.Runtime!.RuntimeId;
            var retainedTransfer = new TransferPersistenceMutation(
                runtimeId,
                Sequence: 1,
                retainedAt,
                transferId,
                Revision: 1,
                PersistenceMutationPriority.Terminal,
                JobId: null,
                WorkflowId: null,
                Direction: "Upload",
                Source: "SoulseekPeer",
                Username: "retained-peer",
                RemotePath: @"Share\Retained.mp3",
                LocalPath: null,
                State: "Failed",
                TerminalOutcome: "Failed",
                TotalBytes: 100,
                TransferredBytes: 50,
                AttemptCount: 3,
                FailureReason: "Unavailable",
                FailureMessage: null);
            var retainedAttempt = new TransferAttemptPersistenceMutation(
                runtimeId,
                Sequence: 2,
                retainedAt,
                retainedAttemptId,
                Revision: 1,
                PersistenceMutationPriority.Terminal,
                transferId,
                AttemptNumber: 3,
                Source: "SoulseekPeer",
                State: "Failed",
                SourceUsername: "retained-peer",
                SourcePath: @"Share\Retained.mp3",
                OutputPath: null,
                FailureReason: "Unavailable",
                FailureMessage: null);
            Assert.IsTrue(persistence.MutationSink!.TryEnqueue(new TransferTerminalPersistenceMutation(
                retainedTransfer,
                retainedAttempt,
                OwningJob: null)));
            await WaitForPersistedTransferAsync(persistence.TransferHistory!, transferId);

            var live = new EngineStateStore();
            Guid liveAttemptId = Guid.NewGuid();
            DateTimeOffset liveStartedAt = DateTimeOffset.UtcNow;
            live.UpdateUploadTransfer(new Sockseek.Core.Transfers.Uploads.UploadTransferSnapshot(
                transferId,
                Revision: 2,
                Username: "live-peer",
                RemotePath: @"Share\Live.mp3",
                SizeBytes: 100,
                RequestedAtUtc: liveStartedAt,
                State: Sockseek.Core.Transfers.Uploads.UploadTransferState.InProgress,
                FailureReason: Sockseek.Core.Transfers.Uploads.UploadFailureReason.None,
                CancellationSource: Sockseek.Core.Transfers.Uploads.UploadCancellationSource.None,
                BytesTransferred: 75,
                BytesPerSecond: 25,
                LastProgressAtUtc: liveStartedAt,
                Attempt: new Sockseek.Core.Transfers.Uploads.UploadAttemptSnapshot(
                    liveAttemptId,
                    Number: 4,
                    StartedAtUtc: liveStartedAt,
                    FinishedAtUtc: null,
                    BytesTransferred: 75,
                    BytesPerSecond: 25),
                FinishedAtUtc: null));

            var facade = new HistoricalQueryFacade(live, supervisor: null!, persistence);
            TransferDetailDto? detail = await facade.GetTransferDetailAsync(transferId);

            Assert.IsNotNull(detail);
            Assert.AreEqual(TransferDetailSource.Merged, detail.Source);
            Assert.AreEqual("live-peer", detail.Live?.Identity.Username);
            Assert.AreEqual("retained-peer", detail.History?.Username);
            Assert.AreEqual(4, detail.AttemptCount);
            Assert.AreEqual(liveAttemptId, detail.LatestAttempt?.AttemptId);
            Assert.AreEqual("Started", detail.LatestAttempt?.State);
        }
        finally
        {
            await persistence.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task JobPages_MergeLiveAndPersistedRowsWithoutGapsOrDuplicates()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-combined-job-pages",
            Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = dataDirectory,
                RetentionEnabled = false,
            },
        });
        var persistence = new PersistenceCoordinator(options);
        await persistence.StartAsync(CancellationToken.None);

        try
        {
            Guid workflowId = Guid.NewGuid();
            var jobs = Enumerable.Range(1, 6)
                .Select(displayOrder => new SongJob(new SongQuery { Title = $"Job {displayOrder}" })
                {
                    WorkflowId = workflowId,
                })
                .ToArray();
            foreach (var job in jobs)
                job.EnsureDisplayId();

            var live = new EngineStateStore();
            Register(live, jobs[1]); // overlaps persisted state
            Register(live, jobs[2]); // live only
            Register(live, jobs[4]); // live only

            Guid runtimeId = persistence.Runtime!.RuntimeId;
            int sequence = 0;
            foreach (int index in new[] { 0, 1, 3, 5 })
            {
                var job = jobs[index];
                Assert.IsTrue(persistence.MutationSink!.TryEnqueue(new JobPersistenceMutation(
                    runtimeId,
                    ++sequence,
                    DateTimeOffset.UtcNow,
                    job.Id,
                    Revision: 1,
                    Priority: PersistenceMutationPriority.Structural,
                    WorkflowId: workflowId,
                    ParentJobId: null,
                    SourceJobId: null,
                    ResultJobId: null,
                    DisplayId: job.DisplayId,
                    Kind: "Song",
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
                    QueryText: job.Query.Title,
                    PayloadSchemaVersion: 1,
                    PayloadJson: null)));
            }

            await WaitForPersistedCountAsync(persistence.JobHistory!, workflowId, expected: 4);
            var facade = new HistoricalQueryFacade(live, supervisor: null!, persistence: persistence);
            var combined = new List<JobSummaryDto>();
            string? cursor = null;
            do
            {
                CombinedJobPage page = await facade.GetJobsAsync(
                    new JobQuery(null, null, null, workflowId, IncludeAll: true),
                    cursor,
                    limit: 2);
                combined.AddRange(page.Items);
                cursor = page.NextCursor;
            }
            while (cursor != null);

            CollectionAssert.AreEqual(
                jobs.Select(job => job.Id).ToArray(),
                combined.Select(job => job.JobId).ToArray());
            Assert.AreEqual(6, combined.Select(job => job.JobId).Distinct().Count());
            Assert.AreEqual(
                ServerJobLifecycleState.Pending,
                combined.Single(job => job.JobId == jobs[1].Id).LifecycleState,
                "The live overlap must replace the persisted terminal row.");
        }
        finally
        {
            await persistence.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static void Register(EngineStateStore store, Job job)
        => typeof(EngineStateStore)
            .GetMethod("OnJobRegistered", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobRegisteredChange(
                1,
                DateTimeOffset.UtcNow,
                CoreSnapshotFactory.CreateJob(job, revision: 1),
                ParentJobId: null,
                SourceJobId: null)]);

    private static async Task WaitForPersistedCountAsync(
        IJobHistoryReader reader,
        Guid workflowId,
        int expected)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            PersistedJobPage page = await reader.GetJobsAsync(new JobHistoryQuery(
                Limit: 100,
                WorkflowId: workflowId,
                IncludeAll: true));
            if (page.Items.Count == expected)
                return;
            await Task.Delay(10);
        }
        Assert.Fail($"Persistence did not retain {expected} test jobs before the deadline.");
    }

    private static async Task WaitForPersistedTransferAsync(
        ITransferHistoryReader reader,
        Guid transferId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await reader.GetTransferAsync(transferId) != null)
                return;
            await Task.Delay(10);
        }
        Assert.Fail("Persistence did not retain the test transfer before the deadline.");
    }
}

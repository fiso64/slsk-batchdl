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
    public async Task DashboardAnalytics_WhenPersistenceIsDisabled_IsExplicitlyUnavailable()
    {
        var persistence = new PersistenceCoordinator(Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions { Enabled = false },
        }));
        var facade = new DashboardAnalyticsFacade(persistence);

        DashboardAnalyticsDto result = await facade.GetAsync("24h");

        Assert.AreEqual(1, result.AccountingVersion);
        Assert.AreEqual(
            DashboardAnalyticsCoverageState.Unavailable,
            result.Range.Coverage.State);
        Assert.IsFalse(result.Range.Coverage.IsComplete);
        Assert.AreEqual(0, result.Bandwidth.Count);
    }

    [TestMethod]
    public async Task DashboardAnalytics_UsesFixedBoundedBucketsAndIndependentComparisonCoverage()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-dashboard-analytics",
            Guid.NewGuid().ToString("N"));
        var persistence = new PersistenceCoordinator(Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = dataDirectory,
                RetentionEnabled = false,
            },
        }));
        await persistence.StartAsync(CancellationToken.None);
        try
        {
            var facade = new DashboardAnalyticsFacade(
                persistence,
                new FixedClock(DateTimeOffset.UtcNow.AddMinutes(10)));

            DashboardAnalyticsDto day = await facade.GetAsync("24h");
            DashboardAnalyticsDto all = await facade.GetAsync("all");

            Assert.AreEqual(48, day.Bandwidth.Count);
            Assert.AreEqual(1_800, day.Range.BucketSeconds);
            Assert.IsNotNull(day.Comparison);
            Assert.IsFalse(day.Range.Coverage.IsComplete);
            Assert.IsFalse(day.Comparison.Coverage.IsComplete);
            Assert.IsTrue(all.Bandwidth.Count is >= 1 and <= 60);
            Assert.IsTrue(all.Range.Coverage.IsComplete);
            Assert.IsNull(all.Comparison);
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
    public async Task TransferTimeline_ReturnsLiveRowsWithExplicitUnavailableRetainedCoverage()
    {
        var persistence = new PersistenceCoordinator(Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions { Enabled = false },
        }));
        var live = new EngineStateStore();
        Guid transferId = Guid.NewGuid();
        DateTimeOffset requested = DateTimeOffset.UnixEpoch.AddHours(2);
        live.UpdateUploadTransfer(new Sockseek.Core.Transfers.Uploads.UploadTransferSnapshot(
            transferId,
            Revision: 1,
            Username: "peer",
            RemotePath: @"Share\Live.flac",
            SizeBytes: 100,
            RequestedAtUtc: requested,
            State: Sockseek.Core.Transfers.Uploads.UploadTransferState.InProgress,
            FailureReason: Sockseek.Core.Transfers.Uploads.UploadFailureReason.None,
            CancellationSource: Sockseek.Core.Transfers.Uploads.UploadCancellationSource.None,
            BytesTransferred: 50,
            BytesPerSecond: 25,
            LastProgressAtUtc: requested.AddSeconds(1),
            Attempt: new Sockseek.Core.Transfers.Uploads.UploadAttemptSnapshot(
                Guid.NewGuid(), 1, requested, null, 50, 25),
            FinishedAtUtc: null,
            File: new TransferFileMetadataSnapshot(
                "Live.flac", 100, "flac", 900, 24, 96_000, 180),
            GroupRef: @"Share"));

        var facade = new HistoricalQueryFacade(live, supervisor: null!, persistence);
        TransferTimelinePageDto page = await facade.GetTransfersAsync(
            null, 10, null, null, null, null, null, null, null, null, null);

        Assert.AreEqual(TransferRetainedCoverageState.Unavailable, page.RetainedCoverage.State);
        Assert.AreEqual("PersistenceDisabled", page.RetainedCoverage.Reason);
        TransferHistoryDto row = page.Items.Single();
        Assert.AreEqual(transferId, row.TransferId);
        Assert.AreEqual(requested, row.CreatedAtUtc);
        Assert.AreEqual("Live.flac", row.File?.Name);
        Assert.AreEqual(@"Share", row.GroupRef);
    }

    [TestMethod]
    public void TransferTimelineComposition_OverlaysLiveRowsAndDeduplicatesAcrossSources()
    {
        Guid overlap = Guid.Parse("00000000-0000-0000-0000-000000000003");
        Guid older = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Guid queued = Guid.Parse("00000000-0000-0000-0000-000000000004");
        DateTimeOffset origin = DateTimeOffset.UnixEpoch;

        DateTimeOffset completedAt = origin.AddSeconds(5);
        TransferHistoryDto retainedOverlap = TimelineRow(overlap, origin.AddSeconds(3), "Retained") with
        {
            CompletedAtUtc = completedAt,
            FailureMessage = "retained-detail",
        };
        TransferTimelinePageDto page = HistoricalQueryFacade.ComposeTransferTimeline(
            [retainedOverlap, TimelineRow(older, origin.AddSeconds(2), "Completed")],
            [TimelineRow(overlap, origin.AddSeconds(3), "InProgress")],
            [TimelineRow(queued, origin.AddSeconds(4), "Queued")],
            limit: 2,
            retainedHasMore: false,
            queueHasMore: false,
            new TransferRetainedCoverageDto(TransferRetainedCoverageState.Available));

        CollectionAssert.AreEqual(
            new[] { queued, overlap },
            page.Items.Select(item => item.TransferId).ToArray());
        Assert.AreEqual("InProgress", page.Items.Single(item => item.TransferId == overlap).State);
        Assert.AreEqual(completedAt, page.Items.Single(item => item.TransferId == overlap).CompletedAtUtc);
        Assert.AreEqual("retained-detail", page.Items.Single(item => item.TransferId == overlap).FailureMessage);
        Assert.AreEqual(2, page.Items.Select(item => item.TransferId).Distinct().Count());
        Assert.IsNotNull(page.NextCursor);
    }

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

    private static TransferHistoryDto TimelineRow(
        Guid id,
        DateTimeOffset createdAtUtc,
        string state)
        => new(
            id, null, null, "Upload", "SoulseekPeer", "peer", @"Share\File.bin", null,
            state, "None", 100, 0, 0, createdAtUtc, null, "None", null,
            TransferCancellationSource.None, Revision: 1);

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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Sockseek.Api;
using Sockseek.Server;
using Soulseek;

namespace Tests.Server;

[TestClass]
public class EngineStateStoreTests
{
    [TestMethod]
    public void SnapshotAndEverySubsequentDelta_MatchCurrentServerProjection()
    {
        var server = new EngineStateStore();
        var client = new DaemonClientStore();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var scope = StateStreamScopeDto.Workflow(song.WorkflowId);
        client.ApplySnapshot(server.GetWorkflowSnapshot(song.WorkflowId));
        server.StateBatchPublished += batch =>
        {
            if (batch.Scope == scope)
                Assert.AreEqual(DaemonClientApplyStatus.Applied, client.Apply(batch).Status);
        };

        Register(server, song);
        AssertClientMatchesProjection(server, client, song.WorkflowId);

        song.UpdateActivity(JobActivityPhase.Downloading);
        UpdateState(server, song);
        AssertClientMatchesProjection(server, client, song.WorkflowId);

        DownloadStateChanged(server, song, TransferStates.InProgress);
        AssertClientMatchesProjection(server, client, song.WorkflowId);

        song.SetDone("C:/music/track.mp3");
        UpdateState(server, song);
        AssertClientMatchesProjection(server, client, song.WorkflowId);
    }

    [TestMethod]
    public async Task ConcurrentCompletions_PublishWorkflowBatchesInSequence()
    {
        var server = new EngineStateStore();
        var client = new DaemonClientStore();
        var first = new SongJob(new SongQuery { Artist = "Artist", Title = "First" });
        var second = new SongJob(new SongQuery { Artist = "Artist", Title = "Second" })
        {
            WorkflowId = first.WorkflowId,
        };
        Register(server, first);
        Register(server, second);

        var scope = StateStreamScopeDto.Workflow(first.WorkflowId);
        client.ApplySnapshot(server.GetWorkflowSnapshot(first.WorkflowId));
        var statuses = new List<DaemonClientApplyStatus>();
        server.StateBatchPublished += batch =>
        {
            if (batch.Scope != scope)
                return;

            lock (statuses)
                statuses.Add(client.Apply(batch).Status);
        };

        using var firstUpsertEntered = new ManualResetEventSlim();
        using var releaseFirstUpsert = new ManualResetEventSlim();
        server.JobUpserted += summary =>
        {
            if (summary.JobId != first.Id)
                return;

            firstUpsertEntered.Set();
            releaseFirstUpsert.Wait(TimeSpan.FromSeconds(5));
        };

        var firstCompletion = Task.Run(() => ExecutionCompleted(server, first));
        var secondCompletion = Task.CompletedTask;
        try
        {
            Assert.IsTrue(firstUpsertEntered.Wait(TimeSpan.FromSeconds(5)));
            secondCompletion = Task.Run(() => ExecutionCompleted(server, second));
            await secondCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseFirstUpsert.Set();
            await Task.WhenAll(firstCompletion, secondCompletion).WaitAsync(TimeSpan.FromSeconds(5));
        }

        lock (statuses)
        {
            CollectionAssert.AreEqual(
                new[] { DaemonClientApplyStatus.Applied, DaemonClientApplyStatus.Applied },
                statuses.ToArray());
        }
        AssertClientMatchesProjection(server, client, first.WorkflowId);
    }

    [TestMethod]
    public void SongPayload_IncludesSnapshotProgress()
    {
        var store = new EngineStateStore();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
        {
            BytesTransferred = 25,
            FileSize = 100,
        };
        song.UpdateActivity(JobActivityPhase.Downloading);

        Register(store, song);

        var payload = store.GetJobDetail(song.Id)?.Payload as SongJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(25, payload.BytesTransferred);
        Assert.AreEqual(100, payload.TotalBytes);
        Assert.AreEqual(25d, payload.ProgressPercent);
    }

    [TestMethod]
    public void SongPayload_IncludesDownloadSource()
    {
        var store = new EngineStateStore();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        song.SetDone("C:/music/track.mp3", downloadSource: SongDownloadSource.Fallback);

        Register(store, song);

        var payload = store.GetJobDetail(song.Id)?.Payload as SongJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(ServerSongDownloadSource.Fallback, payload.DownloadSource);
    }

    [TestMethod]
    public void JobSummary_ExposesLifecycleActivityAndTerminalOutcome()
    {
        var store = new EngineStateStore();
        var until = DateTimeOffset.UtcNow.AddSeconds(30);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        song.UpdateActivity(JobActivityPhase.SearchRateLimited, until);

        Register(store, song);

        var summary = store.GetJobSummary(song.Id);
        Assert.IsNotNull(summary);
        Assert.AreEqual(ServerJobLifecycleState.Running, summary.LifecycleState);
        Assert.AreEqual(ServerJobActivityPhase.SearchRateLimited, summary.ActivityPhase);
        Assert.AreEqual(until, summary.ActivityUntilUtc);
        Assert.AreEqual(ServerJobTerminalOutcome.None, summary.TerminalOutcome);
    }

    [TestMethod]
    public void JobDiscoveryChanged_UpdatesSummaryDiscoveryCounts()
    {
        var store = new EngineStateStore();
        var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
        JobSummaryDto? published = null;
        store.JobUpserted += summary => published = summary;

        Register(store, album);

        album.Discovery = new DiscoverySummary { RawResultCount = 123, LockedFileCount = 4 };
        DiscoveryChanged(store, album);

        var summary = store.GetJobSummary(album.Id);
        Assert.IsNotNull(summary);
        Assert.AreEqual(123, summary.DiscoveryRawResultCount);
        Assert.AreEqual(4, summary.DiscoveryLockedFileCount);
        Assert.IsNotNull(published);
        Assert.AreEqual(123, published.DiscoveryRawResultCount);
        Assert.AreEqual(4, published.DiscoveryLockedFileCount);
    }

    [TestMethod]
    public void GetJobs_FiltersByLifecycleAndTerminalOutcome()
    {
        var store = new EngineStateStore();
        var running = new SongJob(new SongQuery { Title = "Running" });
        var done = new SongJob(new SongQuery { Title = "Done" });
        var failed = new SongJob(new SongQuery { Title = "Failed" });
        running.UpdateActivity(JobActivityPhase.Downloading);
        done.SetDone();
        failed.Fail(JobFailureReason.Other);

        Register(store, running);
        Register(store, done);
        Register(store, failed);

        var runningJobs = store.GetJobs(new JobQuery(
            ServerJobLifecycleState.Running,
            TerminalOutcome: null,
            Kind: null,
            WorkflowId: null,
            IncludeAll: true));
        CollectionAssert.AreEquivalent(new[] { running.Id }, runningJobs.Select(job => job.JobId).ToArray());

        var failedJobs = store.GetJobs(new JobQuery(
            ServerJobLifecycleState.Terminal,
            ServerJobTerminalOutcome.Failed,
            Kind: null,
            WorkflowId: null,
            IncludeAll: true));
        CollectionAssert.AreEquivalent(new[] { failed.Id }, failedJobs.Select(job => job.JobId).ToArray());
    }

    [TestMethod]
    public void AggregatePayload_IncludesSongOutcomeCounts()
    {
        var store = new EngineStateStore();
        var aggregate = new AggregateJob(new SongQuery { Artist = "Artist" });
        var s1 = new SongJob(new SongQuery { Title = "One" }); s1.SetDone();
        var s2 = new SongJob(new SongQuery { Title = "Two" }); s2.Fail(JobFailureReason.Other);
        var s3 = new SongJob(new SongQuery { Title = "Three" }); s3.UpdateActivity(JobActivityPhase.Downloading);
        aggregate.Songs.Add(s1);
        aggregate.Songs.Add(s2);
        aggregate.Songs.Add(s3);

        Register(store, aggregate);

        var payload = store.GetJobDetail(aggregate.Id)?.Payload as AggregateJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(3, payload.SongCount);
        Assert.AreEqual(2, payload.CompletedSongCount);
        Assert.AreEqual(1, payload.SucceededSongCount);
        Assert.AreEqual(1, payload.FailedSongCount);
    }

    [TestMethod]
    public void JobListPayload_IncludesDirectChildOutcomeCounts()
    {
        var store = new EngineStateStore();
        var list = new JobList("batch");
        var j1 = new SongJob(new SongQuery { Title = "One" }); j1.SetDone();
        var j2 = new SongJob(new SongQuery { Title = "Two" }); j2.Fail(JobFailureReason.Other);
        var j3 = new SongJob(new SongQuery { Title = "Three" }); j3.UpdateActivity(JobActivityPhase.Searching);
        list.Add(j1);
        list.Add(j2);
        list.Add(j3);

        Register(store, list);

        var payload = store.GetJobDetail(list.Id)?.Payload as JobListPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(3, payload.Count);
        Assert.AreEqual(1, payload.ActiveJobCount);
        Assert.AreEqual(2, payload.CompletedJobCount);
        Assert.AreEqual(1, payload.SucceededJobCount);
        Assert.AreEqual(1, payload.FailedJobCount);
    }

    [TestMethod]
    public void JobListSummary_UsesCoreRunningState()
    {
        var store = new EngineStateStore();
        var list = new JobList("batch");
        var child = new SongJob(new SongQuery { Title = "One" });
        list.Add(child);

        Register(store, list);
        Register(store, child, list);

        list.UpdateActivity(JobActivityPhase.RunningChildren);
        UpdateState(store, list);

        var summary = store.GetJobSummary(list.Id);
        Assert.IsNotNull(summary);
        Assert.AreEqual(ServerJobLifecycleState.Running, summary.LifecycleState);
        Assert.AreEqual(ServerJobActivityPhase.RunningChildren, summary.ActivityPhase);
        Assert.AreEqual(ServerJobTerminalOutcome.None, summary.TerminalOutcome);
    }

    [TestMethod]
    public void WorkflowSummary_TracksRootsTitleAndCountsAcrossJobUpdates()
    {
        var store = new EngineStateStore();
        var list = new JobList("batch");
        var done = new SongJob(new SongQuery { Title = "Done" }) { WorkflowId = list.WorkflowId };
        var failed = new SongJob(new SongQuery { Title = "Failed" }) { WorkflowId = list.WorkflowId };

        Register(store, list);
        Register(store, done, list);
        Register(store, failed, list);

        var initial = store.GetWorkflowSummary(list.WorkflowId);
        Assert.IsNotNull(initial);
        Assert.AreEqual("batch", initial.Title);
        CollectionAssert.AreEqual(new[] { list.Id }, initial.RootJobIds.ToArray());
        Assert.AreEqual(ServerWorkflowState.Active, initial.State);
        Assert.AreEqual(3, initial.ActiveJobCount);
        Assert.AreEqual(0, initial.CompletedJobCount);
        Assert.AreEqual(0, initial.FailedJobCount);

        done.SetDone();
        UpdateState(store, done);
        failed.Fail(JobFailureReason.Other);
        UpdateState(store, failed);
        list.SetDone();
        UpdateState(store, list);

        var terminal = store.GetWorkflowSummary(list.WorkflowId);
        Assert.IsNotNull(terminal);
        Assert.AreEqual(ServerWorkflowState.Failed, terminal.State);
        Assert.AreEqual(0, terminal.ActiveJobCount);
        Assert.AreEqual(3, terminal.CompletedJobCount);
        Assert.AreEqual(1, terminal.FailedJobCount);
    }

    [TestMethod]
    public void WorkflowSummaryCache_MatchesBruteForceSnapshotAcrossMutations()
    {
        var store = new EngineStateStore();
        var extract = new ExtractJob("input.csv", InputType.CSV)
        {
            AutoProcessResult = true,
        };
        var list = new JobList("batch") { WorkflowId = extract.WorkflowId };
        var done = new SongJob(new SongQuery { Title = "Done" }) { WorkflowId = extract.WorkflowId };
        var failed = new SongJob(new SongQuery { Title = "Failed" }) { WorkflowId = extract.WorkflowId };
        var projected = new SongJob(new SongQuery { Title = "Projected" }) { WorkflowId = extract.WorkflowId };
        list.Add(done);
        list.Add(failed);
        list.Add(projected);
        extract.Result = list;

        Register(store, extract);
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);

        ResultCreated(store, extract, list);
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);

        Register(store, list);
        Register(store, done, list);
        Register(store, failed, list);
        Register(store, projected, list);
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);

        store.SetSourceJob(done.Id, extract.Id);
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);

        done.SetDone();
        UpdateState(store, done);
        failed.Fail(JobFailureReason.Other);
        UpdateState(store, failed);
        ExecutionCompleted(store, projected);
        list.SetDone();
        UpdateState(store, list);
        extract.SetDone();
        UpdateState(store, extract);
        AssertWorkflowSummaryMatchesBruteForceSnapshot(store, extract.WorkflowId);
    }

    [TestMethod]
    public void AlbumAggregatePayload_CountsProducedAlbumDescendants()
    {
        var store = new EngineStateStore();
        var aggregate = new AlbumAggregateJob(new AlbumQuery { Artist = "Artist" });
        var list = new JobList("albums");
        var firstAlbum = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "One" });
        var secondAlbum = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Two" });
        list.Add(firstAlbum);
        list.Add(secondAlbum);

        Register(store, aggregate);
        Register(store, list, aggregate);
        Register(store, firstAlbum, list);
        Register(store, secondAlbum, list);

        var payload = store.GetJobDetail(aggregate.Id)?.Payload as AlbumAggregateJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(2, payload.ResultCount);
    }

    [TestMethod]
    public void AlbumDetail_TracksReflectCurrentTransferState()
    {
        var store = new EngineStateStore();
        var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
        var song1 = new SongJob(new SongQuery { Title = "One" });
        var song2 = new SongJob(new SongQuery { Title = "Two" });
        song1.UpdateActivity(JobActivityPhase.Downloading);
        song2.UpdateActivity(JobActivityPhase.Downloading);

        Register(store, album);
        Register(store, song1, album);
        Register(store, song2, album);

        // Fire transfer state after registration — the cached record payload won't have it
        DownloadStateChanged(store, song1, TransferStates.InProgress);
        DownloadStateChanged(store, song2, TransferStates.Queued | TransferStates.Remotely);

        var tracks = (store.GetJobDetail(album.Id)?.Payload as AlbumJobPayloadDto)?.Tracks;
        Assert.IsNotNull(tracks);
        Assert.AreEqual(2, tracks.Count);
        Assert.AreEqual(TransferStates.InProgress.ToString(), tracks[0].TransferState);
        Assert.AreEqual((TransferStates.Queued | TransferStates.Remotely).ToString(), tracks[1].TransferState);
    }


    [TestMethod]
    public void ResultDraft_RoundTripsSourceMutationProvenance()
    {
        var store = new EngineStateStore();
        var album = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
        {
            ItemNumber = 2,
            LineNumber = 4,
            SourceMutation = SourceMutation.ClearCsvRow("input.csv", 4, 2, 3),
        };
        var extract = new ExtractJob("input.csv", InputType.CSV)
        {
            AutoProcessResult = false,
            Result = album,
        };

        Register(store, extract);

        var payload = store.GetJobDetail(extract.Id)?.Payload as ExtractJobPayloadDto;
        Assert.IsNotNull(payload);
        var draft = payload.ResultDraft as AlbumJobDraftDto;
        Assert.IsNotNull(draft);
        Assert.IsNotNull(draft.Provenance);
        Assert.AreEqual(2, draft.Provenance.ItemNumber);
        Assert.AreEqual(4, draft.Provenance.LineNumber);
        Assert.AreEqual(nameof(SourceMutationKind.ClearCsvRow), draft.Provenance.SourceMutation?.Kind);
        Assert.AreEqual("input.csv", draft.Provenance.SourceMutation?.Source);
        Assert.AreEqual(3, draft.Provenance.SourceMutation?.CsvColumnCount);

        var roundTripped = JobRequestMapper.CreateJob(draft);
        Assert.AreEqual(2, roundTripped.ItemNumber);
        Assert.AreEqual(4, roundTripped.LineNumber);
        Assert.AreEqual(SourceMutationKind.ClearCsvRow, roundTripped.SourceMutation?.Kind);
        Assert.AreEqual("input.csv", roundTripped.SourceMutation?.Source);
        Assert.AreEqual(3, roundTripped.SourceMutation?.CsvColumnCount);
    }

    [TestMethod]
    public void AutoProcessedExtractPayload_DoesNotInlineResultDraft()
    {
        var store = new EngineStateStore();
        var list = new JobList("batch");
        var extract = new ExtractJob("input.csv", InputType.CSV)
        {
            AutoProcessResult = true,
            Result = list,
        };
        list.WorkflowId = extract.WorkflowId;
        list.Add(new SongJob(new SongQuery { Artist = "Artist", Title = "One" }) { WorkflowId = list.WorkflowId });

        Register(store, extract);

        var payload = store.GetJobDetail(extract.Id)?.Payload as ExtractJobPayloadDto;
        Assert.IsNotNull(payload);
        Assert.AreEqual(list.Id, payload.ResultJobId);
        Assert.IsNull(payload.ResultDraft);
    }

    [TestMethod]
    public void AutoProcessedExtractResult_GetsDisplayIdBeforeRegistration()
    {
        var store = new EngineStateStore();
        var extract = new ExtractJob("input.csv", InputType.CSV)
        {
            AutoProcessResult = true,
        };
        var result = new JobList("batch") { WorkflowId = extract.WorkflowId };
        extract.Result = result;

        Register(store, extract);
        ResultCreated(store, extract, result);

        var resultSummary = store.GetJobSummary(result.Id);
        Assert.IsNotNull(resultSummary);
        Assert.AreNotEqual(0, resultSummary.DisplayId);
    }

    private static void Register(EngineStateStore store, Job job, Job? parent = null)
    {
        job.EnsureDisplayId();
        parent?.EnsureDisplayId();
        typeof(EngineStateStore)
            .GetMethod("OnJobRegistered", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobRegisteredChange(1, DateTimeOffset.UtcNow, Snapshot(job), parent?.Id, null)]);
    }

    private static void UpdateState(EngineStateStore store, Job job)
    {
        typeof(EngineStateStore)
            .GetMethod("OnJobStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobStateChangedChange(1, DateTimeOffset.UtcNow, Snapshot(job))]);
    }

    private static void DiscoveryChanged(EngineStateStore store, Job job)
    {
        typeof(EngineStateStore)
            .GetMethod("OnJobDiscoveryChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobDiscoveryChangedChange(1, DateTimeOffset.UtcNow, Snapshot(job))]);
    }

    private static void ResultCreated(EngineStateStore store, ExtractJob job, Job result)
    {
        job.EnsureDisplayId();
        result.EnsureDisplayId();
        typeof(EngineStateStore)
            .GetMethod("OnJobResultCreated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobResultCreatedChange(1, DateTimeOffset.UtcNow, Snapshot(job), Snapshot(result))]);
    }

    private static void ExecutionCompleted(EngineStateStore store, Job job)
    {
        typeof(EngineStateStore)
            .GetMethod("OnJobExecutionCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new JobExecutionCompletedChange(1, DateTimeOffset.UtcNow, Snapshot(job))]);
    }

    private static void DownloadStateChanged(EngineStateStore store, SongJob song, TransferStates state)
    {
        typeof(EngineStateStore)
            .GetMethod("OnDownloadStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [new DownloadStateChangedChange(1, DateTimeOffset.UtcNow, Snapshot(song), Transfer(song, state))]);
    }

    private static JobSnapshot Snapshot(Job job)
        => CoreSnapshotFactory.CreateJob(job, revision: 1);

    private static TransferSnapshot Transfer(SongJob song, TransferStates state)
    {
        var response = new SearchResponse("user", 1, true, 100_000, 0, []);
        var file = new Soulseek.File(1, $"{song.Query.Title}.mp3", song.FileSize > 0 ? song.FileSize : 100, ".mp3");
        var candidate = new FileCandidate(response, file);
        return CoreSnapshotFactory.CreateDownloadTransfer(
            Guid.NewGuid(),
            song,
            candidate,
            $"C:/downloads/{song.Query.Title}.mp3",
            revision: 1,
            state: state.ToString(),
            bytesTransferred: song.BytesTransferred,
            totalBytes: song.FileSize > 0 ? song.FileSize : file.Size,
            attemptCount: 0);
    }

    private static void AssertWorkflowSummaryMatchesBruteForceSnapshot(EngineStateStore store, Guid workflowId)
    {
        var cached = store.GetWorkflowSummary(workflowId);
        var detail = store.GetWorkflow(workflowId, includeAll: true);

        Assert.IsNotNull(cached);
        Assert.IsNotNull(detail);

        var expected = BuildBruteForceWorkflowSummary(workflowId, detail.Jobs);
        Assert.AreEqual(expected.WorkflowId, cached.WorkflowId);
        Assert.AreEqual(expected.Title, cached.Title);
        Assert.AreEqual(expected.State, cached.State);
        CollectionAssert.AreEqual(expected.RootJobIds.ToArray(), cached.RootJobIds.ToArray());
        Assert.AreEqual(expected.ActiveJobCount, cached.ActiveJobCount);
        Assert.AreEqual(expected.FailedJobCount, cached.FailedJobCount);
        Assert.AreEqual(expected.CompletedJobCount, cached.CompletedJobCount);
    }

    private static WorkflowSummaryDto BuildBruteForceWorkflowSummary(Guid workflowId, IReadOnlyList<JobSummaryDto> jobs)
    {
        var ordered = jobs.OrderBy(job => job.DisplayId).ToList();
        string title = ordered.FirstOrDefault(job => !string.IsNullOrWhiteSpace(job.ItemName))?.ItemName
            ?? ordered.First().QueryText
            ?? ordered.First().Kind.ToWireString();

        int active = ordered.Count(job => job.LifecycleState != ServerJobLifecycleState.Terminal);
        int failed = ordered.Count(IsFailed);
        int completed = ordered.Count - active;
        var state = active > 0 ? ServerWorkflowState.Active
            : failed > 0 ? ServerWorkflowState.Failed
            : ServerWorkflowState.Completed;

        return new WorkflowSummaryDto(
            workflowId,
            title,
            state,
            ordered.Where(job => job.ParentJobId == null).Select(job => job.JobId).ToList(),
            active,
            failed,
            completed);
    }

    private static bool IsFailed(JobSummaryDto job)
        => job.TerminalOutcome is ServerJobTerminalOutcome.Failed
            or ServerJobTerminalOutcome.Cancelled
            or ServerJobTerminalOutcome.PartialSuccess
            || (job.TerminalOutcome == ServerJobTerminalOutcome.Skipped
                && job.SkipReason != ServerJobSkipReason.AlreadyExists);

    private static void AssertClientMatchesProjection(
        EngineStateStore server,
        DaemonClientStore client,
        Guid workflowId)
    {
        var expected = server.GetWorkflowSnapshot(workflowId);
        Assert.AreEqual(expected.Position, client.GetPosition(expected.Scope));
        CollectionAssert.AreEqual(
            expected.Workflows.Select(row => row.Summary).ToArray(),
            client.GetWorkflows().Where(row => row.WorkflowId == workflowId).ToArray());
        CollectionAssert.AreEqual(
            expected.Jobs.Select(row => row.ToSummary()).OrderBy(row => row.JobId).ToArray(),
            client.GetWorkflowJobs(workflowId).OrderBy(row => row.JobId).ToArray());
        CollectionAssert.AreEqual(
            expected.Transfers.OrderBy(row => row.TransferId).ToArray(),
            client.GetTransfers()
                .Where(row => row.Identity.WorkflowId == workflowId)
                .OrderBy(row => row.TransferId)
                .ToArray());
        foreach (var search in expected.Searches)
            Assert.AreEqual(search, client.GetSearchState(search.JobId));
    }
}

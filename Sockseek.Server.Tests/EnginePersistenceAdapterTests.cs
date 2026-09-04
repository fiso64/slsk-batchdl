using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Persistence.Write;
using Sockseek.Persistence.Read;
using Sockseek.Api;
using Sockseek.Server.Persistence;
using Soulseek;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class EnginePersistenceAdapterTests
{
    [TestMethod]
    public void AdapterClassifiesEveryConcreteCoreChangeExplicitly()
    {
        var concreteChanges = typeof(CoreChange).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(CoreChange).IsAssignableFrom(type))
            .ToHashSet();

        CollectionAssert.AreEquivalent(
            concreteChanges.Select(type => type.FullName).OrderBy(name => name).ToArray(),
            EnginePersistenceAdapter.HandledChangeTypes.Select(type => type.FullName).OrderBy(name => name).ToArray());
    }

    [TestMethod]
    public void AdapterMapsRelationshipsWithoutRuntimeLookups()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        var adapter = new EnginePersistenceAdapter(Guid.NewGuid(), sink);
        adapter.Attach(events);
        var parent = new JobList();
        var source = new SearchJob("source");
        var child = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
        {
            WorkflowId = parent.WorkflowId,
        };

        Invoke(events, "RaiseJobRegistered", child, parent.Id, source.Id);

        var mutation = sink.Mutations.OfType<JobPersistenceMutation>().Single();
        Assert.AreEqual(child.Id, mutation.JobId);
        Assert.AreEqual(parent.Id, mutation.ParentJobId);
        Assert.AreEqual(source.Id, mutation.SourceJobId);
        Assert.AreEqual(PersistenceMutationPriority.Structural, mutation.Priority);
        Assert.IsFalse(mutation.PayloadJson?.Contains("CancellationToken", StringComparison.Ordinal) == true);
    }

    [TestMethod]
    public void HistoricalSummary_MapsRawSearchDiscoveryCounts()
    {
        var persisted = new PersistedJob(
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            null,
            null,
            null,
            "Search",
            "Terminal",
            "None",
            null,
            "Succeeded",
            "None",
            "None",
            "None",
            null,
            null,
            null,
            "query",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1,
            1,
            null,
            DiscoveryPublicFileCount: 7,
            DiscoveryLockedFileCount: 3,
            DiscoveryObservedPeerCount: 2);

        JobSummaryDto summary = HistoricalJobDtoMapper.ToSummary(persisted);

        Assert.AreEqual(7, summary.DiscoveryRawResultCount);
        Assert.AreEqual(7, summary.DiscoveryPublicFileCount);
        Assert.AreEqual(3, summary.DiscoveryLockedFileCount);
        Assert.AreEqual(2, summary.DiscoveryObservedPeerCount);
    }

    [TestMethod]
    public void AdapterAndHistoricalMapper_RoundTripEveryCurrentJobPayloadKind()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        new EnginePersistenceAdapter(Guid.NewGuid(), sink).Attach(events);
        var retrieve = new RetrieveFolderJob(new PeerDirectoryIdentity("peer", @"Music\Artist\Album"))
        {
            NewFilesFoundCount = 4,
            RetrievalOutcome = FolderRetrievalOutcome.Completed,
        };
        var exactTarget = new PeerFileTarget(
            new PeerFileIdentity("Exact Peer", @"Share\Folder\File.bin"),
            42,
            ".bin");
        var exactSong = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
        {
            ExactTarget = exactTarget,
        };
        var remoteFile = new RemoteFileJob(exactTarget, new RelativeOutputPath(["Folder", "File.bin"]));
        var resolvedPlan = new DirectoryTransferPlan("Selection", [
            new DirectoryTransferEntry(exactTarget, ["Folder"]),
        ]);
        var remoteDirectory = new RemoteDirectoryJob(
            new RemoteDirectorySource.Resolved(resolvedPlan));
        var jobs = new (Job Job, Type PayloadType)[]
        {
            (new GenericTestJob("generic text"), typeof(GenericJobPayloadDto)),
            (new ExtractJob("https://example.test/list"), typeof(ExtractJobPayloadDto)),
            (new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" }), typeof(SearchJobPayloadDto)),
            (new SearchJob(new AlbumQuery { Artist = "Artist", Album = "Album" }), typeof(SearchJobPayloadDto)),
            (exactSong, typeof(SongJobPayloadDto)),
            (new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" }), typeof(AlbumJobPayloadDto)),
            (new AggregateJob(new SongQuery { Artist = "Artist", Title = "Track" }), typeof(AggregateJobPayloadDto)),
            (new AlbumAggregateJob(new AlbumQuery { Artist = "Artist", Album = "Album" }), typeof(AlbumAggregateJobPayloadDto)),
            (new JobList("list", [new SongJob(new SongQuery { Title = "child" })]), typeof(JobListPayloadDto)),
            (retrieve, typeof(RetrieveFolderJobPayloadDto)),
            (remoteFile, typeof(RemoteFileJobPayloadDto)),
            (remoteDirectory, typeof(RemoteDirectoryJobPayloadDto)),
        };

        foreach (var (job, _) in jobs)
            Invoke(events, "RaiseJobRegistered", job, null, null);

        var mutations = sink.Mutations.OfType<JobPersistenceMutation>().ToDictionary(mutation => mutation.JobId);
        foreach (var (job, expectedPayloadType) in jobs)
        {
            var mutation = mutations[job.Id];
            var payload = HistoricalJobDtoMapper.ToPayload(ToPersistedJob(mutation));
            Assert.AreEqual(expectedPayloadType, payload.GetType(), $"Failed payload kind {mutation.Kind}.");
        }

        var retrievePayload = (RetrieveFolderJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[retrieve.Id]));
        Assert.AreEqual("peer", retrievePayload.Username);
        Assert.AreEqual(@"Music\Artist\Album", retrievePayload.FolderPath);
        Assert.AreEqual(4, retrievePayload.NewFilesFoundCount);
        Assert.AreEqual(ServerFolderRetrievalOutcome.Completed, retrievePayload.RetrievalOutcome);

        var trackSearch = jobs.Select(pair => pair.Job).OfType<SearchJob>()
            .Single(job => job.DefaultFileProjection != null);
        var trackPayload = (SearchJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[trackSearch.Id]));
        Assert.IsNotNull(trackPayload.SongQuery);
        Assert.AreEqual("Track", trackPayload.SongQuery.Title);

        var albumSearch = jobs.Select(pair => pair.Job).OfType<SearchJob>()
            .Single(job => job.DefaultFolderProjection != null);
        var albumPayload = (SearchJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[albumSearch.Id]));
        Assert.IsNotNull(albumPayload.AlbumQuery);
        Assert.AreEqual("Album", albumPayload.AlbumQuery.Album);

        var songPayload = (SongJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[exactSong.Id]));
        Assert.IsNotNull(songPayload.ExactTarget);
        Assert.AreEqual("Exact Peer", songPayload.ExactTarget.Username);
        Assert.AreEqual(@"Share\Folder\File.bin", songPayload.ExactTarget.Filename);
        Assert.IsNull(songPayload.ResolvedUsername);

        var filePayload = (RemoteFileJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[remoteFile.Id]));
        Assert.AreEqual("Exact Peer", filePayload.Target.Username);
        CollectionAssert.AreEqual(new[] { "Folder", "File.bin" }, filePayload.OutputPathComponents.ToArray());

        var directoryPayload = (RemoteDirectoryJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[remoteDirectory.Id]));
        Assert.AreEqual(RemoteDirectorySourceKindDto.Resolved, directoryPayload.SourceKind);
        Assert.IsNull(directoryPayload.SourceUsername);
        Assert.IsNull(directoryPayload.SourceFolderPath);
        Assert.AreEqual("planned", directoryPayload.Directory.Phase);
    }

    [TestMethod]
    public void AdapterCombinesFinalAttemptWithTransferTerminalBarrier()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        var adapter = new EnginePersistenceAdapter(Guid.NewGuid(), sink);
        adapter.Attach(events);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var file = new Soulseek.File(1, @"Music\Artist\Track.mp3", 100, ".mp3");
        var response = new SearchResponse("user", 1, true, 1_000, 0, [file]);
        var candidate = SoulseekSearchAdapter.ToFileCandidate(response, file);
        Guid transferId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();

        Invoke(events, "RaiseDownloadStarted", transferId, song, candidate.Target, "C:/downloads/Track.mp3");
        Invoke(events, "RaiseTransferAttemptStarted", transferId, attemptId, 1, song, candidate.Target, "C:/downloads/Track.mp3", "C:/downloads/Track.mp3.incomplete");
        song.BytesTransferred = 40;
        Invoke(events, "RaiseDownloadProgress", transferId, song, candidate.Target, "C:/downloads/Track.mp3", 40L, 100L);
        song.BytesTransferred = 100;
        Invoke(events, "RaiseTransferAttemptCompleted", transferId, attemptId, 1, song, candidate.Target, "C:/downloads/Track.mp3");
        Invoke(events, "RaiseTransferCompleted", transferId, song, candidate.Target, "C:/downloads/Track.mp3", 100L, 1);

        var terminal = sink.Mutations.OfType<TransferTerminalPersistenceMutation>().Single();
        Assert.AreEqual(transferId, terminal.Transfer.TransferId);
        Assert.AreEqual("Succeeded", terminal.Transfer.TerminalOutcome);
        Assert.IsNotNull(terminal.FinalAttempt);
        Assert.AreEqual(attemptId, terminal.FinalAttempt.AttemptId);
        Assert.AreEqual("Completed", terminal.FinalAttempt.State);
        Assert.AreEqual("user", terminal.FinalAttempt.SourceUsername);
        Assert.AreEqual(@"Music\Artist\Track.mp3", terminal.FinalAttempt.SourcePath);
        Assert.AreEqual("C:/downloads/Track.mp3", terminal.FinalAttempt.OutputPath);
        Assert.AreEqual(2L, terminal.FinalAttempt.Revision);
        Assert.AreEqual(100L, terminal.FinalAttempt.AccountingObservations?.Single().CumulativeBytes);
        Assert.AreEqual(
            40L,
            sink.Mutations.OfType<TransferPersistenceMutation>()
                .Single(mutation => mutation.Priority == PersistenceMutationPriority.Progress)
                .AccountingObservations?.Single().CumulativeBytes);
        Assert.AreEqual(0, sink.Mutations.OfType<TransferAttemptPersistenceMutation>().Count(mutation => mutation.State == "Completed"));
    }

    [TestMethod]
    public void BlockedWriter_DoesNotBlockProgressCallbacks_AndProgressMemoryStaysBounded()
    {
        var options = new PersistenceWriterOptions
        {
            CriticalQueueCapacity = 2,
            OrdinaryQueueCapacity = 2,
            ProgressEntityCapacity = 2,
            DegradedProjectionCapacity = 2,
            SearchResultCapacityPerSearch = 2,
            SearchResultGlobalCapacity = 2,
            MaximumBatchSize = 2,
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var events = new DownloadEvents();
        new EnginePersistenceAdapter(Guid.NewGuid(), inbox).Attach(events);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var file = new Soulseek.File(1, @"Music\Artist\Track.mp3", 10_000, ".mp3");
        var response = new SearchResponse("user", 1, true, 1_000, 0, [file]);
        var candidate = SoulseekSearchAdapter.ToFileCandidate(response, file);
        Guid transferId = Guid.NewGuid();
        Invoke(events, "RaiseDownloadStarted", transferId, song, candidate.Target, "C:/downloads/Track.mp3");

        for (int i = 1; i <= 10_000; i++)
        {
            song.BytesTransferred = i;
            Invoke(events, "RaiseDownloadProgress", transferId, song, candidate.Target, "C:/downloads/Track.mp3", (long)i, 10_000L);
        }

        Assert.AreEqual(10_000L, song.BytesTransferred);
        Assert.AreEqual(1, inbox.ProgressCount);
        Assert.IsTrue(inbox.CriticalDepth <= options.CriticalQueueCapacity);
        Assert.AreEqual(0L, health.Snapshot(inbox).DroppedProgressCount);
    }

    [TestMethod]
    public void WorkflowRetirementOnlyReleasesAdapterBookkeepingAndDoesNotEnqueueABarrier()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        var adapter = new EnginePersistenceAdapter(Guid.NewGuid(), sink);
        adapter.Attach(events);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var file = new Soulseek.File(1, @"Music\Artist\Track.mp3", 100, ".mp3");
        var response = new SearchResponse("user", 1, true, 1_000, 0, [file]);
        var candidate = SoulseekSearchAdapter.ToFileCandidate(response, file);
        Guid transferId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();

        Invoke(events, "RaiseJobRegistered", song, null, null);
        Invoke(events, "RaiseDownloadStarted", transferId, song, candidate.Target, "C:/downloads/Track.mp3");
        Invoke(events, "RaiseTransferAttemptStarted", transferId, attemptId, 1, song, candidate.Target, "C:/downloads/Track.mp3", "C:/downloads/Track.mp3.incomplete");
        Invoke(events, "RaiseTransferAttemptCompleted", transferId, attemptId, 1, song, candidate.Target, "C:/downloads/Track.mp3");
        Assert.AreEqual((1, 1, 1), adapter.RetainedStateCounts);
        int mutationsBeforeRetirement = sink.Mutations.Count;

        Invoke(events, "RaiseWorkflowRetired", song.WorkflowId, 1);

        Assert.AreEqual(mutationsBeforeRetirement, sink.Mutations.Count);
        Assert.AreEqual((0, 0, 0), adapter.RetainedStateCounts);
        Assert.AreEqual((0, 0, 0, 0, 0), events.RetainedStateCounts);
    }

    [TestMethod]
    public async Task WorkflowHandoffIsPendingBeforeLaterRetirementObserversRun()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        var handoffs = new PersistenceHandoffTracker();
        var adapter = new EnginePersistenceAdapter(Guid.NewGuid(), sink, handoffs);
        adapter.Attach(events);
        var job = new SearchJob("query");
        Task? handoffObservedDuringRetirement = null;
        events.WorkflowRetired += _ =>
        {
            handoffObservedDuringRetirement = handoffs.WaitForJobAsync(
                job.Id,
                CancellationToken.None);
        };

        Invoke(events, "RaiseJobRegistered", job, null, null);
        job.SetDone();
        Invoke(events, "RaiseJobStateChanged", job);
        Invoke(events, "RaiseWorkflowRetired", job.WorkflowId, 1);

        Assert.IsNotNull(handoffObservedDuringRetirement);
        Assert.IsFalse(
            handoffObservedDuringRetirement.IsCompleted,
            "Persistence must own the retiring generation before later observers can remove live state.");
        handoffs.Committed(sink.Mutations);
        await handoffObservedDuringRetirement;
    }

    [TestMethod]
    public void RejectingPersistenceSinkCannotDelayOrPreventWorkflowRetirement()
    {
        var events = new DownloadEvents();
        var sink = new RejectingSink();
        var adapter = new EnginePersistenceAdapter(Guid.NewGuid(), sink);
        adapter.Attach(events);
        var job = new SearchJob("query");
        bool retired = false;
        events.WorkflowRetired += _ => retired = true;

        Invoke(events, "RaiseJobRegistered", job, null, null);
        Invoke(events, "RaiseWorkflowRetired", job.WorkflowId, 1);

        Assert.IsTrue(retired);
        Assert.IsTrue(sink.RejectedCount > 0);
        Assert.AreEqual((0, 0, 0), adapter.RetainedStateCounts);
        Assert.AreEqual((0, 0, 0, 0, 0), events.RetainedStateCounts);
    }

    private static void Invoke(DownloadEvents events, string methodName, params object?[] args)
        => typeof(DownloadEvents)
            .GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(events, args);

    private static PersistedJob ToPersistedJob(JobPersistenceMutation mutation)
        => new(
            mutation.JobId,
            mutation.DisplayId,
            mutation.WorkflowId,
            mutation.ParentJobId,
            mutation.SourceJobId,
            mutation.ResultJobId,
            mutation.Kind,
            mutation.LifecycleState,
            mutation.ActivityPhase,
            mutation.ActivityUntilUtc,
            mutation.TerminalOutcome,
            mutation.SkipReason,
            mutation.CancellationSource,
            mutation.FailureReason,
            mutation.FailureMessage,
            mutation.FailureDetail,
            mutation.ItemName,
            mutation.QueryText,
            mutation.OccurredAtUtc,
            mutation.OccurredAtUtc,
            null,
            mutation.Revision,
            mutation.PayloadSchemaVersion,
            mutation.PayloadJson);

    private sealed class GenericTestJob(string text) : Job
    {
        protected override bool DefaultCanBeSkipped => false;
        public override SongQuery? QueryTrack => null;
        public override string ToString(bool noInfo) => text;
    }

    private sealed class CapturingSink : IPersistenceMutationSink
    {
        public List<PersistenceMutation> Mutations { get; } = [];

        public bool TryEnqueue(PersistenceMutation mutation)
        {
            Mutations.Add(mutation);
            return true;
        }
    }

    private sealed class RejectingSink : IPersistenceMutationSink
    {
        public int RejectedCount { get; private set; }

        public bool TryEnqueue(PersistenceMutation mutation)
        {
            RejectedCount++;
            return false;
        }
    }
}

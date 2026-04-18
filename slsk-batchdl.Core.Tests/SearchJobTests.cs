using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using System.Collections.Concurrent;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;

namespace Tests.Unit
{
    [TestClass]
    public class SearchJobTests
    {
        [TestMethod]
        public void SearchSession_AddResponse_TracksRawResultsAndRevision()
        {
            var session = new SearchSession();
            var file = TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", length: 180);
            var response = new SearchResponse("User1", 1, true, 100, 2, [file]);
            var rawEvents = 0;
            session.RawResultReceived += (_, _, _) => rawEvents++;

            session.AddResponse(response);
            session.AddResponse(response);

            Assert.AreEqual(1, session.Results.Count, "Duplicate raw result keys should not be added twice.");
            Assert.AreEqual(1, session.Revision, "Revision should change only when a new raw result is added.");
            Assert.AreEqual(1, rawEvents, "Raw result event should fire only for newly added files.");
        }

        [TestMethod]
        public async Task SearchJob_ProjectionCache_ReusesSameRevisionAndInvalidatesOnNewRawResult()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", length: 180),
                ]),
            };
            var config = TestHelpers.CreateDefaultSettings().Download;
            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(new ClientTests.MockSoulseekClient(index), registry, registry, new EngineEvents(), 10, 10);
            var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });
            var factoryCalls = 0;

            await searcher.Search(job, config.Search, new ResponseData(), CancellationToken.None);

            var first = job.GetOrCreateProjection("count", snapshot =>
            {
                factoryCalls++;
                return snapshot.Count;
            });
            var second = job.GetOrCreateProjection("count", snapshot =>
            {
                factoryCalls++;
                return snapshot.Count;
            });

            job.Session.AddResponse(new SearchResponse("User2", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Music\Artist\Track Alt.mp3", length: 181),
            ]));

            var third = job.GetOrCreateProjection("count", snapshot =>
            {
                factoryCalls++;
                return snapshot.Count;
            });

            Assert.AreEqual(1, first);
            Assert.AreEqual(1, second);
            Assert.AreEqual(2, third);
            Assert.AreEqual(2, factoryCalls, "Projection should be cached until the raw result revision changes.");
        }

        [TestMethod]
        public async Task SearchJob_ReadRawResultsAsync_ReplaysExistingAndStreamsUntilComplete()
        {
            var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });
            var file1 = TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", length: 180);
            var response1 = new SearchResponse("User1", 1, true, 100, 0, [file1]);
            job.Session.AddResponse(response1);

            var results = new List<SearchRawResult>();
            var readerTask = Task.Run(async () =>
            {
                await foreach (var result in job.ReadRawResultsAsync())
                    results.Add(result);
            });

            await Task.Yield();

            var file2 = TestHelpers.CreateSlFile(@"Music\Artist\Track Alt.mp3", length: 181);
            var response2 = new SearchResponse("User2", 1, true, 100, 0, [file2]);
            job.Session.AddResponse(response2);
            job.Session.Complete();

            await readerTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(1, results[0].Sequence);
            Assert.AreEqual(2, results[1].Sequence);
            Assert.AreEqual("User1", results[0].Username);
            Assert.AreEqual("User2", results[1].Username);
        }

        [TestMethod]
        public async Task Searcher_SearchJob_UsesPreexistingSessionForLiveRawResults()
        {
            var index = new List<SearchResponse>
            {
                new("User1", 1, true, 100, 0,
                [
                    TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", length: 180),
                ]),
            };
            var config = TestHelpers.CreateDefaultSettings().Download;
            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(new ClientTests.MockSoulseekClient(index), registry, registry, new EngineEvents(), 10, 10);
            var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });
            var originalSession = job.Session;
            var streamed = new List<SearchRawResult>();
            var readerTask = Task.Run(async () =>
            {
                await foreach (var result in job.ReadRawResultsAsync())
                    streamed.Add(result);
            });

            await searcher.Search(job, config.Search, new ResponseData(), CancellationToken.None);
            await readerTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreSame(originalSession, job.Session);
            Assert.IsTrue(job.IsComplete);
            Assert.AreEqual(1, streamed.Count);
            Assert.AreEqual(@"Music\Artist\Track.mp3", streamed[0].Filename);
        }

        [TestMethod]
        public void SearchJob_TypedTrackProjection_IsCachedByRevisionAndCompletion()
        {
            var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" });
            job.Session.AddResponse(new SearchResponse("User1", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"Music\Artist\Track.mp3", bitrate: 320, length: 180),
            ]));

            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            var userSuccessCounts = new ConcurrentDictionary<string, int>();
            var first = job.GetSortedTrackCandidates(search, userSuccessCounts);
            var second = job.GetSortedTrackCandidates(search, userSuccessCounts);

            job.Session.Complete();
            var completed = job.GetSortedTrackCandidates(search, userSuccessCounts);

            Assert.AreSame(first, second, "Typed projections should reuse the same snapshot while revision and completion state are unchanged.");
            Assert.AreEqual(1, first.Revision);
            Assert.IsFalse(first.IsComplete);
            Assert.IsTrue(completed.IsComplete);
            Assert.AreEqual(first.Revision, completed.Revision);
            Assert.AreNotSame(first, completed, "Completion changes should invalidate cached snapshots even when no new raw results arrived.");
        }

        [TestMethod]
        public void SearchJob_TypedAlbumProjection_ReturnsAlbumFolders()
        {
            var job = new SearchJob(new AlbumQuery { Artist = "ELO", Album = "Time" });
            job.Session.AddResponse(new SearchResponse("User1", 1, true, 100, 0,
            [
                TestHelpers.CreateSlFile(@"ELO\Time\01. Twilight.flac", length: 209),
                TestHelpers.CreateSlFile(@"ELO\Time\02. Yours Truly.flac", length: 200),
                TestHelpers.CreateSlFile(@"ELO\Time\Cover.jpg"),
            ]));

            var search = TestHelpers.CreateDefaultSettings().Download.Search;
            var folders = job.GetAlbumFolders(search);

            Assert.AreEqual(1, folders.Items.Count);
            Assert.AreEqual("User1", folders.Items[0].Username);
            Assert.AreEqual(@"ELO\Time", folders.Items[0].FolderPath);
            Assert.AreEqual(2, folders.Items[0].SearchAudioFileCount);
        }
    }
}

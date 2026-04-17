using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
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

            job.Session!.AddResponse(new SearchResponse("User2", 1, true, 100, 0,
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
    }
}

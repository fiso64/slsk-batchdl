using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core.Services;
using Tests.ClientTests;
using Sldl.Core.Settings;

namespace Tests.Unit
{
    [TestClass]
    public class SearcherTests
    {
        private MockSoulseekClient CreateMockClient(List<Soulseek.SearchResponse> index)
        {
            return new MockSoulseekClient(index);
        }

        private List<SearchResponse> CreateSophisticatedIndex()
        {
            var index = new List<SearchResponse>();

            // User1: Full ELO - Time (Standard, 10 tracks)
            var u1Files = new List<Soulseek.File>();
            for (int i = 1; i <= 10; i++)
                u1Files.Add(TestHelpers.CreateSlFile($@"ELO\Time\{i:D2}. Track {i}.mp3", length: 180 + i));
            u1Files.Add(TestHelpers.CreateSlFile(@"Abba\Gold\Dancing Queen.mp3", length: 230)); // Noise
            index.Add(new SearchResponse("User1", 1, true, 100, 0, u1Files));

            // User2: Full ELO - Time (Deluxe, 13 tracks)
            var u2Files = new List<Soulseek.File>();
            for (int i = 1; i <= 13; i++)
                u2Files.Add(TestHelpers.CreateSlFile($@"ELO\Time (Deluxe)\{i:D2}. Track {i}.mp3", length: 180 + i));
            index.Add(new SearchResponse("User2", 1, true, 100, 0, u2Files));

            // User3: Partial ELO - Time (Standard, 5 tracks - matching User1 lengths)
            var u3Files = new List<Soulseek.File>();
            for (int i = 1; i <= 5; i++)
                u3Files.Add(TestHelpers.CreateSlFile($@"Music\ELO - Time\{i:D2}. Track {i}.mp3", length: 180 + i));
            index.Add(new SearchResponse("User3", 1, true, 100, 0, u3Files));

            // User4: ELO - Discovery (Full, 9 tracks)
            var u4Files = new List<Soulseek.File>();
            for (int i = 1; i <= 9; i++)
                u4Files.Add(TestHelpers.CreateSlFile($@"ELO\Discovery\{i:D2}. Track {i}.mp3", length: 240 + i));
            index.Add(new SearchResponse("User4", 1, true, 100, 0, u4Files));

            // User5: "ELO" but bad uploader/fake version (drastically different lengths)
            var u5Files = new List<Soulseek.File>();
            for (int i = 1; i <= 10; i++)
                u5Files.Add(TestHelpers.CreateSlFile($@"ELO - Time\{i:D2}. Track {i}.mp3", length: 60)); 
            index.Add(new SearchResponse("User5", 1, true, 100, 0, u5Files));

            // User6: Same as User1 (Standard Time 10 tracks) to test multiple shares
            var u6Files = new List<Soulseek.File>();
            for (int i = 1; i <= 10; i++)
                u6Files.Add(TestHelpers.CreateSlFile($@"Shared\ELO\Time\{i:D2}. Track {i}.mp3", length: 180 + i));
            index.Add(new SearchResponse("User6", 1, true, 100, 0, u6Files));

            return index;
        }

        private Searcher CreateSearcher(ISoulseekClient client, DownloadSettings config)
        {
            var registry = TestHelpers.CreateSessionRegistry();
            return new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
        }

        [TestMethod]
        public async Task SearchAlbum_LargeResult_GroupsByFolderAndFiltersIncomplete()
        {
            var index = CreateSophisticatedIndex();
            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            var searcher = CreateSearcher(client, config);
            
            // Search for "ELO Time" specifically
            var job = new AlbumJob(new AlbumQuery { Artist = "ELO", Album = "Time" });
            var responseData = new ResponseData();

            await searcher.SearchAlbum(job, config.Search, responseData, CancellationToken.None);

            // We expect folders matching "ELO" AND "Time" (or at least "Time")
            // 1. User1: ELO\Time (10 tracks)
            // 2. User2: ELO\Time (Deluxe) (13 tracks)
            // 3. User3: Music\ELO - Time (5 tracks)
            // 5. User5: ELO - Time (10 tracks)
            // 6. User6: Shared\ELO\Time (10 tracks)
            // (User4: Discovery - should NOT be returned by MockSoulseekClient because query terms include "Time")
            
            Assert.AreEqual(5, job.Results.Count, "Should find folders matching album name search terms.");
            Assert.IsTrue(job.Results.Any(f => f.Username == "User1"), "User1 folder missing.");
            Assert.IsTrue(job.Results.All(f => !f.Files.Any(fi => fi.ResolvedTarget!.Filename.Contains("Dancing Queen"))), "Noise file not filtered!");
        }

        [TestMethod]
        public async Task AggregateAlbum_DiscographySearch_IdentifiesAllUniqueAlbums()
        {
            var index = CreateSophisticatedIndex();
            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.MinSharesAggregate = 1;

            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var job = new AlbumAggregateJob(new AlbumQuery { Artist = "ELO" });
            var responseData = new ResponseData();

            var results = await searcher.SearchAggregateAlbum(job, config.Search, responseData, CancellationToken.None);

            // Expected groups:
            // 1. Time Standard (10 tracks, lengths 181..190 - shared by User1, User6)
            // 2. Time Deluxe (13 tracks, User2)
            // 3. Time Partial (5 tracks, User3)
            // 4. Discovery (9 tracks, User4)
            // 5. Time BadVersion (10 tracks, length 60 - User5)
            Assert.AreEqual(5, results.Count, "Should identify 5 distinct album/version groups.");
            
            var multiShare = results.FirstOrDefault(r => r.Results.Count == 2);
            Assert.IsNotNull(multiShare, "Standard Multi-Share version missing.");
            Assert.AreEqual(10, multiShare.Results[0].Files.Count(f => !f.IsNotAudio));
        }

        [TestMethod]
        public async Task AggregateAlbum_HighEntropy_FiltersByShares()
        {
            var index = CreateSophisticatedIndex();
            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.MinSharesAggregate = 2; // Only return if shared by 2+ peers

            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var job = new AlbumAggregateJob(new AlbumQuery { Artist = "ELO" });
            var responseData = new ResponseData();

            var results = await searcher.SearchAggregateAlbum(job, config.Search, responseData, CancellationToken.None);

            // Only the "Standard Time" version (shared by User1 and User6) meets the threshold.
            Assert.AreEqual(1, results.Count, "High entropy filter (minShares=2) failed.");
            Assert.AreEqual(2, results[0].Results.Count);
        }

        [TestMethod]
        public async Task SongAggregate_Comprehensive_GroupsEquivalentTracks()
        {
            var index = new List<SearchResponse>();
            
            // Standard version (User1 and User2 share lengths 180-181)
            index.Add(new SearchResponse("User1", 1, true, 100, 0, new List<Soulseek.File> {
                TestHelpers.CreateSlFile(@"ELO\Time\Blue Sky.mp3", length: 180)
            }));
            index.Add(new SearchResponse("User2", 1, true, 100, 0, new List<Soulseek.File> {
                TestHelpers.CreateSlFile(@"ELO - Blue Sky.mp3", length: 181)
            }));
            
            // Group 2: Live/Odd Version (User3 - diff length)
            index.Add(new SearchResponse("User3", 1, true, 100, 0, new List<Soulseek.File> {
                TestHelpers.CreateSlFile(@"ELO - Blue Sky (Live).mp3", length: 300)
            }));

            var client = new MockSoulseekClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.MinSharesAggregate = 1;

            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var job = new AggregateJob(new SongQuery { Artist = "ELO", Title = "Blue Sky" });
            var responseData = new ResponseData();

            await searcher.SearchAggregate(job, config.Search, responseData, CancellationToken.None);

            // Verified groups: 
            // 1. "Blue Sky" group (User1, User2)
            // 2. "Live Blue Sky" group (User3)
            Assert.AreEqual(2, job.Songs.Count);
            var shared = job.Songs.FirstOrDefault(s => s.Candidates.Count == 2);
            Assert.IsNotNull(shared, "Failed to aggregate shared track version across users.");
        }
        [TestMethod]
        public async Task SongAggregate_NameVariations_GroupedByInference()
        {
            var index = new List<SearchResponse>();
            
            // Three variations of the same track name
            index.Add(new SearchResponse("User1", 1, true, 100, 0, new List<Soulseek.File> {
                TestHelpers.CreateSlFile(@"ELO - Blue Sky.mp3", length: 180)
            }));
            index.Add(new SearchResponse("User2", 1, true, 100, 0, new List<Soulseek.File> {
                TestHelpers.CreateSlFile(@"01. ELO - Blue Sky.mp3", length: 180) // With track number
            }));
            index.Add(new SearchResponse("User3", 1, true, 100, 0, new List<Soulseek.File> {
                TestHelpers.CreateSlFile(@"ELO - Blue Sky  .mp3", length: 180) // With extra whitespace
            }));

            var client = CreateMockClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.MinSharesAggregate = 1;

            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var job = new AggregateJob(new SongQuery { Artist = "ELO", Title = "Blue Sky" });
            var responseData = new ResponseData();

            await searcher.SearchAggregate(job, config.Search, responseData, CancellationToken.None);

            // Should all three group into one job after inference handles the name variations.
            Assert.AreEqual(1, job.Songs.Count, "Should group all name variations into a single SongJob.");
            Assert.AreEqual(3, job.Songs[0].Candidates.Count);
        }
        [TestMethod]
        public async Task SearchAggregate_ResultsSortedByPopularity()
        {
            var index = new List<SearchResponse>();
            
            // 3 peers have Version A (Low Bitrate)
            for (int i = 1; i <= 3; i++)
                index.Add(new SearchResponse($"User{i}", 1, true, 100, 0, new List<Soulseek.File> {
                    TestHelpers.CreateSlFile(@"ELO - Blue Sky.mp3", length: 180, bitrate: 128)
                }));
            // 2 peers have Version B (High Bitrate)
            for (int i = 4; i <= 5; i++)
                index.Add(new SearchResponse($"User{i}", 1, true, 100, 0, new List<Soulseek.File> {
                    TestHelpers.CreateSlFile(@"ELO - Blue Sky.mp3", length: 300, bitrate: 320)
                }));

            var client = CreateMockClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.MinSharesAggregate = 1;

            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var job = new AggregateJob(new SongQuery { Artist = "ELO", Title = "Blue Sky" });
            var responseData = new ResponseData();

            await searcher.SearchAggregate(job, config.Search, responseData, CancellationToken.None);

            // Version A has more shares (3) so it should be first, even though bitrate is lower.
            Assert.AreEqual(2, job.Songs.Count);
            Assert.AreEqual(3, job.Songs[0].Candidates.Count);
            Assert.AreEqual(2, job.Songs[1].Candidates.Count);
        }

        [TestMethod]
        public async Task AggregateAlbum_ResultsSortedByPopularity()
        {
            var index = new List<SearchResponse>();
            
            // 2 peers have album
            for (int i = 1; i <= 2; i++) {
                var files = new List<Soulseek.File>();
                for (int j = 1; j <= 5; j++) files.Add(TestHelpers.CreateSlFile($@"ELO\Time\Track {j}.mp3", length: 200));
                index.Add(new SearchResponse($"User{i}", 1, true, 100, 0, files));
            }
            // 1 peer has different version
            var files3 = new List<Soulseek.File>();
            for (int j = 1; j <= 5; j++) files3.Add(TestHelpers.CreateSlFile($@"ELO\Time (Alt)\Track {j}.mp3", length: 300));
            index.Add(new SearchResponse("User3", 1, true, 100, 0, files3));

            var client = CreateMockClient(index);
            var config = TestHelpers.CreateDefaultSettings().Download;
            config.Search.MinSharesAggregate = 1;

            var registry = TestHelpers.CreateSessionRegistry();
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 10, 10);
            var job = new AlbumAggregateJob(new AlbumQuery { Artist = "ELO" });
            var responseData = new ResponseData();

            var results = await searcher.SearchAggregateAlbum(job, config.Search, responseData, CancellationToken.None);

            // First result should be the one with 2 shares.
            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(2, results[0].Results.Count);
            Assert.AreEqual(1, results[1].Results.Count);
        }
    }
}

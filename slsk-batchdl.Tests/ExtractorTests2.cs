using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;
using Jobs;
using Enums;
using Extractors;

namespace Tests.ExtractorTests2
{
    [TestClass]
    public class SoulseekExtractorTests
    {
        [TestMethod]
        public void InputMatches_SlskUrl_ReturnsTrue()
        {
            Assert.IsTrue(SoulseekExtractor.InputMatches("slsk://user/path/to/file.mp3"));
        }

        [TestMethod]
        public void InputMatches_SlskUrlCaseInsensitive_ReturnsTrue()
        {
            Assert.IsTrue(SoulseekExtractor.InputMatches("SLSK://user/path/file.mp3"));
        }

        [TestMethod]
        public void InputMatches_HttpUrl_ReturnsFalse()
        {
            Assert.IsFalse(SoulseekExtractor.InputMatches("https://example.com/file.mp3"));
        }

        [TestMethod]
        public void InputMatches_PlainString_ReturnsFalse()
        {
            Assert.IsFalse(SoulseekExtractor.InputMatches("artist - title"));
        }

        [TestMethod]
        public async Task GetTracks_FileLink_CreatesDirectDownload()
        {
            var extractor = new SoulseekExtractor();
            var config = TestHelpers.CreateDefaultConfig();
            var result = await extractor.GetTracks("slsk://someuser/Music/Artist/Song.mp3", 100, 0, false, config);

            var slj = (SongJob)result;
            Assert.IsTrue(slj.Query.IsDirectLink);
        }

        [TestMethod]
        public async Task GetTracks_FolderLink_CreatesAlbumType()
        {
            var extractor = new SoulseekExtractor();
            var config = TestHelpers.CreateDefaultConfig();
            var result = await extractor.GetTracks("slsk://someuser/Music/Artist/Album/", 100, 0, false, config);

            Assert.IsInstanceOfType(result, typeof(AlbumJob));
        }

        [TestMethod]
        public async Task GetTracks_WithAlbumConfig_CreatesAlbumType()
        {
            var extractor = new SoulseekExtractor();
            var config = TestHelpers.CreateDefaultConfig();
            config.album = true;
            var result = await extractor.GetTracks("slsk://someuser/Music/Song.mp3", 100, 0, false, config);

            Assert.IsInstanceOfType(result, typeof(AlbumJob));
        }

        [TestMethod]
        public async Task GetTracks_FileLink_SetsUsernameAndPath()
        {
            var extractor = new SoulseekExtractor();
            var config = TestHelpers.CreateDefaultConfig();
            var result = await extractor.GetTracks("slsk://myuser/Music/folder/track.mp3", 100, 0, false, config);

            var song = (SongJob)result;
            Assert.IsNotNull(song.Candidates);
            Assert.IsTrue(song.Candidates.Count > 0);
            Assert.AreEqual("myuser", song.Candidates[0].Response.Username);
        }
    }

    [TestClass]
    public class CsvExtractorTests
    {
        private string _tempCsv = "";

        [TestInitialize]
        public void Setup()
        {
            _tempCsv = Path.GetTempFileName() + ".csv";
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(_tempCsv)) File.Delete(_tempCsv);
        }

        [TestMethod]
        public void InputMatches_CsvFile_ReturnsTrue()
        {
            Assert.IsTrue(CsvExtractor.InputMatches("playlist.csv"));
        }

        [TestMethod]
        public void InputMatches_CsvFileWithPath_ReturnsTrue()
        {
            Assert.IsTrue(CsvExtractor.InputMatches("/home/user/music/list.csv"));
        }

        [TestMethod]
        public void InputMatches_NonCsv_ReturnsFalse()
        {
            Assert.IsFalse(CsvExtractor.InputMatches("playlist.m3u"));
        }

        [TestMethod]
        public void InputMatches_HttpCsvUrl_ReturnsFalse()
        {
            Assert.IsFalse(CsvExtractor.InputMatches("https://example.com/list.csv"));
        }

        [TestMethod]
        public async Task GetTracks_WithArtistTitleColumns_ParsesCorrectly()
        {
            File.WriteAllText(_tempCsv, "artist,title\nArtist1,Song1\nArtist2,Song2\n");
            var extractor = new CsvExtractor();
            var config = TestHelpers.CreateDefaultConfig();

            var result = await extractor.GetTracks(_tempCsv, 100, 0, false, config);
            var songs = ((JobList)result).AllSongs().ToList();

            Assert.AreEqual(2, songs.Count);
            Assert.AreEqual("Artist1", songs[0].Query.Artist);
            Assert.AreEqual("Song1",   songs[0].Query.Title);
            Assert.AreEqual("Artist2", songs[1].Query.Artist);
            Assert.AreEqual("Song2",   songs[1].Query.Title);
        }

        [TestMethod]
        public async Task GetTracks_WithAlbumColumn_ParsesAlbum()
        {
            File.WriteAllText(_tempCsv, "artist,title,album\nBand,Track,TheAlbum\n");
            var extractor = new CsvExtractor();
            var config = TestHelpers.CreateDefaultConfig();

            var result = await extractor.GetTracks(_tempCsv, 100, 0, false, config);
            var songs = ((JobList)result).AllSongs().ToList();

            Assert.AreEqual("TheAlbum", songs[0].Query.Album);
        }

        [TestMethod]
        public async Task GetTracks_NoTitleColumn_CreatesAlbumType()
        {
            File.WriteAllText(_tempCsv, "artist,album\nBand,TheAlbum\n");
            var extractor = new CsvExtractor();
            var config = TestHelpers.CreateDefaultConfig();

            var result = await extractor.GetTracks(_tempCsv, 100, 0, false, config);

            Assert.IsTrue(result is AlbumJob || result is AlbumAggregateJob);
        }

        [TestMethod]
        public async Task GetTracks_WithOffset_SkipsTracks()
        {
            File.WriteAllText(_tempCsv, "artist,title\nArtist1,Song1\nArtist2,Song2\nArtist3,Song3\n");
            var extractor = new CsvExtractor();
            var config = TestHelpers.CreateDefaultConfig();

            var result = await extractor.GetTracks(_tempCsv, 100, 1, false, config);
            var songs = ((JobList)result).AllSongs().ToList();

            Assert.AreEqual(2, songs.Count);
            Assert.AreEqual("Artist2", songs[0].Query.Artist);
        }

        [TestMethod]
        public async Task GetTracks_WithReverse_ReversesOrder()
        {
            File.WriteAllText(_tempCsv, "artist,title\nArtist1,Song1\nArtist2,Song2\n");
            var extractor = new CsvExtractor();
            var config = TestHelpers.CreateDefaultConfig();

            var result = await extractor.GetTracks(_tempCsv, 100, 0, true, config);
            var songs = ((JobList)result).AllSongs().ToList();

            Assert.AreEqual("Artist2", songs[0].Query.Artist);
            Assert.AreEqual("Artist1", songs[1].Query.Artist);
        }

        [TestMethod]
        public async Task GetTracks_WithMaxTracks_LimitsResults()
        {
            File.WriteAllText(_tempCsv, "artist,title\nA,T1\nB,T2\nC,T3\nD,T4\n");
            var extractor = new CsvExtractor();
            var config = TestHelpers.CreateDefaultConfig();

            var result = await extractor.GetTracks(_tempCsv, 2, 0, false, config);
            var songs = ((JobList)result).AllSongs().ToList();

            Assert.AreEqual(2, songs.Count);
        }

        [TestMethod]
        public async Task GetTracks_LengthInSeconds_ParsesCorrectly()
        {
            File.WriteAllText(_tempCsv, "artist,title,length\nArtist,Track,200\n");
            var extractor = new CsvExtractor();
            var config = TestHelpers.CreateDefaultConfig();
            config.timeUnit = "s";

            var result = await extractor.GetTracks(_tempCsv, 100, 0, false, config);
            var songs = ((JobList)result).AllSongs().ToList();

            Assert.AreEqual(200, songs[0].Query.Length);
        }
    }

    [TestClass]
    public class ExtractorRegistryTests
    {
        [TestMethod]
        public void GetMatchingExtractor_CsvFile_ReturnsCsvExtractor()
        {
            var (type, extractor) = ExtractorRegistry.GetMatchingExtractor("playlist.csv");
            Assert.AreEqual(InputType.CSV, type);
            Assert.IsInstanceOfType(extractor, typeof(CsvExtractor));
        }

        [TestMethod]
        public void GetMatchingExtractor_SlskUrl_ReturnsSoulseekExtractor()
        {
            var (type, extractor) = ExtractorRegistry.GetMatchingExtractor("slsk://user/file.mp3");
            Assert.AreEqual(InputType.Soulseek, type);
            Assert.IsInstanceOfType(extractor, typeof(SoulseekExtractor));
        }

        [TestMethod]
        public void GetMatchingExtractor_PlainString_ReturnsStringExtractor()
        {
            var (type, extractor) = ExtractorRegistry.GetMatchingExtractor("Artist - Title");
            Assert.AreEqual(InputType.String, type);
            Assert.IsInstanceOfType(extractor, typeof(StringExtractor));
        }

        [TestMethod]
        public void GetMatchingExtractor_EmptyInput_Throws()
        {
            Assert.ThrowsException<ArgumentException>(() =>
                ExtractorRegistry.GetMatchingExtractor(""));
        }

        [TestMethod]
        public void GetMatchingExtractor_ExplicitInputType_ReturnsCorrectExtractor()
        {
            var (type, extractor) = ExtractorRegistry.GetMatchingExtractor("anything", InputType.Soulseek);
            Assert.AreEqual(InputType.Soulseek, type);
            Assert.IsInstanceOfType(extractor, typeof(SoulseekExtractor));
        }
    }
}

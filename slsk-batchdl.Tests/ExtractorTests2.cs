using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;
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

            Assert.AreEqual(1, result.lists.Count);
            var tracks = result.lists[0].list.SelectMany(x => x).ToList();
            Assert.AreEqual(1, tracks.Count);
            Assert.IsTrue(tracks[0].IsDirectLink);
            Assert.AreEqual(TrackType.Normal, tracks[0].Type);
        }

        [TestMethod]
        public async Task GetTracks_FolderLink_CreatesAlbumType()
        {
            var extractor = new SoulseekExtractor();
            var config = TestHelpers.CreateDefaultConfig();
            var result = await extractor.GetTracks("slsk://someuser/Music/Artist/Album/", 100, 0, false, config);

            Assert.AreEqual(1, result.lists.Count);
            Assert.AreEqual(TrackType.Album, result.lists[0].source.Type);
        }

        [TestMethod]
        public async Task GetTracks_WithAlbumConfig_CreatesAlbumType()
        {
            var extractor = new SoulseekExtractor();
            var config = TestHelpers.CreateDefaultConfig();
            config.album = true;
            var result = await extractor.GetTracks("slsk://someuser/Music/Song.mp3", 100, 0, false, config);

            Assert.AreEqual(TrackType.Album, result.lists[0].source.Type);
        }

        [TestMethod]
        public async Task GetTracks_FileLink_SetsUsernameAndPath()
        {
            var extractor = new SoulseekExtractor();
            var config = TestHelpers.CreateDefaultConfig();
            var result = await extractor.GetTracks("slsk://myuser/Music/folder/track.mp3", 100, 0, false, config);

            var track = result.lists[0].list[0][0];
            Assert.IsNotNull(track.Downloads);
            Assert.IsTrue(track.Downloads.Count > 0);
            Assert.AreEqual("myuser", track.Downloads[0].Item1.Username);
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
            var tracks = result.lists.SelectMany(e => e.list.SelectMany(x => x)).ToList();

            Assert.AreEqual(2, tracks.Count);
            Assert.AreEqual("Artist1", tracks[0].Artist);
            Assert.AreEqual("Song1", tracks[0].Title);
            Assert.AreEqual("Artist2", tracks[1].Artist);
            Assert.AreEqual("Song2", tracks[1].Title);
        }

        [TestMethod]
        public async Task GetTracks_WithAlbumColumn_ParsesAlbum()
        {
            File.WriteAllText(_tempCsv, "artist,title,album\nBand,Track,TheAlbum\n");
            var extractor = new CsvExtractor();
            var config = TestHelpers.CreateDefaultConfig();

            var result = await extractor.GetTracks(_tempCsv, 100, 0, false, config);
            var tracks = result.lists.SelectMany(e => e.list.SelectMany(x => x)).ToList();

            Assert.AreEqual("TheAlbum", tracks[0].Album);
        }

        [TestMethod]
        public async Task GetTracks_NoTitleColumn_CreatesAlbumType()
        {
            File.WriteAllText(_tempCsv, "artist,album\nBand,TheAlbum\n");
            var extractor = new CsvExtractor();
            var config = TestHelpers.CreateDefaultConfig();

            var result = await extractor.GetTracks(_tempCsv, 100, 0, false, config);

            Assert.IsTrue(result.lists.Any(e => e.source.Type == TrackType.Album || e.source.Type == TrackType.AlbumAggregate));
        }

        [TestMethod]
        public async Task GetTracks_WithOffset_SkipsTracks()
        {
            File.WriteAllText(_tempCsv, "artist,title\nArtist1,Song1\nArtist2,Song2\nArtist3,Song3\n");
            var extractor = new CsvExtractor();
            var config = TestHelpers.CreateDefaultConfig();

            var result = await extractor.GetTracks(_tempCsv, 100, 1, false, config);
            var tracks = result.lists.SelectMany(e => e.list.SelectMany(x => x)).ToList();

            Assert.AreEqual(2, tracks.Count);
            Assert.AreEqual("Artist2", tracks[0].Artist);
        }

        [TestMethod]
        public async Task GetTracks_WithReverse_ReversesOrder()
        {
            File.WriteAllText(_tempCsv, "artist,title\nArtist1,Song1\nArtist2,Song2\n");
            var extractor = new CsvExtractor();
            var config = TestHelpers.CreateDefaultConfig();

            var result = await extractor.GetTracks(_tempCsv, 100, 0, true, config);
            var tracks = result.lists.SelectMany(e => e.list.SelectMany(x => x)).ToList();

            Assert.AreEqual("Artist2", tracks[0].Artist);
            Assert.AreEqual("Artist1", tracks[1].Artist);
        }

        [TestMethod]
        public async Task GetTracks_WithMaxTracks_LimitsResults()
        {
            File.WriteAllText(_tempCsv, "artist,title\nA,T1\nB,T2\nC,T3\nD,T4\n");
            var extractor = new CsvExtractor();
            var config = TestHelpers.CreateDefaultConfig();

            var result = await extractor.GetTracks(_tempCsv, 2, 0, false, config);
            var tracks = result.lists.SelectMany(e => e.list.SelectMany(x => x)).ToList();

            Assert.AreEqual(2, tracks.Count);
        }

        [TestMethod]
        public async Task GetTracks_LengthInSeconds_ParsesCorrectly()
        {
            File.WriteAllText(_tempCsv, "artist,title,length\nArtist,Track,200\n");
            var extractor = new CsvExtractor();
            var config = TestHelpers.CreateDefaultConfig();
            config.timeUnit = "s";

            var result = await extractor.GetTracks(_tempCsv, 100, 0, false, config);
            var tracks = result.lists.SelectMany(e => e.list.SelectMany(x => x)).ToList();

            Assert.AreEqual(200, tracks[0].Length);
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

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;
using Enums;
using Extractors;

namespace Tests.Extractors
{
    [TestClass]
    public class StringExtractorTests
    {
        private StringExtractor extractor;
        private Config config;
        private List<string> testStrings;
        private List<Track> expectedTracks;
        private List<Track> expectedAlbums;

        [TestInitialize]
        public void Setup()
        {
            extractor = new StringExtractor();
            config = new Config();
            config.aggregate = false;

            testStrings = new List<string>
            {
                "Some Title",
                "Some, Title",
                "artist = Some artist, title = some title",
                "Artist - Title, length = 42",
                "title=Some, Title, artist=Some, Artist, album = Some, Album, length= 42",
                "Some, Artist = a - Some, Title = b, album = Some, Album, length = 42",
                "artist=artist, title=title, album=",
                "artist=, title=title, album=album",
            };

            expectedTracks = new List<Track>
            {
                new Track() { Title="Some Title" },
                new Track() { Title="Some, Title" },
                new Track() { Title = "some title", Artist = "Some artist" },
                new Track() { Title = "Title", Artist = "Artist", Length = 42 },
                new Track() { Title="Some, Title", Artist = "Some, Artist", Album = "Some, Album", Length = 42 },
                new Track() { Title="Some, Title = b", Artist = "Some, Artist = a", Album = "Some, Album", Length = 42 },
                new Track() { Title="title", Artist="artist", Album="" },
                new Track() { Title="title", Artist="", Album="album" },
            };

            expectedAlbums = new List<Track>
            {
                new Track() { Album="Some Title", Type = TrackType.Album },
                new Track() { Album="Some, Title", Type = TrackType.Album },
                new Track() { Title = "some title", Artist = "Some artist", Type = TrackType.Album },
                new Track() { Artist = "Artist", Album="Title", Length = 42, Type = TrackType.Album },
                new Track() { Title="Some, Title", Artist = "Some, Artist", Album = "Some, Album", Length = 42, Type = TrackType.Album },
                new Track() { Album = "Some, Album", Length = 42, Type = TrackType.Album },
                new Track() { Title="title", Artist="artist", Album="", Type = TrackType.Album },
                new Track() { Title="title", Artist="", Album="album", Type = TrackType.Album },
            };
        }

        [TestMethod]
        public async Task GetTracks_WithSongMode_ExtractsCorrectTrackInfo()
        {
            config.album = false;

            for (int i = 0; i < testStrings.Count; i++)
            {
                config.input = testStrings[i];
                var result = await extractor.GetTracks(config.input, 0, 0, false, config);
                var track = result[0].list[0][0];

                Assert.IsTrue(StringExtractor.InputMatches(config.input));
                Assert.AreEqual(expectedTracks[i].ToKey(), track.ToKey());
            }
        }

        [TestMethod]
        public async Task GetTracks_WithAlbumMode_ExtractsCorrectAlbumInfo()
        {
            config.album = true;

            for (int i = 0; i < testStrings.Count; i++)
            {
                config.input = testStrings[i];
                var result = await extractor.GetTracks(config.input, 0, 0, false, config);
                var track = result[0].source;
                var expected = expectedAlbums[i];

                Assert.IsTrue(StringExtractor.InputMatches(config.input));
                Assert.AreEqual(expectedAlbums[i].ToKey(), track.ToKey());
            }
        }
    }
}
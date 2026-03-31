using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;
using Enums;

namespace Tests.InferTrackTests
{
    [TestClass]
    public class InferTrackTests
    {
        private Track DefaultTrack(string artist = "DefaultArtist", string title = "DefaultTitle", string album = "DefaultAlbum")
        {
            return new Track { Artist = artist, Title = title, Album = album };
        }

        // --- Single-part filenames (no " - " delimiter) ---

        [TestMethod]
        public void InferTrack_SinglePart_SetsTitleOnly()
        {
            var result = Searcher.InferTrack("Music\\SomeFolder\\MySong.mp3", DefaultTrack());
            Assert.AreEqual("MySong", result.Title);
        }

        [TestMethod]
        public void InferTrack_SinglePartWithRemix_SetsArtistMaybeWrong()
        {
            var def = DefaultTrack(artist: "DJ Cool");
            var result = Searcher.InferTrack("Music\\Folder\\Song (DJ Cool Remix).mp3", def);
            Assert.IsTrue(result.ArtistMaybeWrong);
        }

        // --- Two-part filenames ---

        [TestMethod]
        public void InferTrack_TwoParts_SetsArtistAndTitle()
        {
            var result = Searcher.InferTrack("Music\\Folder\\SomeArtist - SomeTitle.mp3", DefaultTrack());
            Assert.AreEqual("SomeArtist", result.Artist);
            Assert.AreEqual("SomeTitle", result.Title);
        }

        [TestMethod]
        public void InferTrack_TwoParts_MatchingDefaults_NoArtistMaybeWrong()
        {
            var def = DefaultTrack(artist: "TheArtist", title: "TheTitle");
            var result = Searcher.InferTrack("Music\\Folder\\TheArtist - TheTitle.mp3", def);
            Assert.IsFalse(result.ArtistMaybeWrong);
        }

        [TestMethod]
        public void InferTrack_TwoParts_NonMatchingDefaults_SetsArtistMaybeWrong()
        {
            var def = DefaultTrack(artist: "ExpectedArtist", title: "ExpectedTitle");
            var result = Searcher.InferTrack("Music\\Folder\\DifferentArtist - DifferentTitle.mp3", def);
            Assert.IsTrue(result.ArtistMaybeWrong);
        }

        // --- Three-part filenames ---

        [TestMethod]
        public void InferTrack_ThreeParts_ArtistAlbumTitle()
        {
            var def = DefaultTrack(artist: "MyArtist", title: "MyTitle", album: "MyAlbum");
            var result = Searcher.InferTrack("Music\\Folder\\MyArtist - MyAlbum - MyTitle.mp3", def);
            Assert.AreEqual("MyTitle", result.Title);
            Assert.AreEqual("MyArtist", result.Artist);
        }

        [TestMethod]
        public void InferTrack_ThreeParts_ArtistUnknown_SetsMaybeWrong()
        {
            var def = DefaultTrack(artist: "Unknown", title: "MyTitle");
            var result = Searcher.InferTrack("Music\\Folder\\PartA - PartB - MyTitle.mp3", def);
            Assert.AreEqual("MyTitle", result.Title);
            Assert.IsTrue(result.ArtistMaybeWrong);
        }

        [TestMethod]
        public void InferTrack_ThreeParts_AlbumDetected_CorrectPosition()
        {
            var def = DefaultTrack(artist: "TheArtist", title: "TheTitle", album: "TheAlbum");
            var result = Searcher.InferTrack("Music\\Folder\\TheArtist - TheAlbum - TheTitle.mp3", def);
            Assert.AreEqual("TheArtist", result.Artist);
            Assert.AreEqual("TheTitle", result.Title);
        }

        // --- Four+ part filenames ---

        [TestMethod]
        public void InferTrack_FourParts_FindsArtistAndTitle()
        {
            var def = DefaultTrack(artist: "ArtistX", title: "TitleX");
            var result = Searcher.InferTrack("Music\\Folder\\ArtistX - Foo - Bar - TitleX.mp3", def);
            Assert.AreEqual("ArtistX", result.Artist);
            Assert.AreEqual("TitleX", result.Title);
        }

        [TestMethod]
        public void InferTrack_FourParts_NoMatch_UsesFallback()
        {
            var def = DefaultTrack(artist: "Nobody", title: "Nothing");
            var result = Searcher.InferTrack("Music\\Folder\\A - B - C - D.mp3", def);
            // When no match, title should be set (not empty)
            Assert.IsTrue(result.Title.Length > 0);
        }

        // --- Track number removal ---

        [TestMethod]
        public void InferTrack_TrackNumAtStart_Removed()
        {
            var def = DefaultTrack(artist: "Artist", title: "Title");
            var result = Searcher.InferTrack("Music\\Folder\\01. Artist - Title.mp3", def);
            Assert.AreEqual("Artist", result.Artist);
            Assert.AreEqual("Title", result.Title);
        }

        [TestMethod]
        public void InferTrack_TrackNumAtStart_ThreeDigit_Removed()
        {
            var def = DefaultTrack(artist: "Artist", title: "Title");
            var result = Searcher.InferTrack("Music\\Folder\\101 Artist - Title.mp3", def);
            Assert.AreEqual("Artist", result.Artist);
            Assert.AreEqual("Title", result.Title);
        }

        [TestMethod]
        public void InferTrack_TrackNumMiddle_Removed()
        {
            var def = DefaultTrack(artist: "Artist", title: "Title");
            var result = Searcher.InferTrack("Music\\Folder\\Artist - 01 Title.mp3", def);
            Assert.AreEqual("Title", result.Title);
        }

        // --- Special format (NN) [Artist] Title ---

        [TestMethod]
        public void InferTrack_SpecialFormat_NoClosingBracket_FallsThrough()
        {
            var result = Searcher.InferTrack("Music\\Folder\\(01) [NoClosing.mp3", DefaultTrack());
            // Should not crash, falls through to normal parsing
            Assert.IsTrue(result.Title.Length > 0);
        }

        // --- Edge cases ---

        [TestMethod]
        public void InferTrack_EmptyFilename_ReturnsDefault()
        {
            var def = DefaultTrack();
            var result = Searcher.InferTrack(".mp3", def);
            // Should handle gracefully
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void InferTrack_UnderscoresReplacedWithSpaces()
        {
            var result = Searcher.InferTrack("Music\\Folder\\Some_Artist - Some_Title.mp3", DefaultTrack());
            Assert.AreEqual("Some Artist", result.Artist);
            Assert.AreEqual("Some Title", result.Title);
        }

        [TestMethod]
        public void InferTrack_EmDashReplacedWithHyphen()
        {
            var result = Searcher.InferTrack("Music\\Folder\\Artist — Title.mp3", DefaultTrack());
            Assert.AreEqual("Artist", result.Artist);
            Assert.AreEqual("Title", result.Title);
        }

        [TestMethod]
        public void InferTrack_FeatRemoved_FromResult()
        {
            var result = Searcher.InferTrack("Music\\Folder\\Artist feat. Other - Title ft. Someone.mp3", DefaultTrack());
            Assert.IsFalse(result.Title.Contains("ft."));
            Assert.IsFalse(result.Artist.Contains("feat."));
        }

        [TestMethod]
        public void InferTrack_PreservesTrackType()
        {
            var def = DefaultTrack();
            var result = Searcher.InferTrack("Music\\Folder\\Artist - Title.mp3", def, TrackType.Album);
            Assert.AreEqual(TrackType.Album, result.Type);
        }

        // --- Permutation fallback ---

        [TestMethod]
        public void InferTrack_WrongOrder_PermutationFixesIt()
        {
            var def = DefaultTrack(artist: "RealArtist", title: "RealTitle");
            // Two parts: "RealTitle - RealArtist" — parts[0] contains title, parts[1] contains artist
            var result = Searcher.InferTrack("Music\\Folder\\RealTitle - RealArtist.mp3", def);
            // The permutation logic should detect the swap and fix it
            Assert.AreEqual("RealArtist", result.Artist);
            Assert.AreEqual("RealTitle", result.Title);
        }

        [TestMethod]
        public void InferTrack_TwoPartWithTrackNumber_ParsesCorrectly()
        {
            var def = DefaultTrack(artist: "Pink Floyd", title: "Comfortably Numb");
            var result = Searcher.InferTrack("Music\\Folder\\06 Pink Floyd - Comfortably Numb.mp3", def);
            Assert.AreEqual("Pink Floyd", result.Artist);
            Assert.AreEqual("Comfortably Numb", result.Title);
            Assert.IsFalse(result.ArtistMaybeWrong);
        }

        [TestMethod]
        public void InferTrack_DiscTrackNumber_Removed()
        {
            var def = DefaultTrack(artist: "Artist", title: "Song");
            var result = Searcher.InferTrack("Music\\Folder\\1-01 Artist - Song.mp3", def);
            Assert.AreEqual("Artist", result.Artist);
            Assert.AreEqual("Song", result.Title);
        }
    }
}

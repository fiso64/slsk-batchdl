using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;
using Enums;

namespace Tests.Index
{
    [TestClass]
    public class IndexTests
    {
        private string testM3uPath;

        [TestInitialize]
        public void Setup()
        {
            testM3uPath = Path.Join(Path.GetTempPath(), $"test_m3u_{Guid.NewGuid()}.m3u8");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(testM3uPath))
                File.Delete(testM3uPath);
        }

        [TestMethod]
        public void Index_LoadsOldFormat_PreviousRunData()
        {
            string initialContent =
                "#SLDL:" +
                "path/to/file1,Artist1,,Title1,-1,0,1,0;" +
                "path/to/file2,Artist2,,Title2,-1,0,3,0;" +
                ",Artist3,,Title3,-1,0,2,3;";

            File.WriteAllText(testM3uPath, initialContent);

            var trackLists = new TrackLists();
            trackLists.AddEntry(new TrackListEntry(TrackType.Normal));
            var editor = new M3uEditor(testM3uPath, trackLists, M3uOption.Index, true);

            // Verify downloaded track
            var t1 = new Track { Artist = "Artist1", Title = "Title1" };
            editor.TryGetPreviousRunResult(t1, out var prev1);
            Assert.IsNotNull(prev1);
            Assert.AreEqual(TrackState.Downloaded, prev1.State);
            Assert.AreEqual("path/to/file1", prev1.DownloadPath);

            // Verify already-exists track
            var t2 = new Track { Artist = "Artist2", Title = "Title2" };
            editor.TryGetPreviousRunResult(t2, out var prev2);
            Assert.IsNotNull(prev2);
            Assert.AreEqual(TrackState.AlreadyExists, prev2.State);

            // Verify failed track
            var t3 = new Track { Artist = "Artist3", Title = "Title3" };
            editor.TryGetPreviousRunResult(t3, out var prev3);
            Assert.IsNotNull(prev3);
            Assert.AreEqual(TrackState.Failed, prev3.State);
            Assert.AreEqual(FailureReason.NoSuitableFileFound, prev3.FailureReason);
        }

        [TestMethod]
        public void Index_IndexRoundTrip_PreservesData()
        {
            var tracks = new List<Track>
            {
                new() { Artist = "Artist1", Title = "Title1", DownloadPath = "path/to/file1", State = TrackState.Downloaded },
                new() { Artist = "Artist2", Title = "Title2", State = TrackState.Failed, FailureReason = FailureReason.NoSuitableFileFound },
                new() { Artist = "Artist3", Title = "Title3" },
            };

            var trackLists = new TrackLists();
            trackLists.AddEntry(new TrackListEntry(TrackType.Normal));
            foreach (var t in tracks)
                trackLists.AddTrackToLast(t);

            File.WriteAllText(testM3uPath, "");
            var editor = new M3uEditor(testM3uPath, trackLists, M3uOption.Index, true);
            editor.Update();

            // Load back with a fresh editor
            var trackLists2 = new TrackLists();
            trackLists2.AddEntry(new TrackListEntry(TrackType.Normal));
            foreach (var t in tracks)
                trackLists2.AddTrackToLast(new Track { Artist = t.Artist, Title = t.Title });

            var editor2 = new M3uEditor(testM3uPath, trackLists2, M3uOption.Index, true);

            // Verify downloaded track round-tripped
            editor2.TryGetPreviousRunResult(tracks[0], out var prev1);
            Assert.IsNotNull(prev1);
            Assert.AreEqual(TrackState.Downloaded, prev1.State);
            Assert.AreEqual("path/to/file1", prev1.DownloadPath);

            // Verify failed track round-tripped
            editor2.TryGetPreviousRunResult(tracks[1], out var prev2);
            Assert.IsNotNull(prev2);
            Assert.AreEqual(TrackState.Failed, prev2.State);
            Assert.AreEqual(FailureReason.NoSuitableFileFound, prev2.FailureReason);

            // Initial track should not be in previous run data (state is Initial, it was skipped)
            editor2.TryGetPreviousRunResult(tracks[2], out var prev3);
            Assert.IsNull(prev3);
        }

        [TestMethod]
        public void Index_WithAlbumTracks_RoundTripsCorrectly()
        {
            var albumTracks = new List<Track>
            {
                new() { Artist = "ArtistA", Album = "AlbumA", Type = TrackType.Album },
                new() { Artist = "ArtistB", Album = "AlbumB", Type = TrackType.Album },
                new() { Artist = "ArtistC", Album = "AlbumC", Type = TrackType.Album },
            };

            var tl = new TrackLists();
            foreach (var t in albumTracks)
                tl.AddEntry(new TrackListEntry(t));

            File.WriteAllText(testM3uPath, "");
            var editor = new M3uEditor(testM3uPath, tl, M3uOption.Index, true);

            // Update album states
            albumTracks[0].State = TrackState.Downloaded;
            albumTracks[0].DownloadPath = "download/path";
            albumTracks[1].State = TrackState.Failed;
            albumTracks[1].FailureReason = FailureReason.NoSuitableFileFound;
            albumTracks[2].State = TrackState.AlreadyExists;

            editor.Update();

            // Read back with new editor
            var tl2 = new TrackLists();
            foreach (var t in albumTracks)
                tl2.AddEntry(new TrackListEntry(new Track { Artist = t.Artist, Album = t.Album, Type = TrackType.Album }));

            var editor2 = new M3uEditor(testM3uPath, tl2, M3uOption.Index, true);

            foreach (var t in albumTracks)
            {
                editor2.TryGetPreviousRunResult(t, out var prevTrack);
                Assert.IsNotNull(prevTrack, $"Previous run result not found for {t.Artist} - {t.Album}");
                Assert.AreEqual(t.ToKey(), prevTrack.ToKey());

                // Verify prevTrack is a separate copy
                string originalPath = t.DownloadPath;
                t.DownloadPath = "this should not change prevTrack.DownloadPath";
                Assert.AreNotEqual(t.DownloadPath, prevTrack.DownloadPath);
                t.DownloadPath = originalPath;
            }
        }

        [TestMethod]
        public void Index_SpecialCharacters_RoundTripCorrectly()
        {
            var tracks = new List<Track>
            {
                new() { Artist = "Artist, with commas", Title = "Title \"with\" quotes", DownloadPath = "path/file.mp3", State = TrackState.Downloaded },
                new() { Artist = "Artist; semi", Title = "Title; semi", State = TrackState.Failed, FailureReason = FailureReason.AllDownloadsFailed },
            };

            var trackLists = new TrackLists();
            trackLists.AddEntry(new TrackListEntry(TrackType.Normal));
            foreach (var t in tracks)
                trackLists.AddTrackToLast(t);

            File.WriteAllText(testM3uPath, "");
            var editor = new M3uEditor(testM3uPath, trackLists, M3uOption.Index, true);
            editor.Update();

            // Load back
            var trackLists2 = new TrackLists();
            trackLists2.AddEntry(new TrackListEntry(TrackType.Normal));
            foreach (var t in tracks)
                trackLists2.AddTrackToLast(new Track { Artist = t.Artist, Title = t.Title });

            var editor2 = new M3uEditor(testM3uPath, trackLists2, M3uOption.Index, true);

            editor2.TryGetPreviousRunResult(tracks[0], out var prev1);
            Assert.IsNotNull(prev1);
            Assert.AreEqual("Artist, with commas", prev1.Artist);
            Assert.AreEqual("Title \"with\" quotes", prev1.Title);

            editor2.TryGetPreviousRunResult(tracks[1], out var prev2);
            Assert.IsNotNull(prev2);
            Assert.AreEqual("Artist; semi", prev2.Artist);
            Assert.AreEqual(FailureReason.AllDownloadsFailed, prev2.FailureReason);
        }

        [TestMethod]
        public void Index_TryGetFailureReason_ReturnsCorrectReason()
        {
            var tracks = new List<Track>
            {
                new() { Artist = "A1", Title = "T1", State = TrackState.Failed, FailureReason = FailureReason.NoSuitableFileFound },
                new() { Artist = "A2", Title = "T2", State = TrackState.Downloaded, DownloadPath = "p" },
            };

            var trackLists = new TrackLists();
            trackLists.AddEntry(new TrackListEntry(TrackType.Normal));
            foreach (var t in tracks)
                trackLists.AddTrackToLast(t);

            File.WriteAllText(testM3uPath, "");
            var editor = new M3uEditor(testM3uPath, trackLists, M3uOption.Index, true);
            editor.Update();

            // Reload
            var trackLists2 = new TrackLists();
            trackLists2.AddEntry(new TrackListEntry(TrackType.Normal));
            foreach (var t in tracks)
                trackLists2.AddTrackToLast(new Track { Artist = t.Artist, Title = t.Title });

            var editor2 = new M3uEditor(testM3uPath, trackLists2, M3uOption.Index, true);

            Assert.IsTrue(editor2.TryGetFailureReason(tracks[0], out var reason));
            Assert.AreEqual(FailureReason.NoSuitableFileFound, reason);

            Assert.IsFalse(editor2.TryGetFailureReason(tracks[1], out _));
        }
    }
}

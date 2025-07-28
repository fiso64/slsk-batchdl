//using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Tests.M3u
{
    //[TestClass]
    //public class M3uTests
    //{
    //    private string testM3uPath;
    //    private Config config;
    //    private List<Track> notFoundInitial;
    //    private List<Track> existingInitial;
    //    private List<Track> toBeDownloadedInitial;
    //    private TrackLists trackLists;

    //    [TestInitialize]
    //    public void Setup()
    //    {
    //        testM3uPath = Path.Join(Directory.GetCurrentDirectory(), "test_m3u.m3u8");

    //        config = new Config(new string[] { });
    //        config.skipMode = SkipMode.Index;
    //        config.skipMusicDir = "";
    //        config.printOption = PrintOption.Tracks | PrintOption.Full;
    //        config.skipExisting = true;

    //        notFoundInitial = new List<Track>()
    //        {
    //            new() { Artist = "Artist; ,3", Title = "Title3 ;a" },
    //            new() { Artist = "Artist,,, ;4", Title = "Title4", State = TrackState.Failed, FailureReason = FailureReason.NoSuitableFileFound }
    //        };

    //        existingInitial = new List<Track>()
    //        {
    //            new() { Artist = "Artist, 1", Title = "Title, , 1", DownloadPath = "path/to/file1", State = TrackState.Downloaded },
    //            new() { Artist = "Artist, 1.5", Title = "Title, , 1.5", DownloadPath = Path.Join(Directory.GetCurrentDirectory(), "file1.5"), State = TrackState.Downloaded },
    //            new() { Artist = "Artist, 2", Title = "Title2", DownloadPath = "path/to/file2", State = TrackState.AlreadyExists }
    //        };

    //        toBeDownloadedInitial = new List<Track>()
    //        {
    //            new() { Artist = "ArtistA", Album = "Albumm", Title = "TitleA" },
    //            new() { Artist = "ArtistB", Album = "Albumm", Title = "TitleB" }
    //        };

    //        trackLists = new TrackLists();
    //        trackLists.AddEntry(new TrackListEntry(TrackType.Normal));
    //        foreach (var t in notFoundInitial)
    //            trackLists.AddTrackToLast(t);
    //        foreach (var t in existingInitial)
    //            trackLists.AddTrackToLast(t);
    //        foreach (var t in toBeDownloadedInitial)
    //            trackLists.AddTrackToLast(t);
    //    }

    //    [TestCleanup]
    //    public void Cleanup()
    //    {
    //        if (File.Exists(testM3uPath))
    //            File.Delete(testM3uPath);
    //    }

    //    [TestMethod]
    //    public void M3uEditor_WithInitialTracks_UpdatesFileCorrectly()
    //    {
    //        string initialContent = $"#SLDL:" +
    //            $"{Path.Join(Directory.GetCurrentDirectory(), "file1.5")},\"Artist, 1.5\",,\"Title, , 1.5\",-1,0,3,0;" +
    //            $"path/to/file1,\"Artist, 1\",,\"Title, , 1\",-1,0,3,0;" +
    //            $"path/to/file2,\"Artist, 2\",,Title2,-1,0,3,0;" +
    //            $",\"Artist; ,3\",,Title3 ;a,-1,0,4,0;" +
    //            $",\"Artist,,, ;4\",,Title4,-1,0,4,3;" +
    //            $",,,,-1,0,0,0;";

    //        File.WriteAllText(testM3uPath, initialContent);

    //        var editor = new M3uEditor(testM3uPath, trackLists, M3uOption.All);
    //        trackLists[0].indexEditor = editor;
    //        trackLists[0].outputDirSkipper = new IndexSkipper();

    //        var notFound = trackLists[0].list[0].Where(t => t.State == TrackState.Failed).ToList();
    //        var existing = trackLists[0].list[0].Where(t => t.State == TrackState.Downloaded || t.State == TrackState.AlreadyExists).ToList();
    //        var toBeDownloaded = trackLists[0].list[0].Where(t => t.State == TrackState.Initial).ToList();

    //        CollectionAssert.AreEquivalent(notFoundInitial, notFound);
    //        CollectionAssert.AreEquivalent(existingInitial, existing);
    //        CollectionAssert.AreEquivalent(toBeDownloadedInitial, toBeDownloaded);

    //        editor.Update();
    //        string output = File.ReadAllText(testM3uPath);
    //        string expectedOutput =
    //            "#SLDL:./file1.5,\"Artist, 1.5\",,\"Title, , 1.5\",-1,0,3,0;path/to/file1,\"Artist, 1\",,\"Title, , 1\",-1,0,3,0;path/to/file2,\"Artist, 2\",,Title2,-1,0,3,0;,\"Artist; ,3\",,Title3 ;a,-1,0,4,0;,\"Artist,,, ;4\",,Title4,-1,0,4,3;,,,,-1,0,0,0;" +
    //            "\n" +
    //            "\n#FAIL: Artist; ,3 - Title3 ;a [NoSuitableFileFound]" +
    //            "\n#FAIL: Artist,,, ;4 - Title4 [NoSuitableFileFound]" +
    //            "\npath/to/file1" +
    //            "\nfile1.5" +
    //            "\npath/to/file2" +
    //            "\n";

    //        Assert.AreEqual(expectedOutput, output);
    //    }

    //    [TestMethod]
    //    public void M3uEditor_WithUpdatedTracks_UpdatesFileCorrectly()
    //    {
    //        string initialContent = $"#SLDL:" +
    //            $"{Path.Join(Directory.GetCurrentDirectory(), "file1.5")},\"Artist, 1.5\",,\"Title, , 1.5\",-1,0,3,0;" +
    //            $"path/to/file1,\"Artist, 1\",,\"Title, , 1\",-1,0,3,0;" +
    //            $"path/to/file2,\"Artist, 2\",,Title2,-1,0,3,0;" +
    //            $",\"Artist; ,3\",,Title3 ;a,-1,0,4,0;" +
    //            $",\"Artist,,, ;4\",,Title4,-1,0,4,3;" +
    //            $",,,,-1,0,0,0;";

    //        File.WriteAllText(testM3uPath, initialContent);

    //        var editor = new M3uEditor(testM3uPath, trackLists, M3uOption.All);
    //        trackLists[0].indexEditor = editor;

    //        // Update track states
    //        toBeDownloadedInitial[0].State = TrackState.Downloaded;
    //        toBeDownloadedInitial[0].DownloadPath = "new/file/path";
    //        toBeDownloadedInitial[1].State = TrackState.Failed;
    //        toBeDownloadedInitial[1].FailureReason = FailureReason.NoSuitableFileFound;
    //        existingInitial[1].DownloadPath = "/other/new/file/path";

    //        editor.Update();
    //        string output = File.ReadAllText(testM3uPath);
    //        string expectedOutput =
    //            "#SLDL:/other/new/file/path,\"Artist, 1.5\",,\"Title, , 1.5\",-1,0,3,0;path/to/file1,\"Artist, 1\",,\"Title, , 1\",-1,0,3,0;path/to/file2,\"Artist, 2\",,Title2,-1,0,3,0;,\"Artist; ,3\",,Title3 ;a,-1,0,4,0;,\"Artist,,, ;4\",,Title4,-1,0,4,3;" +
    //            ",,,,-1,0,0,0;new/file/path,ArtistA,Albumm,TitleA,-1,0,1,0;,ArtistB,Albumm,TitleB,-1,0,2,3;" +
    //            "\n" +
    //            "\n#FAIL: Artist; ,3 - Title3 ;a [NoSuitableFileFound]" +
    //            "\n#FAIL: Artist,,, ;4 - Title4 [NoSuitableFileFound]" +
    //            "\npath/to/file1" +
    //            "\n/other/new/file/path" +
    //            "\npath/to/file2" +
    //            "\nnew/file/path" +
    //            "\n#FAIL: ArtistB - TitleB [NoSuitableFileFound]" +
    //            "\n";

    //        Assert.AreEqual(expectedOutput, output);
    //    }

    //    [TestMethod]
    //    public void M3uEditor_WithAlbumTracks_HandlesAlbumsCorrectly()
    //    {
    //        var albumTracks = new List<Track>
    //        {
    //            new() { Artist = "ArtistA", Album = "AlbumA", Type = TrackType.Album },
    //            new() { Artist = "ArtistB", Album = "AlbumB", Type = TrackType.Album },
    //            new() { Artist = "ArtistC", Album = "AlbumC", Type = TrackType.Album },
    //        };

    //        trackLists = new TrackLists();
    //        foreach (var t in albumTracks)
    //            trackLists.AddEntry(new TrackListEntry(t));

    //        File.WriteAllText(testM3uPath, "");
    //        var editor = new M3uEditor(testM3uPath, trackLists, M3uOption.Index);
    //        editor.Update();

    //        Assert.AreEqual("", File.ReadAllText(testM3uPath));

    //        albumTracks[0].State = TrackState.Downloaded;
    //        albumTracks[0].DownloadPath = "download/path";
    //        albumTracks[1].State = TrackState.Failed;
    //        albumTracks[1].FailureReason = FailureReason.NoSuitableFileFound;
    //        albumTracks[2].State = TrackState.AlreadyExists;

    //        editor.Update();

    //        editor = new M3uEditor(testM3uPath, trackLists, M3uOption.Index);

    //        foreach (var t in albumTracks)
    //        {
    //            editor.TryGetPreviousRunResult(t, out var prevTrack);
    //            Assert.IsNotNull(prevTrack);
    //            Assert.AreEqual(t.ToKey(), prevTrack.ToKey());

    //            string originalPath = t.DownloadPath;
    //            t.DownloadPath = "this should not change prevTrack.DownloadPath";
    //            Assert.AreNotEqual(t.DownloadPath, prevTrack.DownloadPath);
    //            t.DownloadPath = originalPath;
    //        }
    //    }
    //}
}
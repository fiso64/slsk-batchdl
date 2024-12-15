using Models;
using Enums;
using FileSkippers;
using System.Diagnostics;
using System.Reflection;

using static Tests.Helpers;

namespace Tests
{
    static class Test
    {
        public static async Task RunAllTests()
        {
            TestStringUtils();
            //TestAutoProfiles();
            //TestProfileConditions();
            //await TestStringExtractor();
            //TestM3uEditor();

            Console.WriteLine('\n' + new string('#', 50) + '\n' + "All tests passed.");
        }

        public static void TestStringUtils()
        {
            SetCurrentTest("TestStringUtils");

            // RemoveFt
            Assert("blah blah ft. blah blah".RemoveFt() == "blah blah");
            Assert("blah blah feat. blah blah".RemoveFt() == "blah blah");
            Assert("blah (feat. blah blah) blah".RemoveFt() == "blah blah");
            Assert("blah (ft. blah blah) blah".RemoveFt() == "blah blah");

            // RemoveConsecutiveWs
            Assert(" blah    blah  blah blah ".RemoveConsecutiveWs() == " blah blah blah blah ");

            // RemoveSquareBrackets
            Assert("foo [aaa] bar".RemoveSquareBrackets() == "foo  bar");

            // ReplaceInvalidChars
            Assert("Invalid chars: \\/:|?<>*\"".ReplaceInvalidChars("", true) == "Invalid chars ");
            Assert("Invalid chars: \\/:|?<>*\"".ReplaceInvalidChars("", true, false) == "Invalid chars \\/");

            // ContainsWithBoundary
            Assert("foo blah bar".ContainsWithBoundary("blah"));
            Assert("foo/blah/bar".ContainsWithBoundary("blah"));
            Assert("foo - blah 2".ContainsWithBoundary("blah"));
            Assert(!"foo blah bar".ContainsWithBoundaryIgnoreWs("blah"));
            Assert(!"foo - blah 2".ContainsWithBoundaryIgnoreWs("blah"));
            Assert("foo - blah 2 - bar".ContainsWithBoundaryIgnoreWs("blah 2"));
            Assert("foo/blah/bar".ContainsWithBoundaryIgnoreWs("blah"));
            Assert("01 blah".ContainsWithBoundaryIgnoreWs("blah", acceptLeftDigit: true));
            Assert(!"foo - blah 2blah".ContainsWithBoundaryIgnoreWs("blah", acceptLeftDigit: true));
            Assert("foo - blah 2 blah".ContainsWithBoundaryIgnoreWs("blah", acceptLeftDigit: true));

            // GreatestCommonPath
            var paths = new string[]
            {
                "/home/user/docs/nested/file",
                "/home/user/docs/nested/folder/",
                "/home/user/docs/letter.txt",
                "/home/user/docs/report.pdf",
                "/home/user/docs/",
            };
            Assert(Utils.GreatestCommonPath(paths) == "/home/user/docs/");
            Assert(Utils.GreatestCommonPath(new string[] { "/path/file", "" }) == "");
            Assert(Utils.GreatestCommonPath(new string[] { "/path/file", "/" }) == "/");
            Assert(Utils.GreatestCommonPath(new string[] { "/path/dir1", "/path/dir2" }) == "/path/");
            Assert(Utils.GreatestCommonPath(new string[] { "/path\\dir1/blah", "/path/dir2\\blah" }) == "/path\\");
            Assert(Utils.GreatestCommonPath(new string[] { "dir1", "dir2" }) == "");

            // RemoveDiacritics
            Assert(" Café Crème à la mode Ü".RemoveDiacritics() == " Cafe Creme a la mode U");

            Passed();
        }

        //public static void TestAutoProfiles()
        //{
        //    SetCurrentTest("TestAutoProfiles");

        //    var config = new Config();
        //    config.inputType = InputType.YouTube;
        //    config.interactiveMode = true;
        //    config.aggregate = false;
        //    config.maxStaleTime = 50000;

        //    string path = Path.Join(Directory.GetCurrentDirectory(), "test_conf.conf");

        //    string content =
        //        "max-stale-time = 5" +
        //        "\nfast-search = true" +
        //        "\nformat = flac" +

        //        "\n[profile-true-1]" +
        //        "\nprofile-cond = input-type == \"youtube\" && download-mode == \"album\"" +
        //        "\nmax-stale-time = 10" +

        //        "\n[profile-true-2]" +
        //        "\nprofile-cond = !aggregate" +
        //        "\nfast-search = false" +

        //        "\n[profile-false-1]" +
        //        "\nprofile-cond = input-type == \"string\"" +
        //        "\nformat = mp3" +

        //        "\n[profile-no-cond]" +
        //        "\nformat = opus";

        //    File.WriteAllText(path, content);

        //    config.LoadAndParse(new string[] { "-c", path });

        //    var tle = new TrackListEntry(TrackType.Album);
        //    Config.UpdateProfiles(tle);

        //    Assert(config.maxStaleTime == 10 && !config.fastSearch && config.necessaryCond.Formats[0] == "flac");

        //    ResetConfig();
        //    config.inputType = InputType.CSV;
        //    config.album = true;
        //    config.interactiveMode = true;
        //    config.useYtdlp = false;
        //    config.maxStaleTime = 50000;
        //    content =
        //        "\n[no-stale]" +
        //        "\nprofile-cond = interactive && download-mode == \"album\"" +
        //        "\nmax-stale-time = 999999" +
        //        "\n[youtube]" +
        //        "\nprofile-cond = input-type == \"youtube\"" +
        //        "\nyt-dlp = true";

        //    File.WriteAllText(path, content);


        //    config.LoadAndParse(new string[] { "-c", path });
        //    Config.UpdateProfiles(tle);
        //    Assert(config.maxStaleTime == 999999 && !config.useYtdlp);

        //    ResetConfig();
        //    config.inputType = InputType.YouTube;
        //    config.album = false;
        //    config.interactiveMode = true;
        //    config.useYtdlp = false;
        //    config.maxStaleTime = 50000;
        //    content =
        //        "\n[no-stale]" +
        //        "\nprofile-cond = interactive && download-mode == \"album\"" +
        //        "\nmax-stale-time = 999999" +
        //        "\n[youtube]" +
        //        "\nprofile-cond = input-type == \"youtube\"" +
        //        "\nyt-dlp = true";

        //    File.WriteAllText(path, content);
        //    config.LoadAndParse(new string[] { "-c", path });
        //    Config.UpdateProfiles(new TrackListEntry(TrackType.Normal));

        //    Assert(config.maxStaleTime == 50000 && config.useYtdlp);

        //    if (File.Exists(path))
        //        File.Delete(path);

        //    Passed();
        //}

        //public static void TestProfileConditions()
        //{
        //    SetCurrentTest("TestProfileConditions");

        //    config.inputType = InputType.YouTube;
        //    config.interactiveMode = true;
        //    config.album = true;
        //    config.aggregate = false;

        //    var conds = new (bool, string)[]
        //    {
        //        (true,  "input-type == \"youtube\""),
        //        (true,  "download-mode == \"album\""),
        //        (false, "aggregate"),
        //        (true,  "interactive"),
        //        (true,  "album"),
        //        (false, "!interactive"),
        //        (true,  "album && input-type == \"youtube\""),
        //        (false, "album && input-type != \"youtube\""),
        //        (false, "(interactive && aggregate)"),
        //        (true,  "album && (interactive || aggregate)"),
        //        (true,  "input-type == \"spotify\" || aggregate || input-type == \"csv\" || interactive && album"),
        //        (true,  "    input-type!=\"youtube\"||(album&&!interactive  ||(aggregate    ||    interactive  )  )"),
        //        (false, "    input-type!=\"youtube\"||(album&&!interactive  ||(aggregate    ||    !interactive  )  )"),
        //    };

        //    foreach ((var b, var c) in conds)
        //    {
        //        Console.WriteLine(c);
        //        Assert(b == config.ProfileConditionSatisfied(c));
        //    }

        //    Passed();
        //}

        //public static async Task TestStringExtractor()
        //{
        //    SetCurrentTest("TestStringExtractor");

        //    var strings = new List<string>()
        //    {
        //        "Some Title",
        //        "Some, Title",
        //        "artist = Some artist, title = some title",
        //        "Artist - Title, length = 42",
        //        "title=Some, Title, artist=Some, Artist, album = Some, Album, length= 42",
        //        "Some, Artist = a - Some, Title = b, album = Some, Album, length = 42",

        //        "Foo Bar",
        //        "Foo - Bar",
        //        "Artist - Title, length=42",
        //        "title=Title, artist=Artist, length=42",
        //    };

        //    var tracks = new List<Track>()
        //    {
        //        new Track() { Title="Some Title" },
        //        new Track() { Title="Some, Title" },
        //        new Track() { Title = "some title", Artist = "Some artist" },
        //        new Track() { Title = "Title", Artist = "Artist", Length = 42 },
        //        new Track() { Title="Some, Title", Artist = "Some, Artist", Album = "Some, Album", Length = 42 },
        //        new Track() { Title="Some, Title = b", Artist = "Some, Artist = a", Album = "Some, Album", Length = 42 },

        //        new Track() { Title = "Foo Bar" },
        //        new Track() { Title = "Bar", Artist = "Foo" },
        //        new Track() { Title = "Title", Artist = "Artist", Length = 42 },
        //        new Track() { Title = "Title", Artist = "Artist", Length = 42 },
        //    };

        //    var albums = new List<Track>()
        //    {
        //        new Track() { Album="Some Title", Type = TrackType.Album },
        //        new Track() { Album="Some, Title", Type = TrackType.Album },
        //        new Track() { Title = "some title", Artist = "Some artist", Type = TrackType.Album },
        //        new Track() { Album = "Title", Artist = "Artist", Length = 42, Type = TrackType.Album },
        //        new Track() { Title="Some, Title", Artist = "Some, Artist", Album = "Some, Album", Length = 42, Type = TrackType.Album },
        //        new Track() { Artist = "Some, Artist = a", Album = "Some, Album", Length = 42, Type = TrackType.Album },

        //        new Track() { Album = "Foo Bar", Type = TrackType.Album },
        //        new Track() { Album = "Bar", Artist = "Foo", Type = TrackType.Album },
        //        new Track() { Album = "Title", Artist = "Artist", Length = 42, Type = TrackType.Album },
        //        new Track() { Title = "Title", Artist = "Artist", Length = 42, Type = TrackType.Album },
        //    };

        //    var extractor = new Extractors.StringExtractor();

        //    config.aggregate = false;
        //    config.album = false;

        //    Console.WriteLine("Testing songs: ");
        //    for (int i = 0; i < strings.Count; i++)
        //    {
        //        config.input = strings[i];
        //        Console.WriteLine(config.input);
        //        var res = await extractor.GetTracks(config.input, 0, 0, false);
        //        var t = res[0].list[0][0];
        //        Assert(Extractors.StringExtractor.InputMatches(config.input));
        //        Assert(t.ToKey() == tracks[i].ToKey());
        //    }

        //    Console.WriteLine();
        //    Console.WriteLine("Testing albums");
        //    config.album = true;
        //    for (int i = 0; i < strings.Count; i++)
        //    {
        //        config.input = strings[i];
        //        Console.WriteLine(config.input);
        //        var t = (await extractor.GetTracks(config.input, 0, 0, false))[0].source;
        //        Assert(Extractors.StringExtractor.InputMatches(config.input));
        //        Assert(t.ToKey() == albums[i].ToKey());
        //    }

        //    Passed();
        //}

        //public static void TestM3uEditor()
        //{
        //    SetCurrentTest("TestM3uEditor");

        //    config.skipMode = SkipMode.Index;
        //    config.skipMusicDir = "";
        //    config.printOption = PrintOption.Tracks | PrintOption.Full;
        //    config.skipExisting = true;

        //    string path = Path.Join(Directory.GetCurrentDirectory(), "test_m3u.m3u8");

        //    if (File.Exists(path))
        //        File.Delete(path);

        //    File.WriteAllText(path, $"#SLDL:" +
        //        $"{Path.Join(Directory.GetCurrentDirectory(), "file1.5")},\"Artist, 1.5\",,\"Title, , 1.5\",-1,0,3,0;" +
        //        $"path/to/file1,\"Artist, 1\",,\"Title, , 1\",-1,0,3,0;" +
        //        $"path/to/file2,\"Artist, 2\",,Title2,-1,0,3,0;" +
        //        $",\"Artist; ,3\",,Title3 ;a,-1,0,4,0;" +
        //        $",\"Artist,,, ;4\",,Title4,-1,0,4,3;" +
        //        $",,,,-1,0,0,0;");

        //    var notFoundInitial = new List<Track>()
        //    {
        //        new() { Artist = "Artist; ,3", Title = "Title3 ;a" },
        //        new() { Artist = "Artist,,, ;4", Title = "Title4", State = TrackState.Failed, FailureReason = FailureReason.NoSuitableFileFound }
        //    };
        //    var existingInitial = new List<Track>()
        //    {
        //        new() { Artist = "Artist, 1", Title = "Title, , 1", DownloadPath = "path/to/file1", State = TrackState.Downloaded },
        //        new() { Artist = "Artist, 1.5", Title = "Title, , 1.5", DownloadPath = Path.Join(Directory.GetCurrentDirectory(), "file1.5"), State = TrackState.Downloaded },
        //        new() { Artist = "Artist, 2", Title = "Title2", DownloadPath = "path/to/file2", State = TrackState.AlreadyExists }
        //    };
        //    var toBeDownloadedInitial = new List<Track>()
        //    {
        //        new() { Artist = "ArtistA", Album = "Albumm", Title = "TitleA" },
        //        new() { Artist = "ArtistB", Album = "Albumm", Title = "TitleB" }
        //    };

        //    var trackLists = new TrackLists();
        //    trackLists.AddEntry(new TrackListEntry(TrackType.Normal));
        //    foreach (var t in notFoundInitial)
        //        trackLists.AddTrackToLast(t);
        //    foreach (var t in existingInitial)
        //        trackLists.AddTrackToLast(t);
        //    foreach (var t in toBeDownloadedInitial)
        //        trackLists.AddTrackToLast(t);

        //    Program.indexEditor = new M3uEditor(path, trackLists, M3uOption.All);

        //    Program.outputDirSkipper = new IndexSkipper(Program.indexEditor, false);

        //    var notFound = (List<Track>)ProgramInvoke("DoSkipNotFound", new object[] { trackLists[0].list[0] });
        //    var existing = (List<Track>)ProgramInvoke("DoSkipExisting", new object[] { trackLists[0].list[0] });
        //    var toBeDownloaded = trackLists[0].list[0].Where(t => t.State == TrackState.Initial).ToList();

        //    Assert(notFound.SequenceEqualUpToPermutation(notFoundInitial));
        //    Assert(existing.SequenceEqualUpToPermutation(existingInitial));
        //    Assert(toBeDownloaded.SequenceEqualUpToPermutation(toBeDownloadedInitial));

        //    Printing.PrintTracksTbd(toBeDownloaded, existing, notFound, TrackType.Normal);

        //    Program.indexEditor.Update();
        //    string output = File.ReadAllText(path);
        //    string need =
        //        "#SLDL:./file1.5,\"Artist, 1.5\",,\"Title, , 1.5\",-1,0,3,0;path/to/file1,\"Artist, 1\",,\"Title, , 1\",-1,0,3,0;path/to/file2,\"Artist, 2\",,Title2,-1,0,3,0;,\"Artist; ,3\",,Title3 ;a,-1,0,4,0;,\"Artist,,, ;4\",,Title4,-1,0,4,3;,,,,-1,0,0,0;" +
        //        "\n" +
        //        "\n#FAIL: Artist; ,3 - Title3 ;a [NoSuitableFileFound]" +
        //        "\n#FAIL: Artist,,, ;4 - Title4 [NoSuitableFileFound]" +
        //        "\npath/to/file1" +
        //        "\nfile1.5" +
        //        "\npath/to/file2" +
        //        "\n";
        //    Assert(output == need);

        //    toBeDownloaded[0].State = TrackState.Downloaded;
        //    toBeDownloaded[0].DownloadPath = "new/file/path";
        //    toBeDownloaded[1].State = TrackState.Failed;
        //    toBeDownloaded[1].FailureReason = FailureReason.NoSuitableFileFound;
        //    existing[1].DownloadPath = "/other/new/file/path";

        //    Program.indexEditor.Update();
        //    output = File.ReadAllText(path);
        //    need =
        //        "#SLDL:/other/new/file/path,\"Artist, 1.5\",,\"Title, , 1.5\",-1,0,3,0;path/to/file1,\"Artist, 1\",,\"Title, , 1\",-1,0,3,0;path/to/file2,\"Artist, 2\",,Title2,-1,0,3,0;,\"Artist; ,3\",,Title3 ;a,-1,0,4,0;,\"Artist,,, ;4\",,Title4,-1,0,4,3;" +
        //        ",,,,-1,0,0,0;new/file/path,ArtistA,Albumm,TitleA,-1,0,1,0;,ArtistB,Albumm,TitleB,-1,0,2,3;" +
        //        "\n" +
        //        "\n#FAIL: Artist; ,3 - Title3 ;a [NoSuitableFileFound]" +
        //        "\n#FAIL: Artist,,, ;4 - Title4 [NoSuitableFileFound]" +
        //        "\npath/to/file1" +
        //        "\n/other/new/file/path" +
        //        "\npath/to/file2" +
        //        "\nnew/file/path" +
        //        "\n#FAIL: ArtistB - TitleB [NoSuitableFileFound]" +
        //        "\n";
        //    Assert(output == need);

        //    Console.WriteLine();
        //    Console.WriteLine(output);

        //    Program.indexEditor = new M3uEditor(path, trackLists, M3uOption.All);

        //    foreach (var t in trackLists.Flattened(false, false))
        //    {
        //        Program.indexEditor.TryGetPreviousRunResult(t, out var prev);
        //        Assert(prev != null);
        //        Assert(prev.ToKey() == t.ToKey());
        //        Assert(prev.DownloadPath == t.DownloadPath);
        //        Assert(prev.State == t.State || prev.State == TrackState.NotFoundLastTime);
        //        Assert(prev.FailureReason == t.FailureReason);
        //    }

        //    Program.indexEditor.Update();
        //    output = File.ReadAllText(path);
        //    Assert(output == need);


        //    var test = new List<Track>
        //    {
        //        new() { Artist = "ArtistA", Album = "AlbumA", Type = TrackType.Album },
        //        new() { Artist = "ArtistB", Album = "AlbumB", Type = TrackType.Album },
        //        new() { Artist = "ArtistC", Album = "AlbumC", Type = TrackType.Album },
        //    };

        //    trackLists = new TrackLists();
        //    foreach (var t in test)
        //        trackLists.AddEntry(new TrackListEntry(t));

        //    File.WriteAllText(path, "");
        //    Program.indexEditor = new M3uEditor(path, trackLists, M3uOption.Index);
        //    Program.indexEditor.Update();

        //    Assert(File.ReadAllText(path) == "");

        //    test[0].State = TrackState.Downloaded;
        //    test[0].DownloadPath = "download/path";
        //    test[1].State = TrackState.Failed;
        //    test[1].FailureReason = FailureReason.NoSuitableFileFound;
        //    test[2].State = TrackState.AlreadyExists;

        //    Program.indexEditor.Update();

        //    Program.indexEditor = new M3uEditor(path, trackLists, M3uOption.Index);

        //    foreach (var t in test)
        //    {
        //        Program.indexEditor.TryGetPreviousRunResult(t, out var tt);
        //        Assert(tt != null);
        //        Assert(tt.ToKey() == t.ToKey());
        //        t.DownloadPath = "this should not change tt.DownloadPath";
        //        Assert(t.DownloadPath != tt.DownloadPath);
        //    }

        //    File.Delete(path);

        //    Passed();
        //}
    }

    static class Helpers
    {
        static string? currentTest = null;

        public static void SetCurrentTest(string name)
        {
            currentTest = name;
        }

        public static object? ProgramInvoke(string name, object[] parameters)
        {
            var method = typeof(Program).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            return method.Invoke(null, parameters);
        }

        public class AssertionError : Exception
        {
            public AssertionError() : base("Assertion failed.") { }
            public AssertionError(string message) : base(message) { }
        }

        public static void Assert(bool condition, string message = "Assertion failed")
        {
            if (!condition)
            {
                var stackTrace = new StackTrace(true);
                var frame = stackTrace.GetFrame(1);
                var fileName = frame.GetFileName();
                var lineNumber = frame.GetFileLineNumber();
                throw new AssertionError($"{currentTest}: {message} (at {fileName}:{lineNumber})");
            }
        }

        public static void ResetConfig()
        {
            var singletonType = typeof(Config);
            var instanceField = singletonType.GetField("Instance", BindingFlags.Static | BindingFlags.NonPublic);
            var constructor = singletonType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            var newInstance = constructor.Invoke(null);
            instanceField.SetValue(null, newInstance);
        }

        public static void Passed()
        {
            Console.WriteLine($"{currentTest} passed");
            currentTest = null;
        }
    }
}

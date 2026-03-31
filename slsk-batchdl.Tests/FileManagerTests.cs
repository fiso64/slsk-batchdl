using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;
using Enums;
using System.Reflection;

namespace Tests.FileManagerTests
{
    [TestClass]
    public class GetSavePathTests
    {
        private static FileManager MakeManager(Config? config = null, TrackListEntry? tle = null)
        {
            config ??= TestHelpers.CreateDefaultConfig();
            tle ??= new TrackListEntry(new Track()) { config = config };
            return new FileManager(tle, config);
        }

        [TestMethod]
        public void GetSavePath_NormalTrack_ReturnsParentDirPlusFilename()
        {
            var config = TestHelpers.CreateDefaultConfig();
            config.parentDir = "/music";
            var manager = MakeManager(config);

            string path = manager.GetSavePath("Music\\Artist\\Song.mp3");

            Assert.IsTrue(path.EndsWith("Song.mp3"));
            Assert.IsTrue(path.Contains("music"));
        }

        [TestMethod]
        public void GetSavePath_PreservesExtension()
        {
            var config = TestHelpers.CreateDefaultConfig();
            var manager = MakeManager(config);

            string path = manager.GetSavePath("folder\\track.flac");

            Assert.IsTrue(path.EndsWith(".flac"));
        }

        [TestMethod]
        public void GetSavePath_AlbumTrack_WithRemoteBaseDir_PreservesRelativePath()
        {
            var config = TestHelpers.CreateDefaultConfig();
            config.parentDir = "/music";
            var source = new Track { Type = TrackType.Album };
            var tle = new TrackListEntry(source) { config = config };
            var manager = new FileManager(tle, config);

            manager.SetremoteBaseDir("Music\\Artist\\Album");

            string path = manager.GetSavePath("Music\\Artist\\Album\\SubDir\\track.mp3");

            Assert.IsTrue(path.Contains("SubDir") || path.Contains(Path.DirectorySeparatorChar + "SubDir" + Path.DirectorySeparatorChar));
        }

        [TestMethod]
        public void GetSavePathNoExt_NoExtension_OmitsExtension()
        {
            var config = TestHelpers.CreateDefaultConfig();
            var manager = MakeManager(config);

            string path = manager.GetSavePathNoExt("folder\\track.mp3");

            Assert.IsFalse(path.EndsWith(".mp3"));
        }
    }

    [TestClass]
    public class GetFolderNameTests
    {
        private static string? InvokeGetFolderName(Soulseek.File? slfile, string? remoteBaseDir)
        {
            var method = typeof(FileManager).GetMethod("GetFolderName", BindingFlags.NonPublic | BindingFlags.Static);
            return (string?)method!.Invoke(null, new object?[] { slfile, remoteBaseDir });
        }

        [TestMethod]
        public void GetFolderName_BothNull_ReturnsEmpty()
        {
            var result = InvokeGetFolderName(null, null);
            Assert.AreEqual("", result);
        }

        [TestMethod]
        public void GetFolderName_OnlyRemoteBaseDir_ReturnsBasename()
        {
            var result = InvokeGetFolderName(null, "Music\\Artist\\Album");
            Assert.AreEqual("Album", result);
        }

        [TestMethod]
        public void GetFolderName_OnlySlFile_ReturnsParentDirName()
        {
            var file = TestHelpers.CreateSlFile("Music\\Artist\\Album\\track.mp3");
            var result = InvokeGetFolderName(file, null);
            Assert.AreEqual("Album", result);
        }

        [TestMethod]
        public void GetFolderName_BothSet_ReturnsRelativePath()
        {
            var file = TestHelpers.CreateSlFile("Music\\Artist\\Album\\SubDir\\track.mp3");
            var result = InvokeGetFolderName(file, "Music\\Artist\\Album");
            Assert.IsTrue(result!.Contains("Album"));
            Assert.IsTrue(result.Contains("SubDir"));
        }
    }

    [TestClass]
    public class TryGetCleanVarValueTests
    {
        private static TrackListEntry MakeTle(Config? config = null)
        {
            config ??= TestHelpers.CreateDefaultConfig();
            return new TrackListEntry(new Track()) { config = config };
        }

        [TestMethod]
        public void TryGetCleanVarValue_KnownNonTagVar_ReturnsTrue()
        {
            var tle = MakeTle();
            var track = TestHelpers.CreateTrack(title: "MyTitle");
            track.DownloadPath = "/music/file.mp3";

            bool found = FileManager.TryGetCleanVarValue("stitle", tle, () => null, null, track, null, " ", out string res);

            Assert.IsTrue(found);
            Assert.AreEqual("MyTitle", res);
        }

        [TestMethod]
        public void TryGetCleanVarValue_UnknownVar_ReturnsFalse()
        {
            var tle = MakeTle();
            var track = TestHelpers.CreateTrack();

            bool found = FileManager.TryGetCleanVarValue("nonexistent", tle, () => null, null, track, null, " ", out string res);

            Assert.IsFalse(found);
        }

        [TestMethod]
        public void TryGetCleanVarValue_SlskFilename_ReturnsFilenameWithoutExt()
        {
            var tle = MakeTle();
            var track = TestHelpers.CreateTrack();
            var file = TestHelpers.CreateSlFile("Music\\Artist\\Album\\My Track.mp3");

            bool found = FileManager.TryGetCleanVarValue("slsk-filename", tle, () => null, file, track, null, " ", out string res);

            Assert.IsTrue(found);
            Assert.AreEqual("My Track", res);
        }
    }

    [TestClass]
    public class HasTagVariablesTests
    {
        [TestMethod]
        public void HasTagVariables_ContainsArtist_ReturnsTrue()
        {
            Assert.IsTrue(FileManager.HasTagVariables("{artist}"));
        }

        [TestMethod]
        public void HasTagVariables_ContainsAlbum_ReturnsTrue()
        {
            Assert.IsTrue(FileManager.HasTagVariables("{albumartist}/{album}/{title}"));
        }

        [TestMethod]
        public void HasTagVariables_OnlyNonTagVars_ReturnsFalse()
        {
            Assert.IsFalse(FileManager.HasTagVariables("{slsk-filename}/{foldername}"));
        }

        [TestMethod]
        public void HasTagVariables_EmptyFormat_ReturnsFalse()
        {
            Assert.IsFalse(FileManager.HasTagVariables(""));
        }

        [TestMethod]
        public void HasTagVariables_PlainText_ReturnsFalse()
        {
            Assert.IsFalse(FileManager.HasTagVariables("just a plain string"));
        }
    }
}

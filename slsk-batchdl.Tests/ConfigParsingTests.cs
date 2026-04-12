using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;
using Jobs;
using Enums;
using Services;

namespace Tests.ConfigParsingTests
{
    [TestClass]
    public class DefaultValuesTests
    {
        [TestMethod]
        public void Defaults_NecessaryCondFormats_AreSet()
        {
            var config = TestHelpers.CreateDefaultConfig();
            CollectionAssert.IsSubsetOf(
                new[] { "mp3", "flac", "ogg" },
                config.necessaryCond.Formats);
        }

        [TestMethod]
        public void Defaults_PreferredCondBitrate_IsSet()
        {
            var config = TestHelpers.CreateDefaultConfig();
            Assert.IsNotNull(config.preferredCond.MinBitrate);
            Assert.IsNotNull(config.preferredCond.MaxBitrate);
            Assert.IsTrue(config.preferredCond.MinBitrate > 0);
        }

        [TestMethod]
        public void Defaults_PreferredCondLengthTolerance_IsSet()
        {
            var config = TestHelpers.CreateDefaultConfig();
            Assert.IsNotNull(config.preferredCond.LengthTolerance);
            Assert.IsTrue(config.preferredCond.LengthTolerance >= 0);
        }

        [TestMethod]
        public void Defaults_AlbumFalse_AggregateFalse()
        {
            var config = TestHelpers.CreateDefaultConfig();
            Assert.IsFalse(config.album);
            Assert.IsFalse(config.aggregate);
        }

        [TestMethod]
        public void Defaults_SkipExistingTrue()
        {
            var config = TestHelpers.CreateDefaultConfig();
            Assert.IsTrue(config.skipExisting);
        }

        [TestMethod]
        public void Defaults_DoNotDownload_FalseByDefault()
        {
            var config = TestHelpers.CreateDefaultConfig();
            Assert.IsFalse(config.DoNotDownload);
        }
    }

    [TestClass]
    public class ArgumentParsingTests
    {
        [TestMethod]
        public void Album_Flag_SetsAlbumTrue()
        {
            var config = new Config(new[] { "--config", "none", "--album", "some input" });
            Assert.IsTrue(config.album);
        }

        [TestMethod]
        public void Aggregate_Flag_SetsAggregateTrue()
        {
            var config = new Config(new[] { "--config", "none", "--aggregate", "some input" });
            Assert.IsTrue(config.aggregate);
        }

        [TestMethod]
        public void NameFormat_SetsValue()
        {
            var config = new Config(new[] { "--config", "none", "--name-format", "{artist}/{title}", "some input" });
            Assert.AreEqual("{artist}/{title}", config.nameFormat);
        }

        [TestMethod]
        public void MaxStaleTime_SetsValue()
        {
            var config = new Config(new[] { "--config", "none", "--max-stale-time", "60000", "some input" });
            Assert.AreEqual(60000, config.maxStaleTime);
        }

        [TestMethod]
        public void Format_SetsNecessaryCondFormats()
        {
            var config = new Config(new[] { "--config", "none", "--format", "mp3,flac", "some input" });
            CollectionAssert.AreEquivalent(new[] { "mp3", "flac" }, config.necessaryCond.Formats);
        }

        [TestMethod]
        public void MinBitrate_SetsNecessaryCondMinBitrate()
        {
            var config = new Config(new[] { "--config", "none", "--min-bitrate", "200", "some input" });
            Assert.AreEqual(200, config.necessaryCond.MinBitrate);
        }

        [TestMethod]
        public void MaxBitrate_SetsNecessaryCondMaxBitrate()
        {
            var config = new Config(new[] { "--config", "none", "--max-bitrate", "320", "some input" });
            Assert.AreEqual(320, config.necessaryCond.MaxBitrate);
        }

        [TestMethod]
        public void PrefFormat_SetsPreferredCondFormats()
        {
            var config = new Config(new[] { "--config", "none", "--pref-format", "flac", "some input" });
            CollectionAssert.AreEquivalent(new[] { "flac" }, config.preferredCond.Formats);
        }

        [TestMethod]
        public void PrefLengthTol_SetsPreferredCondTolerance()
        {
            var config = new Config(new[] { "--config", "none", "--pref-length-tol", "5", "some input" });
            Assert.AreEqual(5, config.preferredCond.LengthTolerance);
        }

        [TestMethod]
        public void Path_SetsParentDir()
        {
            var config = new Config(new[] { "--config", "none", "--path", "/tmp/music", "some input" });
            Assert.AreEqual("/tmp/music", config.parentDir);
        }
    }

    [TestClass]
    public class ComputedPropertiesTests
    {
        [TestMethod]
        public void DoNotDownload_TrueWhenPrintTracks()
        {
            var config = new Config(new[] { "--config", "none", "--print-tracks", "some input" });
            Assert.IsTrue(config.DoNotDownload);
            Assert.IsTrue(config.PrintTracks);
        }

        [TestMethod]
        public void DoNotDownload_TrueWhenPrintResults()
        {
            var config = new Config(new[] { "--config", "none", "--print-results", "some input" });
            Assert.IsTrue(config.DoNotDownload);
            Assert.IsTrue(config.PrintResults);
        }

        [TestMethod]
        public void NeedLogin_FalseWhenPrintIndex()
        {
            var config = new Config(new[] { "--config", "none", "--print", "index", "some input" });
            Assert.IsFalse(config.NeedLogin);
        }
    }

    // Confirms that --cond album-track-count=N flows from CLI parsing all the way through
    // the real Spotify/CSV + -a execution path: SongJob (created by extractor) → Upgrade() →
    // JobPreparer.PrepareSubtree() → Preprocessor.PreprocessAlbum() → AlbumQuery.
    [TestClass]
    public class FolderConditionCliPathTests
    {
        // Simulates: spotify input → SongJob created by extractor → Upgrade(album:true) →
        // PrepareSubtree on upgraded AlbumJob → PreprocessAlbum.
        static AlbumJob UpgradeAndPrepare(Config startConfig,
            SongQuery? query = null, bool aggregate = false)
        {
            var songJob = new SongJob(query ?? new SongQuery { Title = "Some Song", Artist = "Some Artist" });
            var albumJob = (AlbumJob)((IUpgradeable)songJob).Upgrade(album: true, aggregate: aggregate).First();
            JobPreparer.PrepareSubtree(albumJob, startConfig);
            Preprocessor.PreprocessAlbum(albumJob, albumJob.Config);
            return albumJob;
        }

        [TestMethod]
        public void CondAlbumTrackCountExact_FlowsThroughUpgradeAndPreprocessor()
        {
            var config = new Config(new[] { "--config", "none", "--cond", "album-track-count=10", "x" });

            Assert.AreEqual(10, config.necessaryFolderCond.MinTrackCount, "CLI --cond must set necessaryFolderCond.MinTrackCount");
            Assert.AreEqual(10, config.necessaryFolderCond.MaxTrackCount, "CLI --cond must set necessaryFolderCond.MaxTrackCount");

            var albumJob = UpgradeAndPrepare(config);

            Assert.AreEqual(10, albumJob.Query.MinTrackCount, "Preprocessor must apply necessaryFolderCond to AlbumQuery after Upgrade");
            Assert.AreEqual(10, albumJob.Query.MaxTrackCount);
        }

        [TestMethod]
        public void CondAlbumTrackCountGe_SetsOnlyMin()
        {
            var config = new Config(new[] { "--config", "none", "--cond", "album-track-count>=8", "x" });

            Assert.AreEqual(8,  config.necessaryFolderCond.MinTrackCount);
            Assert.AreEqual(-1, config.necessaryFolderCond.MaxTrackCount);

            var albumJob = UpgradeAndPrepare(config);

            Assert.AreEqual(8,  albumJob.Query.MinTrackCount);
            Assert.AreEqual(-1, albumJob.Query.MaxTrackCount);
        }

        [TestMethod]
        public void CondAlbumTrackCount_OverridesQueryDefaultAfterUpgrade()
        {
            // If a metadata extractor (e.g. MusicBrainz) embeds a track count in the SongQuery
            // before upgrade, necessaryFolderCond (from CLI --cond) should win after Preprocessor.
            var config = new Config(new[] { "--config", "none", "--cond", "album-track-count=12", "x" });

            // Simulate a SongQuery that already has track-count hints (e.g. from MusicBrainz).
            var albumJob = UpgradeAndPrepare(config,
                query: new SongQuery { Title = "T", Artist = "A" });

            // Even though the upgraded AlbumQuery starts with default MinTrackCount=-1,
            // the CLI folder-cond must still apply.
            Assert.AreEqual(12, albumJob.Query.MinTrackCount, "necessaryFolderCond must apply after Upgrade+Preprocessor");
            Assert.AreEqual(12, albumJob.Query.MaxTrackCount);
        }
    }
}

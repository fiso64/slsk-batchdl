using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;
using Enums;

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
        public void Defaults_ConcurrentProcesses_IsPositive()
        {
            var config = TestHelpers.CreateDefaultConfig();
            Assert.IsTrue(config.concurrentProcesses > 0);
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
        public void ConcurrentProcesses_SetsValue()
        {
            var config = new Config(new[] { "--config", "none", "--concurrent-processes", "5", "some input" });
            Assert.AreEqual(5, config.concurrentProcesses);
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
}

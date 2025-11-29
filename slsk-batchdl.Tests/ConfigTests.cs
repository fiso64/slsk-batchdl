using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;
using Enums;

namespace Tests.ConfigTests
{
    [TestClass]
    public class AutoProfileTests
    {
        private string testConfigPath;

        [TestInitialize]
        public void Setup()
        {
            testConfigPath = Path.Join(Directory.GetCurrentDirectory(), "test_conf.conf");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(testConfigPath))
                File.Delete(testConfigPath);
        }

        [TestMethod]
        public void UpdateProfiles_WithMultipleProfiles_AppliesCorrectSettings()
        {
            string content =
                "max-stale-time = 5" +
                "\nfast-search = true" +
                "\nformat = flac" +
                "\n[profile-true-1]" +
                "\nprofile-cond = input-type == \"youtube\" && download-mode == \"album\"" +
                "\nmax-stale-time = 10" +
                "\n[profile-true-2]" +
                "\nprofile-cond = !aggregate" +
                "\nfast-search = false" +
                "\n[profile-false-1]" +
                "\nprofile-cond = input-type == \"string\"" +
                "\nformat = mp3" +
                "\n[profile-no-cond]" +
                "\nformat = opus";

            File.WriteAllText(testConfigPath, content);
            var config = new Config(new string[] { "-c", testConfigPath, "test-input" });
            config.inputType = InputType.YouTube;
            config.interactiveMode = true;
            config.aggregate = false;
            config.maxStaleTime = 50000;
            var tle = new TrackListEntry(TrackType.Album);
            var ls = new TrackLists();
            ls.AddEntry(tle);
            config = config.UpdateProfiles(tle, ls);

            Assert.AreEqual(10, config.maxStaleTime);
            Assert.IsFalse(config.fastSearch);
            Assert.AreEqual("flac", config.necessaryCond.Formats[0]);
        }

        [TestMethod]
        public void UpdateProfiles_WithInteractiveAndAlbum_AppliesCorrectStaleTime()
        {
            string content =
                "\n[no-stale]" +
                "\nprofile-cond = interactive && download-mode == \"album\"" +
                "\nmax-stale-time = 999999" +
                "\n[youtube]" +
                "\nprofile-cond = input-type == \"youtube\"" +
                "\nyt-dlp = true";

            File.WriteAllText(testConfigPath, content);
            var config = new Config(new string[] { "-c", testConfigPath, "test-input" });
            config.inputType = InputType.CSV;
            config.album = true;
            config.interactiveMode = true;
            config.useYtdlp = false;
            config.maxStaleTime = 50000;
            var tle = new TrackListEntry(TrackType.Album);
            var ls = new TrackLists();
            ls.AddEntry(tle);
            config = config.UpdateProfiles(tle, ls);

            Assert.AreEqual(999999, config.maxStaleTime);
            Assert.IsFalse(config.useYtdlp);
        }

        [TestMethod]
        public void UpdateProfiles_WithYouTubeInput_EnablesYtDlp()
        {
            string content =
                "\n[no-stale]" +
                "\nprofile-cond = interactive && download-mode == \"album\"" +
                "\nmax-stale-time = 999999" +
                "\n[youtube]" +
                "\nprofile-cond = input-type == \"youtube\"" +
                "\nyt-dlp = true";

            File.WriteAllText(testConfigPath, content);
            var config = new Config(new string[] { "-c", testConfigPath, "test-input" });
            config.inputType = InputType.YouTube;
            config.album = false;
            config.interactiveMode = true;
            config.useYtdlp = false;
            config.maxStaleTime = 50000;
            var tle = new TrackListEntry(TrackType.Normal);
            var ls = new TrackLists();
            ls.AddEntry(tle);
            config = config.UpdateProfiles(tle, ls);

            Assert.AreEqual(50000, config.maxStaleTime);
            Assert.IsTrue(config.useYtdlp);
        }

        [TestMethod]
        public void UpdateProfiles_DoesNotDuplicateAppendableArgs()
        {
            string content =
                "on-complete = + action_default" +
                "\n[auto-profile]" +
                "\nprofile-cond = interactive" +
                "\nfast-search = true";

            File.WriteAllText(testConfigPath, content);

            // Simulating CLI args that also add an action
            var args = new string[] { "-c", testConfigPath, "--on-complete", "+ action_cli", "test-input" };

            var config = new Config(args);
            config.interactiveMode = true; // Enable the profile condition

            // Pre-check: Constructor should have applied default + cli
            Assert.IsNotNull(config.onComplete);
            Assert.AreEqual(2, config.onComplete.Count);
            Assert.AreEqual("action_default", config.onComplete[0]);
            Assert.AreEqual("action_cli", config.onComplete[1]);

            var tle = new TrackListEntry(TrackType.Normal);
            var ls = new TrackLists();
            ls.AddEntry(tle);

            // Act
            config = config.UpdateProfiles(tle, ls);

            // Assert
            Assert.IsTrue(config.fastSearch, "Auto profile should have been applied");
            Assert.AreEqual(2, config.onComplete.Count, "on-complete list should not have duplicates");
            Assert.AreEqual("action_default", config.onComplete[0]);
            Assert.AreEqual("action_cli", config.onComplete[1]);
        }
    }

    [TestClass]
    public class ProfileConditionTests
    {
        private Config config;

        [TestInitialize]
        public void Setup()
        {
            config = new Config();
            config.inputType = InputType.YouTube;
            config.interactiveMode = true;
            config.album = true;
            config.aggregate = false;
        }

        [TestMethod]
        public void ProfileConditionSatisfied_WithSimpleConditions_EvaluatesCorrectly()
        {
            Assert.IsTrue(config.ProfileConditionSatisfied("input-type == \"youtube\""));
            Assert.IsTrue(config.ProfileConditionSatisfied("download-mode == \"album\""));
            Assert.IsFalse(config.ProfileConditionSatisfied("aggregate"));
            Assert.IsTrue(config.ProfileConditionSatisfied("interactive"));
            Assert.IsTrue(config.ProfileConditionSatisfied("album"));
            Assert.IsFalse(config.ProfileConditionSatisfied("!interactive"));
        }

        [TestMethod]
        public void ProfileConditionSatisfied_WithComplexConditions_EvaluatesCorrectly()
        {
            Assert.IsTrue(config.ProfileConditionSatisfied("album && input-type == \"youtube\""));
            Assert.IsFalse(config.ProfileConditionSatisfied("album && input-type != \"youtube\""));
            Assert.IsFalse(config.ProfileConditionSatisfied("(interactive && aggregate)"));
            Assert.IsTrue(config.ProfileConditionSatisfied("album && (interactive || aggregate)"));
        }

        [TestMethod]
        public void ProfileConditionSatisfied_WithComplexOrConditions_EvaluatesCorrectly()
        {
            Assert.IsTrue(config.ProfileConditionSatisfied(
                "input-type == \"spotify\" || aggregate || input-type == \"csv\" || interactive && album"));
            Assert.IsTrue(config.ProfileConditionSatisfied(
                "input-type!=\"youtube\"||(album&&!interactive||(aggregate||interactive))"));
            Assert.IsFalse(config.ProfileConditionSatisfied(
                "input-type!=\"youtube\"||(album&&!interactive||(aggregate||!interactive))"));
        }
    }
}
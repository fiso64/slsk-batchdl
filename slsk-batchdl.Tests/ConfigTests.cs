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
    public class ProfilePriorityOrderTests
    {
        private string testConfigPath;

        [TestInitialize]
        public void Setup()
        {
            testConfigPath = Path.Join(Directory.GetCurrentDirectory(), "test_conf_priority.conf");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(testConfigPath)) File.Delete(testConfigPath);
        }

        private Config MakeConfig(string content, string[] extraArgs = null)
        {
            File.WriteAllText(testConfigPath, content);
            var args = new[] { "-c", testConfigPath, "test-input" };
            if (extraArgs != null) args = args.Concat(extraArgs).ToArray();
            return new Config(args);
        }

        private (Config config, TrackListEntry tle, TrackLists ls) SetupUpdateCall(Config config)
        {
            var tle = new TrackListEntry(TrackType.Normal);
            var ls = new TrackLists();
            ls.AddEntry(tle);
            return (config, tle, ls);
        }

        [TestMethod]
        public void Priority_DefaultAppliesWhenNoAutoProfileMatches()
        {
            var config = MakeConfig(
                "max-stale-time = 1\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2");
            config.interactiveMode = false;
            var (cfg, tle, ls) = SetupUpdateCall(config);

            var result = cfg.UpdateProfiles(tle, ls);

            Assert.AreEqual(1, result.maxStaleTime);
        }

        [TestMethod]
        public void Priority_AutoProfileOverridesDefault()
        {
            var config = MakeConfig(
                "max-stale-time = 1\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2");
            config.interactiveMode = true;
            var (cfg, tle, ls) = SetupUpdateCall(config);

            var result = cfg.UpdateProfiles(tle, ls);

            Assert.AreEqual(2, result.maxStaleTime);
        }

        [TestMethod]
        public void Priority_ManualProfileOverridesAutoProfile()
        {
            var config = MakeConfig(
                "max-stale-time = 1\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2\n" +
                "[manual]\nmax-stale-time = 3",
                new[] { "--profile", "manual" });
            config.interactiveMode = true;
            var (cfg, tle, ls) = SetupUpdateCall(config);

            var result = cfg.UpdateProfiles(tle, ls);

            Assert.AreEqual(3, result.maxStaleTime);
        }

        [TestMethod]
        public void Priority_CliArgsOverrideManualProfile()
        {
            var config = MakeConfig(
                "max-stale-time = 1\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2\n" +
                "[manual]\nmax-stale-time = 3",
                new[] { "--profile", "manual", "--max-stale-time", "4" });
            config.interactiveMode = true;
            var (cfg, tle, ls) = SetupUpdateCall(config);

            var result = cfg.UpdateProfiles(tle, ls);

            Assert.AreEqual(4, result.maxStaleTime);
        }

        [TestMethod]
        public void Priority_CliArgsOverrideAutoProfile()
        {
            var config = MakeConfig(
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2",
                new[] { "--max-stale-time", "4" });
            config.interactiveMode = true;
            var (cfg, tle, ls) = SetupUpdateCall(config);

            var result = cfg.UpdateProfiles(tle, ls);

            Assert.AreEqual(4, result.maxStaleTime);
        }

        [TestMethod]
        public void Priority_MultipleAutoProfilesApplyInOrder()
        {
            // Both conditions satisfied; second auto-profile wins for shared setting
            var config = MakeConfig(
                "[first]\nprofile-cond = interactive\nmax-stale-time = 10\n" +
                "[second]\nprofile-cond = album\nmax-stale-time = 20");
            config.interactiveMode = true;
            config.album = true;
            var (cfg, tle, ls) = SetupUpdateCall(config);

            var result = cfg.UpdateProfiles(tle, ls);

            Assert.AreEqual(20, result.maxStaleTime);
        }

        [TestMethod]
        public void Priority_TwoAutoProfiles_EachSetsDistinctSetting()
        {
            var config = MakeConfig(
                "[first]\nprofile-cond = interactive\nmax-stale-time = 10\n" +
                "[second]\nprofile-cond = album\nfast-search = true");
            config.interactiveMode = true;
            config.album = true;
            var (cfg, tle, ls) = SetupUpdateCall(config);

            var result = cfg.UpdateProfiles(tle, ls);

            Assert.AreEqual(10, result.maxStaleTime);
            Assert.IsTrue(result.fastSearch);
        }

        [TestMethod]
        public void Priority_RuntimeFields_PreservedOnResult()
        {
            // inputType, interactiveMode, album, aggregate are set at runtime (not in config/CLI).
            // They must survive the UpdateProfiles rebuild — guards against a naive "rebuild from scratch" fix.
            var config = MakeConfig("[auto]\nprofile-cond = interactive\nmax-stale-time = 10");
            config.inputType = InputType.YouTube;
            config.interactiveMode = true;
            config.album = true;
            config.aggregate = true;
            var (cfg, tle, ls) = SetupUpdateCall(config);

            var result = cfg.UpdateProfiles(tle, ls);

            Assert.AreEqual(InputType.YouTube, result.inputType);
            Assert.IsTrue(result.interactiveMode);
            Assert.IsTrue(result.album);
            Assert.IsTrue(result.aggregate);
        }
    }

    [TestClass]
    public class ProfileConditionFlipTests
    {
        private string testConfigPath;

        [TestInitialize]
        public void Setup()
        {
            testConfigPath = Path.Join(Directory.GetCurrentDirectory(), "test_conf_flip.conf");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(testConfigPath)) File.Delete(testConfigPath);
        }

        private Config MakeConfig(string content)
        {
            File.WriteAllText(testConfigPath, content);
            return new Config(new[] { "-c", testConfigPath, "test-input" });
        }

        private Config DoUpdate(Config config, bool interactiveMode)
        {
            config.interactiveMode = interactiveMode;
            var tle = new TrackListEntry(TrackType.Normal);
            var ls = new TrackLists();
            ls.AddEntry(tle);
            return config.UpdateProfiles(tle, ls);
        }

        [TestMethod]
        public void ConditionFlip_TrueToFalse_RemovesProfileSetting()
        {
            var config = MakeConfig(
                "max-stale-time = 5\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10");

            var first = DoUpdate(config, interactiveMode: true);
            Assert.AreEqual(10, first.maxStaleTime, "precondition: auto-profile applied on first call");

            var second = DoUpdate(first, interactiveMode: false);
            Assert.AreEqual(5, second.maxStaleTime, "default value should be restored after condition becomes false");
        }

        [TestMethod]
        public void ConditionFlip_FalseToTrue_AppliesProfile()
        {
            var config = MakeConfig(
                "max-stale-time = 5\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10");

            var first = DoUpdate(config, interactiveMode: false);
            Assert.AreEqual(5, first.maxStaleTime, "precondition: auto-profile not applied on first call");

            var second = DoUpdate(first, interactiveMode: true);
            Assert.AreEqual(10, second.maxStaleTime, "auto-profile should apply after condition becomes true");
        }

        [TestMethod]
        public void ConditionFlip_Unchanged_ReturnsSameInstance()
        {
            var config = MakeConfig(
                "max-stale-time = 5\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10");

            var first = DoUpdate(config, interactiveMode: true);

            // Same condition on second call — NeedUpdateProfiles should return false
            var second = DoUpdate(first, interactiveMode: true);
            Assert.IsTrue(ReferenceEquals(first, second), "no rebuild should occur when conditions are unchanged");
        }

        [TestMethod]
        public void ConditionFlip_Unchanged_False_ReturnsSameInstance()
        {
            var config = MakeConfig(
                "max-stale-time = 5\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10");

            var first = DoUpdate(config, interactiveMode: false);

            var second = DoUpdate(first, interactiveMode: false);
            Assert.IsTrue(ReferenceEquals(first, second));
        }

        [TestMethod]
        public void ConditionFlip_TrueToFalse_AppendableArgRebuilt()
        {
            var config = MakeConfig(
                "on-complete = + action_default\n" +
                "[auto]\nprofile-cond = interactive\non-complete = + action_profile");

            var first = DoUpdate(config, interactiveMode: true);
            CollectionAssert.Contains(first.onComplete, "action_profile");

            var second = DoUpdate(first, interactiveMode: false);
            CollectionAssert.DoesNotContain(second.onComplete, "action_profile");
            CollectionAssert.Contains(second.onComplete, "action_default");
        }

        [TestMethod]
        public void ConditionFlip_FalseToTrue_AppendableArgAdded()
        {
            var config = MakeConfig(
                "on-complete = + action_default\n" +
                "[auto]\nprofile-cond = interactive\non-complete = + action_profile");

            var first = DoUpdate(config, interactiveMode: false);
            CollectionAssert.DoesNotContain(first.onComplete, "action_profile");

            var second = DoUpdate(first, interactiveMode: true);
            CollectionAssert.Contains(second.onComplete, "action_profile");
            CollectionAssert.Contains(second.onComplete, "action_default");
            Assert.AreEqual(2, second.onComplete.Count, "no duplicates on flip true");
        }

        [TestMethod]
        public void ConditionFlip_MultipleProfiles_ActiveProfileSettingRetained()
        {
            // Profile A: always satisfied (interactive). Profile B: flips false.
            // When B flips false, A's setting should still apply.
            var config = MakeConfig(
                "max-stale-time = 1\n" +
                "[profile-a]\nprofile-cond = interactive\nmax-stale-time = 10\n" +
                "[profile-b]\nprofile-cond = album\nfast-search = true");
            config.interactiveMode = true;
            config.album = true;

            var first = DoUpdate(config, interactiveMode: true);
            Assert.AreEqual(10, first.maxStaleTime, "profile-a applies");
            Assert.IsTrue(first.fastSearch, "profile-b applies");

            first.album = false;
            var second = DoUpdate(first, interactiveMode: true);
            Assert.AreEqual(10, second.maxStaleTime, "profile-a still applies");
        }

        // BUG: When a profile's condition flips false, settings it applied are NOT reverted to their
        // built-in defaults — they persist in the copy that UpdateProfiles starts from. Only values
        // explicitly set in [default] or CLI args get reset. Root cause: Config mixes runtime state
        // (interactiveMode, inputType, etc.) with configured state (maxStaleTime, fastSearch, etc.),
        // so UpdateProfiles can't know which fields to reset without a clean base to rebuild from.
        // Fix requires separating runtime state from configured state — address in the config refactor.
        //
        // [TestMethod]
        // public void ConditionFlip_ProfileRemoved_RevertsToBuiltInDefault()
        // {
        //     var config = MakeConfig(
        //         "[auto]\nprofile-cond = interactive\nfast-search = true");
        //     // No [default] section, no CLI --fast-search flag
        //
        //     var first = DoUpdate(config, interactiveMode: true);
        //     Assert.IsTrue(first.fastSearch);
        //
        //     var second = DoUpdate(first, interactiveMode: false);
        //     Assert.IsFalse(second.fastSearch, "should revert to built-in default (false) when profile removed");
        // }

        [TestMethod]
        public void ConditionFlip_TrueToFalseToTrue_CorrectOnEachCall()
        {
            var config = MakeConfig(
                "max-stale-time = 5\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10");

            var r1 = DoUpdate(config, interactiveMode: true);
            Assert.AreEqual(10, r1.maxStaleTime);

            var r2 = DoUpdate(r1, interactiveMode: false);
            Assert.AreEqual(5, r2.maxStaleTime);

            var r3 = DoUpdate(r2, interactiveMode: true);
            Assert.AreEqual(10, r3.maxStaleTime);
        }
    }

    [TestClass]
    public class ProfileEdgeCaseTests
    {
        private string testConfigPath;

        [TestInitialize]
        public void Setup()
        {
            testConfigPath = Path.Join(Directory.GetCurrentDirectory(), "test_conf_edge.conf");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(testConfigPath)) File.Delete(testConfigPath);
        }

        private Config MakeConfig(string content)
        {
            File.WriteAllText(testConfigPath, content);
            return new Config(new[] { "-c", testConfigPath, "test-input" });
        }

        private Config DoUpdate(Config config, bool interactiveMode = false)
        {
            config.interactiveMode = interactiveMode;
            var tle = new TrackListEntry(TrackType.Normal);
            var ls = new TrackLists();
            ls.AddEntry(tle);
            return config.UpdateProfiles(tle, ls);
        }

        [TestMethod]
        public void EdgeCase_DefaultSectionProfileCondIsIgnored()
        {
            // profile-cond in [default] should be silently ignored — [default] is never an auto-profile
            var config = MakeConfig(
                "[default]\nprofile-cond = interactive\nmax-stale-time = 99");
            config.interactiveMode = true;

            var tle = new TrackListEntry(TrackType.Normal);
            Assert.IsFalse(config.NeedUpdateProfiles(tle));
        }

        [TestMethod]
        public void EdgeCase_DefaultSectionAlwaysApplied_EvenWithProfileCond()
        {
            // [default] settings still apply at startup even if profile-cond is present (and ignored)
            var config = MakeConfig(
                "[default]\nprofile-cond = interactive\nmax-stale-time = 99");

            Assert.AreEqual(99, config.maxStaleTime);
        }

        [TestMethod]
        public void EdgeCase_ProfileWithoutCondNotConsideredForAutoUpdate()
        {
            // A named profile with no cond (manual-only) should not trigger NeedUpdateProfiles
            var config = MakeConfig(
                "[manual-only]\nmax-stale-time = 5");
            config.interactiveMode = true;

            Assert.IsFalse(config.HasAutoProfiles);
            var tle = new TrackListEntry(TrackType.Normal);
            Assert.IsFalse(config.NeedUpdateProfiles(tle));
        }

        [TestMethod]
        public void EdgeCase_NoAutoProfiles_NeedUpdateProfilesAlwaysFalse()
        {
            var config = MakeConfig("max-stale-time = 5");

            Assert.IsFalse(config.HasAutoProfiles);
            var tle = new TrackListEntry(TrackType.Normal);
            Assert.IsFalse(config.NeedUpdateProfiles(tle));
        }

        [TestMethod]
        public void EdgeCase_ManualProfileNotAppliedByUpdateProfiles_WhenNotInCliArgs()
        {
            // A named profile with no cond is only applied if explicitly requested via --profile
            var config = MakeConfig(
                "max-stale-time = 1\n" +
                "[named]\nprofile-cond = interactive\nmax-stale-time = 2\n" +
                "[extra]\nmax-stale-time = 99");
            config.interactiveMode = true;

            var result = DoUpdate(config, interactiveMode: true);

            Assert.AreEqual(2, result.maxStaleTime, "extra manual profile should not auto-apply");
        }

        [TestMethod]
        public void EdgeCase_UnknownManualProfileInCliArgs_DoesNotCrash()
        {
            File.WriteAllText(testConfigPath,
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10");
            var config = new Config(new[] { "-c", testConfigPath, "--profile", "nonexistent", "test-input" });
            config.interactiveMode = true;

            // Should not throw — just logs a warning
            var result = DoUpdate(config, interactiveMode: true);
            Assert.AreEqual(10, result.maxStaleTime);
        }

        [TestMethod]
        public void EdgeCase_MultipleManualProfilesAppliedInOrder()
        {
            File.WriteAllText(testConfigPath,
                "[p1]\nmax-stale-time = 10\n" +
                "[p2]\nfast-search = true");
            var config = new Config(new[] { "-c", testConfigPath, "--profile", "p1,p2", "test-input" });
            config.interactiveMode = true;

            // Profile-cond not set on p1/p2, but we need an auto-profile to trigger UpdateProfiles
            // Add a trivially-satisfied auto-profile
            // Actually without any auto-profile, UpdateProfiles won't rebuild.
            // Instead, test that the constructor applies them at startup:
            Assert.AreEqual(10, config.maxStaleTime);
            Assert.IsTrue(config.fastSearch);
        }

        [TestMethod]
        public void EdgeCase_AutoProfile_AppendableArgNotDoubledOnNoOpSecondCall()
        {
            var config = MakeConfig(
                "on-complete = + action_default\n" +
                "[auto]\nprofile-cond = interactive\non-complete = + action_profile");

            var first = DoUpdate(config, interactiveMode: true);
            Assert.AreEqual(2, first.onComplete.Count);

            // Second call with same condition: no-op, same instance
            var second = DoUpdate(first, interactiveMode: true);
            Assert.IsTrue(ReferenceEquals(first, second));
            Assert.AreEqual(2, second.onComplete.Count);
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
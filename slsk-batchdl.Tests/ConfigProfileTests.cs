using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;
using Jobs;
using Enums;
using Services;
using Settings;

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

        private (ConfigFile file, DownloadSettings dl, string[] args) Bind(string content, params string[] extraArgs)
        {
            File.WriteAllText(testConfigPath, content);
            var file = ConfigManager.Load(testConfigPath);
            var args = new[] { "test-input" }.Concat(extraArgs).ToArray();
            var (_, dl, _) = ConfigManager.Bind(file, args, ExtractProfileName(args));
            return (file, dl, args);
        }

        private static string? ExtractProfileName(string[] args)
        {
            int i = Array.IndexOf(args, "--profile");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static DownloadSettings Update(ConfigFile file, DownloadSettings current, string[] args, Job job, CliSettings? cli = null)
            => ConfigManager.UpdateProfiles(current, file, args, ExtractProfileName(args), job, cli);

        [TestMethod]
        public void UpdateProfiles_WithMultipleProfiles_AppliesCorrectSettings()
        {
            string content =
                "max-stale-time = 5\n" +
                "fast-search = true\n" +
                "format = flac\n" +
                "[profile-true-1]\n" +
                "profile-cond = input-type == \"youtube\" && download-mode == \"album\"\n" +
                "max-stale-time = 10\n" +
                "[profile-true-2]\n" +
                "profile-cond = !aggregate\n" +
                "fast-search = false\n" +
                "[profile-false-1]\n" +
                "profile-cond = input-type == \"string\"\n" +
                "format = mp3\n" +
                "[profile-no-cond]\n" +
                "format = opus";

            var (file, dl, args) = Bind(content, "--input-type", "youtube");
            var job = new AlbumJob(new AlbumQuery());

            var result = Update(file, dl, args, job);

            Assert.AreEqual(10, result.Search.MaxStaleTime);
            Assert.IsFalse(result.Search.FastSearch);
            Assert.AreEqual("flac", result.Search.NecessaryCond.Formats[0]);
        }

        [TestMethod]
        public void UpdateProfiles_WithInteractiveAndAlbum_AppliesCorrectStaleTime()
        {
            string content =
                "[no-stale]\n" +
                "profile-cond = interactive && download-mode == \"album\"\n" +
                "max-stale-time = 999999\n" +
                "[youtube]\n" +
                "profile-cond = input-type == \"youtube\"\n" +
                "yt-dlp = true";

            var (file, dl, args) = Bind(content);
            var cli = new CliSettings { InteractiveMode = true };
            var job = new AlbumJob(new AlbumQuery());

            var result = Update(file, dl, args, job, cli);

            Assert.AreEqual(999999, result.Search.MaxStaleTime);
            Assert.IsFalse(result.YtDlp.UseYtdlp);
        }

        [TestMethod]
        public void UpdateProfiles_WithYouTubeInput_EnablesYtDlp()
        {
            string content =
                "[no-stale]\n" +
                "profile-cond = interactive && download-mode == \"album\"\n" +
                "max-stale-time = 999999\n" +
                "[youtube]\n" +
                "profile-cond = input-type == \"youtube\"\n" +
                "yt-dlp = true";

            var (file, dl, args) = Bind(content, "--input-type", "youtube");
            var cli = new CliSettings { InteractiveMode = true };
            var job = new SongJob(new SongQuery { Title = "test" });

            var result = Update(file, dl, args, job, cli);

            Assert.AreNotEqual(999999, result.Search.MaxStaleTime);
            Assert.IsTrue(result.YtDlp.UseYtdlp);
        }

        [TestMethod]
        public void UpdateProfiles_DoesNotDuplicateAppendableArgs()
        {
            string content =
                "on-complete = + action_default\n" +
                "[auto-profile]\n" +
                "profile-cond = interactive\n" +
                "fast-search = true";

            var (file, dl, args) = Bind(content, "--on-complete", "+ action_cli");
            var cli = new CliSettings { InteractiveMode = true };

            Assert.IsNotNull(dl.Output.OnComplete);
            Assert.AreEqual(2, dl.Output.OnComplete.Count);
            Assert.AreEqual("action_default", dl.Output.OnComplete[0]);
            Assert.AreEqual("action_cli", dl.Output.OnComplete[1]);

            var job = new SongJob(new SongQuery { Title = "test" });
            var result = Update(file, dl, args, job, cli);

            Assert.IsTrue(result.Search.FastSearch);
            Assert.AreEqual(2, result.Output.OnComplete!.Count, "on-complete should not have duplicates");
            Assert.AreEqual("action_default", result.Output.OnComplete[0]);
            Assert.AreEqual("action_cli", result.Output.OnComplete[1]);
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

        private (ConfigFile file, DownloadSettings dl, string[] args) Bind(string content, params string[] extraArgs)
        {
            File.WriteAllText(testConfigPath, content);
            var file = ConfigManager.Load(testConfigPath);
            var args = new[] { "test-input" }.Concat(extraArgs).ToArray();
            var (_, dl, _) = ConfigManager.Bind(file, args, ExtractProfileName(args));
            return (file, dl, args);
        }

        private static string? ExtractProfileName(string[] args)
        {
            int i = Array.IndexOf(args, "--profile");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static DownloadSettings Update(ConfigFile file, DownloadSettings current, string[] args, CliSettings? cli = null)
        {
            var job = new SongJob(new SongQuery { Title = "test" });
            return ConfigManager.UpdateProfiles(current, file, args, ExtractProfileName(args), job, cli);
        }

        [TestMethod]
        public void Priority_DefaultAppliesWhenNoAutoProfileMatches()
        {
            var (file, dl, args) = Bind(
                "max-stale-time = 1\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2");

            var result = Update(file, dl, args, new CliSettings { InteractiveMode = false });

            Assert.AreEqual(1, result.Search.MaxStaleTime);
        }

        [TestMethod]
        public void Priority_AutoProfileOverridesDefault()
        {
            var (file, dl, args) = Bind(
                "max-stale-time = 1\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2");

            var result = Update(file, dl, args, new CliSettings { InteractiveMode = true });

            Assert.AreEqual(2, result.Search.MaxStaleTime);
        }

        [TestMethod]
        public void Priority_ManualProfileOverridesAutoProfile()
        {
            var (file, dl, args) = Bind(
                "max-stale-time = 1\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2\n" +
                "[manual]\nmax-stale-time = 3",
                "--profile", "manual");

            var result = Update(file, dl, args, new CliSettings { InteractiveMode = true });

            Assert.AreEqual(3, result.Search.MaxStaleTime);
        }

        [TestMethod]
        public void Priority_CliArgsOverrideManualProfile()
        {
            var (file, dl, args) = Bind(
                "max-stale-time = 1\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2\n" +
                "[manual]\nmax-stale-time = 3",
                "--profile", "manual", "--max-stale-time", "4");

            var result = Update(file, dl, args, new CliSettings { InteractiveMode = true });

            Assert.AreEqual(4, result.Search.MaxStaleTime);
        }

        [TestMethod]
        public void Priority_CliArgsOverrideAutoProfile()
        {
            var (file, dl, args) = Bind(
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 2",
                "--max-stale-time", "4");

            var result = Update(file, dl, args, new CliSettings { InteractiveMode = true });

            Assert.AreEqual(4, result.Search.MaxStaleTime);
        }

        [TestMethod]
        public void Priority_MultipleAutoProfilesApplyInOrder()
        {
            // Both conditions satisfied; second profile wins for the shared setting.
            var (file, dl, args) = Bind(
                "[first]\nprofile-cond = interactive\nmax-stale-time = 10\n" +
                "[second]\nprofile-cond = album\nmax-stale-time = 20",
                "--album");

            var result = Update(file, dl, args, new CliSettings { InteractiveMode = true });

            Assert.AreEqual(20, result.Search.MaxStaleTime);
        }

        [TestMethod]
        public void Priority_TwoAutoProfiles_EachSetsDistinctSetting()
        {
            var (file, dl, args) = Bind(
                "[first]\nprofile-cond = interactive\nmax-stale-time = 10\n" +
                "[second]\nprofile-cond = album\nfast-search = true",
                "--album");

            var result = Update(file, dl, args, new CliSettings { InteractiveMode = true });

            Assert.AreEqual(10, result.Search.MaxStaleTime);
            Assert.IsTrue(result.Search.FastSearch);
        }

        [TestMethod]
        public void Priority_RuntimeFields_PreservedOnResult()
        {
            // Values passed via CLI args must survive the UpdateProfiles rebuild.
            var (file, dl, args) = Bind(
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10",
                "--input-type", "youtube", "--album", "--aggregate");

            var result = Update(file, dl, args, new CliSettings { InteractiveMode = true });

            Assert.AreEqual(InputType.YouTube, result.Extraction.InputType);
            Assert.IsTrue(result.Extraction.IsAlbum);
            Assert.IsTrue(result.Search.IsAggregate);
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

        private (ConfigFile file, DownloadSettings dl, string[] args) Bind(string content)
        {
            File.WriteAllText(testConfigPath, content);
            var file = ConfigManager.Load(testConfigPath);
            var args = new[] { "test-input" };
            var (_, dl, _) = ConfigManager.Bind(file, args);
            return (file, dl, args);
        }

        private static DownloadSettings Update(ConfigFile file, DownloadSettings current, string[] args, bool interactiveMode)
        {
            var cli = new CliSettings { InteractiveMode = interactiveMode };
            var job = new SongJob(new SongQuery { Title = "test" });
            return ConfigManager.UpdateProfiles(current, file, args, null, job, cli);
        }

        [TestMethod]
        public void ConditionFlip_TrueToFalse_RemovesProfileSetting()
        {
            var (file, dl, args) = Bind(
                "max-stale-time = 5\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10");

            var first = Update(file, dl, args, interactiveMode: true);
            Assert.AreEqual(10, first.Search.MaxStaleTime, "precondition: auto-profile applied");

            var second = Update(file, first, args, interactiveMode: false);
            Assert.AreEqual(5, second.Search.MaxStaleTime, "default value restored after condition becomes false");
        }

        [TestMethod]
        public void ConditionFlip_FalseToTrue_AppliesProfile()
        {
            var (file, dl, args) = Bind(
                "max-stale-time = 5\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10");

            var first = Update(file, dl, args, interactiveMode: false);
            Assert.AreEqual(5, first.Search.MaxStaleTime, "precondition: auto-profile not applied");

            var second = Update(file, first, args, interactiveMode: true);
            Assert.AreEqual(10, second.Search.MaxStaleTime, "auto-profile applies after condition becomes true");
        }

        [TestMethod]
        public void ConditionFlip_Unchanged_ReturnsSameInstance()
        {
            var (file, dl, args) = Bind(
                "max-stale-time = 5\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10");

            var first = Update(file, dl, args, interactiveMode: true);
            var second = Update(file, first, args, interactiveMode: true);

            Assert.IsTrue(ReferenceEquals(first, second), "no rebuild when conditions are unchanged");
        }

        [TestMethod]
        public void ConditionFlip_Unchanged_False_ReturnsSameInstance()
        {
            var (file, dl, args) = Bind(
                "max-stale-time = 5\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10");

            var first = Update(file, dl, args, interactiveMode: false);
            var second = Update(file, first, args, interactiveMode: false);

            Assert.IsTrue(ReferenceEquals(first, second));
        }

        [TestMethod]
        public void ConditionFlip_TrueToFalse_AppendableArgRebuilt()
        {
            var (file, dl, args) = Bind(
                "on-complete = + action_default\n" +
                "[auto]\nprofile-cond = interactive\non-complete = + action_profile");

            var first = Update(file, dl, args, interactiveMode: true);
            CollectionAssert.Contains(first.Output.OnComplete, "action_profile");

            var second = Update(file, first, args, interactiveMode: false);
            CollectionAssert.DoesNotContain(second.Output.OnComplete, "action_profile");
            CollectionAssert.Contains(second.Output.OnComplete, "action_default");
        }

        [TestMethod]
        public void ConditionFlip_FalseToTrue_AppendableArgAdded()
        {
            var (file, dl, args) = Bind(
                "on-complete = + action_default\n" +
                "[auto]\nprofile-cond = interactive\non-complete = + action_profile");

            var first = Update(file, dl, args, interactiveMode: false);
            CollectionAssert.DoesNotContain(first.Output.OnComplete, "action_profile");

            var second = Update(file, first, args, interactiveMode: true);
            CollectionAssert.Contains(second.Output.OnComplete, "action_profile");
            CollectionAssert.Contains(second.Output.OnComplete, "action_default");
            Assert.AreEqual(2, second.Output.OnComplete!.Count, "no duplicates on flip to true");
        }

        [TestMethod]
        public void ConditionFlip_MultipleProfiles_ActiveProfileSettingRetained()
        {
            // Profile A always applies (interactive). Profile B flips when job type changes.
            // When B stops applying, A's setting must still be present.
            var (file, dl, args) = Bind(
                "max-stale-time = 1\n" +
                "[profile-a]\nprofile-cond = interactive\nmax-stale-time = 10\n" +
                "[profile-b]\nprofile-cond = download-mode == \"album\"\nfast-search = true");
            var cli = new CliSettings { InteractiveMode = true };

            var first = ConfigManager.UpdateProfiles(dl, file, args, null, new AlbumJob(new AlbumQuery()), cli);
            Assert.AreEqual(10, first.Search.MaxStaleTime);
            Assert.IsTrue(first.Search.FastSearch);

            var second = ConfigManager.UpdateProfiles(first, file, args, null, new SongJob(new SongQuery { Title = "test" }), cli);
            Assert.AreEqual(10, second.Search.MaxStaleTime, "profile-a still applies after profile-b flips off");
        }

        [TestMethod]
        public void ConditionFlip_TrueToFalseToTrue_CorrectOnEachCall()
        {
            var (file, dl, args) = Bind(
                "max-stale-time = 5\n" +
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10");

            var r1 = Update(file, dl, args, interactiveMode: true);
            Assert.AreEqual(10, r1.Search.MaxStaleTime);

            var r2 = Update(file, r1, args, interactiveMode: false);
            Assert.AreEqual(5, r2.Search.MaxStaleTime);

            var r3 = Update(file, r2, args, interactiveMode: true);
            Assert.AreEqual(10, r3.Search.MaxStaleTime);
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

        private (ConfigFile file, DownloadSettings dl, string[] args) Bind(string content, params string[] extraArgs)
        {
            File.WriteAllText(testConfigPath, content);
            var file = ConfigManager.Load(testConfigPath);
            var args = new[] { "test-input" }.Concat(extraArgs).ToArray();
            var (_, dl, _) = ConfigManager.Bind(file, args, ExtractProfileName(args));
            return (file, dl, args);
        }

        private static string? ExtractProfileName(string[] args)
        {
            int i = Array.IndexOf(args, "--profile");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static DownloadSettings Update(ConfigFile file, DownloadSettings current, string[] args, bool interactiveMode = false)
        {
            var cli = new CliSettings { InteractiveMode = interactiveMode };
            var job = new SongJob(new SongQuery { Title = "test" });
            return ConfigManager.UpdateProfiles(current, file, args, ExtractProfileName(args), job, cli);
        }

        [TestMethod]
        public void EdgeCase_DefaultSectionProfileCondIsIgnored()
        {
            var (file, dl, args) = Bind(
                "[default]\nprofile-cond = interactive\nmax-stale-time = 99");

            Assert.IsFalse(file.HasAutoProfiles);
            var result = Update(file, dl, args, interactiveMode: true);
            Assert.IsTrue(ReferenceEquals(dl, result), "no update should occur when there are no auto-profiles");
        }

        [TestMethod]
        public void EdgeCase_DefaultSectionAlwaysApplied_EvenWithProfileCond()
        {
            var (file, dl, args) = Bind(
                "[default]\nprofile-cond = interactive\nmax-stale-time = 99");

            Assert.AreEqual(99, dl.Search.MaxStaleTime, "[default] settings are applied even when profile-cond is present (and ignored)");
        }

        [TestMethod]
        public void EdgeCase_ProfileWithoutCondNotConsideredForAutoUpdate()
        {
            var (file, dl, args) = Bind(
                "[manual-only]\nmax-stale-time = 5");

            Assert.IsFalse(file.HasAutoProfiles);
            var result = Update(file, dl, args, interactiveMode: true);
            Assert.IsTrue(ReferenceEquals(dl, result));
        }

        [TestMethod]
        public void EdgeCase_NoAutoProfiles_UpdateProfilesAlwaysReturnsCurrent()
        {
            var (file, dl, args) = Bind("max-stale-time = 5");

            Assert.IsFalse(file.HasAutoProfiles);
            var result = Update(file, dl, args);
            Assert.IsTrue(ReferenceEquals(dl, result));
        }

        [TestMethod]
        public void EdgeCase_ManualProfileNotAppliedByUpdateProfiles_WhenNotInCliArgs()
        {
            // [named] is an auto-profile (has profile-cond). [extra] is manual-only (no cond).
            // UpdateProfiles should apply [named] but never touch [extra].
            var (file, dl, args) = Bind(
                "max-stale-time = 1\n" +
                "[named]\nprofile-cond = interactive\nmax-stale-time = 2\n" +
                "[extra]\nmax-stale-time = 99");

            var result = Update(file, dl, args, interactiveMode: true);

            Assert.AreEqual(2, result.Search.MaxStaleTime, "[extra] manual profile must not auto-apply");
        }

        [TestMethod]
        public void EdgeCase_UnknownManualProfileInCliArgs_DoesNotCrash()
        {
            File.WriteAllText(testConfigPath,
                "[auto]\nprofile-cond = interactive\nmax-stale-time = 10");
            var file = ConfigManager.Load(testConfigPath);
            var args = new[] { "test-input", "--profile", "nonexistent" };
            var (_, dl, _) = ConfigManager.Bind(file, args, "nonexistent");

            // Should not throw — just logs a warning
            var result = Update(file, dl, args, interactiveMode: true);
            Assert.AreEqual(10, result.Search.MaxStaleTime);
        }

        [TestMethod]
        public void EdgeCase_MultipleManualProfilesAppliedInOrder()
        {
            // Named profiles are applied at Bind time, in order.
            var (file, dl, args) = Bind(
                "[p1]\nmax-stale-time = 10\n" +
                "[p2]\nfast-search = true",
                "--profile", "p1,p2");

            Assert.AreEqual(10, dl.Search.MaxStaleTime);
            Assert.IsTrue(dl.Search.FastSearch);
        }

        [TestMethod]
        public void EdgeCase_AutoProfile_AppendableArgNotDoubledOnNoOpSecondCall()
        {
            var (file, dl, args) = Bind(
                "on-complete = + action_default\n" +
                "[auto]\nprofile-cond = interactive\non-complete = + action_profile");

            var first = Update(file, dl, args, interactiveMode: true);
            Assert.AreEqual(2, first.Output.OnComplete!.Count);

            var second = Update(file, first, args, interactiveMode: true);
            Assert.IsTrue(ReferenceEquals(first, second), "same condition → same instance");
            Assert.AreEqual(2, second.Output.OnComplete!.Count, "no duplication on no-op call");
        }
    }

    [TestClass]
    public class ProfileConditionTests
    {
        private DownloadSettings dl;
        private CliSettings cli;

        [TestInitialize]
        public void Setup()
        {
            dl = new DownloadSettings();
            dl.Extraction.InputType = InputType.YouTube;
            dl.Extraction.IsAlbum = true;
            dl.Search.IsAggregate = false;
            cli = new CliSettings { InteractiveMode = true };
        }

        [TestMethod]
        public void ProfileConditionSatisfied_WithSimpleConditions_EvaluatesCorrectly()
        {
            Assert.IsTrue(ConfigManager.ProfileConditionSatisfied("input-type == \"youtube\"", dl, null, cli));
            Assert.IsTrue(ConfigManager.ProfileConditionSatisfied("download-mode == \"album\"", dl, null, cli));
            Assert.IsFalse(ConfigManager.ProfileConditionSatisfied("aggregate", dl, null, cli));
            Assert.IsTrue(ConfigManager.ProfileConditionSatisfied("interactive", dl, null, cli));
            Assert.IsTrue(ConfigManager.ProfileConditionSatisfied("album", dl, null, cli));
            Assert.IsFalse(ConfigManager.ProfileConditionSatisfied("!interactive", dl, null, cli));
        }

        [TestMethod]
        public void ProfileConditionSatisfied_WithComplexConditions_EvaluatesCorrectly()
        {
            Assert.IsTrue(ConfigManager.ProfileConditionSatisfied("album && input-type == \"youtube\"", dl, null, cli));
            Assert.IsFalse(ConfigManager.ProfileConditionSatisfied("album && input-type != \"youtube\"", dl, null, cli));
            Assert.IsFalse(ConfigManager.ProfileConditionSatisfied("(interactive && aggregate)", dl, null, cli));
            Assert.IsTrue(ConfigManager.ProfileConditionSatisfied("album && (interactive || aggregate)", dl, null, cli));
        }

        [TestMethod]
        public void ProfileConditionSatisfied_WithComplexOrConditions_EvaluatesCorrectly()
        {
            Assert.IsTrue(ConfigManager.ProfileConditionSatisfied(
                "input-type == \"spotify\" || aggregate || input-type == \"csv\" || interactive && album", dl, null, cli));
            Assert.IsTrue(ConfigManager.ProfileConditionSatisfied(
                "input-type!=\"youtube\"||(album&&!interactive||(aggregate||interactive))", dl, null, cli));
            Assert.IsFalse(ConfigManager.ProfileConditionSatisfied(
                "input-type!=\"youtube\"||(album&&!interactive||(aggregate||!interactive))", dl, null, cli));
        }
    }
}

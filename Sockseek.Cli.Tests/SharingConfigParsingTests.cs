using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core.Settings;

namespace Tests.ConfigParsingTests;

[TestClass]
public sealed class SharingConfigParsingTests
{
    [TestMethod]
    public void SharingDefaults_AreSettledDaemonDefaults()
    {
        var settings = Bind();

        Assert.AreEqual(0, settings.Sharing.Roots.Count);
        Assert.IsTrue(settings.Sharing.ScanOnStart);
        Assert.IsNull(settings.Sharing.RescanInterval);
        Assert.AreEqual(10, settings.Uploads.Slots);
        Assert.IsNull(settings.Uploads.SpeedLimitKiBPerSecond);
    }

    [TestMethod]
    public void ShareAndPolicyLists_UseReplaceAndExplicitAppend()
    {
        string parent = Path.Combine(Path.GetTempPath(), "sockseek-config-sharing");
        string first = Path.Combine(parent, "first");
        string second = Path.Combine(parent, "second");

        var settings = Bind(
            "--share", first,
            "--share", $"+ [Second Alias]{second}",
            "--share-filter", "one",
            "--share-filter", "+ two",
            "--peer-blocked-user", "Alice",
            "--peer-blocked-user", "+ Bob",
            "--peer-blocked-ip", "192.0.2.1",
            "--peer-blocked-ip", "+ 2001:db8::1");

        Assert.AreEqual(2, settings.Sharing.Roots.Count);
        Assert.AreEqual("first", settings.Sharing.Roots[0].EffectiveAlias);
        Assert.AreEqual("Second Alias", settings.Sharing.Roots[1].EffectiveAlias);
        CollectionAssert.AreEqual(new[] { "one", "two" }, settings.Sharing.Filters);
        CollectionAssert.AreEqual(
            new[] { "Alice", "Bob" },
            settings.PeerAccess.BlockedUsernames);
        CollectionAssert.AreEqual(
            new[] { "192.0.2.1", "2001:db8::1" },
            settings.PeerAccess.BlockedIpAddresses);
    }

    [TestMethod]
    public void UnprefixedListValue_ReplacesEarlierValue()
    {
        string parent = Path.Combine(Path.GetTempPath(), "sockseek-config-sharing");
        string first = Path.Combine(parent, "first");
        string second = Path.Combine(parent, "second");

        var settings = Bind("--share", first, "--share", second);

        Assert.AreEqual(1, settings.Sharing.Roots.Count);
        Assert.AreEqual(Path.GetFullPath(second), settings.Sharing.Roots[0].LocalPath);
    }

    [TestMethod]
    public void UploadAndScanPolicy_BindsAndValidates()
    {
        var settings = Bind(
            "--share-scan-on-start", "false",
            "--share-rescan-interval", "30m",
            "--upload-slots", "4",
            "--upload-speed-limit-kib", "512");

        Assert.IsFalse(settings.Sharing.ScanOnStart);
        Assert.AreEqual(TimeSpan.FromMinutes(30), settings.Sharing.RescanInterval);
        Assert.AreEqual(4, settings.Uploads.Slots);
        Assert.AreEqual(512, settings.Uploads.SpeedLimitKiBPerSecond);
    }

    [DataTestMethod]
    [DataRow("--upload-slots", "0")]
    [DataRow("--upload-speed-limit-kib", "0")]
    [DataRow("--share-rescan-interval", "30s")]
    [DataRow("--peer-blocked-ip", "192.0.2.0/24")]
    public void InvalidSharingValues_FailBinding(string option, string value)
    {
        Assert.ThrowsException<ArgumentException>(() => Bind($"{option}={value}"));
    }

    [DataTestMethod]
    [DataRow("--share-scan-workers")]
    [DataRow("--share-search-concurrency")]
    [DataRow("--share-search-queue-capacity")]
    [DataRow("--share-search-result-limit")]
    [DataRow("--upload-max-queued-files-per-user")]
    [DataRow("--upload-max-queued-mib-per-user")]
    public void InternalTuningOptions_AreNotPublicConfiguration(string option)
    {
        Assert.ThrowsException<Exception>(() => Bind(option, "1"));
    }

    [DataTestMethod]
    [DataRow("--shared-files")]
    [DataRow("--shared-folders")]
    [DataRow("--no-modify-share-count")]
    [DataRow("--nmsc")]
    public void RemovedManualCountOptions_AreUnknown(string option)
    {
        Assert.ThrowsException<Exception>(() => Bind(option, "1"));
    }

    [TestMethod]
    public void DaemonSharingSettings_AreRejectedInNamedProfiles()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"sockseek-sharing-profile-{Guid.NewGuid():N}.conf");
        File.WriteAllText(
            path,
            """
            [named]
            upload-slots = 2
            """);

        try
        {
            var ex = Assert.ThrowsException<ArgumentException>(() => ConfigManager.Load(path));
            StringAssert.Contains(ex.Message, "not allowed in named or automatic profile");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static EngineSettings Bind(params string[] arguments)
    {
        var file = new ConfigFile("none", new Dictionary<string, ProfileEntry>());
        return ConfigManager.Bind(file, arguments).Engine;
    }
}

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Sharing;

namespace Tests.Core;

[TestClass]
public sealed class SharingSettingsTests
{
    [TestMethod]
    public void RemotePathKey_UsesOneNfcCaseInsensitiveBinaryRule()
    {
        var composed = RemotePathKey.Create(@"Music\Café\TRACK.FLAC");
        var decomposed = RemotePathKey.Create("music/cafe\u0301/track.flac");

        Assert.AreEqual(composed, decomposed);
        CollectionAssert.AreEqual(composed.ToArray(), decomposed.ToArray());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(@"\rooted")]
    [DataRow(@"Alias\\empty")]
    [DataRow(@"Alias\.\file")]
    [DataRow(@"Alias\..\file")]
    [DataRow("Alias\0file")]
    public void RemotePathKey_RejectsAmbiguousOrUnsafePaths(string path)
    {
        Assert.ThrowsExactly<ArgumentException>(() => RemotePathKey.Create(path));
    }

    [TestMethod]
    public void RemotePathKey_RejectsInvalidUtf16()
    {
        string invalid = "Alias\\" + '\uD800';
        Assert.ThrowsExactly<ArgumentException>(() => RemotePathKey.Create(invalid));
    }

    [TestMethod]
    public void RemotePathKey_PreservesNonNulControlCharacters()
    {
        const string path = "Alias\\line\n\tfile.flac";

        var key = RemotePathKey.Create(path);

        Assert.AreEqual(key, RemotePathKey.Create(path));
        Assert.AreNotEqual(key, RemotePathKey.Create("Alias\\line file.flac"));
    }

    [TestMethod]
    public void RemotePathKey_DoesNotImposeSockseekOnlyEncodedLength()
    {
        string longPath = "Alias\\" + new string('x', 32 * 1_024);

        Assert.IsNotNull(RemotePathKey.Create(longPath));
    }

    [TestMethod]
    public void ShareRootParser_SupportsExplicitAndDerivedAliasForms()
    {
        var explicitRoot = ShareRootParser.Parse("[Live Sets] C:\\media\\live");
        var derivedRoot = ShareRootParser.Parse("C:\\media\\music");

        Assert.AreEqual("Live Sets", explicitRoot.Alias);
        Assert.AreEqual(@"C:\media\live", explicitRoot.LocalPath);
        Assert.IsNull(derivedRoot.Alias);
        Assert.AreEqual(@"C:\media\music", derivedRoot.LocalPath);
    }

    [TestMethod]
    [DataRow("[]C:\\music")]
    [DataRow("[.]C:\\music")]
    [DataRow("[a/b]C:\\music")]
    [DataRow("[missing")]
    [DataRow("[alias]")]
    public void ShareRootParser_RejectsInvalidForms(string value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => ShareRootParser.Parse(value));
    }

    [TestMethod]
    public void Validator_NormalizesAliasesExclusionsAndPeerPolicy()
    {
        string parent = Path.Combine(Path.GetTempPath(), "sockseek-sharing-tests");
        string rootPath = Path.Combine(parent, "MUSIC");
        string excludedPath = Path.Combine(rootPath, "private");
        var settings = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots = [new ShareRootSettings { LocalPath = rootPath }],
                ExcludedDirectories = [excludedPath],
                Filters = [@"\.part$"],
            },
            PeerAccess = new PeerAccessSettings
            {
                BlockedUsernames = [" Alice "],
                BlockedIpAddresses = ["::ffff:192.0.2.10"],
            },
        };

        SharingSettingsValidator.NormalizeAndValidate(settings, PathVariableContext.Empty);

        Assert.AreEqual("MUSIC", settings.Sharing.Roots[0].EffectiveAlias);
        Assert.AreEqual(Path.GetFullPath(rootPath), settings.Sharing.Roots[0].LocalPath);
        Assert.AreEqual(Path.GetFullPath(excludedPath), settings.Sharing.ExcludedDirectories[0]);
        Assert.AreEqual(" Alice ", settings.PeerAccess.BlockedUsernames[0]);
        Assert.AreEqual("192.0.2.10", settings.PeerAccess.BlockedIpAddresses[0]);

        var policy = new PeerAccessPolicy(settings.PeerAccess);
        Assert.IsTrue(policy.IsUsernameBlocked(" Alice "));
        Assert.IsFalse(policy.IsUsernameBlocked("alice"));
        Assert.IsTrue(policy.IsIpAddressBlocked(IPAddress.Parse("::ffff:192.0.2.10")));
        Assert.IsFalse(policy.IsUsernameBlocked("bob"));
    }

    [TestMethod]
    public void Validator_RejectsRemoteAliasCollisions()
    {
        string parent = Path.Combine(Path.GetTempPath(), "sockseek-sharing-tests");
        var settings = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots =
                [
                    new ShareRootSettings
                    {
                        LocalPath = Path.Combine(parent, "one"),
                        Alias = "Music",
                    },
                    new ShareRootSettings
                    {
                        LocalPath = Path.Combine(parent, "two"),
                        Alias = "MUSIC",
                    },
                ],
            },
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => SharingSettingsValidator.NormalizeAndValidate(
                settings,
                PathVariableContext.Empty));
    }

    [TestMethod]
    public void Validator_AllowsOverlappingRootsWithDistinctRemoteAliases()
    {
        string root = Path.Combine(Path.GetTempPath(), "sockseek-sharing-tests", "root");
        var settings = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots =
                [
                    new ShareRootSettings { LocalPath = root },
                    new ShareRootSettings { LocalPath = Path.Combine(root, "child") },
                ],
            },
        };

        SharingSettingsValidator.NormalizeAndValidate(settings, PathVariableContext.Empty);

        Assert.AreEqual(2, settings.Sharing.Roots.Count);
        Assert.AreNotEqual(
            settings.Sharing.Roots[0].EffectiveAlias,
            settings.Sharing.Roots[1].EffectiveAlias);
    }

    [TestMethod]
    public void Validator_AllowsVolumeRootWithExplicitAlias()
    {
        string volumeRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        var settings = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots = [new ShareRootSettings { LocalPath = volumeRoot, Alias = "root" }],
            },
        };

        SharingSettingsValidator.NormalizeAndValidate(settings, PathVariableContext.Empty);

        Assert.AreEqual("root", settings.Sharing.Roots.Single().EffectiveAlias);
    }

    [TestMethod]
    public void Validator_RejectsOversizedPublicAlias()
    {
        var settings = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots =
                [
                    new ShareRootSettings
                    {
                        LocalPath = Path.Combine(
                            Path.GetTempPath(),
                            "sockseek-sharing-tests",
                            "oversized-alias"),
                        Alias = new string('a', SharingSettingsValidator.MaximumEncodedValueBytes + 1),
                    },
                ],
            },
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => SharingSettingsValidator.NormalizeAndValidate(
                settings,
                PathVariableContext.Empty));
    }

    [TestMethod]
    public void Validator_CompilesEveryFilterWithFiniteTimeout()
    {
        var regex = SharingSettingsValidator.CompileFilter(@"^(a+)+\1$");

        Assert.AreEqual(SharingSettingsValidator.RegexTimeout, regex.MatchTimeout);
        Assert.ThrowsExactly<RegexParseException>(
            () => SharingSettingsValidator.CompileFilter("[invalid"));
    }

    [TestMethod]
    public void Validator_RequiresExplicitAliasForVolumeRoot()
    {
        string volumeRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        var settings = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots = [new ShareRootSettings { LocalPath = volumeRoot }],
            },
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => SharingSettingsValidator.NormalizeAndValidate(
                settings,
                PathVariableContext.Empty));
    }

    [TestMethod]
    public void SettingsClone_DoesNotShareMutableDaemonCollections()
    {
        var original = new EngineSettings
        {
            Sharing = new SharingSettings
            {
                Roots = [new ShareRootSettings { LocalPath = "root", EffectiveAlias = "alias" }],
                Filters = ["one"],
            },
            PeerAccess = new PeerAccessSettings { BlockedUsernames = ["alice"] },
        };

        var clone = SettingsCloner.Clone(original);
        clone.Sharing.Roots[0].LocalPath = "changed";
        clone.Sharing.Filters.Add("two");
        clone.PeerAccess.BlockedUsernames.Clear();

        Assert.AreEqual("root", original.Sharing.Roots[0].LocalPath);
        CollectionAssert.AreEqual(new[] { "one" }, original.Sharing.Filters);
        CollectionAssert.AreEqual(new[] { "alice" }, original.PeerAccess.BlockedUsernames);
    }

    [TestMethod]
    public void SettingsClone_DoesNotShareMutableDownloadState()
    {
        var original = new DownloadSettings
        {
            Output = new OutputSettings
            {
                OnComplete = ["first"],
                IncompleteAlbumAction = new IncompleteAlbumActionSettings
                {
                    Kind = IncompleteAlbumActionKind.Move,
                    Path = "incomplete",
                },
            },
            Search = new SearchSettings
            {
                NecessaryCond = new FileConditions { Formats = ["flac"] },
                NecessaryFolderCond = new FolderConditions { RequiredTrackTitles = ["intro"] },
            },
            Preprocess = new PreprocessSettings
            {
                Regex = [(new RegexFields { Title = "before" }, new RegexFields { Title = "after" })],
            },
            AppliedAutoProfiles = ["lossless"],
        };

        DownloadSettings clone = SettingsCloner.Clone(original);
        Assert.AreNotSame(
            original.Preprocess.Regex![0].Item1,
            clone.Preprocess.Regex![0].Item1);
        clone.Output.OnComplete!.Add("second");
        clone.Output.IncompleteAlbumAction.Path = "changed";
        clone.Search.NecessaryCond.Formats[0] = "mp3";
        clone.Search.NecessaryFolderCond.RequiredTrackTitles[0] = "outro";
        clone.Preprocess.Regex[0] = (
            new RegexFields { Title = "changed" },
            clone.Preprocess.Regex[0].Item2);
        clone.AppliedAutoProfiles.Add("portable");

        CollectionAssert.AreEqual(new[] { "first" }, original.Output.OnComplete);
        Assert.AreEqual("incomplete", original.Output.IncompleteAlbumAction.Path);
        CollectionAssert.AreEqual(new[] { "flac" }, original.Search.NecessaryCond.Formats);
        CollectionAssert.AreEqual(
            new[] { "intro" }, original.Search.NecessaryFolderCond.RequiredTrackTitles);
        Assert.AreEqual("before", original.Preprocess.Regex[0].Item1.Title);
        CollectionAssert.AreEquivalent(new[] { "lossless" }, original.AppliedAutoProfiles.ToArray());
    }
}

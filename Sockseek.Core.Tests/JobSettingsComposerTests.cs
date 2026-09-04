using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Concurrent;

namespace Tests;

[TestClass]
public sealed class JobSettingsComposerTests
{
    [TestMethod]
    public void GenericSearchBaseline_HasNoImplicitMusicConditions()
    {
        var composer = new JobSettingsComposer(null, ProfileCatalog.Empty);

        DownloadSettings settings = composer.Compose(
            new DownloadSettings(),
            new SearchJob("manual.pdf"));

        AssertNeutral(settings.Search.NecessaryCond);
        AssertNeutral(settings.Search.PreferredCond);
        Assert.AreEqual(0, settings.Search.NecessaryFolderCond.RequiredTrackTitles.Count);
        Assert.AreEqual(0, settings.Search.PreferredFolderCond.RequiredTrackTitles.Count);
    }

    [TestMethod]
    public void TrackAndAlbumSearchBaselines_UseTheSameMusicConditions()
    {
        var composer = new JobSettingsComposer(null, ProfileCatalog.Empty);

        DownloadSettings track = composer.Compose(
            new DownloadSettings(),
            new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" }));
        DownloadSettings album = composer.Compose(
            new DownloadSettings(),
            new SearchJob(new AlbumQuery { Artist = "Artist", Album = "Album" }));

        Assert.AreEqual(track.Search.NecessaryCond, album.Search.NecessaryCond);
        Assert.AreEqual(track.Search.PreferredCond, album.Search.PreferredCond);
        CollectionAssert.AreEqual(SearchDefaults.Formats.ToArray(), track.Search.NecessaryCond.Formats);
        CollectionAssert.AreEqual(new[] { "mp3" }, track.Search.PreferredCond.Formats);
        Assert.AreEqual(3, track.Search.NecessaryCond.LengthTolerance);
        Assert.AreEqual(200, track.Search.PreferredCond.MinBitrate);
        Assert.AreEqual(48000, track.Search.PreferredCond.MaxSampleRate);
        Assert.IsTrue(track.Search.PreferredCond.StrictTitle);
        Assert.IsTrue(track.Search.PreferredCond.StrictAlbum);
    }

    [TestMethod]
    public void GenericSearch_StillHonorsExplicitProfileConditions()
    {
        var explicitProfile = Profile("documents", settings =>
        {
            settings.Search.NecessaryCond.Formats = ["pdf"];
            settings.Search.PreferredCond.MinBitrate = 17;
        });
        var catalog = new ProfileCatalog { NamedProfiles = [explicitProfile] };
        var composer = new JobSettingsComposer(null, catalog);

        DownloadSettings settings = composer.Compose(
            new DownloadSettings(),
            new SearchJob("manual"),
            request: new JobSettingsRequestLayers(ProfileNames: ["documents"]));

        CollectionAssert.AreEqual(new[] { "pdf" }, settings.Search.NecessaryCond.Formats);
        Assert.AreEqual(17, settings.Search.PreferredCond.MinBitrate);
        Assert.IsNull(settings.Search.NecessaryCond.LengthTolerance);
        Assert.IsNull(settings.Search.PreferredCond.LengthTolerance);
        Assert.IsFalse(settings.Search.PreferredCond.StrictTitle);
        Assert.IsFalse(settings.Search.PreferredCond.StrictAlbum);
    }

    [TestMethod]
    public void Composition_UsesOneSharedPrecedenceAndRequestForAutoMatching()
    {
        var defaultProfile = Profile("default", settings => settings.Transfer.MaxStaleTime = 1);
        var autoProfile = Profile("aggregate-auto", settings => settings.Transfer.MaxStaleTime = 2,
            condition: "aggregate");
        var namedProfile = Profile("named", settings => settings.Transfer.MaxStaleTime = 3);
        var launch = new DownloadSettingsPatch();
        launch.Add(settings => settings.Transfer.MaxStaleTime = 4);
        var request = new DownloadSettingsPatch();
        request.Add(settings =>
        {
            settings.Search.IsAggregate = true;
            settings.Transfer.MaxStaleTime = 5;
        });
        var catalog = new ProfileCatalog
        {
            DefaultProfile = defaultProfile,
            AutoProfiles = [autoProfile],
            NamedProfiles = [namedProfile],
        };
        var composer = new JobSettingsComposer(
            null,
            catalog,
            launchNamedProfiles: [namedProfile],
            launchDownload: launch);

        DownloadSettings settings = composer.Compose(
            new DownloadSettings(),
            new SongJob(new SongQuery { Artist = "Artist", Title = "Track" }),
            request: new JobSettingsRequestLayers(Download: request));

        CollectionAssert.AreEqual(new[] { "aggregate-auto" }, settings.AppliedAutoProfiles.ToArray());
        Assert.IsTrue(settings.Search.IsAggregate);
        Assert.AreEqual(5, settings.Transfer.MaxStaleTime);
    }

    [TestMethod]
    public void Composition_ProvenanceHonorsExplicitEqualValueOverrides()
    {
        var profilePatch = new DownloadSettingsPatch();
        profilePatch.Add(
            settings => settings.Skip.SkipExisting = true,
            ["Skip.SkipExisting"]);
        var requestPatch = new DownloadSettingsPatch();
        requestPatch.Add(
            settings => settings.Skip.SkipExisting = true,
            ["Skip.SkipExisting"]);
        var composer = new JobSettingsComposer(null, new ProfileCatalog
        {
            NamedProfiles =
            [
                new SettingsProfile { Name = "same", Download = profilePatch },
            ],
        });

        JobSettingsCompositionResult profile = composer.ComposeDetailed(
            new DownloadSettings(),
            new SearchJob("query"),
            request: new JobSettingsRequestLayers(ProfileNames: ["same"]));
        JobSettingsCompositionResult request = composer.ComposeDetailed(
            new DownloadSettings(),
            new SearchJob("query"),
            request: new JobSettingsRequestLayers(
                ProfileNames: ["same"],
                Download: requestPatch));

        Assert.AreEqual("profile", profile.Provenance["Skip.SkipExisting"]);
        Assert.AreEqual("request", request.Provenance["Skip.SkipExisting"]);
    }

    [TestMethod]
    public void IncrementalSorter_DerivesPreferenceTierFromAllPreferredConditions()
    {
        var search = SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic).Search;
        search.PreferredCond.Formats = ["mp3"];
        search.PreferredCond.MinBitrate = 200;
        search.PreferredCond.StrictTitle = true;
        var sorter = new IncrementalResultSorter(
            new SongQuery { Title = "Wanted" },
            search,
            new ConcurrentDictionary<string, int>());
        sorter.AddRange([
            Input(1, @"Share\Wanted.mp3", bitrate: 320),
            Input(2, @"Share\Wanted.flac", bitrate: 900),
            Input(3, @"Share\Wanted low.mp3", bitrate: 128),
            Input(4, @"Share\Different.mp3", bitrate: 320),
        ]);

        IReadOnlyDictionary<string, ProjectedFileCandidate> projected = sorter
            .SnapshotProjectedFiles()
            .ToDictionary(item => item.Input.Filename);
        Assert.AreEqual(
            SearchPreferenceTier.Preferred,
            projected[@"Share\Wanted.mp3"].ConditionFacts.PreferenceTier);
        Assert.AreEqual(
            SearchPreferenceTier.Other,
            projected[@"Share\Wanted.flac"].ConditionFacts.PreferenceTier);
        Assert.AreEqual(
            SearchPreferenceTier.Other,
            projected[@"Share\Wanted low.mp3"].ConditionFacts.PreferenceTier);
        Assert.AreEqual(
            SearchPreferenceTier.Other,
            projected[@"Share\Different.mp3"].ConditionFacts.PreferenceTier);

        var neutralSorter = new IncrementalResultSorter(
            new SongQuery { Title = "anything" },
            SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic).Search,
            new ConcurrentDictionary<string, int>());
        neutralSorter.AddRange([Input(5, @"Share\arbitrary.bin", bitrate: null)]);
        Assert.AreEqual(
            SearchPreferenceTier.Preferred,
            neutralSorter.SnapshotProjectedFiles().Single().ConditionFacts.PreferenceTier);
    }

    [TestMethod]
    public void SearchDefinition_RoundTripsStableProjectionInputsAndRejectsUnknownSchema()
    {
        var job = new SearchJob(new AlbumQuery
        {
            Artist = "Exact Artist",
            Album = "Exact Album",
            SearchHint = "hint",
            URI = "slsk://Peer/Share",
            ArtistMaybeWrong = true,
        });
        SearchSettings settings = SearchSettingsBaselines
            .Create(SearchSettingsBaselineKind.Music)
            .Search;
        settings.NecessaryCond.Formats = ["flac"];
        settings.PreferredFolderCond.MinTrackCount = 7;
        settings.PreferredFolderCond.AddRequiredTrackTitles(["Intro", "Finale"]);
        SearchDefinition original = SearchDefinition.Create(job, settings);

        string json = SearchDefinitionCodec.Serialize(original);
        SearchDefinition restored = SearchDefinitionCodec.Deserialize(json);
        SearchSettings restoredSettings = restored.ProjectionSettings.ToSettings();

        Assert.AreEqual(SearchDefinition.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.AreEqual(SearchSettingsBaselineKind.Music, restored.Baseline);
        Assert.AreEqual(SearchDefaultProjectionKind.Album, restored.DefaultProjection);
        Assert.AreEqual(job.QueryText, restored.NetworkQuery);
        Assert.AreEqual("Exact Artist", restored.AlbumQuery?.Artist);
        Assert.AreEqual("Exact Album", restored.AlbumQuery?.Album);
        Assert.AreEqual("hint", restored.AlbumQuery?.SearchHint);
        CollectionAssert.AreEqual(new[] { "flac" }, restoredSettings.NecessaryCond.Formats);
        Assert.AreEqual(7, restoredSettings.PreferredFolderCond.MinTrackCount);
        CollectionAssert.AreEqual(
            new[] { "Intro", "Finale" },
            restoredSettings.PreferredFolderCond.RequiredTrackTitles.ToArray());

        string unsupported = json.Replace(
            "\"schemaVersion\":1",
            "\"schemaVersion\":99",
            StringComparison.Ordinal);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            SearchDefinitionCodec.Deserialize(unsupported));
    }

    private static SettingsProfile Profile(
        string name,
        Action<DownloadSettings> apply,
        string? condition = null)
    {
        var patch = new DownloadSettingsPatch();
        patch.Add(apply);
        return new SettingsProfile { Name = name, Condition = condition, Download = patch };
    }

    private static void AssertNeutral(FileConditions conditions)
    {
        Assert.AreEqual(0, conditions.Formats.Length);
        Assert.IsNull(conditions.LengthTolerance);
        Assert.IsNull(conditions.MinBitrate);
        Assert.IsNull(conditions.MaxBitrate);
        Assert.IsNull(conditions.MinSampleRate);
        Assert.IsNull(conditions.MaxSampleRate);
        Assert.IsNull(conditions.MinBitDepth);
        Assert.IsNull(conditions.MaxBitDepth);
        Assert.IsFalse(conditions.StrictTitle);
        Assert.IsFalse(conditions.StrictArtist);
        Assert.IsFalse(conditions.StrictAlbum);
    }

    private static SearchProjectionInput Input(
        long sequence,
        string filename,
        int? bitrate)
        => new(
            sequence,
            checked((int)sequence),
            "peer",
            1,
            filename,
            100,
            bitrate,
            null,
            null,
            null,
            Path.GetExtension(filename).TrimStart('.'),
            1000,
            true,
            null,
            DateTimeOffset.UnixEpoch);
}

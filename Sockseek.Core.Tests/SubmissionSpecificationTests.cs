using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Planning;

namespace Tests;

[TestClass]
public sealed class SubmissionSpecificationTests
{
    [TestMethod]
    public async Task PlannedExtractionRoundTripRetainsResultAfterSourceChanges()
    {
        string csv = Path.Combine(
            Path.GetTempPath(),
            $"sockseek-retained-plan-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(csv, "artist,title\nOriginal Artist,Original Title\n");
        try
        {
            var root = new ExtractJob(csv, InputType.CSV);
            var planner = new JobPlanner(DefaultJobSettingsResolver.Instance);
            await foreach (PlannedJobNode _ in planner.PlanAsync(
                root,
                new DownloadSettings()))
            {
            }
            SubmissionSpecification specification = SubmissionSpecification.Create(
                root,
                root.PlannedEffectiveSettings!);
            await File.WriteAllTextAsync(csv, "artist,title\nChanged Artist,Changed Title\n");

            var restored = (ExtractJob)SubmissionSpecificationCodec
                .Deserialize(SubmissionSpecificationCodec.Serialize(specification))
                .MaterializeJob();
            Assert.IsTrue(restored.HasPlannedExtraction);
            SongJob song = restored.Result switch
            {
                SongJob direct => direct,
                JobList list => list.Jobs.OfType<SongJob>().Single(),
                _ => throw new AssertFailedException("The retained extraction did not contain a song."),
            };
            Assert.AreEqual("Original Artist", song.Query.Artist);
            Assert.AreEqual("Original Title", song.Query.Title);
        }
        finally
        {
            if (File.Exists(csv))
                File.Delete(csv);
        }
    }

    [TestMethod]
    public void Specification_RoundTripsTypedCommandEffectiveSettingsAndExactPeerSpelling()
    {
        var exactTarget = new PeerFileTarget(
            new PeerFileIdentity("PeerCase", @"Share\MiXeD\Track.FLAC"),
            123,
            ".FLAC",
            bitRate: 900,
            bitDepth: 24,
            sampleRate: 96_000,
            length: 181);
        var remote = new RemoteFileJob(
            exactTarget,
            new RelativeOutputPath(["MiXeD", "Track.FLAC"]))
        {
            ItemNumber = 7,
            LineNumber = 12,
            SourceMutation = SourceMutation.ClearTextLine("/operator/list.txt", 12, 7),
            SourceInputType = InputType.List,
        };
        var search = new SearchJob(new SongQuery
        {
            Artist = "Artist",
            Title = "Title",
            URI = "slsk://PeerCase/Share/MiXeD/Track.FLAC",
        });
        var root = new JobList("review", [search, remote]);
        var settings = SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Music);
        settings.Search.NecessaryCond.Formats = ["flac"];
        settings.Search.PreferredFolderCond.AddRequiredTrackTitles(["Intro", "Finale"]);
        settings.Output.OnComplete = ["-- notify exact"];
        settings.Spotify.ClientSecret = "do-not-retain-spotify";
        settings.YouTube.ApiKey = "do-not-retain-youtube";

        SubmissionSpecification original = SubmissionSpecification.Create(
            root,
            settings,
            new SubmissionSourceRevision("file", "/operator/list.txt", "sha256:abc", 99, DateTimeOffset.UnixEpoch));
        string json = SubmissionSpecificationCodec.Serialize(original);
        SubmissionSpecification restored = SubmissionSpecificationCodec.Deserialize(json);
        JobList restoredRoot = (JobList)restored.MaterializeJob();
        DownloadSettings restoredSettings = restored.MaterializeSettings();

        Assert.IsFalse(json.Contains("do-not-retain-spotify", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("do-not-retain-youtube", StringComparison.Ordinal));
        CollectionAssert.Contains(restored.CredentialBindings.ToArray(), "spotify.client-secret");
        CollectionAssert.Contains(restored.CredentialBindings.ToArray(), "youtube.api-key");
        Assert.AreEqual("sha256:abc", restored.SourceRevision?.Digest);
        Assert.AreEqual(2, restoredRoot.Jobs.Count);
        var restoredRemote = (RemoteFileJob)restoredRoot.Jobs[1];
        Assert.AreEqual("PeerCase", restoredRemote.Target.Username);
        Assert.AreEqual(@"Share\MiXeD\Track.FLAC", restoredRemote.Target.Filename);
        CollectionAssert.AreEqual(new[] { "MiXeD", "Track.FLAC" }, restoredRemote.OutputPath.Components.ToArray());
        Assert.AreEqual(12, restoredRemote.SourceMutation?.LineNumber);
        Assert.AreEqual(InputType.List, restoredRemote.SourceInputType);
        CollectionAssert.AreEqual(new[] { "flac" }, restoredSettings.Search.NecessaryCond.Formats);
        CollectionAssert.AreEqual(
            new[] { "Intro", "Finale" },
            restoredSettings.Search.PreferredFolderCond.RequiredTrackTitles.ToArray());
        CollectionAssert.AreEqual(new[] { "-- notify exact" }, restoredSettings.Output.OnComplete);
        Assert.IsNull(restoredSettings.Spotify.ClientSecret);
        Assert.IsNull(restoredSettings.YouTube.ApiKey);
    }

    [TestMethod]
    public void Specification_RejectsUnknownSchemaVersion()
    {
        var job = new SearchJob("manual.pdf");
        SubmissionSpecification specification = SubmissionSpecification.Create(
            job,
            SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic));
        string json = SubmissionSpecificationCodec.Serialize(specification)
            .Replace("\"schemaVersion\":1", "\"schemaVersion\":99", StringComparison.Ordinal);

        Assert.ThrowsExactly<NotSupportedException>(() =>
            SubmissionSpecificationCodec.Deserialize(json));
    }

    [TestMethod]
    public void DelayedGeneratedResultKeepsSubmissionTimeSeparateFromRegistrationTime()
    {
        DateTimeOffset submittedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var source = new ExtractJob("Artist - Title", InputType.String);
        Guid submissionId = SubmissionIdentity.AssignAccepted(
            source,
            new DownloadSettings(),
            submittedAtUtc: submittedAt);
        var generated = new SongJob(new SongQuery
        {
            Artist = "Artist",
            Title = "Title",
        });

        SubmissionIdentity.AssignGeneratedResult(source, generated);

        Assert.AreEqual(submissionId, generated.SubmissionId);
        Assert.AreEqual(JobSemanticRole.SemanticResult, generated.SemanticRole);
        Assert.IsTrue(generated.CreatedAtUtc > submittedAt,
            "A generated child's creation time is its own registration time, not submission time.");
        Assert.AreEqual(submittedAt, source.CreatedAtUtc);
    }
}

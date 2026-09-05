using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;

namespace Tests.Cli;

[TestClass]
public sealed class CliSearchViewParityTests
{
    [TestMethod]
    public void LocalResultRendererConsumesTheSharedKernelIncludingLockedRows()
    {
        var query = new SongQuery { Title = "Track" };
        var job = new SearchJob(query);
        DownloadSettings settings = SearchSettingsBaselines.Create(
            SearchSettingsBaselineKind.Music);
        job.Config = settings;
        job.Session.AddResponse(new SearchResponse(
            "exact-peer",
            token: 1,
            hasFreeUploadSlot: false,
            uploadSpeed: 12_345,
            queueLength: 7,
            fileList: [File(@"Music\Track public.flac")],
            lockedFileList: [File(@"Private\Track locked.flac")]));
        job.Session.Complete();
        using var output = new StringWriter();
        using (Printing.RedirectOutput(output))
        {
            Printing.PrintResults(
                job,
                PrintOption.Json | PrintOption.Full,
                settings.Search,
                new Dictionary<string, int>(StringComparer.Ordinal));
        }

        string json = output.ToString();
        StringAssert.Contains(json, "Track public.flac");
        StringAssert.Contains(json, "Track locked.flac");
        Assert.AreEqual(
            2,
            System.Text.Json.JsonDocument.Parse(json).RootElement.GetArrayLength());
    }

    [TestMethod]
    public void ResultsFullRendersRetainedConditionFactsWithoutReevaluatingSettings()
    {
        var query = new SongQuery { Title = "Track" };
        DownloadSettings settings = SearchSettingsBaselines.Create(
            SearchSettingsBaselineKind.Music);
        settings.Search.PreferredCond = new FileConditions { MinBitrate = 1 };
        var candidate = new FileCandidate(
            new PeerFileTarget(
                new PeerFileIdentity("peer", @"Music\Track.flac"),
                1_000,
                "flac",
                bitRate: 900),
            new SearchPeerSnapshot("peer", 1, 12_345, true),
            projectionFacts: new SearchConditionFacts(
                NecessaryConditionsSatisfied: true,
                PreferredConditionsSatisfied: false,
                SatisfiedPreferredConditions: [],
                ConfiguredPreferredConditions: [SearchPreferenceCondition.Bitrate]));
        var job = new SongJob(query) { Candidates = [candidate] };
        using var output = new StringWriter();
        using (Printing.RedirectOutput(output))
        {
            Printing.PrintResults(
                job,
                PrintOption.Results | PrintOption.Full,
                settings.Search,
                new Dictionary<string, int>(StringComparer.Ordinal));
        }

        // The current settings and file would satisfy MinBitrate=1. Rendering
        // "Bitrate fails" therefore proves presentation consumed the facts
        // computed by the shared projection instead of evaluating conditions again.
        StringAssert.Contains(output.ToString(), "prf:Bitrate fails");
    }

    private static Soulseek.File File(string path)
        => new(1, path, 1_000, "flac", []);
}

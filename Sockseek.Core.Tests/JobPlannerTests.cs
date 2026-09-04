using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Planning;

namespace Tests;

[TestClass]
public sealed class JobPlannerTests
{
    [TestMethod]
    public async Task Planner_StreamsIndependentFailuresWithoutDroppingLaterSiblings()
    {
        var root = new JobList("mixed",
        [
            new ExtractJob("Artist - Track", InputType.String),
            new ExtractJob("", InputType.String),
            new SongJob(new SongQuery { Artist = "Later", Title = "Sibling" }),
        ]);
        var planner = new JobPlanner(new JobSettingsComposerResolver(
            new JobSettingsComposer(null, ProfileCatalog.Empty)));

        List<PlannedJobNode> nodes = await CollectAsync(
            planner.PlanAsync(root, new DownloadSettings()));

        Assert.AreEqual("0", nodes[0].Ref);
        PlannedJobNode failed = nodes.Single(node => node.Ref == "0/1");
        Assert.AreEqual(PlannedJobState.Failed, failed.State);
        Assert.AreEqual("extraction", failed.FailureCode);
        PlannedJobNode later = nodes.Single(node => node.Ref == "0/2");
        Assert.AreEqual(PlannedJobState.Ready, later.State);
        Assert.IsInstanceOfType<SongJob>(later.RuntimeJob);
        Assert.IsNotNull(later.EffectiveSettings);
        var failedExtract = (ExtractJob)failed.RuntimeJob;
        Assert.IsTrue(failedExtract.HasPlannedExtraction);
        Assert.IsNotNull(failedExtract.PlannedExtractionFailure);
        Assert.IsNotNull(later.RuntimeJob.PlannedEffectiveSettings);
    }

    [TestMethod]
    public async Task Planner_CarriesResolvedExtractorIdentityIntoChildAutoProfiles()
    {
        string csv = Path.Combine(Path.GetTempPath(), $"sockseek-planner-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(csv, "artist,title\nTest Artist,Test Track\n");
        try
        {
            var patch = new DownloadSettingsPatch();
            patch.Add(settings => settings.Transfer.MaxStaleTime = 4242);
            var profile = new SettingsProfile
            {
                Name = "csv-song",
                Condition = "input-type == \"csv\" && download-mode == \"song\"",
                Download = patch,
            };
            var resolver = new ProfileJobSettingsResolver(
                baseDefaults: null,
                new ProfileCatalog { AutoProfiles = [profile], NamedProfiles = [profile] },
                namedProfiles: [],
                cliProfile: null);
            var planner = new JobPlanner(resolver);

            List<PlannedJobNode> nodes = await CollectAsync(planner.PlanAsync(
                new ExtractJob(csv, InputType.None),
                new DownloadSettings()));

            PlannedJobNode song = nodes.Single(node => node.RuntimeJob is SongJob);
            Assert.AreEqual(InputType.CSV, song.RuntimeJob.SourceInputType);
            Assert.AreEqual(4242, song.EffectiveSettings?.Transfer.MaxStaleTime);
            CollectionAssert.Contains(
                song.EffectiveSettings?.AppliedAutoProfiles.ToList(),
                "csv-song");
        }
        finally
        {
            if (File.Exists(csv))
                File.Delete(csv);
        }
    }

    private static async Task<List<PlannedJobNode>> CollectAsync(
        IAsyncEnumerable<PlannedJobNode> source)
    {
        var result = new List<PlannedJobNode>();
        await foreach (PlannedJobNode node in source)
            result.Add(node);
        return result;
    }

    private sealed class JobSettingsComposerResolver(JobSettingsComposer composer)
        : IJobSettingsResolver
    {
        public DownloadSettings Resolve(
            DownloadSettings inherited,
            Job job,
            JobSettingsInheritance inheritance = JobSettingsInheritance.None)
            => composer.Compose(inherited, job, inheritance);
    }
}

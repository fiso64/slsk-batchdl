using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Tests.Unit;

[TestClass]
public sealed class SearchViewKernelTests
{
    [TestMethod]
    public void EveryIncrementalPrefixEqualsRebuildAndCompletionOnlyChangesCompleteness()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions
            {
                StrictTitle = true,
                MinBitrate = 256,
            },
        };
        var projection = new FileSearchProjection(
            new SongQuery { Title = "Track" });
        SearchProjectionInput[] inputs =
        [
            Input(1, "peer-A", @"Music\Track low.mp3", 192, SearchResultVisibility.Public),
            Input(2, "peer-B", @"Music\Track.flac", 900, SearchResultVisibility.Locked),
            Input(3, "peer-C", @"Music\Other.flac", 900, SearchResultVisibility.Public),
            Input(4, "peer-D", @"Music\Track.flac", 900, SearchResultVisibility.Public),
        ];

        var incremental = new SearchViewKernel(projection, settings);
        int consumed = 0;
        foreach (int batchSize in new[] { 1, 2, 1 })
        {
            consumed += batchSize;
            incremental.Apply(
                inputs.Skip(consumed - batchSize).Take(batchSize),
                sourceRevision: consumed,
                isComplete: false);

            var rebuilt = new SearchViewKernel(projection, settings);
            rebuilt.Apply(
                inputs.Take(consumed),
                sourceRevision: consumed,
                isComplete: false);
            AssertEquivalent(rebuilt.Snapshot(), incremental.Snapshot());
        }

        SearchViewKernelSnapshot before = incremental.Snapshot();
        SearchViewKernelUpdate completed = incremental.Apply(
            [],
            sourceRevision: 5,
            isComplete: true);
        SearchViewKernelSnapshot after = incremental.Snapshot();

        Assert.IsTrue(completed.IsComplete);
        Assert.AreEqual(0, completed.ChangedFiles.Count);
        Assert.AreEqual(before.Counters, after.Counters);
        CollectionAssert.AreEqual(
            before.Files.Select(file => file.Input.Sequence).ToArray(),
            after.Files.Select(file => file.Input.Sequence).ToArray());
    }

    [TestMethod]
    public void PreferenceTierIsExactlyAllConfiguredPreferredConditions()
    {
        var withConditions = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions
            {
                StrictTitle = true,
                MinBitrate = 256,
            },
        };
        var projection = new FileSearchProjection(new SongQuery { Title = "Track" });
        var kernel = new SearchViewKernel(projection, withConditions);
        kernel.Apply(
        [
            Input(1, "preferred", @"Music\Track.flac", 900, SearchResultVisibility.Public),
            Input(2, "other", @"Music\Track.mp3", 192, SearchResultVisibility.Public),
        ], 2, false);
        SearchViewKernelSnapshot snapshot = kernel.Snapshot();

        ProjectedFileCandidate preferred = snapshot.Files.Single(file => file.Input.Username == "preferred");
        ProjectedFileCandidate other = snapshot.Files.Single(file => file.Input.Username == "other");
        Assert.AreEqual(SearchPreferenceTier.Preferred, preferred.ConditionFacts.PreferenceTier);
        CollectionAssert.AreEquivalent(
            new[] { SearchPreferenceCondition.Title, SearchPreferenceCondition.Bitrate },
            preferred.ConditionFacts.SatisfiedPreferredConditions!.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { SearchPreferenceCondition.Title, SearchPreferenceCondition.Bitrate },
            preferred.ConditionFacts.ConfiguredPreferredConditions!.ToArray());
        Assert.AreEqual(0, preferred.ConditionFacts.UnsatisfiedPreferredConditions.Count);
        Assert.AreEqual(SearchPreferenceTier.Other, other.ConditionFacts.PreferenceTier);
        CollectionAssert.AreEquivalent(
            new[] { SearchPreferenceCondition.Title },
            other.ConditionFacts.SatisfiedPreferredConditions!.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { SearchPreferenceCondition.Bitrate },
            other.ConditionFacts.UnsatisfiedPreferredConditions.ToArray());

        var withoutConditions = new SearchViewKernel(
            projection,
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            });
        withoutConditions.Apply(
            [Input(1, "any", @"Elsewhere\Anything.bin", null, SearchResultVisibility.Public)],
            1,
            false);
        Assert.AreEqual(
            SearchPreferenceTier.Preferred,
            withoutConditions.Snapshot().Files.Single().ConditionFacts.PreferenceTier);
    }

    [TestMethod]
    public void WorkflowLocalUserSuccessInputProducesTheSameAdmissionAndOrderForEveryConsumer()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions(),
            DownrankOn = -1,
            IgnoreOn = -2,
        };
        var projection = new FileSearchProjection(
            new SongQuery { Title = "Track" },
            IncludeFullResults: true);
        SearchProjectionInput[] inputs =
        [
            Input(1, "failed-peer", @"Music\Track.flac", 900, SearchResultVisibility.Public),
            Input(2, "successful-peer", @"Music\Track.flac", 900, SearchResultVisibility.Public),
            Input(3, "ignored-peer", @"Music\Track.flac", 900, SearchResultVisibility.Public),
        ];
        var workflowCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["failed-peer"] = -1,
            ["successful-peer"] = 0,
            ["ignored-peer"] = -2,
        };

        var local = new SearchViewKernel(projection, settings, workflowCounts);
        var daemon = new SearchViewKernel(projection, settings, workflowCounts);
        local.Apply(inputs, 3, true);
        daemon.Apply(inputs, 3, true);

        AssertEquivalent(local.Snapshot(), daemon.Snapshot());
        CollectionAssert.AreEqual(
            new[] { "successful-peer", "failed-peer" },
            daemon.Snapshot().Files.Select(file => file.Input.Username).ToArray());
    }

    [TestMethod]
    public void RejectsOutOfOrderFreshInputsWithoutPublishingAPartialBatch()
    {
        var kernel = new SearchViewKernel(
            new FileSearchProjection(new SongQuery { Title = "Track" }),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            });

        Assert.ThrowsExactly<InvalidDataException>(() => kernel.Apply(
        [
            Input(2, "later", @"Music\Track 2.flac", 900, SearchResultVisibility.Public),
            Input(1, "earlier", @"Music\Track 1.flac", 900, SearchResultVisibility.Public),
        ], 2, false));

        SearchViewKernelSnapshot snapshot = kernel.Snapshot();
        Assert.AreEqual(0L, snapshot.ConsumedSequence);
        Assert.AreEqual(0L, snapshot.Counters.PublicFileCount);
        Assert.AreEqual(0, snapshot.Files.Count);
    }

    [TestMethod]
    public void GenericDirectoriesAreExactAndEveryPrefixMatchesARebuild()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions { MinBitrate = 256 },
        };
        var projection = new SearchViewProjectionDefinition(
            SearchViewProjectionKind.GenericDirectories,
            new SongQuery { Title = "Track" },
            IncludeFullResults: true);
        SearchProjectionInput[] inputs =
        [
            Input(1, "Peer", @"Share\One\Track low.mp3", 192, SearchResultVisibility.Public),
            Input(2, "Peer", @"Share\One\Track.flac", 900, SearchResultVisibility.Locked),
            Input(3, "Peer", @"Share\Two\Track.flac", 900, SearchResultVisibility.Public),
            Input(4, "peer", @"Share\One\Track.flac", 900, SearchResultVisibility.Public),
        ];
        var incremental = new SearchViewKernel(projection, settings);

        for (int count = 1; count <= inputs.Length; count++)
        {
            SearchViewKernelUpdate update = incremental.Apply(
                [inputs[count - 1]],
                count,
                false);
            Assert.IsNotNull(update.ChangedDirectories);

            var rebuilt = new SearchViewKernel(projection, settings);
            rebuilt.Apply(inputs.Take(count), count, false);
            AssertDirectoriesEqual(
                rebuilt.Snapshot().Directories!,
                incremental.Snapshot().Directories!);
            Assert.AreEqual(
                rebuilt.Snapshot().Counters,
                incremental.Snapshot().Counters);
        }

        SearchViewProjectedDirectory exact = incremental.Snapshot().Directories!
            .Single(directory => directory.Directory == new PeerDirectoryIdentity("Peer", @"Share\One"));
        Assert.AreEqual(1L, exact.PublicMatchingFileCount);
        Assert.AreEqual(1L, exact.LockedMatchingFileCount);
        Assert.AreEqual(SearchResultVisibility.Locked, exact.BestChild.Input.Visibility);
        Assert.AreEqual(3L, incremental.Snapshot().Counters.TopLevelItemCount);
        Assert.AreEqual(3L, incremental.Snapshot().Counters.SelectableOptionCount);
    }

    [TestMethod]
    public void AlbumDirectoriesUseOnePassFactsAndRemovalMatchesEveryPrefix()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions { MinBitrate = 256 },
            NecessaryFolderCond = new FolderConditions { MaxTrackCount = 1 },
        };
        var projection = new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AlbumDirectories,
            AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" });
        SearchProjectionInput[] inputs =
        [
            Input(1, "Peer", @"ELO\Time\01. Twilight.flac", 900, SearchResultVisibility.Public),
            Input(2, "Peer", @"ELO\Time\02. Yours Truly.flac", 192, SearchResultVisibility.Public),
        ];
        var incremental = new SearchViewKernel(projection, settings);

        SearchViewKernelUpdate first = incremental.Apply([inputs[0]], 1, false);
        Assert.AreEqual(1, first.ChangedDirectories!.Count);
        Assert.AreEqual(0, first.RemovedDirectories!.Count);
        SearchViewProjectedDirectory folder = incremental.Snapshot().Directories!.Single();
        Assert.AreEqual(SearchPreferenceTier.Preferred, folder.BestChild.ConditionFacts.PreferenceTier);

        SearchViewKernelUpdate second = incremental.Apply([inputs[1]], 2, false);
        Assert.AreEqual(0, second.ChangedDirectories!.Count);
        Assert.AreEqual(1, second.RemovedDirectories!.Count);
        Assert.AreEqual(0L, second.Counters.TopLevelItemCount);
        Assert.AreEqual(0L, second.Counters.ProjectedFileCount);

        var rebuilt = new SearchViewKernel(projection, settings);
        rebuilt.Apply(inputs, 2, false);
        AssertDirectoriesEqual(
            rebuilt.Snapshot().Directories!,
            incremental.Snapshot().Directories!);
        Assert.AreEqual(rebuilt.Snapshot().Counters, incremental.Snapshot().Counters);
    }

    [TestMethod]
    public void AggregateTrackGroupsAndAlternativesMatchEveryPrefix()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions { MinBitrate = 256 },
            MinSharesAggregate = 1,
            Relax = true,
        };
        var projection = new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AggregateTracks,
            new SongQuery { Artist = "ELO" });
        SearchProjectionInput[] inputs =
        [
            Input(1, "Peer-A", @"ELO - Track.flac", 900, SearchResultVisibility.Public),
            Input(2, "Peer-B", @"ELO - Track.mp3", 192, SearchResultVisibility.Locked),
            Input(3, "Peer-C", @"ELO - Other.flac", 900, SearchResultVisibility.Public),
        ];
        var incremental = new SearchViewKernel(projection, settings);
        for (int count = 1; count <= inputs.Length; count++)
        {
            SearchViewKernelUpdate update = incremental.Apply(
                [inputs[count - 1]],
                count,
                false);
            Assert.IsNotNull(update.ChangedAggregateTrackGroups);
            var rebuilt = new SearchViewKernel(projection, settings);
            rebuilt.Apply(inputs.Take(count), count, false);
            AssertAggregateTracksEqual(
                rebuilt.Snapshot().AggregateTrackGroups!,
                incremental.Snapshot().AggregateTrackGroups!);
            Assert.AreEqual(rebuilt.Snapshot().Counters, incremental.Snapshot().Counters);
        }

        SearchViewProjectedAggregateTrackGroup track = incremental.Snapshot()
            .AggregateTrackGroups!
            .Single(group => group.Query.Title.Contains("Track", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(2, track.ShareCount);
        Assert.AreEqual(1L, track.SelectableOptionCount);
        Assert.AreEqual(2, track.NewOptions.Count);
    }

    [TestMethod]
    public void AggregateAlbumGroupsAndDirectoryOptionsMatchEveryPrefix()
    {
        var settings = new SearchSettings
        {
            NecessaryCond = new FileConditions(),
            PreferredCond = new FileConditions(),
            NecessaryFolderCond = new FolderConditions(),
            MinSharesAggregate = 1,
        };
        var projection = new SearchViewProjectionDefinition(
            SearchViewProjectionKind.AggregateAlbums,
            AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" });
        SearchProjectionInput[] inputs =
        [
            Input(1, "Peer-A", @"ELO\Time\01. Track.flac", 900, SearchResultVisibility.Public),
            Input(2, "Peer-B", @"ELO\Time\01. Track.flac", 900, SearchResultVisibility.Public),
        ];
        var incremental = new SearchViewKernel(projection, settings);
        for (int count = 1; count <= inputs.Length; count++)
        {
            SearchViewKernelUpdate update = incremental.Apply(
                [inputs[count - 1]],
                count,
                false);
            Assert.IsNotNull(update.ChangedAggregateAlbumGroups);
            var rebuilt = new SearchViewKernel(projection, settings);
            rebuilt.Apply(inputs.Take(count), count, false);
            AssertAggregateAlbumsEqual(
                rebuilt.Snapshot().AggregateAlbumGroups!,
                incremental.Snapshot().AggregateAlbumGroups!);
            Assert.AreEqual(rebuilt.Snapshot().Counters, incremental.Snapshot().Counters);
        }

        SearchViewProjectedAggregateAlbumGroup time = incremental.Snapshot()
            .AggregateAlbumGroups!
            .Single(group => group.Options.Any(folder => folder.FolderPath == @"ELO\Time"));
        Assert.AreEqual(2, time.ShareCount);
        Assert.AreEqual(2L, time.SelectableOptionCount);
        Assert.AreEqual(2, time.Options.Count);
    }

    private static void AssertEquivalent(
        SearchViewKernelSnapshot expected,
        SearchViewKernelSnapshot actual)
    {
        Assert.AreEqual(expected.SourceRevision, actual.SourceRevision);
        Assert.AreEqual(expected.ConsumedSequence, actual.ConsumedSequence);
        Assert.AreEqual(expected.IsComplete, actual.IsComplete);
        Assert.AreEqual(expected.Counters, actual.Counters);
        CollectionAssert.AreEqual(
            expected.Files.Select(file => file.Input.Sequence).ToArray(),
            actual.Files.Select(file => file.Input.Sequence).ToArray());
        CollectionAssert.AreEqual(
            expected.Files.Select(file => file.ConditionFacts.PreferenceTier).ToArray(),
            actual.Files.Select(file => file.ConditionFacts.PreferenceTier).ToArray());
        CollectionAssert.AreEqual(
            expected.Files.Select(file => file.SortKey).ToArray(),
            actual.Files.Select(file => file.SortKey).ToArray());
    }

    private static void AssertDirectoriesEqual(
        IReadOnlyList<SearchViewProjectedDirectory> expected,
        IReadOnlyList<SearchViewProjectedDirectory> actual)
    {
        string[] expectedRows = expected
            .OrderBy(item => item.Directory.Username, StringComparer.Ordinal)
            .ThenBy(item => item.Directory.FolderPath, StringComparer.Ordinal)
            .Select(item => $"{item.Directory.Username}\0{item.Directory.FolderPath}\0" +
                $"{item.PublicMatchingFileCount}\0{item.LockedMatchingFileCount}\0" +
                $"{item.PublicMatchingBytes}\0{item.LockedMatchingBytes}\0" +
                $"{item.BestChild.Input.Sequence}")
            .ToArray();
        string[] actualRows = actual
            .OrderBy(item => item.Directory.Username, StringComparer.Ordinal)
            .ThenBy(item => item.Directory.FolderPath, StringComparer.Ordinal)
            .Select(item => $"{item.Directory.Username}\0{item.Directory.FolderPath}\0" +
                $"{item.PublicMatchingFileCount}\0{item.LockedMatchingFileCount}\0" +
                $"{item.PublicMatchingBytes}\0{item.LockedMatchingBytes}\0" +
                $"{item.BestChild.Input.Sequence}")
            .ToArray();
        CollectionAssert.AreEqual(expectedRows, actualRows);
    }

    private static void AssertAggregateTracksEqual(
        IReadOnlyList<SearchViewProjectedAggregateTrackGroup> expected,
        IReadOnlyList<SearchViewProjectedAggregateTrackGroup> actual)
    {
        string[] ExpectedRows() => expected
            .OrderBy(group => group.Index)
            .Select(group => $"{group.Index}\0{group.Query}\0{group.ShareCount}\0" +
                $"{group.SelectableOptionCount}\0{group.Representative.Input.Sequence}\0" +
                string.Join(',', group.NewOptions.Select(option => option.Input.Sequence)))
            .ToArray();
        string[] ActualRows() => actual
            .OrderBy(group => group.Index)
            .Select(group => $"{group.Index}\0{group.Query}\0{group.ShareCount}\0" +
                $"{group.SelectableOptionCount}\0{group.Representative.Input.Sequence}\0" +
                string.Join(',', group.NewOptions.Select(option => option.Input.Sequence)))
            .ToArray();
        CollectionAssert.AreEqual(ExpectedRows(), ActualRows());
    }

    private static void AssertAggregateAlbumsEqual(
        IReadOnlyList<SearchViewProjectedAggregateAlbumGroup> expected,
        IReadOnlyList<SearchViewProjectedAggregateAlbumGroup> actual)
    {
        string[] expectedRows = expected
            .OrderBy(group => group.Index)
            .Select(group => $"{group.StableIdentity.Username}\0{group.StableIdentity.FolderPath}\0" +
                $"{group.ShareCount}\0{group.SelectableOptionCount}\0" +
                string.Join(',', group.Options.Select(folder =>
                    folder.Username + "|" + folder.FolderPath)))
            .ToArray();
        string[] actualRows = actual
            .OrderBy(group => group.Index)
            .Select(group => $"{group.StableIdentity.Username}\0{group.StableIdentity.FolderPath}\0" +
                $"{group.ShareCount}\0{group.SelectableOptionCount}\0" +
                string.Join(',', group.Options.Select(folder =>
                    folder.Username + "|" + folder.FolderPath)))
            .ToArray();
        CollectionAssert.AreEqual(expectedRows, actualRows);
    }

    private static SearchProjectionInput Input(
        long sequence,
        string username,
        string filename,
        int? bitrate,
        SearchResultVisibility visibility)
        => new(
            sequence,
            checked((int)sequence),
            username,
            1,
            filename,
            1_000,
            bitrate,
            null,
            44_100,
            180,
            Path.GetExtension(filename),
            1_000,
            true,
            null,
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"),
            3,
            visibility);
}

using System.Collections.Concurrent;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

using System.Text.Json.Serialization;

namespace Sockseek.Core.Services;

[JsonConverter(typeof(JsonStringEnumConverter<SearchPreferenceTier>))]
public enum SearchPreferenceTier
{
    Preferred,
    Other,
}

[JsonConverter(typeof(JsonStringEnumConverter<SearchPreferenceCondition>))]
public enum SearchPreferenceCondition
{
    Format,
    Length,
    Bitrate,
    SampleRate,
    BitDepth,
    Title,
    Artist,
    Album,
    Username,
}

public readonly record struct SearchConditionFacts(
    bool NecessaryConditionsSatisfied,
    bool PreferredConditionsSatisfied,
    IReadOnlyList<SearchPreferenceCondition>? SatisfiedPreferredConditions = null,
    IReadOnlyList<SearchPreferenceCondition>? ConfiguredPreferredConditions = null)
{
    public SearchPreferenceTier PreferenceTier => PreferredConditionsSatisfied
        ? SearchPreferenceTier.Preferred
        : SearchPreferenceTier.Other;

    public IReadOnlyList<SearchPreferenceCondition> UnsatisfiedPreferredConditions
        => (ConfiguredPreferredConditions ?? [])
            .Except(SatisfiedPreferredConditions ?? [])
            .ToArray();
}

public sealed record ProjectedFileCandidate(
    SearchProjectionInput Input,
    FileCandidate Candidate,
    SearchConditionFacts ConditionFacts,
    SearchProjectionSortKey SortKey);

public readonly record struct SearchProjectionSortKey(
    uint HighFlags,
    int UploadSpeedFast,
    uint MidFlags,
    int InferredTrackCount,
    int UploadSpeedMedium,
    int BitRate,
    int StableTieBreaker);

public sealed class SearchProjectionSortKeyComparer : IComparer<SearchProjectionSortKey>
{
    public static SearchProjectionSortKeyComparer Instance { get; } = new();

    private SearchProjectionSortKeyComparer() { }

    public int Compare(SearchProjectionSortKey x, SearchProjectionSortKey y)
    {
        int comparison = y.HighFlags.CompareTo(x.HighFlags);
        if (comparison != 0) return comparison;
        comparison = y.UploadSpeedFast.CompareTo(x.UploadSpeedFast);
        if (comparison != 0) return comparison;
        comparison = y.MidFlags.CompareTo(x.MidFlags);
        if (comparison != 0) return comparison;
        comparison = y.InferredTrackCount.CompareTo(x.InferredTrackCount);
        if (comparison != 0) return comparison;
        comparison = y.UploadSpeedMedium.CompareTo(x.UploadSpeedMedium);
        if (comparison != 0) return comparison;
        comparison = y.BitRate.CompareTo(x.BitRate);
        return comparison != 0
            ? comparison
            : y.StableTieBreaker.CompareTo(x.StableTieBreaker);
    }
}

public sealed class IncrementalResultSorter
{
    private readonly ResultSorter.SortKeyContext keyContext;
    private readonly SongQuery query;
    private readonly SearchSettings search;
    private readonly bool requireFileSatisfies;
    private readonly bool retainProjectedRows;
    private List<ResultSorter.SortEntry> entries = [];
    private readonly HashSet<(PeerPathKey Path, SearchResultVisibility Visibility)>? seen;
    private int nextOriginalIndex;

    public IncrementalResultSorter(
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool albumMode = false,
        bool useInfer = false,
        bool requireFileSatisfies = false,
        bool ignoreStringSortConditions = false,
        bool retainProjectedRows = true,
        bool deduplicateInputs = true,
        Func<SearchProjectionInput, bool>? necessaryConditionEvaluator = null)
    {
        this.query = query;
        this.search = search;
        this.requireFileSatisfies = requireFileSatisfies;
        this.retainProjectedRows = retainProjectedRows;
        seen = deduplicateInputs ? [] : null;
        keyContext = ResultSorter.CreateSortKeyContext(
            [],
            query,
            search,
            userSuccessCounts,
            useBracketCheck: !albumMode,
            useInfer,
            albumMode,
            ignoreStringSortConditions,
            necessaryConditionEvaluator);
    }

    public int Count => entries.Count;

    public void Clear()
    {
        entries.Clear();
        seen?.Clear();
        nextOriginalIndex = 0;
    }

    public int AddRange(IEnumerable<(SearchResponse Response, SlFile File)> results)
    {
        var newEntries = new List<ResultSorter.SortEntry>();
        foreach (var (response, file) in results)
        {
            var key = (new PeerPathKey(response.Username, file.Filename), SearchResultVisibility.Public);
            if (seen != null && !seen.Add(key))
                continue;

            var entry = ResultSorter.CreateSortEntry(response, file, keyContext, nextOriginalIndex++);
            if (!entry.HasValue)
                continue;
            if (requireFileSatisfies && !entry.Value.Key.ConditionFacts.NecessaryConditionsSatisfied)
                continue;

            newEntries.Add(entry.Value);
        }

        if (newEntries.Count == 0)
            return 0;

        if (retainProjectedRows)
        {
            newEntries.Sort(ResultSorter.SortEntryComparer.Instance);
            MergeSortedEntries(newEntries);
        }
        return newEntries.Count;
    }

    public int AddRange(IEnumerable<SearchProjectionInput> results)
        => AddRangeAndGetProjected(results).Count;

    public IReadOnlyList<ProjectedFileCandidate> AddRangeAndGetProjected(
        IEnumerable<SearchProjectionInput> results)
    {
        var newEntries = new List<ResultSorter.SortEntry>();
        foreach (var input in results)
        {
            var key = (new PeerPathKey(input.Username, input.Filename), input.Visibility);
            if (seen != null && !seen.Add(key))
                continue;
            var entry = ResultSorter.CreateSortEntry(input, keyContext, nextOriginalIndex++);
            if (!entry.HasValue)
                continue;
            if (requireFileSatisfies && !entry.Value.Key.ConditionFacts.NecessaryConditionsSatisfied)
                continue;
            newEntries.Add(entry.Value);
        }
        if (newEntries.Count == 0)
            return [];
        var added = newEntries.Select(Projected).ToArray();
        if (!retainProjectedRows)
            return added;
        newEntries.Sort(ResultSorter.SortEntryComparer.Instance);
        MergeSortedEntries(newEntries);
        return added;
    }

    private void MergeSortedEntries(List<ResultSorter.SortEntry> newEntries)
    {
        if (entries.Count == 0)
        {
            entries.AddRange(newEntries);
            return;
        }

        var merged = new List<ResultSorter.SortEntry>(entries.Count + newEntries.Count);
        int existingIndex = 0;
        int newIndex = 0;
        var comparer = ResultSorter.SortEntryComparer.Instance;

        while (existingIndex < entries.Count && newIndex < newEntries.Count)
        {
            if (comparer.Compare(entries[existingIndex], newEntries[newIndex]) <= 0)
                merged.Add(entries[existingIndex++]);
            else
                merged.Add(newEntries[newIndex++]);
        }

        while (existingIndex < entries.Count)
            merged.Add(entries[existingIndex++]);

        while (newIndex < newEntries.Count)
            merged.Add(newEntries[newIndex++]);

        entries = merged;
    }

    internal IEnumerable<(SearchResponse Response, SlFile File)> OrderedResults()
    {
        for (int i = 0; i < entries.Count; i++)
            yield return (entries[i].Response!, entries[i].File!);
    }

    public List<(SearchResponse Response, SlFile File)> Snapshot()
    {
        var snapshot = new List<(SearchResponse Response, SlFile File)>(entries.Count);
        snapshot.AddRange(OrderedResults());
        return snapshot;
    }

    public List<SearchProjectionInput> SnapshotInputs()
        => retainProjectedRows
            ? entries.Select(entry => entry.Input).ToList()
            : throw new InvalidOperationException(
                "This incremental sorter was configured for disk-backed rows.");

    public List<ProjectedFileCandidate> SnapshotProjectedFiles()
        => retainProjectedRows
            ? entries.Select(Projected).ToList()
            : throw new InvalidOperationException(
                "This incremental sorter was configured for disk-backed rows.");

    private static ProjectedFileCandidate Projected(ResultSorter.SortEntry entry)
        => new(
            entry.Input,
            entry.Input.ToFileCandidate(),
            entry.Key.ConditionFacts,
            entry.Key.PersistenceKey);
}

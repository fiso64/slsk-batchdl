using System.Collections.Concurrent;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sockseek.Core.Services;

public enum FolderSortMode
{
    AlbumRanked,
    DeterministicUnranked,
}

public sealed class IncrementalAlbumFolderProjector
{
    private readonly AlbumFolderProjectionPlan projectionPlan;
    private readonly SearchSettings search;
    private readonly FolderSortMode sortMode;
    private readonly Dictionary<string, List<EvaluatedAlbumProjectionInput>> rowsByUser =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> dirtyUsers = new(StringComparer.Ordinal);
    private readonly HashSet<(PeerPathKey Path, SearchResultVisibility Visibility)> seen = [];
    private readonly Dictionary<PeerPathKey, AlbumFolder> folders = [];
    private readonly Dictionary<PeerPathKey, AlbumFolderSignature> signatures = [];
    private readonly Dictionary<PeerPathKey, long> firstSequences = [];
    private int nextOriginalIndex;

    public IncrementalAlbumFolderProjector(
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null,
        bool ignoreStringSortConditions = false,
        FolderSortMode sortMode = FolderSortMode.AlbumRanked)
    {
        this.search = search;
        this.sortMode = sortMode;
        projectionPlan = new AlbumFolderProjectionPlan(
            query,
            search,
            userSuccessCounts,
            ignoreStringSortConditions,
            sortMode);
    }

    public int Count => seen.Count;

    internal int AddRange(IEnumerable<(SearchResponse Response, SlFile File)> results)
        => AddRange(results.Select((result, index) => SearchProjectionInput.FromLive(
            index + 1L, index + 1, result.Response, result.File, DateTimeOffset.UnixEpoch)));

    public int AddRange(IEnumerable<SearchProjectionInput> results)
        => AddRange(results, admittedFiles: null);

    private int AddRange(
        IEnumerable<SearchProjectionInput> results,
        List<ProjectedFileCandidate>? admittedFiles)
    {
        int added = 0;
        foreach (SearchProjectionInput input in results)
        {
            EvaluatedAlbumProjectionInput? evaluated = projectionPlan.Evaluate(
                input,
                nextOriginalIndex++);
            if (evaluated == null)
                continue;
            var key = (
                new PeerPathKey(input.Username, input.Filename),
                input.Visibility);
            if (!seen.Add(key))
                continue;

            if (!rowsByUser.TryGetValue(
                    input.Username,
                    out List<EvaluatedAlbumProjectionInput>? rows))
            {
                rows = [];
                rowsByUser.Add(input.Username, rows);
            }
            rows.Add(evaluated.Value);
            admittedFiles?.Add(new ProjectedFileCandidate(
                input,
                input.ToFileCandidate(),
                evaluated.Value.SortEntry.Key.ConditionFacts,
                evaluated.Value.SortEntry.Key.PersistenceKey));
            dirtyUsers.Add(input.Username);
            added++;
        }

        return added;
    }

    public AlbumFolderSearchViewChanges AddRangeForSearchView(
        IEnumerable<SearchProjectionInput> results)
    {
        var admitted = new List<ProjectedFileCandidate>();
        AddRange(results, admitted);
        return new(GetChanges(), admitted);
    }

    internal AlbumFolderProjectionChanges AddRangeAndGetChanges(IEnumerable<(SearchResponse Response, SlFile File)> results)
    {
        AddRange(results);
        return GetChanges();
    }

    public AlbumFolderProjectionChanges AddRangeAndGetChanges(IEnumerable<SearchProjectionInput> results)
    {
        AddRange(results);
        return GetChanges();
    }

    public void Clear()
    {
        rowsByUser.Clear();
        dirtyUsers.Clear();
        seen.Clear();
        folders.Clear();
        signatures.Clear();
        firstSequences.Clear();
        nextOriginalIndex = 0;
    }

    public List<AlbumFolder> Snapshot()
    {
        RefreshDirtyUsers(null, null, null);
        return OrderedFolders();
    }

    public AlbumFolderProjectionChanges GetChanges()
    {
        var added = new List<AlbumFolder>();
        var updated = new List<AlbumFolder>();
        var removed = new List<AlbumFolder>();
        RefreshDirtyUsers(added, updated, removed);
        return new AlbumFolderProjectionChanges(
            OrderedFolders(),
            added,
            updated,
            removed);
    }

    private void RefreshDirtyUsers(
        List<AlbumFolder>? added,
        List<AlbumFolder>? updated,
        List<AlbumFolder>? removed)
    {
        if (dirtyUsers.Count == 0)
            return;
        foreach (string username in dirtyUsers.Order(StringComparer.Ordinal))
        {
            PeerPathKey[] oldKeys = folders.Keys
                .Where(key => string.Equals(key.Username, username, StringComparison.Ordinal))
                .ToArray();
            List<AlbumFolder> projected = projectionPlan.ProjectEvaluatedResults(
                rowsByUser[username],
                rowsByUser[username].Count);
            var newKeys = new HashSet<PeerPathKey>();
            foreach (AlbumFolder folder in projected)
            {
                PeerPathKey key = FolderKey(folder);
                newKeys.Add(key);
                AlbumFolderSignature signature = AlbumFolderSignature.Create(folder);
                if (!signatures.TryGetValue(key, out AlbumFolderSignature previous))
                    added?.Add(folder);
                else if (signature != previous)
                    updated?.Add(folder);
                folders[key] = folder;
                signatures[key] = signature;
                firstSequences[key] = folder.Files.Count == 0
                    ? long.MaxValue
                    : folder.Files.Min(file => file.Candidate.Evidence.Sequence);
            }

            foreach (PeerPathKey key in oldKeys)
            {
                if (newKeys.Contains(key))
                    continue;
                removed?.Add(folders[key]);
                folders.Remove(key);
                signatures.Remove(key);
                firstSequences.Remove(key);
            }
        }
        dirtyUsers.Clear();
    }

    private List<AlbumFolder> OrderedFolders()
        => sortMode == FolderSortMode.DeterministicUnranked
            ? folders.Values
                .OrderBy(folder => folder.Username, StringComparer.Ordinal)
                .ThenBy(folder => folder.FolderPath, StringComparer.Ordinal)
                .ToList()
            : folders.Values.Order(Comparer<AlbumFolder>.Create(CompareRanked)).ToList();

    private int CompareRanked(AlbumFolder? x, AlbumFolder? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x == null)
            return 1;
        if (y == null)
            return -1;
        ResultSorter.SortEntry? xEntry = x.SearchAggregateSortEntry;
        ResultSorter.SortEntry? yEntry = y.SearchAggregateSortEntry;
        if (xEntry.HasValue && yEntry.HasValue)
        {
            int comparison = ResultSorter.AlbumBeforeQualitySortEntryComparer.Instance.Compare(
                xEntry.Value,
                yEntry.Value);
            if (comparison != 0)
                return comparison;
        }
        else if (xEntry.HasValue)
            return -1;
        else if (yEntry.HasValue)
            return 1;

        if (AlbumQualityPolicy.ActiveConditions(search.NecessaryCond).IsActive)
        {
            int comparison = CompareCoverage(
                x.SearchAudioQualityCoverage.Format,
                y.SearchAudioQualityCoverage.Format);
            if (comparison != 0)
                return comparison;
            comparison = CompareCoverage(
                x.SearchAudioQualityCoverage.Bitrate,
                y.SearchAudioQualityCoverage.Bitrate);
            if (comparison != 0)
                return comparison;
            comparison = CompareCoverage(
                x.SearchAudioQualityCoverage.SampleRate,
                y.SearchAudioQualityCoverage.SampleRate);
            if (comparison != 0)
                return comparison;
            comparison = CompareCoverage(
                x.SearchAudioQualityCoverage.BitDepth,
                y.SearchAudioQualityCoverage.BitDepth);
            if (comparison != 0)
                return comparison;
        }

        if (xEntry.HasValue && yEntry.HasValue)
        {
            int comparison = ResultSorter.SortEntryComparer.Instance.Compare(
                xEntry.Value,
                yEntry.Value);
            if (comparison != 0)
                return comparison;
        }
        else if (xEntry.HasValue)
            return -1;
        else if (yEntry.HasValue)
            return 1;

        int rank = firstSequences[FolderKey(x)].CompareTo(firstSequences[FolderKey(y)]);
        if (rank != 0)
            return rank;
        int username = string.Compare(x.Username, y.Username, StringComparison.Ordinal);
        return username != 0
            ? username
            : string.Compare(x.FolderPath, y.FolderPath, StringComparison.Ordinal);
    }

    private static int CompareCoverage(
        AlbumQualityCoverageBucket x,
        AlbumQualityCoverageBucket y)
        => y.Bucket.CompareTo(x.Bucket);

    private static PeerPathKey FolderKey(AlbumFolder folder)
        => new(folder.Username, folder.FolderPath);

    private readonly record struct AlbumFolderSignature(
        int FileCount,
        int AudioFileCount,
        string? RepresentativeAudioFilename,
        string Lengths)
    {
        public static AlbumFolderSignature Create(AlbumFolder folder)
            => new(
                folder.SearchFileCount,
                folder.SearchAudioFileCount,
                folder.SearchRepresentativeAudioFilename,
                string.Join(",", folder.SearchSortedAudioLengths));
    }
}

public sealed record AlbumFolderProjectionChanges(
    IReadOnlyList<AlbumFolder> Folders,
    IReadOnlyList<AlbumFolder> Added,
    IReadOnlyList<AlbumFolder> Updated,
    IReadOnlyList<AlbumFolder> Removed)
{
    public bool HasChanges => Added.Count > 0 || Updated.Count > 0 || Removed.Count > 0;
}

public sealed record AlbumFolderSearchViewChanges(
    AlbumFolderProjectionChanges Folders,
    IReadOnlyList<ProjectedFileCandidate> AdmittedFiles);

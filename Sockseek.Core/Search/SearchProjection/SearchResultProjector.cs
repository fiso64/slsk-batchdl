using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Soulseek;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using SlFile = Soulseek.File;
using SlResponse = Soulseek.SearchResponse;

namespace Sockseek.Core.Services;

public static partial class SearchResultProjector
{
    public static List<FileCandidate> SortedTrackCandidates(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> rawResults,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = true)
        => SortedTrackCandidates(
            rawResults.Select((result, index) => SearchProjectionInput.FromLive(
                index + 1L, index + 1, result.Response, result.File, DateTimeOffset.UnixEpoch)),
            query,
            search,
            userSuccessCounts,
            useInfer);

    public static List<FileCandidate> SortedTrackCandidates(
        IEnumerable<SearchProjectionInput> rawResults,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = true,
        bool includeFullResults = true)
    {
        var projectionResults = includeFullResults
            ? rawResults
            : rawResults.Where(input => ConditionSatisfactionPolicy.SearchFileSatisfies(search.NecessaryCond, input, query));
        int capacity = projectionResults.TryGetNonEnumeratedCount(out int resultCount) ? resultCount : 0;
        var candidates = capacity > 0 ? new List<FileCandidate>(capacity) : [];
        foreach (var input in ResultSorter.OrderedInputs(projectionResults, query, search, userSuccessCounts, useInfer))
            candidates.Add(input.ToFileCandidate());
        return candidates;
    }

    public static List<SongJob> AggregateTracks(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> rawResults,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
        => AggregateTracks(rawResults.Select((result, index) => SearchProjectionInput.FromLive(
            index + 1L, index + 1, result.Response, result.File, DateTimeOffset.UnixEpoch)),
            query, search, userSuccessCounts);

    public static List<SongJob> AggregateTracks(
        IEnumerable<SearchProjectionInput> rawResults,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
    {
        var projector = new IncrementalAggregateTrackProjector(query, search, userSuccessCounts);
        projector.AddRange(rawResults);
        return projector.Snapshot();
    }

    internal static bool AggregateTrackProjectionIncludes(
        SearchResponse response,
        Soulseek.File file,
        SongQuery query,
        SearchSettings search)
        => ConditionSatisfactionPolicy.SearchFileSatisfies(search.NecessaryCond, response, file, query);

    internal static bool AggregateTrackProjectionIncludes(
        SearchProjectionInput input,
        SongQuery query,
        SearchSettings search)
        => ConditionSatisfactionPolicy.SearchFileSatisfies(search.NecessaryCond, input, query);

    public static List<AlbumFolder> AlbumFolders(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> rawResults,
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null,
        bool ignoreStringSortConditions = false,
        FolderSortMode sortMode = FolderSortMode.AlbumRanked)
    {
        var plan = new AlbumFolderProjectionPlan(
            query,
            search,
            userSuccessCounts,
            ignoreStringSortConditions,
            sortMode);
        var filteredResults = plan.FilterToList(rawResults);
        return plan.ProjectFilteredResults(filteredResults, filteredResults.Count);
    }

    public static List<AlbumFolder> AlbumFolders(
        IEnumerable<SearchProjectionInput> rawResults,
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null,
        bool ignoreStringSortConditions = false,
        FolderSortMode sortMode = FolderSortMode.AlbumRanked)
    {
        var filter = ConditionSatisfactionPolicy.CreateAlbumSearchFilter(query, search);
        var filtered = rawResults.Where(filter.Satisfies).ToList();
        var keyContext = new ResultSorter.SortKeyContext(
            Array.Empty<SearchProjectionInput>(),
            filter.SortQuery,
            search,
            userSuccessCounts ?? new ConcurrentDictionary<string, int>(),
            useBracketCheck: false,
            useInfer: false,
            albumMode: true,
            ignoreStringSortConditions);
        return AlbumFoldersFromResults(
            filtered,
            query,
            search,
            filtered.Count,
            aggregateSortKeyContext: keyContext,
            useAlbumFolderQualityRanking: sortMode == FolderSortMode.AlbumRanked);
    }

    internal static List<AlbumFolder> AlbumFoldersFromOrderedResults(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> orderedResults,
        AlbumQuery query,
        SearchSettings search,
        int capacity = 0,
        ResultSorter.SortKeyContext? aggregateSortKeyContext = null,
        bool useAlbumFolderQualityRanking = false)
        => AlbumFoldersFromResults(
            orderedResults,
            query,
            search,
            capacity,
            sortByResultOrder: true,
            aggregateSortKeyContext: aggregateSortKeyContext,
            useAlbumFolderQualityRanking: useAlbumFolderQualityRanking);

    internal static List<AlbumFolder> AlbumFoldersFromResults(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> results,
        AlbumQuery query,
        SearchSettings search,
        int capacity = 0,
        bool sortByResultOrder = false,
        ResultSorter.SortKeyContext? aggregateSortKeyContext = null,
        bool useAlbumFolderQualityRanking = false)
        => AlbumFoldersFromResults(
            results.Select((result, index) => SearchProjectionInput.FromLive(
                index + 1L, index + 1, result.Response, result.File, DateTimeOffset.UnixEpoch)),
            query, search, capacity, sortByResultOrder, aggregateSortKeyContext, useAlbumFolderQualityRanking);

    internal static List<AlbumFolder> AlbumFoldersFromResults(
        IEnumerable<SearchProjectionInput> results,
        AlbumQuery query,
        SearchSettings search,
        int capacity = 0,
        bool sortByResultOrder = false,
        ResultSorter.SortKeyContext? aggregateSortKeyContext = null,
        bool useAlbumFolderQualityRanking = false)
    {
        bool canMatchDisc = !DiscPatternRegex().IsMatch(query.Album) && !DiscPatternRegex().IsMatch(query.Artist);
        var dirStructure = capacity > 0
            ? new Dictionary<PeerPathKey, AlbumFolderBuilder>(capacity)
            : new Dictionary<PeerPathKey, AlbumFolderBuilder>();

        int resultIndex = 0;
        foreach (var input in results)
        {
            string username = input.Username;
            int fileSeparator = input.Filename.LastIndexOf('\\');
            if (fileSeparator <= 0)
                continue;

            string folderPath = input.Filename[..fileSeparator];
            string dirName = folderPath[(folderPath.LastIndexOf('\\') + 1)..];

            if (canMatchDisc && DiscPatternRegex().IsMatch(dirName))
            {
                int parentSeparator = folderPath.LastIndexOf('\\');
                if (parentSeparator > 0)
                    folderPath = folderPath[..parentSeparator];
            }

            var key = new PeerPathKey(username, folderPath);
            bool isMusic = Utils.IsMusicFile(input.Filename);
            var folderFile = new AlbumFolderFile(input, isMusic);
            var aggregateSortEntry = aggregateSortKeyContext == null
                ? null
                : ResultSorter.CreateSortEntry(input, aggregateSortKeyContext, resultIndex);
            int rank = sortByResultOrder ? resultIndex : int.MaxValue;
            if (!dirStructure.TryGetValue(key, out var value))
                dirStructure[key] = new AlbumFolderBuilder(username, folderPath, folderFile, rank, aggregateSortEntry, input.ResponseFileCount);
            else
            {
                value.Add(folderFile);
                value.AddRank(rank);
                value.AddAggregateSortEntry(aggregateSortEntry);
            }

            resultIndex++;
        }

        bool rankOrderMayChange = MergeChildDirectories(dirStructure);
        var activeQuality = AlbumQualityPolicy.ActiveConditions(search.NecessaryCond);
        foreach (var folder in dirStructure.Values)
        {
            if (activeQuality.IsActive)
                folder.RefreshQualityCoverage(search.NecessaryCond, activeQuality);
            else
                folder.RefreshInactiveQualityCoverage();
        }

        var folders = new List<AlbumFolder>();
        var inferDefault = new SongQuery { Artist = query.Artist, Album = query.Album };

        IEnumerable<AlbumFolderBuilder> orderedFolders;
        IEnumerable<AlbumFolderBuilder> candidateFolders = dirStructure.Values;
        if (activeQuality.IsActive)
            candidateFolders = candidateFolders.Where(folder => ConditionSatisfactionPolicy.AlbumQualityIsAcceptable(folder.QualityCoverage, search));

        if (useAlbumFolderQualityRanking)
        {
            orderedFolders = candidateFolders.Order(
                activeQuality.IsActive
                    ? AlbumFolderBuilderComparer.WithQualityCoverage
                    : AlbumFolderBuilderComparer.WithoutQualityCoverage);
        }
        else if (!sortByResultOrder)
        {
            orderedFolders = candidateFolders
                .OrderBy(x => x.Username, StringComparer.Ordinal)
                .ThenBy(x => x.FolderPath, StringComparer.Ordinal);
        }
        else if (rankOrderMayChange)
        {
            orderedFolders = candidateFolders
                .OrderBy(x => x.FirstRank)
                .ThenBy(x => x.Username, StringComparer.Ordinal)
                .ThenBy(x => x.FolderPath, StringComparer.Ordinal);
        }
        else
        {
            orderedFolders = candidateFolders;
        }

        foreach (var folder in orderedFolders)
        {
            if (folder.MusicCount == 0) continue;
            if (!ConditionSatisfactionPolicy.SearchAlbumFolderSatisfies(
                    search.NecessaryFolderCond,
                    folder.MusicCount,
                    folder.Files.Select(file => file.Input.Filename),
                    query,
                    search)) continue;

            if (folder.Files.Count > 1)
                folder.Files.Sort(AlbumFolderFileComparer.Instance);

            var qualityCoverage = folder.QualityCoverage;
            if (!ConditionSatisfactionPolicy.AlbumQualityIsAcceptable(qualityCoverage, search))
                continue;

            folders.Add(new AlbumFolder(
                folder.Username,
                folder.FolderPath,
                () => BuildAlbumFiles(folder.Files, inferDefault),
                folder.Files.Count,
                folder.MusicCount,
                SortedAudioLengths(folder.Files),
                RepresentativeAudioFilename(folder.Files),
                qualityCoverage,
                folder.AggregateSortEntry));
        }

        return folders;
    }

    private static int[] SortedAudioLengths(List<AlbumFolderFile> folderFiles)
        => folderFiles
            .Where(f => f.IsMusic)
            .Select(f => f.Input.Length ?? -1)
            .OrderBy(x => x)
            .ToArray();

    private static string? RepresentativeAudioFilename(List<AlbumFolderFile> folderFiles)
        => folderFiles.FirstOrDefault(f => f.IsMusic).Input?.Filename;

    private static List<AlbumFile> BuildAlbumFiles(List<AlbumFolderFile> folderFiles, SongQuery inferDefault)
    {
        var files = new List<AlbumFile>(folderFiles.Count);

        foreach (var item in folderFiles)
        {
            string filename = item.Input.Filename;
            files.Add(AlbumFile.WithLazyQuery(
                () => Searcher.InferSongQuery(filename, inferDefault),
                item.Input.ToFileCandidate()));
        }

        return files;
    }

    public static List<AlbumJob> AggregateAlbums(
        IEnumerable<AlbumFolder> albums,
        AlbumQuery query,
        SearchSettings search)
    {
        var projector = new IncrementalAlbumAggregateProjector(query, search);
        projector.ResetBatch(albums);
        return projector.Snapshot();
    }

    internal static AlbumJob CreateAggregateAlbumJob(AlbumQuery query, List<AlbumFolder> versions)
    {
        var repFolder = versions.FirstOrDefault()?.FolderPath;
        var itemName = !string.IsNullOrWhiteSpace(repFolder)
            ? Utils.GetBaseNameSlsk(repFolder)
            : null;
        // Populate Album so each job gets a unique index key. Without this, all
        // aggregate album jobs share the same key (artist + empty album), causing
        // index collisions: the last write wins and all albums on rerun match the
        // same path.
        var jobQuery = !string.IsNullOrWhiteSpace(itemName)
            ? new AlbumQuery(query) { Album = itemName }
            : query;
        var newJob = new AlbumJob(jobQuery);
        newJob.Results = versions;
        if (itemName != null)
            newJob.ItemName = itemName;
        return newJob;
    }

    // Album search is still file-based under the hood, so we project the album query into:
    // 1. a network search query (Artist + Album, or SearchHint when Album is empty)
    // 2. a file-match/sort query used by StrictTitle and album-mode sorting
    public static SongQuery AlbumNetworkQuery(AlbumQuery query)
        => new()
        {
            Artist = query.Artist,
            Title = query.Album.Length > 0 ? query.Album : query.SearchHint,
            Album = query.Album,
            ArtistMaybeWrong = query.ArtistMaybeWrong,
        };

    // Album search still uses Artist + Album (or SearchHint when Album is empty) for
    // the network query, but filename-level StrictTitle logic should only ever apply to
    // the optional song-title hint, never to the album name itself.
    public static SongQuery AlbumFileMatchQuery(AlbumQuery query)
        => new()
        {
            Artist = query.Artist,
            Title = query.SearchHint,
            Album = query.Album,
            ArtistMaybeWrong = query.ArtistMaybeWrong,
        };

    private static bool MergeChildDirectories(Dictionary<PeerPathKey, AlbumFolderBuilder> dirStructure)
    {
        var sortedKeys = dirStructure.Keys
            .OrderByDescending(k => k.RemotePath.Count(c => c == '\\'))
            .ThenBy(k => k.Username, StringComparer.Ordinal)
            .ThenBy(k => k.RemotePath, StringComparer.Ordinal)
            .ToList();
        var toRemove = new HashSet<PeerPathKey>();
        bool rankOrderMayChange = false;

        foreach (var key in sortedKeys)
        {
            if (toRemove.Contains(key)) continue;
            var parentKey = FindNearestExistingAncestor(key, dirStructure, toRemove);
            if (parentKey is not { } parent)
                continue;

            rankOrderMayChange |= dirStructure[parent].FirstRank > dirStructure[key].FirstRank;
            dirStructure[parent].AddRange(dirStructure[key]);
            toRemove.Add(key);
        }
        foreach (var key in toRemove)
            dirStructure.Remove(key);

        return rankOrderMayChange;
    }

    private static PeerPathKey? FindNearestExistingAncestor(
        PeerPathKey key,
        Dictionary<PeerPathKey, AlbumFolderBuilder> dirStructure,
        HashSet<PeerPathKey> toRemove)
    {
        int slash = key.RemotePath.LastIndexOf('\\');
        while (slash > 0)
        {
            var parentKey = key with { RemotePath = key.RemotePath[..slash] };
            if (!toRemove.Contains(parentKey) && dirStructure.ContainsKey(parentKey))
                return parentKey;

            slash = key.RemotePath.LastIndexOf('\\', slash - 1);
        }

        return null;
    }

    private sealed class AlbumFolderBuilder
    {
        public string Username { get; }
        public string FolderPath { get; }
        public List<AlbumFolderFile> Files { get; }
        public int FirstRank { get; private set; }
        public int MusicCount { get; private set; }
        public AlbumAudioQualityCoverage QualityCoverage { get; private set; }
        public ResultSorter.SortEntry? AggregateSortEntry { get; private set; }

        public AlbumFolderBuilder(
            string username,
            string folderPath,
            AlbumFolderFile file,
            int firstRank,
            ResultSorter.SortEntry? aggregateSortEntry,
            int initialFileCapacity)
        {
            Username = username;
            FolderPath = folderPath;
            Files = new List<AlbumFolderFile>(Math.Max(1, initialFileCapacity)) { file };
            FirstRank = firstRank;
            MusicCount = file.IsMusic ? 1 : 0;
            QualityCoverage = AlbumAudioQualityCoverage.Inactive(MusicCount);
            AggregateSortEntry = aggregateSortEntry;
        }

        public void AddRank(int rank)
            => FirstRank = Math.Min(FirstRank, rank);

        public void RefreshQualityCoverage(FileConditions conditions, ActiveAlbumTrackConditions activeQuality)
            => QualityCoverage = AlbumQualityPolicy.Evaluate(
                Files.Where(file => file.IsMusic).Select(file => new ConditionFile(
                    file.Input.Filename,
                    file.Input.Length,
                    file.Input.BitRate,
                    file.Input.SampleRate,
                    file.Input.BitDepth)),
                conditions,
                activeQuality);

        public void RefreshInactiveQualityCoverage()
            => QualityCoverage = AlbumAudioQualityCoverage.Inactive(MusicCount);

        public void AddAggregateSortEntry(ResultSorter.SortEntry? entry)
        {
            if (!entry.HasValue)
                return;

            if (!AggregateSortEntry.HasValue
                || ResultSorter.SortEntryComparer.Instance.Compare(entry.Value, AggregateSortEntry.Value) < 0)
                AggregateSortEntry = entry;
        }

        public void Add(AlbumFolderFile file)
        {
            Files.Add(file);
            if (file.IsMusic)
                MusicCount++;
        }

        public void AddRange(AlbumFolderBuilder other)
        {
            Files.AddRange(other.Files);
            AddRank(other.FirstRank);
            AddAggregateSortEntry(other.AggregateSortEntry);
            MusicCount += other.MusicCount;
        }
    }

    private sealed class AlbumFolderBuilderComparer : IComparer<AlbumFolderBuilder>
    {
        public static AlbumFolderBuilderComparer WithQualityCoverage { get; } = new(compareQualityCoverage: true);
        public static AlbumFolderBuilderComparer WithoutQualityCoverage { get; } = new(compareQualityCoverage: false);

        private readonly bool compareQualityCoverage;

        private AlbumFolderBuilderComparer(bool compareQualityCoverage)
        {
            this.compareQualityCoverage = compareQualityCoverage;
        }

        public int Compare(AlbumFolderBuilder? x, AlbumFolderBuilder? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x == null)
                return 1;
            if (y == null)
                return -1;

            if (x.AggregateSortEntry.HasValue && y.AggregateSortEntry.HasValue)
            {
                int beforeQualityComparison = ResultSorter.AlbumBeforeQualitySortEntryComparer.Instance.Compare(
                    x.AggregateSortEntry.Value,
                    y.AggregateSortEntry.Value);
                if (beforeQualityComparison != 0)
                    return beforeQualityComparison;
            }
            else if (x.AggregateSortEntry.HasValue)
            {
                return -1;
            }
            else if (y.AggregateSortEntry.HasValue)
            {
                return 1;
            }

            if (compareQualityCoverage)
            {
                // Match the file-sort key order, but lift each audio-quality key to
                // folder-level coverage: identity/length first, then format, bitrate,
                // sample rate, bit depth. This keeps high-quality unrelated folders
                // from outranking the album we asked for, while still preferring e.g.
                // 9/10 FLAC folders over 1/10 FLAC folders.
                int comparison = CompareCoverageBuckets(x.QualityCoverage.Format, y.QualityCoverage.Format);
                if (comparison != 0)
                    return comparison;
                comparison = CompareCoverageBuckets(x.QualityCoverage.Bitrate, y.QualityCoverage.Bitrate);
                if (comparison != 0)
                    return comparison;
                comparison = CompareCoverageBuckets(x.QualityCoverage.SampleRate, y.QualityCoverage.SampleRate);
                if (comparison != 0)
                    return comparison;
                comparison = CompareCoverageBuckets(x.QualityCoverage.BitDepth, y.QualityCoverage.BitDepth);
                if (comparison != 0)
                    return comparison;
            }

            if (x.AggregateSortEntry.HasValue && y.AggregateSortEntry.HasValue)
            {
                int aggregateComparison = ResultSorter.SortEntryComparer.Instance.Compare(
                    x.AggregateSortEntry.Value,
                    y.AggregateSortEntry.Value);
                if (aggregateComparison != 0)
                    return aggregateComparison;
            }
            else if (x.AggregateSortEntry.HasValue)
            {
                return -1;
            }
            else if (y.AggregateSortEntry.HasValue)
            {
                return 1;
            }

            int rankComparison = x.FirstRank.CompareTo(y.FirstRank);
            if (rankComparison != 0)
                return rankComparison;

            int usernameComparison = string.Compare(x.Username, y.Username, StringComparison.Ordinal);
            return usernameComparison != 0
                ? usernameComparison
                : string.Compare(x.FolderPath, y.FolderPath, StringComparison.Ordinal);
        }

        private static int CompareCoverageBuckets(AlbumQualityCoverageBucket x, AlbumQualityCoverageBucket y)
            => y.Bucket.CompareTo(x.Bucket);
    }

    private readonly record struct AlbumFolderFile(SearchProjectionInput Input, bool IsMusic);

    private sealed class AlbumFolderFileComparer : IComparer<AlbumFolderFile>
    {
        public static readonly AlbumFolderFileComparer Instance = new();

        private AlbumFolderFileComparer()
        {
        }

        public int Compare(AlbumFolderFile x, AlbumFolderFile y)
        {
            int comparison = y.IsMusic.CompareTo(x.IsMusic);
            return comparison != 0
                ? comparison
                : string.Compare(x.Input.Filename, y.Input.Filename, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"^(?i)(dis[c|k]|cd)\s*\d{1,2}$")]
    private static partial Regex DiscPatternRegex();
}

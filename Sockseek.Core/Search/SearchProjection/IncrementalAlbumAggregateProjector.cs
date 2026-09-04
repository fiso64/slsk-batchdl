using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public sealed record SearchViewProjectedAggregateAlbumGroup(
    int Index,
    PeerDirectoryIdentity StableIdentity,
    AlbumQuery Query,
    int ShareCount,
    long SelectableOptionCount,
    AlbumFolder Representative,
    IReadOnlyList<AlbumFolder> Options);

public sealed class IncrementalAlbumAggregateProjector
{
    private readonly AlbumQuery query;
    private readonly SearchSettings search;
    private readonly int maxDiff;
    private readonly Dictionary<int, Dictionary<int, List<AlbumAggregateBucket>>> byTrackCountAndFirstLength = [];
    private readonly Dictionary<AlbumFolder, SongQuery?> representativeQueries = [];
    private readonly Dictionary<PeerPathKey, int> folderOrder = [];
    private readonly Dictionary<PeerPathKey, AlbumAggregateBucket> bucketByFolder = [];
    private readonly AlbumFolderAggregateComparer folderComparer;
    private readonly List<AlbumAggregateBucket> buckets = [];
    private readonly HashSet<PeerPathKey> seenFolders = [];
    private List<AlbumFolder> orderedFolders = [];

    public IncrementalAlbumAggregateProjector(AlbumQuery query, SearchSettings search)
    {
        this.query = query;
        this.search = search;
        maxDiff = search.AggregateLengthTol;
        folderComparer = new AlbumFolderAggregateComparer(query, search, folderOrder);
    }

    public int Count => seenFolders.Count;

    public void Clear()
    {
        byTrackCountAndFirstLength.Clear();
        representativeQueries.Clear();
        folderOrder.Clear();
        bucketByFolder.Clear();
        folderComparer.ClearCache();
        buckets.Clear();
        seenFolders.Clear();
        orderedFolders.Clear();
    }

    public int AddRange(IEnumerable<AlbumFolder> albums)
    {
        int added = 0;
        foreach (var folder in albums)
        {
            var key = new PeerPathKey(folder.Username, folder.FolderPath);
            if (!seenFolders.Add(key))
                continue;

            folderOrder[key] = orderedFolders.Count;
            orderedFolders.Add(folder);
            Add(folder);
            added++;
        }

        return added;
    }

    public int ApplyChanges(AlbumFolderProjectionChanges changes)
    {
        if (!changes.HasChanges)
            return 0;

        int boundary = FindStablePrefixBoundary(changes);
        for (int index = orderedFolders.Count - 1; index >= boundary; index--)
            Remove(orderedFolders[index]);
        RemoveEmptySuffixBuckets();

        orderedFolders = changes.Folders.Take(boundary).ToList();
        UpdateFolderOrder(changes.Folders);
        folderComparer.ClearCache();
        int processed = 0;
        for (int index = boundary; index < changes.Folders.Count; index++)
        {
            AlbumFolder folder = changes.Folders[index];
            PeerPathKey key = FolderKey(folder);
            if (!seenFolders.Add(key))
                continue;
            orderedFolders.Add(folder);
            Add(folder);
            processed++;
        }
        return processed;
    }

    public void Reset(IEnumerable<AlbumFolder> albums)
    {
        var albumList = albums as IReadOnlyList<AlbumFolder> ?? albums.ToList();
        Clear();
        UpdateFolderOrder(albumList);
        foreach (AlbumFolder folder in albumList)
        {
            PeerPathKey key = FolderKey(folder);
            if (!seenFolders.Add(key))
                continue;
            orderedFolders.Add(folder);
            Add(folder);
        }
    }

    internal void ResetBatch(IEnumerable<AlbumFolder> albums)
        => Reset(albums);

    public List<AlbumJob> Snapshot()
        => buckets
            .Where(x => x.UserCount >= search.MinSharesAggregate)
            .OrderByDescending(x => x.UserCount)
            .ThenBy(x => x.Index)
            .Select(x => SearchResultProjector.CreateAggregateAlbumJob(query, x.Versions.ToList()))
            .ToList();

    public List<SearchViewProjectedAggregateAlbumGroup> SnapshotForSearchView()
        => buckets
            .Where(bucket => bucket.UserCount >= search.MinSharesAggregate)
            .OrderByDescending(bucket => bucket.UserCount)
            .ThenBy(bucket => bucket.Index)
            .Select(bucket =>
            {
                IReadOnlyList<AlbumFolder> options = bucket.Versions;
                AlbumFolder representative = options[0];
                string? itemName = string.IsNullOrWhiteSpace(representative.FolderPath)
                    ? null
                    : Utils.GetBaseNameSlsk(representative.FolderPath);
                AlbumQuery groupQuery = itemName == null
                    ? new AlbumQuery(query)
                    : new AlbumQuery(query) { Album = itemName };
                return new SearchViewProjectedAggregateAlbumGroup(
                    bucket.Index,
                    bucket.RepresentativeFolder.DirectoryIdentity,
                    groupQuery,
                    bucket.UserCount,
                    options.LongCount(folder => folder.Files.Any(file =>
                        file.Candidate.Visibility == SearchResultVisibility.Public)),
                    representative,
                    options);
            })
            .ToList();

    private void Add(AlbumFolder folder)
    {
        var sortedLengths = GetSearchSortedAudioLengths(folder);
        if (sortedLengths.Length == 0)
            return;

        if (!byTrackCountAndFirstLength.TryGetValue(sortedLengths.Length, out var byFirstLength))
        {
            byFirstLength = [];
            byTrackCountAndFirstLength.Add(sortedLengths.Length, byFirstLength);
        }

        AlbumAggregateBucket? matchingBucket = null;
        int firstLengthBand = LengthBand(sortedLengths[0]);
        for (int bandOffset = -1; bandOffset <= 1; bandOffset++)
        {
            if (!byFirstLength.TryGetValue(firstLengthBand + bandOffset, out var candidates))
                continue;

            for (int i = 0; i < candidates.Count; i++)
            {
                var bucket = candidates[i];
                if (!LengthsAreSimilar(sortedLengths, bucket.Lengths))
                    continue;

                if (sortedLengths.Length == 1 && !SingleTrackAlbumsMatch(bucket.RepresentativeFolder, folder))
                    continue;

                if (matchingBucket == null || bucket.Index < matchingBucket.Index)
                    matchingBucket = bucket;
            }
        }

        if (matchingBucket != null)
        {
            matchingBucket.AddVersion(folder, CompareFolders);
            bucketByFolder[FolderKey(folder)] = matchingBucket;
            return;
        }

        var newBucket = new AlbumAggregateBucket(buckets.Count, sortedLengths, folder);
        buckets.Add(newBucket);
        if (!byFirstLength.TryGetValue(firstLengthBand, out var byLength))
        {
            byLength = [];
            byFirstLength.Add(firstLengthBand, byLength);
        }

        byLength.Add(newBucket);
        bucketByFolder[FolderKey(folder)] = newBucket;
    }

    private int FindStablePrefixBoundary(AlbumFolderProjectionChanges changes)
    {
        var changed = changes.Added
            .Concat(changes.Updated)
            .Concat(changes.Removed)
            .Select(FolderKey)
            .ToHashSet();
        int common = Math.Min(orderedFolders.Count, changes.Folders.Count);
        for (int index = 0; index < common; index++)
        {
            PeerPathKey oldKey = FolderKey(orderedFolders[index]);
            PeerPathKey newKey = FolderKey(changes.Folders[index]);
            if (oldKey != newKey || changed.Contains(oldKey))
                return index;
        }
        return common;
    }

    private void Remove(AlbumFolder folder)
    {
        PeerPathKey key = FolderKey(folder);
        seenFolders.Remove(key);
        representativeQueries.Remove(folder);
        if (!bucketByFolder.Remove(key, out AlbumAggregateBucket? bucket))
            return;
        AlbumFolder? removed = bucket.RemoveVersion(key);
        if (removed != null && !ReferenceEquals(removed, folder))
            representativeQueries.Remove(removed);
    }

    private void RemoveEmptySuffixBuckets()
    {
        while (buckets.Count > 0 && buckets[^1].Versions.Count == 0)
        {
            AlbumAggregateBucket bucket = buckets[^1];
            buckets.RemoveAt(buckets.Count - 1);
            int firstLengthBand = LengthBand(bucket.Lengths[0]);
            Dictionary<int, List<AlbumAggregateBucket>> byFirstLength =
                byTrackCountAndFirstLength[bucket.Lengths.Length];
            List<AlbumAggregateBucket> band = byFirstLength[firstLengthBand];
            band.Remove(bucket);
            if (band.Count == 0)
                byFirstLength.Remove(firstLengthBand);
            if (byFirstLength.Count == 0)
                byTrackCountAndFirstLength.Remove(bucket.Lengths.Length);
        }
    }

    private void UpdateFolderOrder(IEnumerable<AlbumFolder> folders)
    {
        folderOrder.Clear();
        int index = 0;
        foreach (var folder in folders)
            folderOrder[FolderKey(folder)] = index++;
    }

    private int CompareFolders(AlbumFolder x, AlbumFolder y)
        => folderComparer.Compare(x, y);

    private static PeerPathKey FolderKey(AlbumFolder folder)
        => new(folder.Username, folder.FolderPath);

    private bool LengthsAreSimilar(int[] s1, int[] s2)
    {
        for (int i = 0; i < s1.Length; i++)
            if (Math.Abs(s1[i] - s2[i]) > maxDiff)
                return false;
        return true;
    }

    private int LengthBand(int length)
    {
        int bandSize = Math.Max(1, maxDiff + 1);
        return (int)Math.Floor(length / (double)bandSize);
    }

    private static int[] GetSearchSortedAudioLengths(AlbumFolder folder)
        => folder.HasSearchMetadata
            ? folder.SearchSortedAudioLengths
            : folder.Files
                .Where(f => !f.IsNotAudio)
                .Select(f => f.Candidate.Length ?? -1)
                .OrderBy(x => x)
                .ToArray();

    private bool SingleTrackAlbumsMatch(AlbumFolder a, AlbumFolder b)
    {
        SongQuery? q1 = RepresentativeAudioQuery(a);
        SongQuery? q2 = RepresentativeAudioQuery(b);
        if (q1 == null || q2 == null)
            return true;

        return (q2.Artist.ContainsIgnoreCase(q1.Artist) || q1.Artist.ContainsIgnoreCase(q2.Artist))
            && (q2.Title.ContainsIgnoreCase(q1.Title) || q1.Title.ContainsIgnoreCase(q2.Title));
    }

    private SongQuery? RepresentativeAudioQuery(AlbumFolder folder)
    {
        if (representativeQueries.TryGetValue(folder, out var query))
            return query;

        string? filename = RepresentativeAudioFilename(folder);
        query = filename == null
            ? null
            : Searcher.InferSongQuery(filename, new SongQuery());
        representativeQueries.Add(folder, query);
        return query;
    }

    private static string? RepresentativeAudioFilename(AlbumFolder folder)
        => folder.HasSearchMetadata
            ? folder.SearchRepresentativeAudioFilename
            : folder.Files.FirstOrDefault(f => !f.IsNotAudio)?.Filename;

    private sealed class AlbumAggregateBucket
    {
        public int Index { get; }
        public int[] Lengths { get; }
        public List<AlbumFolder> Versions { get; }
        public AlbumFolder RepresentativeFolder { get; }
        private readonly Dictionary<string, int> userCounts = new(StringComparer.Ordinal);
        public int UserCount => userCounts.Count;

        public AlbumAggregateBucket(int index, int[] lengths, AlbumFolder folder)
        {
            Index = index;
            Lengths = lengths;
            Versions = [folder];
            RepresentativeFolder = folder;
            userCounts.Add(folder.Username, 1);
        }

        public void AddVersion(AlbumFolder folder, Comparison<AlbumFolder> comparison)
        {
            int index = Versions.BinarySearch(folder, Comparer<AlbumFolder>.Create(comparison));
            if (index < 0)
                index = ~index;
            Versions.Insert(index, folder);
            userCounts[folder.Username] = userCounts.TryGetValue(
                folder.Username,
                out int count)
                ? checked(count + 1)
                : 1;
        }

        public AlbumFolder? RemoveVersion(PeerPathKey key)
        {
            int index = Versions.FindIndex(folder => FolderKey(folder) == key);
            if (index < 0)
                return null;
            AlbumFolder removed = Versions[index];
            Versions.RemoveAt(index);
            int count = userCounts[removed.Username] - 1;
            if (count == 0)
                userCounts.Remove(removed.Username);
            else
                userCounts[removed.Username] = count;
            return removed;
        }
    }
}

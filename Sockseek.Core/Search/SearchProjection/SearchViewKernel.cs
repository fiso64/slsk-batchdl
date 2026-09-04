using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public sealed record SearchViewCounters(
    long PublicFileCount,
    long LockedFileCount,
    long PublicBytes,
    long LockedBytes,
    int ObservedPeerCount,
    long ProjectedFileCount,
    long ProjectedPublicFileCount,
    long ProjectedLockedFileCount,
    long PreferredFileCount,
    long OtherFileCount,
    long TopLevelItemCount = 0,
    long SelectableOptionCount = 0);

[JsonConverter(typeof(JsonStringEnumConverter<SearchViewProjectionKind>))]
public enum SearchViewProjectionKind
{
    Files,
    GenericDirectories,
    AlbumDirectories,
    AggregateTracks,
    AggregateAlbums,
}

public sealed record SearchViewProjectionDefinition(
    SearchViewProjectionKind Kind,
    SongQuery? SongQuery = null,
    AlbumQuery? AlbumQuery = null,
    bool IncludeFullResults = false)
{
    public void Validate()
    {
        if (Kind is SearchViewProjectionKind.Files or SearchViewProjectionKind.GenericDirectories
            && SongQuery == null)
            throw new ArgumentException("A file-based Search View requires a song query.");
        if (Kind is SearchViewProjectionKind.AlbumDirectories or SearchViewProjectionKind.AggregateAlbums
            && AlbumQuery == null)
            throw new ArgumentException("An album Search View requires an album query.");
        if (Kind == SearchViewProjectionKind.AggregateTracks && SongQuery == null)
            throw new ArgumentException("An aggregate-track Search View requires a song query.");
    }
}

public sealed record SearchViewProjectedDirectory(
    PeerDirectoryIdentity Directory,
    long PublicMatchingFileCount,
    long LockedMatchingFileCount,
    long PublicMatchingBytes,
    long LockedMatchingBytes,
    ProjectedFileCandidate BestChild,
    IReadOnlyList<ProjectedFileCandidate> NewChildren,
    bool IsFullyRetrieved = false,
    long? RetrievedFileCount = null,
    long? RetrievedBytes = null);

public sealed record SearchViewKernelUpdate(
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    SearchViewCounters Counters,
    IReadOnlyList<ProjectedFileCandidate> ChangedFiles,
    IReadOnlyList<SearchProjectionInput>? ObservedInputs = null,
    IReadOnlyList<SearchViewProjectedDirectory>? ChangedDirectories = null,
    IReadOnlyList<PeerDirectoryIdentity>? RemovedDirectories = null,
    IReadOnlyList<SearchViewProjectedAggregateTrackGroup>? ChangedAggregateTrackGroups = null,
    IReadOnlyList<SearchViewProjectedAggregateAlbumGroup>? ChangedAggregateAlbumGroups = null,
    IReadOnlyList<PeerDirectoryIdentity>? RemovedAggregateAlbumGroups = null);

public sealed record SearchViewKernelSeed(
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    SearchViewCounters Counters);

public sealed record SearchViewKernelSnapshot(
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    SearchViewCounters Counters,
    IReadOnlyList<ProjectedFileCandidate> Files,
    IReadOnlyList<SearchViewProjectedDirectory>? Directories = null,
    IReadOnlyList<SearchViewProjectedAggregateTrackGroup>? AggregateTrackGroups = null,
    IReadOnlyList<SearchViewProjectedAggregateAlbumGroup>? AggregateAlbumGroups = null);

/// <summary>
/// Storage-neutral incremental file-view owner. Live batches and retained
/// history both enter through <see cref="Apply"/>; completion only publishes
/// completeness and never switches to a second projection implementation.
/// </summary>
public sealed class SearchViewKernel
{
    private readonly object gate = new();
    private readonly IncrementalResultSorter? sorter;
    private readonly IncrementalAlbumFolderProjector? albumFolderProjector;
    private readonly IncrementalAggregateTrackProjector? aggregateTrackProjector;
    private readonly IncrementalAlbumAggregateProjector? aggregateAlbumProjector;
    private readonly SearchViewProjectionKind projectionKind;
    private readonly Dictionary<PeerPathKey, DirectoryState> directories = [];
    private readonly Dictionary<PeerPathKey, SearchViewProjectedDirectory> albumDirectories = [];
    private readonly Dictionary<PeerPathKey, HashSet<(
        PeerPathKey Path,
        SearchResultVisibility Visibility)>> albumDirectoryChildren = [];
    private readonly Dictionary<
        (PeerPathKey Path, SearchResultVisibility Visibility),
        ProjectedFileCandidate> projectedFiles = [];
    private readonly Dictionary<PeerPathKey, string> aggregateAlbumSignatures = [];
    private readonly bool trackPeerIdentities;
    private readonly HashSet<string> observedPeers = new(StringComparer.Ordinal);
    private long publicFileCount;
    private long lockedFileCount;
    private long publicBytes;
    private long lockedBytes;
    private long consumedSequence;
    private long projectedFileCount;
    private long projectedPublicFileCount;
    private long projectedLockedFileCount;
    private long preferredFileCount;
    private long otherFileCount;
    private long topLevelItemCount;
    private long selectableOptionCount;
    private int observedPeerCount;
    private int sourceRevision;
    private bool isComplete;

    public long ConsumedSequence { get { lock (gate) return consumedSequence; } }
    public bool IsComplete { get { lock (gate) return isComplete; } }

    public SearchViewKernel(
        FileSearchProjection projection,
        SearchSettings settings,
        IReadOnlyDictionary<string, int>? reputationSnapshot = null,
        bool retainProjectedRows = true,
        SearchViewKernelSeed? seed = null,
        bool trackPeerIdentities = true)
        : this(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.Files,
                projection?.Query,
                IncludeFullResults: projection?.IncludeFullResults ?? false),
            settings,
            reputationSnapshot,
            retainProjectedRows,
            seed,
            trackPeerIdentities)
    {
        ArgumentNullException.ThrowIfNull(projection);
    }

    public SearchViewKernel(
        SearchViewProjectionDefinition projection,
        SearchSettings settings,
        IReadOnlyDictionary<string, int>? reputationSnapshot = null,
        bool retainProjectedRows = true,
        SearchViewKernelSeed? seed = null,
        bool trackPeerIdentities = true)
    {
        ArgumentNullException.ThrowIfNull(projection);
        projection.Validate();
        ArgumentNullException.ThrowIfNull(settings);
        if (seed != null && trackPeerIdentities && seed.Counters.ObservedPeerCount != 0)
            throw new ArgumentException(
                "A seeded kernel must use disk-backed peer identity tracking.",
                nameof(trackPeerIdentities));
        this.trackPeerIdentities = trackPeerIdentities;
        projectionKind = projection.Kind;
        if (projectionKind is not (SearchViewProjectionKind.Files
            or SearchViewProjectionKind.GenericDirectories
            or SearchViewProjectionKind.AlbumDirectories
            or SearchViewProjectionKind.AggregateTracks
            or SearchViewProjectionKind.AggregateAlbums))
            throw new NotSupportedException($"Search View projection '{projectionKind}' is not implemented yet.");
        var reputation = reputationSnapshot == null
            ? new ConcurrentDictionary<string, int>()
            : new ConcurrentDictionary<string, int>(
                reputationSnapshot,
                StringComparer.Ordinal);
        if (projectionKind is SearchViewProjectionKind.AlbumDirectories
            or SearchViewProjectionKind.AggregateAlbums)
        {
            albumFolderProjector = new IncrementalAlbumFolderProjector(
                projection.AlbumQuery!,
                SettingsCloner.Clone(settings),
                reputation,
                ignoreStringSortConditions:
                    projectionKind == SearchViewProjectionKind.AggregateAlbums,
                sortMode: projectionKind == SearchViewProjectionKind.AggregateAlbums
                    ? FolderSortMode.DeterministicUnranked
                    : FolderSortMode.AlbumRanked);
            if (projectionKind == SearchViewProjectionKind.AggregateAlbums)
            {
                aggregateAlbumProjector = new IncrementalAlbumAggregateProjector(
                    projection.AlbumQuery!,
                    SettingsCloner.Clone(settings));
            }
        }
        else if (projectionKind == SearchViewProjectionKind.AggregateTracks)
        {
            aggregateTrackProjector = new IncrementalAggregateTrackProjector(
                projection.SongQuery!,
                SettingsCloner.Clone(settings),
                reputation);
        }
        else
        {
            sorter = new IncrementalResultSorter(
                projection.SongQuery!,
                SettingsCloner.Clone(settings),
                reputation,
                useInfer: false,
                requireFileSatisfies: !projection.IncludeFullResults,
                retainProjectedRows: projectionKind == SearchViewProjectionKind.Files
                    && retainProjectedRows,
                // Durable raw admission already owns exact duplicate rejection.
                // Avoid retaining another result-sized identity set in daemon views.
                deduplicateInputs: trackPeerIdentities);
        }
        if (seed != null)
        {
            sourceRevision = seed.SourceRevision;
            consumedSequence = seed.ConsumedSequence;
            isComplete = seed.IsComplete;
            publicFileCount = seed.Counters.PublicFileCount;
            lockedFileCount = seed.Counters.LockedFileCount;
            publicBytes = seed.Counters.PublicBytes;
            lockedBytes = seed.Counters.LockedBytes;
            observedPeerCount = seed.Counters.ObservedPeerCount;
            projectedFileCount = seed.Counters.ProjectedFileCount;
            projectedPublicFileCount = seed.Counters.ProjectedPublicFileCount;
            projectedLockedFileCount = seed.Counters.ProjectedLockedFileCount;
            preferredFileCount = seed.Counters.PreferredFileCount;
            otherFileCount = seed.Counters.OtherFileCount;
            topLevelItemCount = seed.Counters.TopLevelItemCount;
            selectableOptionCount = seed.Counters.SelectableOptionCount;
        }
    }

    public SearchViewKernelUpdate Apply(
        IEnumerable<SearchProjectionInput> inputs,
        int sourceRevision,
        bool isComplete)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        lock (gate)
        {
            if (sourceRevision < this.sourceRevision)
                throw new ArgumentOutOfRangeException(
                    nameof(sourceRevision),
                    "A search view source revision cannot regress.");
            if (this.isComplete && !isComplete)
                throw new InvalidOperationException("A completed search view cannot become live again.");

            var fresh = new List<SearchProjectionInput>();
            long nextSequence = consumedSequence;
            foreach (SearchProjectionInput input in inputs)
            {
                if (input.Sequence <= consumedSequence)
                    continue;
                if (input.Sequence <= nextSequence)
                    throw new InvalidDataException(
                        "Fresh search-view inputs must be strictly ordered by sequence.");
                nextSequence = input.Sequence;
                fresh.Add(input);
            }
            foreach (SearchProjectionInput input in fresh)
            {
                if (trackPeerIdentities && observedPeers.Add(input.Username))
                    observedPeerCount = observedPeers.Count;
                long bytes = Math.Max(0, input.Size);
                if (input.Visibility == SearchResultVisibility.Locked)
                {
                    lockedFileCount++;
                    lockedBytes = checked(lockedBytes + bytes);
                }
                else
                {
                    publicFileCount++;
                    publicBytes = checked(publicBytes + bytes);
                }
            }
            consumedSequence = nextSequence;

            IReadOnlyList<ProjectedFileCandidate> changed;
            IReadOnlyList<SearchViewProjectedDirectory>? changedDirectories = null;
            IReadOnlyList<PeerDirectoryIdentity>? removedDirectories = null;
            IReadOnlyList<SearchViewProjectedAggregateTrackGroup>? changedTrackGroups = null;
            IReadOnlyList<SearchViewProjectedAggregateAlbumGroup>? changedAlbumGroups = null;
            IReadOnlyList<PeerDirectoryIdentity>? removedAlbumGroups = null;
            if (projectionKind == SearchViewProjectionKind.AggregateAlbums)
            {
                AlbumFolderSearchViewChanges albumChanges = albumFolderProjector!
                    .AddRangeForSearchView(fresh);
                changed = albumChanges.AdmittedFiles;
                foreach (ProjectedFileCandidate file in changed)
                {
                    projectedFiles[(
                        new PeerPathKey(file.Input.Username, file.Input.Filename),
                        file.Input.Visibility)] = file;
                }
                (changedDirectories, removedDirectories) = ApplyAlbumDirectories(
                    albumChanges.Folders);
                aggregateAlbumProjector!.ApplyChanges(albumChanges.Folders);
                IReadOnlyList<SearchViewProjectedAggregateAlbumGroup> groups =
                    aggregateAlbumProjector.SnapshotForSearchView();
                (changedAlbumGroups, removedAlbumGroups) = DiffAggregateAlbumGroups(groups);
                RecalculateAlbumCounters(albumChanges.Folders.Folders);
                topLevelItemCount = groups.Count;
                selectableOptionCount = groups.Sum(group => group.SelectableOptionCount);
            }
            else if (projectionKind == SearchViewProjectionKind.AggregateTracks)
            {
                AggregateTrackSearchViewChanges aggregateChanges = aggregateTrackProjector!
                    .AddRangeForSearchView(fresh);
                changed = aggregateChanges.AdmittedFiles;
                changedTrackGroups = aggregateChanges.ChangedGroups;
                RecalculateAggregateTrackCounters(
                    aggregateTrackProjector.SnapshotForSearchView());
            }
            else if (projectionKind == SearchViewProjectionKind.AlbumDirectories)
            {
                AlbumFolderSearchViewChanges albumChanges = albumFolderProjector!
                    .AddRangeForSearchView(fresh);
                changed = albumChanges.AdmittedFiles;
                foreach (ProjectedFileCandidate file in changed)
                {
                    projectedFiles[(
                        new PeerPathKey(file.Input.Username, file.Input.Filename),
                        file.Input.Visibility)] = file;
                }
                (changedDirectories, removedDirectories) = ApplyAlbumDirectories(
                    albumChanges.Folders);
                RecalculateAlbumCounters(albumChanges.Folders.Folders);
            }
            else
            {
                changed = sorter!.AddRangeAndGetProjected(fresh);
                foreach (ProjectedFileCandidate file in changed)
                {
                    projectedFileCount++;
                    if (file.Input.Visibility == SearchResultVisibility.Locked)
                        projectedLockedFileCount++;
                    else
                        projectedPublicFileCount++;
                    if (file.ConditionFacts.PreferenceTier == SearchPreferenceTier.Preferred)
                        preferredFileCount++;
                    else
                        otherFileCount++;
                }
                if (projectionKind == SearchViewProjectionKind.GenericDirectories)
                {
                    changedDirectories = ApplyDirectories(changed);
                    topLevelItemCount = directories.Count;
                    selectableOptionCount = projectedPublicFileCount;
                }
                else
                {
                    topLevelItemCount = projectedFileCount;
                    selectableOptionCount = projectedPublicFileCount;
                }
            }
            this.sourceRevision = sourceRevision;
            this.isComplete = isComplete;
            return new SearchViewKernelUpdate(
                sourceRevision,
                consumedSequence,
                isComplete,
                Counters(),
                changed,
                fresh,
                changedDirectories,
                removedDirectories,
                changedTrackGroups,
                changedAlbumGroups,
                removedAlbumGroups);
        }
    }

    public SearchViewKernelSnapshot Snapshot()
    {
        lock (gate)
        {
            IReadOnlyList<ProjectedFileCandidate> files = projectionKind == SearchViewProjectionKind.Files
                ? sorter!.SnapshotProjectedFiles()
                : [];
            return new(
                sourceRevision,
                consumedSequence,
                isComplete,
                Counters(),
                files,
                projectionKind switch
                {
                    SearchViewProjectionKind.GenericDirectories
                        => directories.Values.Select(state => state.Snapshot([])).ToArray(),
                    SearchViewProjectionKind.AlbumDirectories
                        => albumDirectories.Values.ToArray(),
                    _ => null,
                },
                projectionKind == SearchViewProjectionKind.AggregateTracks
                    ? aggregateTrackProjector!.SnapshotForSearchView()
                    : null,
                projectionKind == SearchViewProjectionKind.AggregateAlbums
                    ? aggregateAlbumProjector!.SnapshotForSearchView()
                    : null);
        }
    }

    private SearchViewCounters Counters()
        => new(
            publicFileCount,
            lockedFileCount,
            publicBytes,
            lockedBytes,
            observedPeerCount,
            projectedFileCount,
            projectedPublicFileCount,
            projectedLockedFileCount,
            preferredFileCount,
            otherFileCount,
            topLevelItemCount,
            selectableOptionCount);

    private IReadOnlyList<SearchViewProjectedDirectory> ApplyDirectories(
        IReadOnlyList<ProjectedFileCandidate> changed)
    {
        var touched = new Dictionary<PeerPathKey, List<ProjectedFileCandidate>>();
        foreach (ProjectedFileCandidate file in changed)
        {
            string directoryPath = RemoteContainingDirectory(file.Input.Filename);
            if (directoryPath.Length == 0)
                continue;
            var key = new PeerPathKey(file.Input.Username, directoryPath);
            if (!directories.TryGetValue(key, out DirectoryState? state))
            {
                state = new DirectoryState(new PeerDirectoryIdentity(
                    file.Input.Username,
                    directoryPath));
                directories.Add(key, state);
            }
            state.Add(file);
            if (!touched.TryGetValue(key, out List<ProjectedFileCandidate>? children))
            {
                children = [];
                touched.Add(key, children);
            }
            children.Add(file);
        }
        return touched.Select(pair => directories[pair.Key].Snapshot(pair.Value)).ToArray();
    }

    private (
        IReadOnlyList<SearchViewProjectedDirectory> Changed,
        IReadOnlyList<PeerDirectoryIdentity> Removed) ApplyAlbumDirectories(
        AlbumFolderProjectionChanges changes)
    {
        var changed = new List<SearchViewProjectedDirectory>(
            changes.Added.Count + changes.Updated.Count);
        foreach (AlbumFolder folder in changes.Added.Concat(changes.Updated))
        {
            var directoryKey = new PeerPathKey(folder.Username, folder.FolderPath);
            var currentKeys = new HashSet<(
                PeerPathKey Path,
                SearchResultVisibility Visibility)>();
            var currentFiles = new List<ProjectedFileCandidate>(folder.Files.Count);
            foreach (AlbumFile file in folder.Files)
            {
                var fileKey = (
                    new PeerPathKey(file.Candidate.Username, file.Candidate.Filename),
                    file.Candidate.Visibility);
                if (!projectedFiles.TryGetValue(fileKey, out ProjectedFileCandidate? projected))
                    throw new InvalidDataException(
                        "An album projection lost its one-pass file evidence.");
                currentKeys.Add(fileKey);
                currentFiles.Add(projected);
            }
            albumDirectoryChildren.TryGetValue(directoryKey, out var previousKeys);
            IReadOnlyList<ProjectedFileCandidate> newChildren = previousKeys == null
                ? currentFiles
                : currentFiles.Where(file => !previousKeys.Contains((
                    new PeerPathKey(file.Input.Username, file.Input.Filename),
                    file.Input.Visibility))).ToArray();
            ResultSorter.SortEntry bestEntry = folder.SearchAggregateSortEntry
                ?? throw new InvalidDataException(
                    "An album directory has no retained best-child evidence.");
            var bestKey = (
                new PeerPathKey(bestEntry.Input.Username, bestEntry.Input.Filename),
                bestEntry.Input.Visibility);
            if (!projectedFiles.TryGetValue(bestKey, out ProjectedFileCandidate? best))
                throw new InvalidDataException(
                    "An album directory lost its best-child evidence.");
            long publicCount = 0;
            long lockedCount = 0;
            long publicSize = 0;
            long lockedSize = 0;
            foreach (ProjectedFileCandidate file in currentFiles)
            {
                long size = Math.Max(0, file.Input.Size);
                if (file.Input.Visibility == SearchResultVisibility.Locked)
                {
                    lockedCount++;
                    lockedSize = checked(lockedSize + size);
                }
                else
                {
                    publicCount++;
                    publicSize = checked(publicSize + size);
                }
            }
            var projectedDirectory = new SearchViewProjectedDirectory(
                folder.DirectoryIdentity,
                publicCount,
                lockedCount,
                publicSize,
                lockedSize,
                best,
                newChildren,
                folder.IsFullyRetrieved,
                folder.IsFullyRetrieved ? folder.Files.Count : null,
                folder.IsFullyRetrieved
                    ? currentFiles.Sum(file => Math.Max(0, file.Input.Size))
                    : null);
            albumDirectories[directoryKey] = projectedDirectory;
            albumDirectoryChildren[directoryKey] = currentKeys;
            changed.Add(projectedDirectory);
        }

        var removed = new List<PeerDirectoryIdentity>(changes.Removed.Count);
        foreach (AlbumFolder folder in changes.Removed)
        {
            var key = new PeerPathKey(folder.Username, folder.FolderPath);
            albumDirectories.Remove(key);
            albumDirectoryChildren.Remove(key);
            removed.Add(folder.DirectoryIdentity);
        }
        return (changed, removed);
    }

    private void RecalculateAlbumCounters(IReadOnlyList<AlbumFolder> _)
    {
        projectedFileCount = 0;
        projectedPublicFileCount = 0;
        projectedLockedFileCount = 0;
        preferredFileCount = 0;
        otherFileCount = 0;
        foreach (HashSet<(PeerPathKey Path, SearchResultVisibility Visibility)> children
            in albumDirectoryChildren.Values)
        {
            foreach (var child in children)
            {
                ProjectedFileCandidate file = projectedFiles[child];
                projectedFileCount++;
                if (file.Input.Visibility == SearchResultVisibility.Locked)
                    projectedLockedFileCount++;
                else
                    projectedPublicFileCount++;
                if (file.ConditionFacts.PreferenceTier == SearchPreferenceTier.Preferred)
                    preferredFileCount++;
                else
                    otherFileCount++;
            }
        }
        topLevelItemCount = albumDirectories.Count;
        selectableOptionCount = projectedPublicFileCount;
    }

    private void RecalculateAggregateTrackCounters(
        IReadOnlyList<SearchViewProjectedAggregateTrackGroup> groups)
    {
        projectedFileCount = 0;
        projectedPublicFileCount = 0;
        projectedLockedFileCount = 0;
        preferredFileCount = 0;
        otherFileCount = 0;
        selectableOptionCount = 0;
        foreach (SearchViewProjectedAggregateTrackGroup group in groups)
        {
            selectableOptionCount = checked(
                selectableOptionCount + group.SelectableOptionCount);
            foreach (ProjectedFileCandidate file in group.NewOptions)
            {
                projectedFileCount++;
                if (file.Input.Visibility == SearchResultVisibility.Locked)
                    projectedLockedFileCount++;
                else
                    projectedPublicFileCount++;
                if (file.ConditionFacts.PreferenceTier == SearchPreferenceTier.Preferred)
                    preferredFileCount++;
                else
                    otherFileCount++;
            }
        }
        topLevelItemCount = groups.Count;
    }

    private (
        IReadOnlyList<SearchViewProjectedAggregateAlbumGroup> Changed,
        IReadOnlyList<PeerDirectoryIdentity> Removed) DiffAggregateAlbumGroups(
        IReadOnlyList<SearchViewProjectedAggregateAlbumGroup> groups)
    {
        var changed = new List<SearchViewProjectedAggregateAlbumGroup>();
        var current = new HashSet<PeerPathKey>();
        foreach (SearchViewProjectedAggregateAlbumGroup group in groups)
        {
            var key = new PeerPathKey(
                group.StableIdentity.Username,
                group.StableIdentity.FolderPath);
            current.Add(key);
            string signature = string.Join('\0',
                group.ShareCount,
                group.SelectableOptionCount,
                group.Representative.Username,
                group.Representative.FolderPath,
                group.Query.Artist,
                group.Query.Album,
                group.Query.SearchHint,
                string.Join('\u001f', group.Options.Select(folder =>
                    folder.Username + "\u001e" + folder.FolderPath)));
            if (!aggregateAlbumSignatures.TryGetValue(key, out string? previous)
                || !string.Equals(previous, signature, StringComparison.Ordinal))
            {
                changed.Add(group);
                aggregateAlbumSignatures[key] = signature;
            }
        }
        PeerPathKey[] removedKeys = aggregateAlbumSignatures.Keys
            .Where(key => !current.Contains(key))
            .ToArray();
        var removed = new List<PeerDirectoryIdentity>(removedKeys.Length);
        foreach (PeerPathKey key in removedKeys)
        {
            aggregateAlbumSignatures.Remove(key);
            removed.Add(new PeerDirectoryIdentity(key.Username, key.RemotePath));
        }
        return (changed, removed);
    }

    private static string RemoteContainingDirectory(string filename)
    {
        int separator = Math.Max(
            filename.LastIndexOf('\\'),
            filename.LastIndexOf('/'));
        return separator <= 0 ? "" : filename[..separator];
    }

    private sealed class DirectoryState(PeerDirectoryIdentity directory)
    {
        private ProjectedFileCandidate? best;
        private long publicCount;
        private long lockedCount;
        private long publicBytes;
        private long lockedBytes;

        public void Add(ProjectedFileCandidate file)
        {
            if (file.Input.Visibility == SearchResultVisibility.Locked)
            {
                lockedCount++;
                lockedBytes = checked(lockedBytes + Math.Max(0, file.Input.Size));
            }
            else
            {
                publicCount++;
                publicBytes = checked(publicBytes + Math.Max(0, file.Input.Size));
            }
            if (best == null
                || SearchProjectionSortKeyComparer.Instance.Compare(
                    file.SortKey,
                    best.SortKey) < 0
                || file.SortKey == best.SortKey && file.Input.Sequence < best.Input.Sequence)
                best = file;
        }

        public SearchViewProjectedDirectory Snapshot(
            IReadOnlyList<ProjectedFileCandidate> newChildren)
            => new(
                directory,
                publicCount,
                lockedCount,
                publicBytes,
                lockedBytes,
                best ?? throw new InvalidOperationException("A projected directory has no best child."),
                newChildren);
    }
}

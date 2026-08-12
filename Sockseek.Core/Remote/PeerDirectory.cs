using Sockseek.Core.Services;

namespace Sockseek.Core.Models;

/// <summary>The exact peer and wire folder path used to retrieve a directory.</summary>
public sealed record PeerDirectoryIdentity
{
    public PeerDirectoryIdentity(string username, string folderPath)
    {
        Username = PeerIdentityValidator.ValidateUsername(username);
        FolderPath = PeerIdentityValidator.ValidateRemotePath(folderPath);
    }

    public string Username { get; }
    public string FolderPath { get; }
}

/// <summary>An owned, immutable result of retrieving one peer directory subtree.</summary>
public sealed record PeerDirectorySnapshot
{
    public PeerDirectorySnapshot(
        PeerDirectoryIdentity identity,
        IReadOnlyList<PeerFileTarget> files,
        bool isComplete)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(files);

        var owned = files.ToArray();
        if (owned.Any(file => file is null))
            throw new ArgumentException("Directory files cannot contain null targets.", nameof(files));
        if (owned.Any(file => !StringComparer.Ordinal.Equals(file.Username, identity.Username)))
            throw new ArgumentException("Every directory file must belong to the directory peer.", nameof(files));
        if (owned.Select(file => file.Identity).Distinct().Count() != owned.Length)
            throw new ArgumentException("A directory snapshot cannot contain duplicate exact targets.", nameof(files));

        Identity = identity;
        Files = Array.AsReadOnly(owned);
        IsComplete = isComplete;
    }

    public PeerDirectoryIdentity Identity { get; }
    public IReadOnlyList<PeerFileTarget> Files { get; }
    public bool IsComplete { get; }
}

/// <summary>Album-search facts which are not part of directory identity.</summary>
public sealed class AlbumSearchEvidence
{
    public AlbumSearchEvidence(
        int searchFileCount,
        int searchAudioFileCount,
        IReadOnlyList<int> searchSortedAudioLengths,
        string? searchRepresentativeAudioFilename,
        AlbumAudioQualityCoverage searchAudioQualityCoverage,
        bool hasSearchMetadata)
        : this(
            searchFileCount,
            searchAudioFileCount,
            searchSortedAudioLengths,
            searchRepresentativeAudioFilename,
            searchAudioQualityCoverage,
            hasSearchMetadata,
            aggregateSortEntry: null)
    {
    }

    internal AlbumSearchEvidence(
        int searchFileCount,
        int searchAudioFileCount,
        IReadOnlyList<int> searchSortedAudioLengths,
        string? searchRepresentativeAudioFilename,
        AlbumAudioQualityCoverage searchAudioQualityCoverage,
        bool hasSearchMetadata,
        ResultSorter.SortEntry? aggregateSortEntry)
    {
        if (searchFileCount < 0)
            throw new ArgumentOutOfRangeException(nameof(searchFileCount));
        if (searchAudioFileCount < 0 || searchAudioFileCount > searchFileCount)
            throw new ArgumentOutOfRangeException(nameof(searchAudioFileCount));
        ArgumentNullException.ThrowIfNull(searchSortedAudioLengths);

        SearchFileCount = searchFileCount;
        SearchAudioFileCount = searchAudioFileCount;
        SearchSortedAudioLengths = Array.AsReadOnly(searchSortedAudioLengths.ToArray());
        SearchRepresentativeAudioFilename = searchRepresentativeAudioFilename;
        SearchAudioQualityCoverage = searchAudioQualityCoverage;
        HasSearchMetadata = hasSearchMetadata;
        AggregateSortEntry = aggregateSortEntry;
    }

    public int SearchFileCount { get; }
    public int SearchAudioFileCount { get; }
    public IReadOnlyList<int> SearchSortedAudioLengths { get; }
    public string? SearchRepresentativeAudioFilename { get; }
    public AlbumAudioQualityCoverage SearchAudioQualityCoverage { get; }
    public bool HasSearchMetadata { get; }
    internal ResultSorter.SortEntry? AggregateSortEntry { get; }
}

/// <summary>Associates album query evidence with one exact peer target.</summary>
public sealed class AlbumFileMatch
{
    private readonly Lazy<SongQuery> query;

    public AlbumFileMatch(SongQuery query, FileCandidate candidate)
        : this(() => query, candidate)
    {
    }

    public static AlbumFileMatch WithLazyQuery(Func<SongQuery> queryFactory, FileCandidate candidate)
        => new(queryFactory, candidate);

    private AlbumFileMatch(Func<SongQuery> queryFactory, FileCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(queryFactory);
        ArgumentNullException.ThrowIfNull(candidate);
        query = new Lazy<SongQuery>(() => new SongQuery(queryFactory()));
        Candidate = candidate;
    }

    public SongQuery Query => query.Value;
    public FileCandidate Candidate { get; }
    public PeerFileTarget Target => Candidate.Target;
}

/// <summary>A peer directory composed with the album evidence that selected it.</summary>
public sealed class AlbumDirectoryCandidate
{
    public AlbumDirectoryCandidate(
        PeerDirectorySnapshot directory,
        IReadOnlyList<AlbumFileMatch> matches,
        AlbumSearchEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(evidence);

        var owned = matches.ToArray();
        if (owned.Any(match => match is null))
            throw new ArgumentException("Album matches cannot contain null values.", nameof(matches));
        if (owned.Any(match => !StringComparer.Ordinal.Equals(match.Target.Username, directory.Identity.Username)))
            throw new ArgumentException("Album matches must belong to the directory peer.", nameof(matches));

        Directory = directory;
        Matches = Array.AsReadOnly(owned);
        Evidence = evidence;
    }

    public PeerDirectorySnapshot Directory { get; }
    public IReadOnlyList<AlbumFileMatch> Matches { get; }
    public AlbumSearchEvidence Evidence { get; }
}

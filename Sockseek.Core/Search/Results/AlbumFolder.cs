using Sockseek.Core.Services;

namespace Sockseek.Core.Models;
    public class AlbumFolder
    {
        public string        Username   { get; }
        public string        FolderPath { get; }
        public List<AlbumFile> Files    => files.Value;
        public int           SearchFileCount => Evidence.SearchFileCount;
        public int           SearchAudioFileCount => Evidence.SearchAudioFileCount;
        public int[]         SearchSortedAudioLengths => Evidence.SearchSortedAudioLengths.ToArray();
        public string?       SearchRepresentativeAudioFilename => Evidence.SearchRepresentativeAudioFilename;
        public AlbumAudioQualityCoverage SearchAudioQualityCoverage => Evidence.SearchAudioQualityCoverage;
        public bool          HasSearchMetadata => Evidence.HasSearchMetadata;
        public bool          IsFullyRetrieved { get; set; }
        internal ResultSorter.SortEntry? SearchAggregateSortEntry => Evidence.AggregateSortEntry;

        public PeerDirectoryIdentity DirectoryIdentity => new(Username, FolderPath);

        public PeerDirectorySnapshot Directory => new(
            DirectoryIdentity,
            Files.Select(file => file.Candidate.Target).ToArray(),
            IsFullyRetrieved);

        public AlbumSearchEvidence Evidence { get; }

        public AlbumDirectoryCandidate ToDirectoryCandidate()
            => new(
                Directory,
                Files.Select(file => file.Match).ToArray(),
                Evidence);

        private readonly Lazy<List<AlbumFile>> files;

        public AlbumFolder(string username, string folderPath, List<AlbumFile> files)
        {
            Username = username;
            FolderPath = folderPath;
            this.files = new Lazy<List<AlbumFile>>(() => files);

            var audioFiles = files
                .Where(f => !f.IsNotAudio)
                .ToList();
            int searchFileCount = files.Count;
            int searchAudioFileCount = audioFiles.Count;
            int[] searchSortedAudioLengths = audioFiles
                .Select(f => f.Candidate.Length ?? -1)
                .OrderBy(x => x)
                .ToArray();
            string? searchRepresentativeAudioFilename = audioFiles
                .FirstOrDefault()
                ?.Filename;
            Evidence = new AlbumSearchEvidence(
                searchFileCount,
                searchAudioFileCount,
                searchSortedAudioLengths,
                searchRepresentativeAudioFilename,
                AlbumAudioQualityCoverage.Inactive(searchAudioFileCount),
                hasSearchMetadata: true);
        }

        public AlbumFolder(string username, string folderPath, Func<List<AlbumFile>> filesFactory)
            : this(username, folderPath, filesFactory, 0, 0, [], null, AlbumAudioQualityCoverage.Inactive(0), hasSearchMetadata: false)
        {
        }

        public AlbumFolder(
            string username,
            string folderPath,
            Func<List<AlbumFile>> filesFactory,
            int searchAudioFileCount,
            int[] searchSortedAudioLengths,
            string? searchRepresentativeAudioFilename)
            : this(username, folderPath, filesFactory, searchAudioFileCount, searchAudioFileCount, searchSortedAudioLengths, searchRepresentativeAudioFilename, AlbumAudioQualityCoverage.Inactive(searchAudioFileCount), hasSearchMetadata: true)
        {
        }

        public AlbumFolder(
            string username,
            string folderPath,
            Func<List<AlbumFile>> filesFactory,
            int searchFileCount,
            int searchAudioFileCount,
            int[] searchSortedAudioLengths,
            string? searchRepresentativeAudioFilename)
            : this(username, folderPath, filesFactory, searchFileCount, searchAudioFileCount, searchSortedAudioLengths, searchRepresentativeAudioFilename, AlbumAudioQualityCoverage.Inactive(searchAudioFileCount), hasSearchMetadata: true, searchAggregateSortEntry: null)
        {
        }

        internal AlbumFolder(
            string username,
            string folderPath,
            Func<List<AlbumFile>> filesFactory,
            int searchFileCount,
            int searchAudioFileCount,
            int[] searchSortedAudioLengths,
            string? searchRepresentativeAudioFilename,
            AlbumAudioQualityCoverage searchAudioQualityCoverage,
            ResultSorter.SortEntry? searchAggregateSortEntry)
            : this(username, folderPath, filesFactory, searchFileCount, searchAudioFileCount, searchSortedAudioLengths, searchRepresentativeAudioFilename, searchAudioQualityCoverage, hasSearchMetadata: true, searchAggregateSortEntry)
        {
        }

        private AlbumFolder(
            string username,
            string folderPath,
            Func<List<AlbumFile>> filesFactory,
            int searchFileCount,
            int searchAudioFileCount,
            int[] searchSortedAudioLengths,
            string? searchRepresentativeAudioFilename,
            AlbumAudioQualityCoverage searchAudioQualityCoverage,
            bool hasSearchMetadata,
            ResultSorter.SortEntry? searchAggregateSortEntry = null)
        {
            Username = username;
            FolderPath = folderPath;
            files = new Lazy<List<AlbumFile>>(filesFactory);
            Evidence = new AlbumSearchEvidence(
                searchFileCount,
                searchAudioFileCount,
                searchSortedAudioLengths,
                searchRepresentativeAudioFilename,
                searchAudioQualityCoverage,
                hasSearchMetadata,
                searchAggregateSortEntry);
        }
    }

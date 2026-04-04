using Enums;
using Models;

namespace Jobs
{
    // Replaces TrackListEntry with source.Type == TrackType.Album.
    public class AlbumJob : DownloadJob
    {
        public AlbumQuery Query { get; set; }

        // SongQuery-shaped view of the album query (artist + album as title).
        // Recomputed from Query so it stays current after preprocessing.
        public override SongQuery QueryTrack =>
            new SongQuery { Artist = Query.Artist, Title = Query.Album, IsDirectLink = Query.IsDirectLink, URI = Query.URI };

        public override bool OutputsDirectory  => true;
        protected override bool DefaultCanBeSkipped => true;

        // Populated after search. Each element is one candidate folder version.
        public List<AlbumFolder> FoundFolders { get; set; } = new();

        // Set when the user or auto-selection picks a version to download.
        private AlbumFolder? _chosenFolder;
        public AlbumFolder? ChosenFolder
        {
            get => _chosenFolder;
            set { _chosenFolder = value; OnPropertyChanged(); }
        }

        public AlbumJob(AlbumQuery query)
        {
            Query = query;
        }

        public override string ToString(bool noInfo)
            => ItemName ?? Query.ToString(noInfo);

        // True only when ALL in-progress files are stale (nothing making progress).
        public bool IsStale(int maxStaleTimeMs)
        {
            if (ChosenFolder == null) return false;

            var inProgress = ChosenFolder.Files
                .Where(f => f.State == TrackState.Initial)
                .ToList();

            if (inProgress.Count == 0) return false;

            return inProgress.All(f =>
                f.LastActivityTime.HasValue &&
                (DateTime.Now - f.LastActivityTime.Value).TotalMilliseconds > maxStaleTimeMs);
        }

        // Returns only the stale in-progress files (for individual cancellation).
        public List<AlbumFile> GetStaleFiles(int maxStaleTimeMs)
        {
            if (ChosenFolder == null) return new();

            return ChosenFolder.Files
                .Where(f => f.State == TrackState.Initial
                    && f.LastActivityTime.HasValue
                    && (DateTime.Now - f.LastActivityTime.Value).TotalMilliseconds > maxStaleTimeMs)
                .ToList();
        }
    }
}

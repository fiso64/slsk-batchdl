using Enums;
using Models;

namespace Jobs
{
    public class AlbumQueryJob : QueryJob
    {
        public AlbumQuery Query { get; set; }

        // SongQuery-shaped view of the album query (artist + album as title).
        // Recomputed from Query so it stays current after preprocessing.
        public override SongQuery QueryTrack =>
            new SongQuery { Artist = Query.Artist, Title = Query.Album, IsDirectLink = Query.IsDirectLink, URI = Query.URI };

        public override bool OutputsDirectory      => true;
        protected override bool DefaultCanBeSkipped => true;

        // Populated after search. Each element is one candidate folder version.
        public List<AlbumFolder> FoundFolders { get; set; } = new();

        // Set by the engine when the download phase completes. Read by observers (Printing, OnCompleteExecutor).
        private AlbumDownloadJob? _completedDownload;
        public AlbumDownloadJob? CompletedDownload
        {
            get => _completedDownload;
            set { _completedDownload = value; OnPropertyChanged(); }
        }

        public AlbumQueryJob(AlbumQuery query)
        {
            Query = query;
        }

        public override string ToString(bool noInfo)
            => ItemName ?? Query.ToString(noInfo);
    }
}

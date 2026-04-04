using Models;

namespace Jobs
{
    // A batch of known albums from a single source (e.g. a Bandcamp wishlist, Spotify liked albums,
    // a Spotify playlist upgraded to album mode). Preserves the source grouping context so progress
    // reporting and on-complete actions can treat the whole list as one unit.
    //
    // The engine processes this by enqueuing each AlbumQueryJob child into the main queue.
    public class AlbumListJob : QueryJob
    {
        public List<AlbumQueryJob> Albums { get; } = new();

        public override SongQuery QueryTrack => new SongQuery { Title = ItemName ?? "" };

        public override bool OutputsDirectory      => false;
        protected override bool DefaultCanBeSkipped => false;

        public AlbumListJob(IEnumerable<AlbumQueryJob>? albums = null)
        {
            if (albums != null) Albums.AddRange(albums);
        }

        public override string ToString(bool noInfo)
            => ItemName ?? $"AlbumList ({Albums.Count} albums)";
    }
}

using Models;

namespace Jobs
{
    // Schedulable job for a batch of individual songs that share config/editors
    // (e.g. all songs from a Spotify playlist or a CSV file).
    // Replaces TrackListEntry with source.Type == TrackType.Normal.
    public class SongListQueryJob : QueryJob
    {
        public override bool OutputsDirectory      => false;
        protected override bool DefaultCanBeSkipped => false;

        // A display/identity query used for naming (e.g. playlist name). Not a download target.
        public override SongQuery QueryTrack { get; }

        public List<SongJob> Songs { get; } = new();

        public SongListQueryJob(SongQuery? sourceQuery = null)
        {
            QueryTrack = sourceQuery ?? new SongQuery();
        }

        public void AddSong(SongJob song) => Songs.Add(song);

        public override string ToString() => ItemName ?? QueryTrack.ToString();
    }
}

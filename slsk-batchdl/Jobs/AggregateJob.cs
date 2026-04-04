using Models;

namespace Jobs
{
    // Downloads all search results matching a query (e.g. all songs by an artist,
    // all versions of a song, or all results for a given name). Groups same songs.
    // Replaces TrackListEntry with source.Type == TrackType.Aggregate.
    public class AggregateQueryJob : QueryJob
    {
        public SongQuery Query { get; }
        public override SongQuery QueryTrack => Query;

        public override bool OutputsDirectory      => true;
        protected override bool DefaultCanBeSkipped => false;

        // One SongJob per found variant. Populated after search.
        public List<SongJob> Songs { get; set; } = new();

        public AggregateQueryJob(SongQuery query)
        {
            Query = query;
        }

        public override string ToString(bool noInfo)
            => ItemName ?? Query.ToString(noInfo);
    }
}

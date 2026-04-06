using Models;

namespace Jobs
{
    // Finds all distinct albums matching a query (e.g. by an artist); converts each to an AlbumJob added to the queue.
    // Has no download state of its own after conversion.
    public class AlbumAggregateJob : Job
    {
        public AlbumQuery Query { get; set; }

        // SongQuery-shaped view for base-class helpers. Recomputed from Query so it stays current after preprocessing.
        public override SongQuery QueryTrack =>
            new SongQuery { Artist = Query.Artist, Title = Query.Album, IsDirectLink = Query.IsDirectLink, URI = Query.URI };

        public override bool OutputsDirectory      => false;
        protected override bool DefaultCanBeSkipped => false;

        public AlbumAggregateJob(AlbumQuery query)
        {
            Query = query;
        }

        public override string ToString(bool noInfo)
            => ItemName ?? Query.ToString(noInfo);
    }
}

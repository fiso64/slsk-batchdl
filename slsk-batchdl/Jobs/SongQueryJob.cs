using Models;

namespace Jobs
{
    // A top-level job for a single-song query. For standalone API use cases where
    // a consumer searches for and downloads one song without a playlist wrapper.
    public class SongQueryJob : QueryJob
    {
        public SongQuery Query { get; }

        // Populated after search; ordered best-first. Null = not yet searched.
        public List<FileCandidate>? Results { get; set; }

        public override SongQuery QueryTrack => Query;

        public override bool OutputsDirectory      => false;
        protected override bool DefaultCanBeSkipped => true;

        public SongQueryJob(SongQuery query)
        {
            Query = query;
        }

        public override string ToString(bool noInfo)
            => ItemName ?? Query.ToString(noInfo);
    }
}

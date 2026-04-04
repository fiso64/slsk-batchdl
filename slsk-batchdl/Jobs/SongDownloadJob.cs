using Models;

namespace Jobs
{
    // A top-level job to download a single pre-resolved file. Created when the
    // search phase has already happened and a FileCandidate has been chosen.
    // Accepted directly by DownloadEngine without a search phase.
    public class SongDownloadJob : DownloadJob
    {
        public FileCandidate Target { get; }
        public SongQuery     Origin { get; }

        public override SongQuery QueryTrack => Origin;

        public override bool OutputsDirectory      => false;
        protected override bool DefaultCanBeSkipped => false;

        public SongDownloadJob(FileCandidate target, SongQuery origin)
        {
            Target = target;
            Origin = origin;
        }

        public override string ToString(bool noInfo)
            => ItemName ?? Origin.ToString(noInfo);
    }
}

using Enums;
using Models;

namespace Jobs
{
    public class AlbumDownloadJob : DownloadJob
    {
        public AlbumFolder Target { get; }
        public AlbumQuery  Origin { get; }

        public override SongQuery QueryTrack =>
            new SongQuery { Artist = Origin.Artist, Title = Origin.Album, IsDirectLink = Origin.IsDirectLink, URI = Origin.URI };

        public override bool OutputsDirectory      => true;
        protected override bool DefaultCanBeSkipped => true;

        public AlbumDownloadJob(AlbumFolder target, AlbumQuery origin)
        {
            Target = target;
            Origin = origin;
        }

        public override string ToString(bool noInfo)
            => ItemName ?? Origin.ToString(noInfo);

        // True only when ALL in-progress files are stale (nothing making progress).
        public bool IsStale(int maxStaleTimeMs)
        {
            var inProgress = Target.Files.Where(f => f.State == TrackState.Initial).ToList();
            if (inProgress.Count == 0) return false;
            return inProgress.All(f =>
                f.LastActivityTime.HasValue &&
                (DateTime.Now - f.LastActivityTime.Value).TotalMilliseconds > maxStaleTimeMs);
        }

        // Returns only the stale in-progress files (for individual cancellation).
        public List<AlbumFile> GetStaleFiles(int maxStaleTimeMs)
        {
            return Target.Files
                .Where(f => f.State == TrackState.Initial
                    && f.LastActivityTime.HasValue
                    && (DateTime.Now - f.LastActivityTime.Value).TotalMilliseconds > maxStaleTimeMs)
                .ToList();
        }
    }
}

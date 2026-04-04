using Models;

namespace Jobs
{
    public class JobQueue
    {
        public List<DownloadJob> Jobs { get; } = new();

        public int Count => Jobs.Count;

        public DownloadJob this[int index] => Jobs[index];

        public void Enqueue(DownloadJob job) => Jobs.Add(job);

        public void Reverse()
        {
            Jobs.Reverse();
            foreach (var job in Jobs)
            {
                if (job is SongListJob slj)
                    slj.Songs.Reverse();
            }
        }

        // Converts SongListJob entries (and existing typed jobs) to the appropriate job type
        // based on album/aggregate flags. Replaces TrackLists.UpgradeListTypes().
        public void UpgradeToAlbumMode(bool album, bool aggregate)
        {
            if (!album && !aggregate) return;

            var upgraded = new List<DownloadJob>();

            foreach (var job in Jobs)
            {
                if (job is AlbumJob aj && aggregate)
                {
                    var newJob = new AggregateAlbumJob(aj.Query);
                    CopySharedFields(aj, newJob);
                    upgraded.Add(newJob);
                }
                else if (job is AggregateJob agj && album)
                {
                    // AggregateJob uses SongQuery; promote to AggregateAlbumJob using AlbumQuery.
                    var newJob = new AggregateAlbumJob(SongQueryToAlbumQuery(agj.Query));
                    CopySharedFields(agj, newJob);
                    upgraded.Add(newJob);
                }
                else if (job is SongListJob slj)
                {
                    foreach (var song in slj.Songs)
                    {
                        var q = song.Query;
                        DownloadJob newJob;

                        if (album && aggregate)
                            newJob = new AggregateAlbumJob(SongQueryToAlbumQuery(q));
                        else if (album)
                            newJob = new AlbumJob(SongQueryToAlbumQuery(q));
                        else // aggregate only
                            newJob = new AggregateJob(q);

                        CopySharedFields(slj, newJob);
                        newJob.ItemNumber = slj.ItemNumber;
                        upgraded.Add(newJob);
                    }
                }
                else
                {
                    upgraded.Add(job);
                }
            }

            Jobs.Clear();
            Jobs.AddRange(upgraded);
        }

        // Sets ItemName on aggregate-type jobs that don't have one.
        // Replaces TrackLists.SetListEntryOptions().
        public void SetAggregateItemNames()
        {
            foreach (var job in Jobs)
            {
                if (job is AggregateJob or AggregateAlbumJob)
                    job.ItemName ??= job.ToString(noInfo: true);
            }
        }

        // Yields all SongJobs in the queue, including those inside SongListJobs.
        public IEnumerable<SongJob> AllSongs()
        {
            foreach (var job in Jobs)
            {
                if (job is SongListJob slj)
                    foreach (var s in slj.Songs) yield return s;
            }
        }

        public IEnumerable<DownloadJob> All() => Jobs;

        private static AlbumQuery SongQueryToAlbumQuery(SongQuery q)
            => new AlbumQuery { Artist = q.Artist, Album = q.Title, URI = q.URI, ArtistMaybeWrong = q.ArtistMaybeWrong, IsDirectLink = q.IsDirectLink };

        private static void CopySharedFields(DownloadJob src, DownloadJob dst)
        {
            dst.Config                = src.Config;
            dst.ExtractorCond         = src.ExtractorCond;
            dst.ExtractorPrefCond     = src.ExtractorPrefCond;
            dst.PlaylistEditor        = src.PlaylistEditor;
            dst.IndexEditor           = src.IndexEditor;
            dst.OutputDirSkipper      = src.OutputDirSkipper;
            dst.MusicDirSkipper       = src.MusicDirSkipper;
            dst.ItemName              = src.ItemName;
            dst.SubItemName           = src.SubItemName;
            dst.EnablesIndexByDefault = src.EnablesIndexByDefault;
            dst.PreprocessTracks      = src.PreprocessTracks;
            dst.ItemNumber            = src.ItemNumber;
            dst.LineNumber            = src.LineNumber;
            dst.CanBeSkippedOverride  = src.CanBeSkippedOverride;
        }
    }
}

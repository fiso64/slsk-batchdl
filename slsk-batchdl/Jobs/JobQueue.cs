using Models;

namespace Jobs
{
    public class JobQueue
    {
        public List<Job> Jobs { get; } = new();

        public int Count => Jobs.Count;

        public Job this[int index] => Jobs[index];

        public void Enqueue(Job job) => Jobs.Add(job);

        public void Reverse()
        {
            Jobs.Reverse();
            foreach (var job in Jobs)
            {
                if (job is SongListQueryJob slj)
                    slj.Songs.Reverse();
            }
        }

        // Converts SongListQueryJob entries (and existing typed jobs) to the appropriate job type
        // based on album/aggregate flags. Replaces TrackLists.UpgradeListTypes().
        public void UpgradeToAlbumMode(bool album, bool aggregate)
        {
            if (!album && !aggregate) return;

            var upgraded = new List<Job>();

            foreach (var job in Jobs)
            {
                if (job is AlbumQueryJob aj && aggregate)
                {
                    var newJob = new AlbumAggregateQueryJob(aj.Query);
                    CopySharedFields(aj, newJob);
                    upgraded.Add(newJob);
                }
                else if (job is AggregateQueryJob agj && album)
                {
                    // AggregateQueryJob uses SongQuery; promote to AlbumAggregateQueryJob using AlbumQuery.
                    var newJob = new AlbumAggregateQueryJob(SongQueryToAlbumQuery(agj.Query));
                    CopySharedFields(agj, newJob);
                    upgraded.Add(newJob);
                }
                else if (job is SongListQueryJob slj)
                {
                    if (album && !aggregate)
                    {
                        // Preserve the playlist grouping as an AlbumListJob.
                        var albumList = new AlbumListJob();
                        CopySharedFields(slj, albumList);
                        foreach (var song in slj.Songs)
                        {
                            var childAj = new AlbumQueryJob(SongQueryToAlbumQuery(song.Query));
                            CopySharedFields(slj, childAj);
                            childAj.ItemNumber = song.ItemNumber;
                            childAj.LineNumber = song.LineNumber;
                            albumList.Albums.Add(childAj);
                        }
                        upgraded.Add(albumList);
                    }
                    else
                    {
                        foreach (var song in slj.Songs)
                        {
                            var q = song.Query;
                            Job newJob;

                            if (album && aggregate)
                                newJob = new AlbumAggregateQueryJob(SongQueryToAlbumQuery(q));
                            else // aggregate only
                                newJob = new AggregateQueryJob(q);

                            CopySharedFields(slj, newJob);
                            newJob.ItemNumber = slj.ItemNumber;
                            upgraded.Add(newJob);
                        }
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
                if (job is AggregateQueryJob or AlbumAggregateQueryJob)
                    job.ItemName ??= job.ToString(noInfo: true);
            }
        }

        // Yields all SongJobs in the queue, including those inside SongListJobs.
        public IEnumerable<SongJob> AllSongs()
        {
            foreach (var job in Jobs)
            {
                if (job is SongListQueryJob slj)
                    foreach (var s in slj.Songs) yield return s;
            }
        }

        public IEnumerable<Job> All() => Jobs;

        // Reverses a list of query jobs and the children inside any container jobs.
        public static void ReverseJobList(List<QueryJob> jobs)
        {
            jobs.Reverse();
            foreach (var job in jobs)
            {
                if (job is SongListQueryJob slj)
                    slj.Songs.Reverse();
                else if (job is AlbumListJob alj)
                    alj.Albums.Reverse();
            }
        }

        private static AlbumQuery SongQueryToAlbumQuery(SongQuery q)
            => new AlbumQuery { Artist = q.Artist, Album = q.Title, URI = q.URI, ArtistMaybeWrong = q.ArtistMaybeWrong, IsDirectLink = q.IsDirectLink };

        private static void CopySharedFields(Job src, Job dst)
        {
            dst.ExtractorCond         = src.ExtractorCond;
            dst.ExtractorPrefCond     = src.ExtractorPrefCond;
            dst.ItemName              = src.ItemName;
            dst.SubItemName           = src.SubItemName;
            dst.EnablesIndexByDefault = src.EnablesIndexByDefault;
            dst.ItemNumber            = src.ItemNumber;
            dst.LineNumber            = src.LineNumber;
            dst.CanBeSkippedOverride  = src.CanBeSkippedOverride;
        }
    }
}

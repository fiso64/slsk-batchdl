using Models;

namespace Jobs
{
    // Universal grouping container. Holds any mix of Job subtypes.
    // Replaces SongListQueryJob, AlbumListJob, and JobQueue as the root container.
    //
    // State is derived: Done when all children are done, Failed if all failed.
    public class JobList : Job
    {
        public List<Job> Jobs { get; } = new();

        public int Count => Jobs.Count;

        public Job this[int index] => Jobs[index];

        public override bool    OutputsDirectory      => false;
        protected override bool DefaultCanBeSkipped   => false;

        public override SongQuery QueryTrack => new SongQuery { Title = ItemName ?? "" };

        public JobList(string? name = null, IEnumerable<Job>? jobs = null)
        {
            ItemName = name;
            if (jobs != null) Jobs.AddRange(jobs);
        }

        public void Add(Job job) => Jobs.Add(job);

        public void Reverse()
        {
            Jobs.Reverse();
            foreach (var job in Jobs)
            {
                if (job is JobList jl)
                    jl.Jobs.Reverse();
            }
        }

        // Yields all SongJobs in this list (not recursing further than one level).
        public IEnumerable<SongJob> AllSongs()
        {
            foreach (var job in Jobs)
            {
                if (job is JobList jl)
                    foreach (var s in jl.Jobs.OfType<SongJob>()) yield return s;
                else if (job is SongJob sj)
                    yield return sj;
            }
        }

        // Reverses a list of jobs and the children inside any JobList containers.
        public static void ReverseJobList(List<Job> jobs)
        {
            jobs.Reverse();
            foreach (var job in jobs)
            {
                if (job is JobList jl)
                    jl.Jobs.Reverse();
            }
        }

        // Converts JobList entries (and existing typed jobs) to the appropriate job type
        // based on album/aggregate flags. Replaces TrackLists.UpgradeListTypes().
        public void UpgradeToAlbumMode(bool album, bool aggregate)
        {
            if (!album && !aggregate) return;

            var upgraded = new List<Job>();

            foreach (var job in Jobs)
            {
                if (job is AlbumJob aj && aggregate)
                {
                    var newJob = new AlbumAggregateJob(aj.Query);
                    CopySharedFields(aj, newJob);
                    upgraded.Add(newJob);
                }
                else if (job is AggregateJob agj && album)
                {
                    var newJob = new AlbumAggregateJob(SongQueryToAlbumQuery(agj.Query));
                    CopySharedFields(agj, newJob);
                    upgraded.Add(newJob);
                }
                else if (job is JobList jl)
                {
                    if (album && !aggregate)
                    {
                        var albumList = new JobList();
                        CopySharedFields(jl, albumList);
                        foreach (var song in jl.Jobs.OfType<SongJob>())
                        {
                            var childAj = new AlbumJob(SongQueryToAlbumQuery(song.Query));
                            CopySharedFields(jl, childAj);
                            childAj.ItemNumber = song.ItemNumber;
                            childAj.LineNumber = song.LineNumber;
                            albumList.Jobs.Add(childAj);
                        }
                        upgraded.Add(albumList);
                    }
                    else
                    {
                        foreach (var song in jl.Jobs.OfType<SongJob>())
                        {
                            var q = song.Query;
                            Job newJob;

                            if (album && aggregate)
                                newJob = new AlbumAggregateJob(SongQueryToAlbumQuery(q));
                            else // aggregate only
                                newJob = new AggregateJob(q);

                            CopySharedFields(jl, newJob);
                            newJob.ItemNumber = jl.ItemNumber;
                            upgraded.Add(newJob);
                        }
                    }
                }
                else if (job is SongJob sj)
                {
                    Job newJob;
                    if (album && aggregate)
                        newJob = new AlbumAggregateJob(SongQueryToAlbumQuery(sj.Query));
                    else if (album)
                        newJob = new AlbumJob(SongQueryToAlbumQuery(sj.Query));
                    else // aggregate only
                        newJob = new AggregateJob(sj.Query);
                    CopySharedFields(sj, newJob);
                    upgraded.Add(newJob);
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
        public void SetAggregateItemNames()
        {
            foreach (var job in Jobs)
            {
                if (job is AggregateJob or AlbumAggregateJob)
                    job.ItemName ??= job.ToString(noInfo: true);
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

        public override string ToString(bool noInfo)
            => ItemName ?? $"JobList ({Jobs.Count} jobs)";
    }
}

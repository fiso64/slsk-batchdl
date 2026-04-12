using Models;

namespace Jobs
{
    // Universal grouping container. Holds any mix of Job subtypes.
    // Replaces SongListQueryJob, AlbumListJob, and JobQueue as the root container.
    //
    // State is derived: Done when all children are done, Failed if all failed.
    public class JobList : Job, IUpgradeable
    {
        public List<Job> Jobs { get; } = new();

        public int Count => Jobs.Count;

        public Job this[int index] => Jobs[index];

        protected override bool DefaultCanBeSkipped => false;

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

        // Yields all SongJobs reachable from this list, traversing ExtractJob.Result nodes.
        public IEnumerable<SongJob> AllSongs() => AllJobs().OfType<SongJob>();

        // Yields every Job reachable from this list (depth-first), following ExtractJob.Result.
        public IEnumerable<Job> AllJobs()
        {
            foreach (var job in Jobs)
                foreach (var j in WalkJob(job))
                    yield return j;
        }

        private static IEnumerable<Job> WalkJob(Job job)
        {
            yield return job;

            if (job is ExtractJob ej && ej.Result != null)
                foreach (var j in WalkJob(ej.Result))
                    yield return j;

            if (job is JobList jl)
                foreach (var child in jl.Jobs)
                    foreach (var j in WalkJob(child))
                        yield return j;
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

        public override string ToString(bool noInfo)
            => ItemName ?? $"JobList ({Jobs.Count} jobs)";

        public IEnumerable<Job> Upgrade(bool album, bool aggregate)
        {
            if (!album && !aggregate)
            {
                yield return this;
                yield break;
            }

            if (album && !aggregate)
            {
                var albumList = new JobList();
                albumList.CopySharedFieldsFrom(this);
                foreach (var job in Jobs)
                {
                    if (job is IUpgradeable u)
                    {
                        foreach (var upgraded in u.Upgrade(album, aggregate))
                            albumList.Add(upgraded);
                    }
                    else
                    {
                        albumList.Add(job);
                    }
                }
                yield return albumList;
            }
            else
            {
                foreach (var job in Jobs)
                {
                    if (job is IUpgradeable u)
                    {
                        foreach (var upgraded in u.Upgrade(album, aggregate))
                            yield return upgraded;
                    }
                    else
                    {
                        yield return job;
                    }
                }
            }
        }
    }
}

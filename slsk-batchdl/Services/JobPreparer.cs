using Jobs;
using Enums;

namespace Services
{
    /// <summary>
    /// Tree-walks the job tree and sets up per-job Config, M3uEditors, and TrackSkippers.
    /// Config flows top-down: each job inherits its parent's config independently (siblings
    /// do not accumulate — a profile applied to one job does not affect its siblings).
    /// Returns a JobContext keyed by job ID for every job in the tree.
    /// </summary>
    public static class JobPreparer
    {
        public static Dictionary<Guid, JobContext> PrepareJobs(JobList queue, Config startConfig)
        {
            var contexts = new Dictionary<Guid, JobContext>();
            var editors  = new Dictionary<(string path, M3uOption option), M3uEditor>();
            var skippers = new Dictionary<(string dir, SkipMode mode, bool checkCond), TrackSkipper>();

            foreach (var job in queue.Jobs)
                PrepareJob(job, queue, startConfig, contexts, editors, skippers);

            return contexts;
        }

        private static void PrepareJob(
            Job job,
            JobList ownerList,
            Config parentConfig,
            Dictionary<Guid, JobContext> contexts,
            Dictionary<(string, M3uOption), M3uEditor> editors,
            Dictionary<(string, SkipMode, bool), TrackSkipper> skippers)
        {
            var ctx = new JobContext();

            job.Config = parentConfig.Copy();
            job.Config = job.Config.UpdateProfiles(job, ownerList);

            ctx.EnablesIndexByDefault = job.EnablesIndexByDefault;

            if (job.ExtractorCond != null)
            {
                job.Config.necessaryCond.AddConditions(job.ExtractorCond);
                job.ExtractorCond = null;
            }
            if (job.ExtractorPrefCond != null)
            {
                job.Config.preferredCond.AddConditions(job.ExtractorPrefCond);
                job.ExtractorPrefCond = null;
            }

            SetupIndexEditor(job, ctx, ownerList, editors);
            SetupPlaylistEditor(job, ctx, ownerList, editors);
            SetupSkippers(job, ctx, skippers);

            contexts[job.Id] = ctx;

            // Recurse into JobList children — they inherit this list's config and
            // use this list as their ownerList for editor/index scoping.
            if (job is JobList childList)
            {
                foreach (var child in childList.Jobs)
                    PrepareJob(child, childList, job.Config, contexts, editors, skippers);
            }
        }

        private static void SetupIndexEditor(Job job, JobContext ctx, JobList ownerList,
            Dictionary<(string, M3uOption), M3uEditor> editors)
        {
            var config = job.Config;
            var indexOption = config.WillWriteIndex(ownerList) ? M3uOption.Index : M3uOption.None;

            bool needIndex = indexOption != M3uOption.None
                || (config.skipExisting && config.skipMode == SkipMode.Index)
                || config.skipNotFound;

            if (!needIndex) return;

            string indexPath;
            if (config.indexFilePath.Length > 0)
                indexPath = config.indexFilePath.Replace("{playlist-name}", job.ItemNameOrSource().ReplaceInvalidChars(" ").Trim());
            else
                indexPath = Path.Join(config.parentDir, job.DefaultFolderName(), "_index.csv");

            var key = (indexPath, indexOption);
            if (editors.TryGetValue(key, out var existing))
            {
                ctx.IndexEditor = existing;
            }
            else
            {
                ctx.IndexEditor = new M3uEditor(indexPath, ownerList, indexOption, true);
                editors.Add(key, ctx.IndexEditor);
            }
        }

        private static void SetupPlaylistEditor(Job job, JobContext ctx, JobList ownerList,
            Dictionary<(string, M3uOption), M3uEditor> editors)
        {
            var config = job.Config;
            if (!config.writePlaylist) return;

            string m3uPath;
            if (config.m3uFilePath.Length > 0)
                m3uPath = config.m3uFilePath.Replace("{playlist-name}", job.ItemNameOrSource().ReplaceInvalidChars(" ").Trim());
            else
                m3uPath = Path.Join(config.parentDir, job.DefaultFolderName(), job.DefaultPlaylistName());

            var key = (m3uPath, M3uOption.Playlist);
            if (editors.TryGetValue(key, out var existing))
            {
                ctx.PlaylistEditor = existing;
            }
            else
            {
                ctx.PlaylistEditor = new M3uEditor(m3uPath, ownerList, M3uOption.Playlist, false);
                editors.Add(key, ctx.PlaylistEditor);
            }
        }

        private static void SetupSkippers(Job job, JobContext ctx,
            Dictionary<(string, SkipMode, bool), TrackSkipper> skippers)
        {
            var config = job.Config;
            if (!config.skipExisting) return;

            bool checkCond = config.skipCheckCond || config.skipCheckPrefCond;

            var outputKey = (config.parentDir, config.skipMode, checkCond);
            if (skippers.TryGetValue(outputKey, out var outputSkipper))
            {
                ctx.OutputDirSkipper = outputSkipper;
            }
            else
            {
                ctx.OutputDirSkipper = TrackSkipperRegistry.GetSkipper(config.skipMode, config.parentDir, checkCond);
                skippers.Add(outputKey, ctx.OutputDirSkipper);
            }

            if (config.skipMusicDir.Length > 0)
            {
                var musicKey = (config.skipMusicDir, config.skipModeMusicDir, checkCond);
                if (skippers.TryGetValue(musicKey, out var musicSkipper))
                {
                    ctx.MusicDirSkipper = musicSkipper;
                }
                else
                {
                    ctx.MusicDirSkipper = TrackSkipperRegistry.GetSkipper(config.skipModeMusicDir, config.skipMusicDir, checkCond);
                    skippers.Add(musicKey, ctx.MusicDirSkipper);
                }
            }
        }
    }
}

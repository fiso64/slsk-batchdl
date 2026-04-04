using Jobs;
using Enums;

namespace Services
{
    /// <summary>
    /// Sets up per-job Config, M3uEditors, and TrackSkippers for every job in a queue.
    /// Extracted from DownloaderApplication.PrepareListEntries.
    /// </summary>
    public static class JobPreparer
    {
        public static void PrepareJobs(JobQueue queue, Config startConfig)
        {
            var editors  = new Dictionary<(string path, M3uOption option), M3uEditor>();
            var skippers = new Dictionary<(string dir, SkipMode mode, bool checkCond), TrackSkipper>();

            foreach (var job in queue.Jobs)
            {
                job.Config = startConfig.Copy();
                job.Config = job.Config.UpdateProfiles(job, queue);
                startConfig = job.Config;

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

                SetupIndexEditor(job, queue, editors);
                SetupPlaylistEditor(job, queue, editors);
                SetupSkippers(job, skippers);
            }
        }

        private static void SetupIndexEditor(DownloadJob job, JobQueue queue,
            Dictionary<(string, M3uOption), M3uEditor> editors)
        {
            var config = job.Config;
            var indexOption = config.WillWriteIndex(queue) ? M3uOption.Index : M3uOption.None;

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
                job.IndexEditor = existing;
            }
            else
            {
                job.IndexEditor = new M3uEditor(indexPath, queue, indexOption, true);
                editors.Add(key, job.IndexEditor);
            }
        }

        private static void SetupPlaylistEditor(DownloadJob job, JobQueue queue,
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
                job.PlaylistEditor = existing;
            }
            else
            {
                job.PlaylistEditor = new M3uEditor(m3uPath, queue, M3uOption.Playlist, false);
                editors.Add(key, job.PlaylistEditor);
            }
        }

        private static void SetupSkippers(DownloadJob job,
            Dictionary<(string, SkipMode, bool), TrackSkipper> skippers)
        {
            var config = job.Config;
            if (!config.skipExisting) return;

            bool checkCond = config.skipCheckCond || config.skipCheckPrefCond;

            var outputKey = (config.parentDir, config.skipMode, checkCond);
            if (skippers.TryGetValue(outputKey, out var outputSkipper))
            {
                job.OutputDirSkipper = outputSkipper;
            }
            else
            {
                job.OutputDirSkipper = TrackSkipperRegistry.GetSkipper(config.skipMode, config.parentDir, checkCond);
                skippers.Add(outputKey, job.OutputDirSkipper);
            }

            if (config.skipMusicDir.Length > 0)
            {
                var musicKey = (config.skipMusicDir, config.skipModeMusicDir, checkCond);
                if (skippers.TryGetValue(musicKey, out var musicSkipper))
                {
                    job.MusicDirSkipper = musicSkipper;
                }
                else
                {
                    job.MusicDirSkipper = TrackSkipperRegistry.GetSkipper(config.skipModeMusicDir, config.skipMusicDir, checkCond);
                    skippers.Add(musicKey, job.MusicDirSkipper);
                }
            }
        }
    }
}

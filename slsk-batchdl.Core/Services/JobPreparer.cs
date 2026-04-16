using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core;
using Sldl.Core.Settings;

namespace Sldl.Core.Services;

/// <summary>
/// Tree-walks the job tree and sets up per-job Config, M3uEditors, and TrackSkippers.
/// Config flows top-down: each job inherits its parent's config independently (siblings
/// do not accumulate — a profile applied to one job does not affect its siblings).
/// Returns a JobContext keyed by job ID for every job in the tree.
/// </summary>
public static class JobPreparer
{
    /// <summary>
    /// Optional hook for applying auto-profiles per job. Receives the inherited config and
    /// the job being prepared; returns the (possibly modified) config to assign to that job.
    /// Set by the CLI after constructing the engine when per-job profile resolution is needed.
    /// </summary>
    public static Func<DownloadSettings, Job, DownloadSettings>? ApplyProfiles { get; set; }

    public static Dictionary<Guid, JobContext> PrepareJobs(
        JobList queue,
        DownloadSettings startConfig)
    {
        var contexts = new Dictionary<Guid, JobContext>();
        var editors  = new Dictionary<(string path, M3uOption option), M3uEditor>();
        var skippers = new Dictionary<(string dir, SkipMode mode, bool checkCond), TrackSkipper>();

        foreach (var job in queue.Jobs)
            PrepareJob(job, queue, startConfig, contexts, editors, skippers);

        return contexts;
    }

    // Prepares a single subtree rooted at `root`, using `parentConfig` as the starting config.
    // Returns only the newly created contexts (caller merges them into the main dict).
    public static Dictionary<Guid, JobContext> PrepareSubtree(
        Job root,
        DownloadSettings parentConfig)
    {
        var newContexts = new Dictionary<Guid, JobContext>();
        var editors     = new Dictionary<(string path, M3uOption option), M3uEditor>();
        var skippers    = new Dictionary<(string dir, SkipMode mode, bool checkCond), TrackSkipper>();

        // Use a synthetic owner list so index/playlist paths are scoped correctly when
        // root is a bare leaf job (not a JobList).
        var ownerList = root as JobList ?? new JobList();
        PrepareJob(root, ownerList, parentConfig, newContexts, editors, skippers);
        return newContexts;
    }

    public static void ApplySearchSettings(Job job, SearchSettings search)
    {
        switch (job)
        {
            case JobList jl:
                foreach (var s in jl.Jobs.OfType<SongJob>())  ApplySearchSettings(s, search);
                foreach (var a in jl.Jobs.OfType<AlbumJob>()) ApplySearchSettings(a, search);
                break;

            case SongJob sj:
                if (search.ArtistMaybeWrong && !sj.Query.ArtistMaybeWrong)
                    sj.Query = new SongQuery(sj.Query) { ArtistMaybeWrong = true };
                break;

            case AlbumJob aj:
                ApplySearchSettingsToAlbumQuery(aj, search);
                break;

            case AggregateJob ag:
                foreach (var s in ag.Songs) ApplySearchSettings(s, search);
                break;

            case AlbumAggregateJob aaj:
                ApplySearchSettingsToAlbumAggregateQuery(aaj, search);
                break;
        }
    }

    static void ApplySearchSettingsToAlbumQuery(AlbumJob aj, SearchSettings search)
    {
        var q   = aj.Query;
        bool amw = q.ArtistMaybeWrong;
        int  min = q.MinTrackCount;
        int  max = q.MaxTrackCount;

        if (search.ArtistMaybeWrong)                               amw = true;
        if (search.NecessaryFolderCond.MinTrackCount != -1)        min = search.NecessaryFolderCond.MinTrackCount;
        if (search.NecessaryFolderCond.MaxTrackCount != -1)        max = search.NecessaryFolderCond.MaxTrackCount;

        if (amw != q.ArtistMaybeWrong || min != q.MinTrackCount || max != q.MaxTrackCount)
            aj.Query = new AlbumQuery(q) { ArtistMaybeWrong = amw, MinTrackCount = min, MaxTrackCount = max };
    }

    static void ApplySearchSettingsToAlbumAggregateQuery(AlbumAggregateJob aaj, SearchSettings search)
    {
        var q   = aaj.Query;
        bool amw = q.ArtistMaybeWrong;
        int  min = q.MinTrackCount;
        int  max = q.MaxTrackCount;

        if (search.ArtistMaybeWrong)                               amw = true;
        if (search.NecessaryFolderCond.MinTrackCount != -1)        min = search.NecessaryFolderCond.MinTrackCount;
        if (search.NecessaryFolderCond.MaxTrackCount != -1)        max = search.NecessaryFolderCond.MaxTrackCount;

        if (amw != q.ArtistMaybeWrong || min != q.MinTrackCount || max != q.MaxTrackCount)
            aaj.Query = new AlbumQuery(q) { ArtistMaybeWrong = amw, MinTrackCount = min, MaxTrackCount = max };
    }

    private static void PrepareJob(
        Job job,
        JobList ownerList,
        DownloadSettings parentConfig,
        Dictionary<Guid, JobContext> contexts,
        Dictionary<(string, M3uOption), M3uEditor> editors,
        Dictionary<(string, SkipMode, bool), TrackSkipper> skippers)
    {
        var ctx = new JobContext();

        job.Config = ApplyProfiles != null
            ? ApplyProfiles(parentConfig, job)
            : parentConfig;

        ctx.EnablesIndexByDefault = job.EnablesIndexByDefault;

        if (job.ExtractorCond != null)
        {
            job.Config.Search.NecessaryCond.AddConditions(job.ExtractorCond);
            job.ExtractorCond = null;
        }
        if (job.ExtractorPrefCond != null)
        {
            job.Config.Search.PreferredCond.AddConditions(job.ExtractorPrefCond);
            job.ExtractorPrefCond = null;
        }
        if (job.ExtractorFolderCond != null)
        {
            job.Config.Search.NecessaryFolderCond.AddConditions(job.ExtractorFolderCond);
            job.ExtractorFolderCond = null;
        }
        if (job.ExtractorPrefFolderCond != null)
        {
            job.Config.Search.PreferredFolderCond.AddConditions(job.ExtractorPrefFolderCond);
            job.ExtractorPrefFolderCond = null;
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
        var indexOption = WillWriteIndex(config, ownerList) ? M3uOption.Index : M3uOption.None;

        bool needIndex = indexOption != M3uOption.None
            || (config.Skip.SkipExisting && config.Skip.SkipMode == SkipMode.Index)
            || config.Skip.SkipNotFound;

        if (!needIndex) return;

        string indexPath;
        if (config.Output.IndexFilePath != null)
            indexPath = config.Output.IndexFilePath.Replace("{playlist-name}", job.ItemNameOrSource().ReplaceInvalidChars(" ").Trim());
        else
            indexPath = Path.Join(config.Output.ParentDir, job.DefaultFolderName(), "_index.csv");

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
        if (!config.Output.WritePlaylist) return;

        string m3uPath;
        if (config.Output.M3uFilePath != null)
            m3uPath = config.Output.M3uFilePath.Replace("{playlist-name}", job.ItemNameOrSource().ReplaceInvalidChars(" ").Trim());
        else
            m3uPath = Path.Join(config.Output.ParentDir, job.DefaultFolderName(), job.DefaultPlaylistName());

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
        if (!config.Skip.SkipExisting) return;

        bool checkCond = config.Skip.SkipCheckCond || config.Skip.SkipCheckPrefCond;

        var outputKey = (config.Output.ParentDir!, config.Skip.SkipMode, checkCond);
        if (skippers.TryGetValue(outputKey, out var outputSkipper))
        {
            ctx.OutputDirSkipper = outputSkipper;
        }
        else
        {
            ctx.OutputDirSkipper = TrackSkipperRegistry.GetSkipper(config.Skip.SkipMode, config.Output.ParentDir!, checkCond);
            skippers.Add(outputKey, ctx.OutputDirSkipper);
        }

        if (config.Skip.SkipMusicDir != null)
        {
            var musicKey = (config.Skip.SkipMusicDir, config.Skip.SkipModeMusicDir, checkCond);
            if (skippers.TryGetValue(musicKey, out var musicSkipper))
            {
                ctx.MusicDirSkipper = musicSkipper;
            }
            else
            {
                ctx.MusicDirSkipper = TrackSkipperRegistry.GetSkipper(config.Skip.SkipModeMusicDir, config.Skip.SkipMusicDir, checkCond);
                skippers.Add(musicKey, ctx.MusicDirSkipper);
            }
        }
    }

    public static bool WillWriteIndex(DownloadSettings dl, JobList? queue = null)
    {
        if (dl.DoNotDownload) return false;
        if (!dl.Output.HasConfiguredIndex && queue != null && !queue.Jobs.Any(x => x.EnablesIndexByDefault))
            return false;
        return dl.Output.WriteIndex;
    }
}

using System.Text.RegularExpressions;

using Jobs;
using Models;
using Enums;


// Context object passed to VarExtractors lambdas and name-format helpers.
// Constructed from either a SongJob or an AlbumFile, so name format works uniformly.
public struct FileManagerContext
{
    public DownloadJob   Job;
    public SongQuery     Query;         // artist, title, album, length, uri, artistMaybeWrong
    public FileCandidate? Candidate;    // slsk-filename, slsk-foldername
    public string?       DownloadPath;  // path, path-noext, ext
    public TrackState    State;
    public FailureReason FailureReason;
    public bool          IsNotAudio;
    public int           LineNumber;
    public int           ItemNumber;
    public string?       RemoteBaseDir;

    public static FileManagerContext FromSongJob(SongJob song, DownloadJob job, string? remoteBaseDir = null)
    {
        return new FileManagerContext
        {
            Job           = job,
            Query         = song.Query,
            Candidate     = song.ChosenCandidate ?? song.Candidates?.FirstOrDefault(),
            DownloadPath  = song.DownloadPath,
            State         = song.State,
            FailureReason = song.FailureReason,
            IsNotAudio    = false,
            LineNumber    = song.LineNumber,
            ItemNumber    = song.ItemNumber,
            RemoteBaseDir = remoteBaseDir,
        };
    }

    public static FileManagerContext FromAlbumFile(AlbumFile file, DownloadJob job, string? remoteBaseDir = null)
    {
        return new FileManagerContext
        {
            Job           = job,
            Query         = file.Info,
            Candidate     = file.Candidate,
            DownloadPath  = file.DownloadPath,
            State         = file.State,
            FailureReason = file.FailureReason,
            IsNotAudio    = file.IsNotAudio,
            LineNumber    = job.LineNumber,
            ItemNumber    = job.ItemNumber,
            RemoteBaseDir = remoteBaseDir,
        };
    }
}


public class FileManager
{
    readonly DownloadJob job;
    readonly HashSet<object> organized = new();
    public string? remoteBaseDir { get; private set; }
    public string? remoteImagesCommonDir { get; private set; }
    public string? defaultFolderName { get; private set; }
    public bool downloadingAdditionalImages = false;
    private readonly Config config;

    public FileManager(DownloadJob job, Config config)
    {
        this.job    = job;
        this.config = config;
    }

    public string GetSavePath(string sourceFname)
    {
        return GetSavePathNoExt(sourceFname) + Path.GetExtension(sourceFname);
    }

    public string GetSavePathNoExt(string sourceFname)
    {
        string? rcd    = downloadingAdditionalImages ? remoteImagesCommonDir : remoteBaseDir;
        string  parent = config.parentDir;
        string  name   = Utils.GetFileNameWithoutExtSlsk(sourceFname);

        if (!string.IsNullOrEmpty(job.DefaultFolderName()))
            parent = Path.Join(parent, job.DefaultFolderName());

        if (job is AlbumJob && !string.IsNullOrEmpty(rcd))
        {
            string dirname   = defaultFolderName ?? Path.GetFileName(rcd);
            string normFname = Utils.NormalizedPath(sourceFname);
            string relpath   = normFname.StartsWith(rcd) ? Path.GetRelativePath(rcd, normFname) : "";
            parent = Path.Join(parent, dirname, Path.GetDirectoryName(relpath) ?? "");
        }

        return Path.Join(parent, name).CleanPath(config.invalidReplaceStr);
    }

    public void SetremoteBaseDir(string? dir)
    {
        this.remoteBaseDir = dir != null ? Utils.NormalizedPath(dir) : null;
    }

    public void SetRemoteCommonImagesDir(string? dir)
    {
        this.remoteImagesCommonDir = dir != null ? Utils.NormalizedPath(dir) : null;
    }

    public void SetDefaultFolderName(string? name)
    {
        this.defaultFolderName = name != null ? Utils.NormalizedPath(name) : null;
    }

    // Organizes all files in a completed album download, then sets the job's DownloadPath to the
    // greatest common directory of the audio files.
    public void OrganizeAlbum(AlbumJob albumJob, List<AlbumFile> allFiles, List<AlbumFile>? additionalImages, bool remainingOnly = true)
    {
        foreach (var file in allFiles.Where(f => !f.IsNotAudio))
        {
            if (remainingOnly && organized.Contains(file))
                continue;
            OrganizeAlbumFile(file);
        }

        albumJob.DownloadPath = Utils.GreatestCommonDirectory(
            allFiles.Where(f => !f.IsNotAudio).Select(f => f.DownloadPath));

        var nonAudioToOrganize = string.IsNullOrEmpty(config.nameFormat)
            ? additionalImages
            : (IEnumerable<AlbumFile>)allFiles.Where(f => f.IsNotAudio);

        if (nonAudioToOrganize == null || !nonAudioToOrganize.Any())
            return;

        string parent = Utils.GreatestCommonDirectory(
            allFiles
                .Where(f => !f.IsNotAudio && f.State == TrackState.Downloaded && f.DownloadPath?.Length > 0)
                .Select(f => f.DownloadPath));

        foreach (var file in nonAudioToOrganize)
        {
            if (remainingOnly && organized.Contains(file))
                continue;
            OrganizeNonAudio(file, parent, additionalImages != null && additionalImages.Contains(file));
        }
    }

    public void OrganizeAlbumFile(AlbumFile file)
    {
        if (string.IsNullOrEmpty(file.DownloadPath) || !Utils.IsMusicFile(file.DownloadPath))
            return;

        if (config.nameFormat.Length == 0)
        {
            organized.Add(file);
            return;
        }

        string pathPart    = ApplyNameFormat(config.nameFormat, FileManagerContext.FromAlbumFile(file, job, remoteBaseDir));
        string newFilePath = Path.Join(config.parentDir, pathPart + Path.GetExtension(file.DownloadPath));

        if (Utils.NormalizedPath(newFilePath) != Utils.NormalizedPath(file.DownloadPath))
        {
            try { Utils.MoveAndDeleteParent(file.DownloadPath, newFilePath, config.parentDir); }
            catch (Exception ex) { Logger.Error($"Failed to move: {ex}"); return; }
        }

        file.DownloadPath = newFilePath;
        organized.Add(file);
    }

    public void OrganizeSong(SongJob song)
    {
        if (string.IsNullOrEmpty(song.DownloadPath) || !Utils.IsMusicFile(song.DownloadPath))
            return;

        if (config.nameFormat.Length == 0)
        {
            organized.Add(song);
            return;
        }

        string pathPart    = ApplyNameFormat(config.nameFormat, FileManagerContext.FromSongJob(song, job, remoteBaseDir));
        string newFilePath = Path.Join(config.parentDir, pathPart + Path.GetExtension(song.DownloadPath));

        if (Utils.NormalizedPath(newFilePath) != Utils.NormalizedPath(song.DownloadPath))
        {
            try { Utils.MoveAndDeleteParent(song.DownloadPath, newFilePath, config.parentDir); }
            catch (Exception ex) { Logger.Error($"Failed to move: {ex}"); return; }
        }

        song.DownloadPath = newFilePath;
        organized.Add(song);
    }

    private void OrganizeNonAudio(AlbumFile file, string parent, bool isAdditionalImage)
    {
        if (string.IsNullOrEmpty(file.DownloadPath))
            return;

        string? part = null;
        string? rcd  = isAdditionalImage ? remoteImagesCommonDir : remoteBaseDir;

        if (rcd != null && Utils.IsInDirectory(Utils.GetDirectoryNameSlsk(file.Candidate.Filename), rcd, true))
            part = Utils.GetFileNameSlsk(Utils.GetDirectoryNameSlsk(file.Candidate.Filename));

        string newFilePath = Path.Join(parent, part, Path.GetFileName(file.DownloadPath));

        if (Utils.NormalizedPath(newFilePath) != Utils.NormalizedPath(file.DownloadPath))
        {
            try { Utils.MoveAndDeleteParent(file.DownloadPath, newFilePath, config.parentDir); }
            catch (Exception ex) { Logger.Error($"Failed to move: {ex}"); return; }
        }

        file.DownloadPath = newFilePath;
        organized.Add(file);
    }

    private string ApplyNameFormat(string format, FileManagerContext ctx)
    {
        TagLib.File? tagFile  = null;
        bool         tried    = false;
        TagLib.File? getTagFile()
        {
            if (!tried)
            {
                tried = true;
                try { tagFile = TagLib.File.Create(ctx.DownloadPath); }
                catch (Exception ex) { Logger.Trace($"Failed to read tags for '{ctx.DownloadPath}': {ex.Message}"); }
            }
            return tagFile;
        }
        return ApplyNameFormatInternal(format, config, ctx, getTagFile);
    }

    static string ApplyNameFormatInternal(string format, Config config, FileManagerContext ctx, Func<TagLib.File?> getTagFile)
    {
        string newName = format;
        var regex   = new Regex(@"(\{(?:\{??[^\{]*?\}))");
        var matches = regex.Matches(newName);

        while (matches.Count > 0)
        {
            foreach (var match in matches.Cast<Match>())
            {
                string inner = match.Groups[1].Value[1..^1];
                var options  = inner.Split('|');
                string? chosenOpt = null;

                foreach (var opt in options)
                {
                    string[] parts  = Regex.Split(opt, @"\([^\)]*\)");
                    string[] result = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
                    if (result.All(x => TryGetCleanVarValue(x, ctx, getTagFile, config.invalidReplaceStr, out string res) && res.Length > 0))
                    {
                        chosenOpt = opt;
                        break;
                    }
                }

                if (chosenOpt == null)
                    chosenOpt = options[^1];

                chosenOpt = Regex.Replace(chosenOpt, @"\([^()]*\)|[^()]+", m =>
                {
                    if (m.Value.StartsWith("(") && m.Value.EndsWith(")"))
                        return m.Value[1..^1].ReplaceInvalidChars(config.invalidReplaceStr, removeSlash: false);
                    TryGetCleanVarValue(m.Value, ctx, getTagFile, config.invalidReplaceStr, out string res);
                    return res;
                });

                string old = match.Groups[1].Value;
                old        = old.StartsWith("{{") ? old[1..] : old;
                newName    = newName.Replace(old, chosenOpt);
            }

            matches = regex.Matches(newName);
        }

        if (newName != format)
        {
            char dirsep = Path.DirectorySeparatorChar;
            newName = newName.Replace('/', dirsep).Replace('\\', dirsep);
            var x   = newName.Split(dirsep, StringSplitOptions.RemoveEmptyEntries);
            newName = string.Join(dirsep, x.Select(s => s.ReplaceInvalidChars(config.invalidReplaceStr).Trim(' ', '.')));
            return newName;
        }

        return format;
    }

    // Key: variable name. Value: (ctx, tagFile) → string.
    private static readonly Dictionary<string, Func<FileManagerContext, TagLib.File?, string>> VarExtractors = new()
    {
        // Tag-based (read from the downloaded file's embedded tags)
        { "artist",       (_, f) => f?.Tag.FirstPerformer ?? "" },
        { "artists",      (_, f) => f != null ? string.Join(" & ", f.Tag.Performers) : "" },
        { "albumartist",  (_, f) => f?.Tag.FirstAlbumArtist ?? "" },
        { "albumartists", (_, f) => f != null ? string.Join(" & ", f.Tag.AlbumArtists) : "" },
        { "title",        (_, f) => f?.Tag.Title ?? "" },
        { "album",        (_, f) => f?.Tag.Album ?? "" },
        { "year",         (_, f) => f?.Tag.Year.ToString() ?? "" },
        { "track",        (_, f) => f?.Tag.Track.ToString("D2") ?? "" },
        { "disc",         (_, f) => f?.Tag.Disc.ToString() ?? "" },
        { "length",       (_, f) => f?.Tag.Length.ToString() ?? "" },

        // Search-query fields (from the original query, prefix 's' = "source")
        { "sartist",  (ctx, _) => ctx.Query.Artist },
        { "sartists", (ctx, _) => ctx.Query.Artist },
        { "stitle",   (ctx, _) => ctx.Query.Title },
        { "salbum",   (ctx, _) => ctx.Query.Album },
        { "slength",  (ctx, _) => ctx.Query.Length.ToString() },
        { "uri",      (ctx, _) => ctx.Query.URI },
        { "url",      (ctx, _) => ctx.Query.URI },

        // Download state
        { "type",             (ctx, _) => ctx.Job.GetType().Name.Replace("Job", "") },
        { "state",            (ctx, _) => ctx.State.ToString() },
        { "is-audio",         (ctx, _) => (!ctx.IsNotAudio).ToString().ToLower() },
        { "failure-reason",   (ctx, _) => ctx.FailureReason.ToString() },
        { "artist-maybe-wrong", (ctx, _) => ctx.Query.ArtistMaybeWrong.ToString().ToLower() },
        { "row",              (ctx, _) => ctx.LineNumber.ToString() },
        { "line",             (ctx, _) => ctx.LineNumber.ToString() },
        { "snum",             (ctx, _) => ctx.ItemNumber.ToString() },

        // Soulseek file path vars (from the remote file)
        { "slsk-filename", (ctx, _) => Utils.GetFileNameWithoutExtSlsk(ctx.Candidate?.Filename ?? "") },
        { "filename",      (ctx, _) => Utils.GetFileNameWithoutExtSlsk(ctx.Candidate?.Filename ?? "") },
        { "slsk-foldername", (ctx, _) => GetFolderName(ctx.Candidate?.File, ctx.RemoteBaseDir) },
        { "foldername",      (ctx, _) => GetFolderName(ctx.Candidate?.File, ctx.RemoteBaseDir) },

        // Job / config vars
        { "extractor",      (ctx, _) => ctx.Job.Config.inputType.ToString() },
        { "input",          (ctx, _) => ctx.Job.Config.input },
        { "item-name",      (ctx, _) => ctx.Job.ItemNameOrSource() },
        { "default-folder", (ctx, _) => ctx.Job.DefaultFolderName() },
        { "output-dir",     (ctx, _) => ctx.Job.Config.parentDir },

        // Local path vars (from the downloaded file's local path)
        { "path",      (ctx, _) => (ctx.DownloadPath ?? "").TrimEnd('/').TrimEnd('\\') },
        { "path-noext",(ctx, _) => ctx.DownloadPath != null ? Path.Combine(Path.GetDirectoryName(ctx.DownloadPath), Path.GetFileNameWithoutExtension(ctx.DownloadPath)) : "" },
        { "ext",       (ctx, _) => ctx.DownloadPath != null ? Path.GetExtension(ctx.DownloadPath) : "" },
        { "bindir",    (_, _)   => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('/').TrimEnd('\\') },
    };

    private static readonly HashSet<string> PreserveSeparatorVars = new()
    {
        "slsk-foldername",
        "foldername"
    };

    private static readonly HashSet<string> NoCleanSeparatorVars = new()
    {
        "path",
        "path-noext",
        "bindir",
    };

    private static readonly HashSet<string> TagVars = new()
    {
        "artist", "artists", "albumartist", "albumartists",
        "title", "album", "year", "track", "disc", "length"
    };

    public static bool HasTagVariables(string x)
    {
        return TagVars.Any(v => x.Contains($"{{{v}}}"));
    }

    private static string GetFolderName(Soulseek.File? slfile, string? remoteBaseDir)
    {
        if (string.IsNullOrEmpty(remoteBaseDir) || slfile == null)
        {
            if (!string.IsNullOrEmpty(remoteBaseDir))
                return Path.GetFileName(Utils.NormalizedPath(remoteBaseDir));
            if (slfile != null)
                return Path.GetFileName(Path.GetDirectoryName(Utils.NormalizedPath(slfile.Filename)));
            return "";
        }

        string normalizedRbd = Utils.NormalizedPath(remoteBaseDir);
        string d   = Path.GetDirectoryName(Utils.NormalizedPath(slfile.Filename));
        string r   = Path.GetFileName(normalizedRbd);
        string result = Path.Join(r, Path.GetRelativePath(normalizedRbd, d));
        return result;
    }

    public static bool TryGetCleanVarValue(string x, FileManagerContext ctx, Func<TagLib.File?> getFile, string replaceWith, out string res)
    {
        if (VarExtractors.TryGetValue(x, out var extractor))
        {
            var tagFile = TagVars.Contains(x) ? getFile() : null;
            string value = extractor(ctx, tagFile);
            if (NoCleanSeparatorVars.Contains(x))
                res = value;
            else if (PreserveSeparatorVars.Contains(x))
                res = value.CleanPath(replaceWith);
            else
                res = value.ReplaceInvalidChars(replaceWith);
            return true;
        }

        res = x.ReplaceInvalidChars(replaceWith);
        return false;
    }

    public static IEnumerable<string> GetAllVariableNames()
    {
        return VarExtractors.Keys;
    }

    public static string ReplaceVariables(string x, FileManagerContext ctx, TagLib.File? tagFile)
    {
        foreach (var (key, extractor) in VarExtractors)
        {
            var k = '{' + key + '}';
            if (x.Contains(k))
            {
                var val = extractor(ctx, tagFile);
                x = x.Replace(k, val);
            }
        }
        return x;
    }
}

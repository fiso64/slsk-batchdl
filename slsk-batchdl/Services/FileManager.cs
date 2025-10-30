using System.Text.RegularExpressions;

using Models;
using Enums;


public class FileManager
{
    readonly TrackListEntry tle;
    readonly HashSet<Track> organized = new();
    public string? remoteBaseDir { get; private set; }
    public string? remoteImagesCommonDir { get; private set; }
    public string? defaultFolderName { get; private set; }
    public bool downloadingAdditinalImages = false;
    private readonly Config config;

    public FileManager(TrackListEntry tle, Config config)
    {
        this.tle = tle;
        this.config = config;
    }

    public string GetSavePath(string sourceFname)
    {
        return GetSavePathNoExt(sourceFname) + Path.GetExtension(sourceFname);
    }

    public string GetSavePathNoExt(string sourceFname)
    {
        string? rcd = downloadingAdditinalImages ? remoteImagesCommonDir : remoteBaseDir;
        string parent = config.parentDir;
        string name = Utils.GetFileNameWithoutExtSlsk(sourceFname);

        if (!string.IsNullOrEmpty(tle.DefaultFolderName()))
        {
            parent = Path.Join(parent, tle.DefaultFolderName());
        }

        if (tle.source.Type == TrackType.Album && !string.IsNullOrEmpty(rcd))
        {
            string dirname = defaultFolderName != null ? defaultFolderName : Path.GetFileName(rcd);
            string normFname = Utils.NormalizedPath(sourceFname);
            string relpath = normFname.StartsWith(rcd) ? Path.GetRelativePath(rcd, normFname) : "";
            parent = Path.Join(parent, dirname, Path.GetDirectoryName(relpath) ?? "");
        }

        return Path.Join(parent, name).CleanPath(config.invalidReplaceStr);
    }

    public void SetremoteBaseDir(string? remoteBaseDir)
    {
        this.remoteBaseDir = remoteBaseDir != null ? Utils.NormalizedPath(remoteBaseDir) : null;
    }

    public void SetRemoteCommonImagesDir(string? remoteBaseDir)
    {
        this.remoteImagesCommonDir = remoteBaseDir != null ? Utils.NormalizedPath(remoteBaseDir) : null;
    }

    public void SetDefaultFolderName(string? defaultFolderName)
    {
        this.defaultFolderName = defaultFolderName != null ? Utils.NormalizedPath(defaultFolderName) : null;
    }

    public void OrganizeAlbum(Track source, List<Track> allDownloadedFiles, List<Track>? additionalImages, bool remainingOnly = true)
    {
        foreach (var track in allDownloadedFiles.Where(t => !t.IsNotAudio))
        {
            if (remainingOnly && organized.Contains(track))
                continue;

            OrganizeAudio(track, track.FirstDownload);
        }

        source.DownloadPath = Utils.GreatestCommonDirectory(allDownloadedFiles.Where(t => !t.IsNotAudio).Select(t => t.DownloadPath));

        var nonAudioToOrganize = string.IsNullOrEmpty(config.nameFormat) ? additionalImages : allDownloadedFiles.Where(t => t.IsNotAudio);

        if (nonAudioToOrganize == null || !nonAudioToOrganize.Any())
            return;

        string parent = Utils.GreatestCommonDirectory(
            allDownloadedFiles.Where(t => !t.IsNotAudio && t.State == TrackState.Downloaded && t.DownloadPath.Length > 0).Select(t => t.DownloadPath));

        foreach (var track in nonAudioToOrganize)
        {
            if (remainingOnly && organized.Contains(track))
                continue;

            OrganizeNonAudio(track, parent, additionalImages != null && additionalImages.Contains(track));
        }
    }

    public void OrganizeAudio(Track track, Soulseek.File? file)
    {
        if (track.DownloadPath.Length == 0 || !Utils.IsMusicFile(track.DownloadPath))
            return;

        if (config.nameFormat.Length == 0)
        {
            organized.Add(track);
            return;
        }

        string pathPart = ApplyNameFormat(config.nameFormat, track, file);
        string newFilePath = Path.Join(config.parentDir, pathPart + Path.GetExtension(track.DownloadPath));

        if (Utils.NormalizedPath(newFilePath) != Utils.NormalizedPath(track.DownloadPath))
        {
            try
            {
                Utils.MoveAndDeleteParent(track.DownloadPath, newFilePath, config.parentDir);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to move: {ex}");
                return;
            }

        }

        track.DownloadPath = newFilePath;

        organized.Add(track);
    }

    private void OrganizeNonAudio(Track track, string parent, bool isAdditionalImage)
    {
        if (track.DownloadPath.Length == 0)
            return;

        string? part = null;

        string? rcd = isAdditionalImage ? remoteImagesCommonDir : remoteBaseDir;

        if (rcd != null && Utils.IsInDirectory(Utils.GetDirectoryNameSlsk(track.FirstDownload.Filename), rcd, true))
        {
            part = Utils.GetFileNameSlsk(Utils.GetDirectoryNameSlsk(track.FirstDownload.Filename));
        }

        string newFilePath = Path.Join(parent, part, Path.GetFileName(track.DownloadPath));

        if (Utils.NormalizedPath(newFilePath) != Utils.NormalizedPath(track.DownloadPath))
        {
            try
            {
                Utils.MoveAndDeleteParent(track.DownloadPath, newFilePath, config.parentDir);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to move: {ex}");
                return;
            }
        }

        track.DownloadPath = newFilePath;

        organized.Add(track);
    }

    string ApplyNameFormat(string format, Track track, Soulseek.File? slfile)
    {
        TagLib.File? file = null;
        bool triedGettingFile = false;
        TagLib.File? getTagFile()
        {
            if (!triedGettingFile && file == null)
            {
                triedGettingFile = true;
                try { file = TagLib.File.Create(track.DownloadPath); }
                catch { }
            }
            return file;
        }

        return ApplyNameFormatInternal(format, config, tle, getTagFile, slfile, track, remoteBaseDir);
    }

    static string ApplyNameFormatInternal(string format, Config config, TrackListEntry tle, Func<TagLib.File?> getTagFile, Soulseek.File? slfile, Track track, string? remoteBaseDir)
    {
        string newName = format;
        var regex = new Regex(@"(\{(?:\{??[^\{]*?\}))");
        var matches = regex.Matches(newName);

        while (matches.Count > 0)
        {
            foreach (var match in matches.Cast<Match>())
            {
                string inner = match.Groups[1].Value;
                inner = inner[1..^1];

                var options = inner.Split('|');
                string? chosenOpt = null;

                foreach (var opt in options)
                {
                    string[] parts = Regex.Split(opt, @"\([^\)]*\)");
                    string[] result = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
                    if (result.All(x => TryGetCleanVarValue(x, tle, getTagFile, slfile, track, remoteBaseDir, config.invalidReplaceStr, out string res) && res.Length > 0))
                    {
                        chosenOpt = opt;
                        break;
                    }
                }

                if (chosenOpt == null)
                {
                    chosenOpt = options[^1];
                }

                chosenOpt = Regex.Replace(chosenOpt, @"\([^()]*\)|[^()]+", match =>
                {
                    if (match.Value.StartsWith("(") && match.Value.EndsWith(")"))
                        return match.Value[1..^1].ReplaceInvalidChars(config.invalidReplaceStr, removeSlash: false);
                    else
                    {
                        TryGetCleanVarValue(match.Value, tle, getTagFile, slfile, track, remoteBaseDir, config.invalidReplaceStr, out string res);
                        return res;
                    }
                });

                string old = match.Groups[1].Value;
                old = old.StartsWith("{{") ? old[1..] : old;
                newName = newName.Replace(old, chosenOpt);
            }

            matches = regex.Matches(newName);
        }

        if (newName != format)
        {
            char dirsep = Path.DirectorySeparatorChar;
            newName = newName.Replace('/', dirsep).Replace('\\', dirsep);
            var x = newName.Split(dirsep, StringSplitOptions.RemoveEmptyEntries);
            newName = string.Join(dirsep, x.Select(x => x.ReplaceInvalidChars(config.invalidReplaceStr).Trim(' ', '.')));
            return newName;
        }

        return format;
    }

    private static readonly Dictionary<string, Func<TrackListEntry, TagLib.File?, Soulseek.File?, Track, string?, string>> VarExtractors = new()
    {
        { "artist", (_, f, _, _, _) => f?.Tag.FirstPerformer ?? "" },
        { "artists", (_, f, _, _, _) => f != null ? string.Join(" & ", f.Tag.Performers) : "" },
        { "albumartist", (_, f, _, _, _) => f?.Tag.FirstAlbumArtist ?? "" },
        { "albumartists", (_, f, _, _, _) => f != null ? string.Join(" & ", f.Tag.AlbumArtists) : "" },
        { "title", (_, f, _, _, _) => f?.Tag.Title ?? "" },
        { "album", (_, f, _, _, _) => f?.Tag.Album ?? "" },
        { "year", (_, f, _, _, _) => f?.Tag.Year.ToString() ?? "" },
        { "track", (_, f, _, _, _) => f?.Tag.Track.ToString("D2") ?? "" },
        { "disc", (_, f, _, _, _) => f?.Tag.Disc.ToString() ?? "" },
        { "length", (_, f, _, _, _) => f?.Tag.Length.ToString() ?? "" },

        { "sartist", (_, _, _, t, _) => t.Artist },
        { "sartists", (_, _, _, t, _) => t.Artist },
        { "stitle", (_, _, _, t, _) => t.Title },
        { "salbum", (_, _, _, t, _) => t.Album },
        { "slength", (_, _, _, t, _) => t.Length.ToString() },
        { "uri", (_, _, _, t, _) => t.URI },
        { "url", (_, _, _, t, _) => t.URI },
        { "type", (_, _, _, t, _) => t.Type.ToString() },
        { "state", (_, _, _, t, _) => t.State.ToString() },
        { "is-audio", (_, _, _, t, _) => (!t.IsNotAudio).ToString().ToLower() },
        { "failure-reason", (_, _, _, t, _) => t.FailureReason.ToString() },
        { "artist-maybe-wrong", (_, _, _, t, _) => t.ArtistMaybeWrong.ToString().ToLower() },
        { "row", (_, _, _, t, _) => t.LineNumber.ToString() },
        { "line", (_, _, _, t, _) => t.LineNumber.ToString() },
        { "snum", (_, _, _, t, _) => t.ItemNumber.ToString() },

        { "slsk-filename", (_, _, s, _, _) => Utils.GetFileNameWithoutExtSlsk(s?.Filename ?? "") },
        { "filename", (_, _, s, _, _) => Utils.GetFileNameWithoutExtSlsk(s?.Filename ?? "") },
        { "slsk-foldername", (_, _, s, _, r) => GetFolderName(s, r) },
        { "foldername", (_, _, s, _, r) => GetFolderName(s, r) },
        { "extractor", (t, _, _, _, _) => t.config.inputType.ToString() },
        { "input", (t, _, _, _, _) => t.config.input },
        { "item-name", (t, _, _, _, _) => t.ItemNameOrSource() },
        { "default-folder", (t, _, _, _, _) => t.DefaultFolderName() },
        { "output-dir", (t, _, _, _, _) => t.config.parentDir },
        { "path", (_, _, _, t, _) => t.DownloadPath.TrimEnd('/').TrimEnd('\\') },
        { "path-noext", (_, _, _, t, _) => Path.Combine(Path.GetDirectoryName(t.DownloadPath), Path.GetFileNameWithoutExtension(t.DownloadPath)) },
        { "ext", (_, _, _, t, _) => Path.GetExtension(t.DownloadPath) },
        { "bindir", (_, _, _, _, _) => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('/').TrimEnd('\\') },
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
        string d = Path.GetDirectoryName(Utils.NormalizedPath(slfile.Filename));
        string r = Path.GetFileName(normalizedRbd);
        string result = Path.Join(r, Path.GetRelativePath(normalizedRbd, d));
        return result;
    }

    public static bool TryGetCleanVarValue(string x, TrackListEntry tle, Func<TagLib.File?> getFile, Soulseek.File? slfile, Track track, string? remoteBaseDir, string replaceWith, out string res)
    {
        if (VarExtractors.TryGetValue(x, out var extractor))
        {
            var file = TagVars.Contains(x) ? getFile() : null;
            string value = extractor(tle, file, slfile, track, remoteBaseDir);
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

    public static string ReplaceVariables(string x, TrackListEntry tle, TagLib.File? file, Soulseek.File? slfile, Track track, string? remoteBaseDir)
    {
        foreach (var (key, extractor) in VarExtractors)
        {
            var k = '{' + key + '}';
            if (x.Contains(k))
            {
                var val = extractor(tle, file, slfile, track, remoteBaseDir);
                x = x.Replace(k, val);
            }
        }

        return x;
    }

}

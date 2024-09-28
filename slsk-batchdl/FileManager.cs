using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

using Data;
using Enums;


public class FileManager
{
    readonly TrackListEntry tle;
    readonly HashSet<Track> organized = new();
    public string? remoteCommonDir { get; private set; }

    public FileManager(TrackListEntry tle)
    {
        this.tle = tle;
    }

    public string GetSavePath(string sourceFname)
    {
        return GetSavePathNoExt(sourceFname) + Path.GetExtension(sourceFname);
    }

    public string GetSavePathNoExt(string sourceFname)
    {
        string parent = Config.I.parentDir;
        string name = Utils.GetFileNameWithoutExtSlsk(sourceFname);

        if (tle.defaultFolderName != null)
        {
            parent = Path.Join(parent, tle.defaultFolderName.ReplaceInvalidChars(Config.I.invalidReplaceStr, removeSlash: false));
        } 

        if (tle.source.Type == TrackType.Album && !string.IsNullOrEmpty(remoteCommonDir))
        {
            string dirname = Path.GetFileName(remoteCommonDir);
            string normFname = Utils.NormalizedPath(sourceFname);
            string relpath = normFname.StartsWith(remoteCommonDir) ? Path.GetRelativePath(remoteCommonDir, normFname) : "";
            parent = Path.Join(parent, dirname, Path.GetDirectoryName(relpath) ?? "");
        }

        return Path.Join(parent, name);
    }

    public void SetRemoteCommonDir(string? remoteCommonDir)
    {
        this.remoteCommonDir = remoteCommonDir != null ? Utils.NormalizedPath(remoteCommonDir) : null;
    }

    public void OrganizeAlbum(List<Track> tracks, List<Track>? additionalImages, bool remainingOnly = true)
    {
        foreach (var track in tracks.Where(t => !t.IsNotAudio))
        {
            if (remainingOnly && organized.Contains(track))
                continue;

            OrganizeAudio(track, track.FirstDownload);
        }

        bool onlyAdditionalImages = Config.I.nameFormat.Length == 0;

        var nonAudioToOrganize = onlyAdditionalImages ? additionalImages : tracks.Where(t => t.IsNotAudio);

        if (nonAudioToOrganize == null || !nonAudioToOrganize.Any())
            return;

        string parent = Utils.GreatestCommonDirectory(
            tracks.Where(t => !t.IsNotAudio && t.State == TrackState.Downloaded && t.DownloadPath.Length > 0).Select(t => t.DownloadPath));

        foreach (var track in nonAudioToOrganize)
        {
            if (remainingOnly && organized.Contains(track))
                continue;

            OrganizeNonAudio(track, parent);
        }
    }

    public void OrganizeAudio(Track track, Soulseek.File? file)
    {
        if (track.DownloadPath.Length == 0 || !Utils.IsMusicFile(track.DownloadPath))
            return;

        if (Config.I.nameFormat.Length == 0)
        {
            organized.Add(track);
            return;
        }

        string pathPart = SubstituteValues(Config.I.nameFormat, track, file);
        string newFilePath = Path.Join(Config.I.parentDir, pathPart + Path.GetExtension(track.DownloadPath));

        try
        {
            MoveAndDeleteParent(track.DownloadPath, newFilePath);
        }
        catch (Exception ex)
        {
            Printing.WriteLine($"\nFailed to move: {ex.Message}\n", ConsoleColor.DarkYellow, true);
            return;
        }

        track.DownloadPath = newFilePath;

        organized.Add(track);
    }

    public void OrganizeNonAudio(Track track, string parent)
    {
        if (track.DownloadPath.Length == 0)
            return;

        string newFilePath = Path.Join(parent, Path.GetFileName(track.DownloadPath));

        try
        {
            MoveAndDeleteParent(track.DownloadPath, newFilePath);
        }
        catch (Exception ex)
        {
            Printing.WriteLine($"\nFailed to move: {ex.Message}\n", ConsoleColor.DarkYellow, true);
            return;
        }

        track.DownloadPath = newFilePath;

        organized.Add(track);
    }

    static void MoveAndDeleteParent(string oldPath, string newPath)
    {
        if (Utils.NormalizedPath(oldPath) != Utils.NormalizedPath(newPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath));
            Utils.Move(oldPath, newPath);
            Utils.DeleteAncestorsIfEmpty(Path.GetDirectoryName(oldPath), Config.I.parentDir);
        }
    }
         
    string SubstituteValues(string format, Track track, Soulseek.File? slfile)
    {
        string newName = format;
        TagLib.File? file = null;

        try { file = TagLib.File.Create(track.DownloadPath); }
        catch { }

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
                    if (result.All(x => TryGetVarValue(x, file, slfile, track, out string res) && res.Length > 0))
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
                        return match.Value[1..^1].ReplaceInvalidChars(Config.I.invalidReplaceStr, removeSlash: false);
                    else
                    {
                        TryGetVarValue(match.Value, file, slfile, track, out string res);
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
            newName = string.Join(dirsep, x.Select(x => x.ReplaceInvalidChars(Config.I.invalidReplaceStr).Trim(' ', '.')));
            return newName;
        }

        return format;
    }

    bool TryGetVarValue(string x, TagLib.File? file, Soulseek.File? slfile, Track track, out string res)
    {
        switch (x)
        {
            case "artist":
                res = file?.Tag.FirstPerformer ?? ""; break;
            case "artists":
                res = file != null ? string.Join(" & ", file.Tag.Performers) : ""; break;
            case "albumartist":
                res = file?.Tag.FirstAlbumArtist ?? ""; break;
            case "albumartists":
                res = file != null ? string.Join(" & ", file.Tag.AlbumArtists) : ""; break;
            case "title":
                res = file?.Tag.Title ?? ""; break;
            case "album":
                res = file?.Tag.Album ?? ""; break;
            case "sartist":
            case "sartists":
                res = track.Artist; break;
            case "stitle":
                res = track.Title; break;
            case "salbum":
                res = track.Album; break;
            case "year":
                res = file?.Tag.Year.ToString() ?? ""; break;
            case "track":
                res = file?.Tag.Track.ToString("D2") ?? ""; break;
            case "disc":
                res = file?.Tag.Disc.ToString() ?? ""; break;
            case "filename":
                res = Utils.GetFileNameWithoutExtSlsk(slfile?.Filename ?? ""); break;
            case "foldername":
                if (string.IsNullOrEmpty(remoteCommonDir) || slfile == null)
                {
                    if (!string.IsNullOrEmpty(remoteCommonDir))
                        res = Path.GetFileName(Utils.NormalizedPath(remoteCommonDir));
                    else
                        res = Path.GetDirectoryName(Utils.NormalizedPath(slfile.Filename));
                }
                else
                {
                    string d = Path.GetDirectoryName(Utils.NormalizedPath(slfile.Filename));
                    string r = Path.GetFileName(remoteCommonDir);
                    res = Path.Join(r, Path.GetRelativePath(remoteCommonDir, d));
                }
                return true;
            case "extractor":
                res = Config.I.inputType.ToString(); break;
            case "default-folder":
                res = tle.defaultFolderName ?? tle.source.ToString(false); break;
            default:
                res = x; return false;
        }

        res = res.ReplaceInvalidChars(Config.I.invalidReplaceStr);
        return true;
    }
}


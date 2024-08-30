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
    readonly TrackListEntry? tle;
    readonly HashSet<Track> organized = new();
    string? remoteCommonDir;

    public FileManager() { }

    public FileManager(TrackListEntry tle)
    {
        this.tle = tle;
    }

    public string GetSavePath(string sourceFname)
    {
        return $"{GetSavePathNoExt(sourceFname)}{Path.GetExtension(sourceFname)}";
    }

    public string GetSavePathNoExt(string sourceFname)
    {
        string name = Utils.GetFileNameWithoutExtSlsk(sourceFname);
        name = name.ReplaceInvalidChars(Config.invalidReplaceStr);

        string parent = Config.outputFolder;

        if (tle != null && tle.placeInSubdir && remoteCommonDir != null)
        {
            string dirname = Path.GetFileName(remoteCommonDir);
            string relpath = Path.GetRelativePath(remoteCommonDir, Utils.NormalizedPath(sourceFname));
            parent = Path.Join(parent, dirname, Path.GetDirectoryName(relpath));
        }

        return Path.Join(parent, name);
    }

    public void SetRemoteCommonDir(string? remoteCommonDir)
    {
        this.remoteCommonDir = remoteCommonDir != null ? Utils.NormalizedPath(remoteCommonDir) : null;
    }

    public void OrganizeAlbum(List<Track> tracks, List<Track>? additionalImages, bool remainingOnly = true)
    {
        if (tle == null)
            throw new NullReferenceException("TrackListEntry should not be null.");

        string outputFolder = Config.outputFolder;

        foreach (var track in tracks.Where(t => !t.IsNotAudio))
        {
            if (remainingOnly && organized.Contains(track))
                continue;

            OrganizeAudio(tle, track, track.FirstDownload);
        }

        bool onlyAdditionalImages = Config.nameFormat.Length == 0;

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

    public void OrganizeAudio(TrackListEntry tle, Track track, Soulseek.File? file)
    {
        if (track.DownloadPath.Length == 0 || !Utils.IsMusicFile(track.DownloadPath))
        {
            return;
        }
        else if (Config.nameFormat.Length == 0)
        {
            organized.Add(track);
            return;
        }

        string pathPart = SubstituteValues(Config.nameFormat, track, file);
        string newFilePath = Path.Join(Config.outputFolder, pathPart + Path.GetExtension(track.DownloadPath));

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

            string x = Utils.NormalizedPath(Path.GetFullPath(Config.outputFolder));
            string y = Utils.NormalizedPath(Path.GetDirectoryName(oldPath));

            while (x.Length > 0 && y.StartsWith(x + '/') && Utils.FileCountRecursive(y) == 0)
            {
                Directory.Delete(y, true); // hopefully this is fine
                y = Utils.NormalizedPath(Path.GetDirectoryName(y));
            }
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
                string chosenOpt = "";

                foreach (var opt in options)
                {
                    string[] parts = Regex.Split(opt, @"\([^\)]*\)");
                    string[] result = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
                    if (result.All(x => GetVarValue(x, file, slfile, track).Length > 0))
                    {
                        chosenOpt = opt;
                        break;
                    }
                }

                chosenOpt = Regex.Replace(chosenOpt, @"\([^()]*\)|[^()]+", match =>
                {
                    if (match.Value.StartsWith("(") && match.Value.EndsWith(")"))
                        return match.Value[1..^1].ReplaceInvalidChars(Config.invalidReplaceStr, removeSlash: false);
                    else
                        return GetVarValue(match.Value, file, slfile, track);
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
            newName = string.Join(dirsep, x.Select(x => x.ReplaceInvalidChars(Config.invalidReplaceStr).Trim(' ', '.')));
            return newName;
        }

        return format;
    }

    string GetVarValue(string x, TagLib.File? file, Soulseek.File? slfile, Track track)
    {
        string res;

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
                if (remoteCommonDir ==  null || slfile == null)
                {
                    return Utils.GetBaseNameSlsk(Utils.GetDirectoryNameSlsk(slfile?.Filename ?? ""));
                }
                else
                {
                    string d = Path.GetDirectoryName(Utils.NormalizedPath(slfile.Filename));
                    string r = Path.GetFileName(remoteCommonDir);
                    return Path.Join(r, Path.GetRelativePath(remoteCommonDir, d));
                }
            case "default-foldername":
                res = Config.defaultFolderName; break;
            case "extractor":
                res = Config.inputType.ToString(); break;
            default:
                res = ""; break;
        }

        return res.ReplaceInvalidChars(Config.invalidReplaceStr);
    }
}


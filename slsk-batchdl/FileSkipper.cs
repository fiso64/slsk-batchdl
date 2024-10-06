
using Models;
using Enums;

namespace FileSkippers
{
    public static class FileSkipperRegistry
    {
        public static FileSkipper GetSkipper(SkipMode mode, string dir, FileConditions conditions, M3uEditor m3uEditor)
        {
            bool noConditions = conditions.Equals(new FileConditions());
            return mode switch
            {
                SkipMode.Name => new NameSkipper(dir),
                SkipMode.NameCond => noConditions ? new NameSkipper(dir) : new NameConditionalSkipper(dir, conditions),
                SkipMode.Tag => new TagSkipper(dir),
                SkipMode.TagCond => noConditions ? new TagSkipper(dir) : new TagConditionalSkipper(dir, conditions),
                SkipMode.M3u => new M3uSkipper(m3uEditor, false),
                SkipMode.M3uCond => noConditions ? new M3uSkipper(m3uEditor, true) : new M3uConditionalSkipper(m3uEditor, conditions),
            };
        }
    }

    public abstract class FileSkipper
    {
        public abstract bool TrackExists(Track track, out string? foundPath);
        public virtual void BuildIndex() { IndexIsBuilt = true; }
        public bool IndexIsBuilt { get; protected set; } = false;
    }

    public class NameSkipper : FileSkipper
    {
        readonly string[] ignore = new string[] { "_", "-", ".", "(", ")", "[", "]" };
        readonly string dir;
        readonly List<(string, string, string)> index = new(); // (Path, PreprocessedPath, PreprocessedName)

        public NameSkipper(string dir)
        {
            this.dir = dir;
        }

        private string Preprocess(string s, bool removeSlash)
        {
            s = s.ToLower();
            s = s.RemoveFt();
            s = s.Replace(ignore, " ");
            s = s.ReplaceInvalidChars(' ', false, removeSlash);
            s = s.RemoveConsecutiveWs();
            return s;
        }

        public override void BuildIndex()
        {
            if (!Directory.Exists(dir))
            {
                IndexIsBuilt = true;
                return;
            }

            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);

            int removeLen = Preprocess(dir, false).Length + 1;

            foreach (var path in files)
            {
                if (Utils.IsMusicFile(path))
                {
                    string ppath = Preprocess(path[removeLen..path.LastIndexOf('.')], false);
                    string pname = Path.GetFileName(ppath);
                    index.Add((path, ppath, pname));
                }
            }

            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, out string? foundPath)
        {
            foundPath = null;

            if (track.OutputsDirectory)
                return false;

            string title = Preprocess(track.Title, true);
            string artist = Preprocess(track.Artist, true);

            foreach ((var path, var ppath, var pname) in index)
            {
                if (pname.ContainsWithBoundary(title) && ppath.ContainsWithBoundary(artist))
                {
                    foundPath = path;
                    return true;
                }
            }

            foundPath = null;
            return false;
        }
    }

    public class NameConditionalSkipper : FileSkipper
    {
        readonly string[] ignore = new string[] { "_", "-", ".", "(", ")", "[", "]" };
        readonly string dir;
        readonly List<(string, string, SimpleFile)> index = new(); // (PreprocessedPath, PreprocessedName, file)
        FileConditions conditions;

        public NameConditionalSkipper(string dir, FileConditions conditions)
        {
            this.dir = dir;
            this.conditions = conditions;
        }

        private string Preprocess(string s, bool removeSlash)
        {
            s = s.ToLower();
            s = s.RemoveFt();
            s = s.Replace(ignore, " ");
            s = s.ReplaceInvalidChars(' ', false, removeSlash);
            s = s.RemoveConsecutiveWs();
            return s;
        }

        public override void BuildIndex()
        {
            if (!Directory.Exists(dir))
            {
                IndexIsBuilt = true;
                return;
            }

            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);

            int removeLen = Preprocess(dir, false).Length + 1;

            foreach (var path in files)
            {
                if (Utils.IsMusicFile(path))
                {
                    TagLib.File musicFile;
                    try { musicFile = TagLib.File.Create(path); }
                    catch { continue; }

                    string ppath = Preprocess(path[..path.LastIndexOf('.')], false)[removeLen..];
                    string pname = Path.GetFileName(ppath);
                    index.Add((ppath, pname, new SimpleFile(musicFile)));
                }
            }

            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, out string? foundPath)
        {
            foundPath = null;

            if (track.OutputsDirectory)
                return false;

            string title = Preprocess(track.Title, true);
            string artist = Preprocess(track.Artist, true);

            foreach ((var ppath, var pname, var musicFile) in index)
            {
                if (pname.ContainsWithBoundary(title) && ppath.ContainsWithBoundary(artist) && conditions.FileSatisfies(musicFile, track))
                {
                    foundPath = musicFile.Path;
                    return true;
                }
            }

            foundPath = null;
            return false;
        }
    }

    public class TagSkipper : FileSkipper
    {
        readonly string dir;
        readonly List<(string, string, string)> index = new(); // (Path, PreprocessedArtist, PreprocessedTitle)

        public TagSkipper(string dir)
        {
            this.dir = dir;
        }

        private string Preprocess(string s)
        {
            return s.RemoveFt().Replace(" ", "").ToLower();
        }

        public override void BuildIndex()
        {
            if (!Directory.Exists(dir))
            {
                IndexIsBuilt = true;
                return;
            }

            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);

            foreach (var path in files)
            {
                if (Utils.IsMusicFile(path))
                {
                    TagLib.File musicFile;
                    try { musicFile = TagLib.File.Create(path); }
                    catch { continue; }

                    string partist = Preprocess(musicFile.Tag.JoinedPerformers ?? "");
                    string ptitle = Preprocess(musicFile.Tag.Title ?? "");
                    index.Add((path, partist, ptitle));
                }
            }

            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, out string? foundPath)
        {
            foundPath = null;

            if (track.OutputsDirectory)
                return false;

            string title = Preprocess(track.Title);
            string artist = Preprocess(track.Artist);

            foreach ((var path, var partist, var ptitle) in index)
            {
                if (title==ptitle && partist.Contains(artist))
                {
                    foundPath = path;
                    return true;
                }
            }

            return false;
        }
    }

    public class TagConditionalSkipper : FileSkipper
    {
        readonly string dir;
        readonly List<(string, string, SimpleFile)> index = new(); // (PreprocessedArtist, PreprocessedTitle, file)
        FileConditions conditions;

        public TagConditionalSkipper(string dir, FileConditions conditions)
        {
            this.dir = dir;
            this.conditions = conditions;
        }

        private string Preprocess(string s)
        {
            return s.RemoveFt().Replace(" ", "").ToLower();
        }

        public override void BuildIndex()
        {
            if (!Directory.Exists(dir))
            {
                IndexIsBuilt = true;
                return;
            }

            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);

            foreach (var path in files)
            {
                if (Utils.IsMusicFile(path))
                {
                    TagLib.File musicFile;
                    try { musicFile = TagLib.File.Create(path); }
                    catch { continue; }

                    string partist = Preprocess(musicFile.Tag.JoinedPerformers ?? "");
                    string ptitle = Preprocess(musicFile.Tag.Title ?? "");
                    index.Add((partist, ptitle, new SimpleFile(musicFile)));
                }
            }

            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, out string? foundPath)
        {
            foundPath = null;

            if (track.OutputsDirectory)
                return false;

            string title = Preprocess(track.Title);
            string artist = Preprocess(track.Artist);

            foreach ((var partist, var ptitle, var musicFile) in index)
            {
                if (title == ptitle && partist.Contains(artist) && conditions.FileSatisfies(musicFile, track))
                {
                    foundPath = musicFile.Path;
                    return true;
                }
            }

            return false;
        }
    }

    public class M3uSkipper : FileSkipper
    {
        M3uEditor m3uEditor;
        bool checkFileExists;
        
        public M3uSkipper(M3uEditor m3UEditor, bool checkFileExists) 
        {
            this.m3uEditor = m3UEditor;
            this.checkFileExists = checkFileExists;
            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, out string? foundPath)
        {
            foundPath = null;
            var t = m3uEditor.PreviousRunResult(track);
            if (t != null && (t.State == TrackState.Downloaded || t.State == TrackState.AlreadyExists))
            {
                if (checkFileExists)
                {
                    if (t.DownloadPath.Length == 0)
                        return false;

                    if (t.OutputsDirectory)
                    {
                        if (!Directory.Exists(t.DownloadPath))
                            return false;
                    }
                    else
                    {
                        if (!File.Exists(t.DownloadPath))
                            return false;
                    }
                }

                foundPath = t.DownloadPath;
                return true;
            }
            return false;
        }
    }

    public class M3uConditionalSkipper : FileSkipper
    {
        M3uEditor m3uEditor;
        FileConditions conditions;

        public M3uConditionalSkipper(M3uEditor m3UEditor, FileConditions conditions)
        {
            this.m3uEditor = m3UEditor;
            this.conditions = conditions;
            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, out string? foundPath)
        {
            foundPath = null;
            var t = m3uEditor.PreviousRunResult(track);

            if (t == null || t.DownloadPath.Length == 0)
                return false;

            if (!t.OutputsDirectory)
            {
                if (!File.Exists(t.DownloadPath))
                    return false;

                TagLib.File musicFile;
                try 
                { 
                    musicFile = TagLib.File.Create(t.DownloadPath);
                    if (conditions.FileSatisfies(musicFile, track, false))
                    {
                        foundPath = t.DownloadPath;
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                catch 
                { 
                    return false;
                }
            }
            else
            {
                if (!Directory.Exists(t.DownloadPath))
                    return false;

                var files = Directory.GetFiles(t.DownloadPath, "*", SearchOption.AllDirectories);

                if (t.MaxAlbumTrackCount > -1 || t.MinAlbumTrackCount > -1)
                {
                    int count = files.Count(x=> Utils.IsMusicFile(x));

                    if (t.MaxAlbumTrackCount > -1 && count > t.MaxAlbumTrackCount)
                        return false;
                    if (t.MinAlbumTrackCount > -1 && count < t.MinAlbumTrackCount)
                        return false;
                }

                foreach (var path in files)
                {
                    if (Utils.IsMusicFile(path))
                    {
                        TagLib.File musicFile;
                        try { musicFile = TagLib.File.Create(path); }
                        catch { return false; }

                        if (!conditions.FileSatisfies(musicFile, track))
                            return false;
                    }
                }

                foundPath = t.DownloadPath;
                return true;
            }
        }
    }
}
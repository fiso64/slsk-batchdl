
using Models;
using Enums;

namespace Services
{
    public static class FileSkipperRegistry
    {
        public static FileSkipper GetSkipper(SkipMode mode, string dir, bool useConditions)
        {
            return mode switch
            {
                SkipMode.Name => useConditions ? new NameConditionalSkipper(dir) : new NameSkipper(dir),
                SkipMode.Tag => useConditions ? new TagConditionalSkipper(dir) : new TagSkipper(dir),
                SkipMode.Index => useConditions ? new IndexConditionalSkipper() : new IndexSkipper(),
                _ => throw new ArgumentException("Invalid SkipMode")
            };
        }
    }

    public struct FileSkipperContext
    {
        public FileConditions? conditions;
        public M3uEditor? indexEditor;
        public bool checkFileExists;

        public static FileSkipperContext FromTrackListEntry(TrackListEntry tle)
        {
            FileConditions? cond = null;
            if (tle.config.skipCheckPrefCond)
                cond = tle.config.necessaryCond.With(tle.config.preferredCond);
            else if (tle.config.skipCheckCond)
                cond = tle.config.necessaryCond;

            var context = new FileSkipperContext
            {
                checkFileExists = cond != null,
                indexEditor = tle.indexEditor,
                conditions = cond,
            };

            return context;
        }
    }

    public abstract class FileSkipper
    {
        public abstract bool TrackExists(Track track, FileSkipperContext context, out string? foundPath);
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

        private string Preprocess(string s, bool removeSlash, bool isQuery)
        {
            s = s.ToLower();
            if (isQuery) s = s.RemoveFt();
            s = s.Replace(ignore, " ");
            s = s.ReplaceInvalidChars(' ', false, removeSlash);
            s = s.RemoveConsecutiveWs().Trim();
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

            int removeLen = Preprocess(dir, false, false).Length + 1;

            foreach (var path in files)
            {
                if (Utils.IsMusicFile(path))
                {
                    string ppath = Preprocess(path[removeLen..path.LastIndexOf('.')], false, false);
                    string pname = Path.GetFileName(ppath);
                    index.Add((path, ppath, pname));
                }
            }

            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, FileSkipperContext context, out string? foundPath)
        {
            foundPath = null;

            if (track.OutputsDirectory)
                return false;

            string title = Preprocess(track.Title, true, true);
            string artist = Preprocess(track.Artist, true, true);

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

        public NameConditionalSkipper(string dir)
        {
            this.dir = dir;
        }

        private string Preprocess(string s, bool removeSlash, bool isQuery)
        {
            s = s.ToLower();
            if (isQuery) s = s.RemoveFt();
            s = s.Replace(ignore, " ");
            s = s.ReplaceInvalidChars(' ', false, removeSlash);
            s = s.RemoveConsecutiveWs().Trim();
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

            int removeLen = Preprocess(dir, false, false).Length + 1;

            foreach (var path in files)
            {
                if (Utils.IsMusicFile(path))
                {
                    TagLib.File musicFile;
                    try { musicFile = TagLib.File.Create(path); }
                    catch { continue; }

                    string ppath = Preprocess(path[..path.LastIndexOf('.')], false, false)[removeLen..];
                    string pname = Path.GetFileName(ppath);
                    index.Add((ppath, pname, new SimpleFile(musicFile)));
                }
            }

            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, FileSkipperContext context, out string? foundPath)
        {
            foundPath = null;

            if (track.OutputsDirectory)
                return false;

            string title = Preprocess(track.Title, true, true);
            string artist = Preprocess(track.Artist, true, true);

            foreach ((var ppath, var pname, var musicFile) in index)
            {
                if (pname.ContainsWithBoundary(title) && ppath.ContainsWithBoundary(artist) && context.conditions.FileSatisfies(musicFile, track))
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

        private string Preprocess(string s, bool isQuery)
        {
            if (isQuery) s = s.RemoveFt();
            return s.Replace(" ", "").ToLower();
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

                    string partist = Preprocess(musicFile.Tag.JoinedPerformers ?? "", false);
                    string ptitle = Preprocess(musicFile.Tag.Title ?? "", false);
                    index.Add((path, partist, ptitle));
                }
            }

            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, FileSkipperContext context, out string? foundPath)
        {
            foundPath = null;

            if (track.OutputsDirectory)
                return false;

            string title = Preprocess(track.Title, true);
            string artist = Preprocess(track.Artist, true);

            foreach ((var path, var partist, var ptitle) in index)
            {
                if (title == ptitle && partist.Contains(artist))
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

        public TagConditionalSkipper(string dir)
        {
            this.dir = dir;
        }

        private string Preprocess(string s, bool isQuery)
        {
            if (isQuery) s = s.RemoveFt();
            return s.Replace(" ", "").ToLower();
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

                    string partist = Preprocess(musicFile.Tag.JoinedPerformers ?? "", false);
                    string ptitle = Preprocess(musicFile.Tag.Title ?? "", false);
                    index.Add((partist, ptitle, new SimpleFile(musicFile)));
                }
            }

            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, FileSkipperContext context, out string? foundPath)
        {
            foundPath = null;

            if (track.OutputsDirectory)
                return false;

            string title = Preprocess(track.Title, true);
            string artist = Preprocess(track.Artist, true);

            foreach ((var partist, var ptitle, var musicFile) in index)
            {
                if (title == ptitle && partist.Contains(artist) && context.conditions.FileSatisfies(musicFile, track))
                {
                    foundPath = musicFile.Path;
                    return true;
                }
            }

            return false;
        }
    }

    public class IndexSkipper : FileSkipper
    {
        public IndexSkipper()
        {
            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, FileSkipperContext context, out string? foundPath)
        {
            foundPath = null;
            var t = context.indexEditor.PreviousRunResult(track);
            if (t != null && (t.State == TrackState.Downloaded || t.State == TrackState.AlreadyExists))
            {
                if (context.checkFileExists)
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

    public class IndexConditionalSkipper : FileSkipper
    {
        public IndexConditionalSkipper()
        {
            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, FileSkipperContext context, out string? foundPath)
        {
            foundPath = null;
            var t = context.indexEditor.PreviousRunResult(track);

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
                    if (context.conditions.FileSatisfies(musicFile, track, false))
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
                    int count = files.Count(x => Utils.IsMusicFile(x));

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

                        if (!context.conditions.FileSatisfies(musicFile, track))
                            return false;
                    }
                }

                foundPath = t.DownloadPath;
                return true;
            }
        }
    }
}
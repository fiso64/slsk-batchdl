
using Models;
using Enums;

namespace Services
{
    public static class TrackSkipperRegistry
    {
        public static TrackSkipper GetSkipper(SkipMode mode, string dir, bool useConditions)
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

    public struct TrackSkipperContext
    {
        public FileConditions? conditions;
        public M3uEditor? indexEditor;
        public bool checkFileExists;

        public static TrackSkipperContext FromTrackListEntry(TrackListEntry tle)
        {
            FileConditions? cond = null;
            if (tle.config.skipCheckPrefCond)
                cond = tle.config.necessaryCond.With(tle.config.preferredCond);
            else if (tle.config.skipCheckCond)
                cond = tle.config.necessaryCond;

            var context = new TrackSkipperContext
            {
                checkFileExists = cond != null,
                indexEditor = tle.indexEditor,
                conditions = cond,
            };

            return context;
        }
    }

    public abstract class TrackSkipper
    {
        public abstract bool TrackExists(Track track, TrackSkipperContext context, out string? foundPath);
        public virtual void BuildIndex() { IndexIsBuilt = true; }
        public bool IndexIsBuilt { get; protected set; } = false;
    }

    public abstract class FileBasedSkipper<T> : TrackSkipper
    {
        protected abstract IEnumerable<(string path, T item)> Index { get; }
        protected abstract string Preprocess(string s, bool removeSlash, bool isQuery);
        protected abstract bool FileMatchesTrack(string path, string artist, string title, T item, TrackSkipperContext context, Track track);
        protected abstract bool DirectoryMatchesAlbum(string directory, string? albumArtist, string album, T item, TrackSkipperContext context, Track track);

        public override bool TrackExists(Track track, TrackSkipperContext context, out string? foundPath)
        {
            foundPath = null;

            string title = Preprocess(track.Title, true, true);
            string artist = Preprocess(track.Artist, true, true);

            if (track.OutputsDirectory)
            {
                string? albumArtist = track.Title.Length > 0 ? null : track.Artist;
                string album = Preprocess(track.Album, true, true);
                var parents = new HashSet<string>();

                foreach ((var path, T item) in Index)
                {
                    if (track.Title.Length > 0 && !FileMatchesTrack(path, artist, title, item, context, track))
                        continue;

                    var parent = Path.GetDirectoryName(path);
                    if (!parents.Contains(parent) && (track.Album.Length == 0 || DirectoryMatchesAlbum(parent, albumArtist, album, item, context, track)))
                    {
                        if (DirectoryHasGoodCount(parent, track.MinAlbumTrackCount, track.MaxAlbumTrackCount))
                        {
                            foundPath = parent;
                            return true;
                        }
                        parents.Add(parent);
                    }
                }
            }
            else
            {
                foreach ((var path, T item) in Index)
                {
                    if (FileMatchesTrack(path, artist, title, item, context, track))
                    {
                        foundPath = path;
                        return true;
                    }
                }
            }

            foundPath = null;
            return false;
        }

        public static bool DirectoryHasGoodCount(string dir, int min = -1, int max = -1)
        {
            if (min <= 0 && max == -1)
                return true;
            var count = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Count(x => Utils.IsMusicFile(x));
            return count >= min && (max == -1 || count <= max);
        }
    }

    public class NameSkipper : FileBasedSkipper<(string ppath, string pname)>  // (preprocessed filepath, preprocessed filename)
    {
        private readonly List<(string path, (string ppath, string pname) item)> index = new();
        protected override IEnumerable<(string path, (string ppath, string pname) item)> Index => index;
        readonly string[] ignore = new string[] { "_", "-", ".", "(", ")", "[", "]" };
        readonly string dir;

        public NameSkipper(string dir)
        {
            this.dir = dir;
        }

        protected override string Preprocess(string s, bool removeSlash, bool isQuery)
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
                    index.Add((path, (ppath, pname)));
                }
            }

            IndexIsBuilt = true;
        }

        protected override bool FileMatchesTrack(string path, string artist, string title, (string ppath, string pname) item, TrackSkipperContext c, Track t)
        {
            return item.pname.ContainsWithBoundary(title) && item.ppath.ContainsWithBoundary(artist);
        }

        protected override bool DirectoryMatchesAlbum(string dir, string? albumArtist, string album, (string ppath, string pname) item, TrackSkipperContext c, Track t)
        {
            return item.ppath.ContainsWithBoundary(album) && item.ppath.ContainsWithBoundary(albumArtist);
        }
    }

    public class NameConditionalSkipper : FileBasedSkipper<(string ppath, string pname, SimpleFile file)> // (preprocessed path, preprocessed name, file)
    {
        private readonly Dictionary<string, List<(string path, (string ppath, string pname, SimpleFile file) item)>> index = new(); // parent to files map
        protected override IEnumerable<(string path, (string ppath, string pname, SimpleFile file) item)> Index => index.Values.SelectMany(x => x);
        readonly string[] ignore = new string[] { "_", "-", ".", "(", ")", "[", "]" };
        readonly string dir;

        public NameConditionalSkipper(string dir)
        {
            this.dir = dir;
        }

        protected override string Preprocess(string s, bool removeSlash, bool isQuery)
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

                    var parent = Utils.NormalizedPath(Path.GetDirectoryName(path));

                    if (!index.ContainsKey(parent))
                        index[parent] = new() { (path, (ppath, pname, new SimpleFile(musicFile))) };
                    else
                        index[parent].Add((path, (ppath, pname, new SimpleFile(musicFile))));
                }
            }

            IndexIsBuilt = true;
        }

        protected override bool FileMatchesTrack(string path, string artist, string title, (string ppath, string pname, SimpleFile file) item, TrackSkipperContext context, Track track)
        {
            return item.pname.ContainsWithBoundary(title)
                && item.ppath.ContainsWithBoundary(artist)
                && (context.conditions == null || context.conditions.FileSatisfies(item.file, track));
        }

        protected override bool DirectoryMatchesAlbum(string dir, string? albumArtist, string album, (string ppath, string pname, SimpleFile file) item, TrackSkipperContext context, Track track)
        {
            if (!item.ppath.ContainsWithBoundary(album) || !item.ppath.ContainsWithBoundary(albumArtist))
                return false;
            if (context.conditions == null)
                return true;

            var parent = Utils.NormalizedPath(Path.GetDirectoryName(item.file.Path));

            foreach (var x in index[parent])
            {
                if (!context.conditions.FileSatisfies(x.item.file, track))
                    return false;
            }

            return true;
        }
    }

    public class TagSkipper : FileBasedSkipper<(string partist, string ptitle, string palbum, string palbumArtist)>  // preprocessed strings
    {
        readonly string dir;
        readonly List<(string path, (string partist, string ptitle, string palbum, string palbumArtist) item)> index = new();
        protected override IEnumerable<(string path, (string partist, string ptitle, string palbum, string palbumArtist) item)> Index => index;

        public TagSkipper(string dir)
        {
            this.dir = dir;
        }

        protected override string Preprocess(string s, bool _, bool isQuery)
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

                    string partist = Preprocess(musicFile.Tag.JoinedPerformers ?? "", false, false);
                    string ptitle = Preprocess(musicFile.Tag.Title ?? "", false, false);
                    string palbum = Preprocess(musicFile.Tag.Album ?? "", false, false);
                    string palbumArtist = Preprocess(musicFile.Tag.JoinedAlbumArtists ?? "", false, false);
                    index.Add((path, (partist, ptitle, palbum, palbumArtist)));
                }
            }

            IndexIsBuilt = true;
        }

        protected override bool FileMatchesTrack(string path, string artist, string title, (string partist, string ptitle, string palbum, string palbumArtist) item, TrackSkipperContext c, Track t)
        {
            return title == item.ptitle && item.partist.Contains(artist);
        }

        protected override bool DirectoryMatchesAlbum(string dir, string? albumArtist, string album, (string partist, string ptitle, string palbum, string palbumArtist) item, TrackSkipperContext c, Track t)
        {
            return album == item.palbum && (albumArtist == null || item.palbumArtist.Contains(albumArtist));
        }
    }

    public class TagConditionalSkipper : FileBasedSkipper<(string partist, string ptitle, string palbum, string palbumArtist, SimpleFile file)> // todo: too long
    {
        readonly string dir;
        private readonly Dictionary<string, List<(string path, (string partist, string ptitle, string palbum, string palbumArtist, SimpleFile file) item)>> index = new(); // parent to files map
        protected override IEnumerable<(string path, (string partist, string ptitle, string palbum, string palbumArtist, SimpleFile file) item)> Index => index.Values.SelectMany(x => x);

        public TagConditionalSkipper(string dir)
        {
            this.dir = dir;
        }

        protected override string Preprocess(string s, bool _, bool isQuery)
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

                    string partist = Preprocess(musicFile.Tag.JoinedPerformers ?? "", false, false);
                    string ptitle = Preprocess(musicFile.Tag.Title ?? "", false, false);
                    string palbum = Preprocess(musicFile.Tag.Album ?? "", false, false);
                    string palbumArtist = Preprocess(musicFile.Tag.JoinedAlbumArtists ?? "", false, false);

                    var parent = Utils.NormalizedPath(Path.GetDirectoryName(path));

                    if (!index.ContainsKey(parent))
                        index[parent] = new() { (path, (partist, ptitle, palbum, palbumArtist, new SimpleFile(musicFile))) };
                    else
                        index[parent].Add((path, (partist, ptitle, palbum, palbumArtist, new SimpleFile(musicFile))));
                }
            }

            IndexIsBuilt = true;
        }

        protected override bool FileMatchesTrack(string path, string artist, string title, (string partist, string ptitle, string palbum, string palbumArtist, SimpleFile file) item, TrackSkipperContext c, Track t)
        {
            return title == item.ptitle && item.partist.Contains(artist);
        }

        protected override bool DirectoryMatchesAlbum(string dir, string? albumArtist, string album, (string partist, string ptitle, string palbum, string palbumArtist, SimpleFile file) item, TrackSkipperContext c, Track t)
        {
            if (album != item.palbum)
                return false;
            if (albumArtist != null && !item.palbumArtist.Contains(albumArtist))
                return false;
            if (c.conditions == null)
                return true;

            var parent = Utils.NormalizedPath(Path.GetDirectoryName(item.file.Path));

            foreach (var x in index[parent])
            {
                if (!c.conditions.FileSatisfies(x.item.file, t))
                    return false;
            }

            return true;
        }
    }

    public class IndexSkipper : TrackSkipper
    {
        public IndexSkipper()
        {
            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, TrackSkipperContext context, out string? foundPath)
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
                        if (t.DownloadPath.Length != 0 && !Directory.Exists(t.DownloadPath))
                            return false;
                    }
                    else
                    {
                        if (t.DownloadPath.Length != 0 && !File.Exists(t.DownloadPath))
                            return false;
                    }
                }

                foundPath = t.DownloadPath;
                return true;
            }
            return false;
        }
    }

    public class IndexConditionalSkipper : TrackSkipper
    {
        public IndexConditionalSkipper()
        {
            IndexIsBuilt = true;
        }

        public override bool TrackExists(Track track, TrackSkipperContext context, out string? foundPath)
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
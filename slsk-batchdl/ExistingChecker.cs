
using Data;
using Enums;
using System.IO;

namespace ExistingCheckers
{
    public static class Registry
    {
        static IExistingChecker GetChecker(SkipMode mode, string dir, FileConditions conditions, M3uEditor m3uEditor)
        {
            bool noConditions = conditions.Equals(new FileConditions());
            return mode switch
            {
                SkipMode.Name => new NameExistingChecker(dir),
                SkipMode.NameCond => noConditions ? new NameExistingChecker(dir) : new NameConditionExistingChecker(dir, conditions),
                SkipMode.Tag => new TagExistingChecker(dir),
                SkipMode.TagCond => noConditions ? new TagExistingChecker(dir) : new TagConditionExistingChecker(dir, conditions),
                SkipMode.M3u => new M3uExistingChecker(m3uEditor, false),
                SkipMode.M3uCond => noConditions ? new M3uExistingChecker(m3uEditor, true) : new M3uConditionExistingChecker(m3uEditor, conditions),
            };
        }

        public static Dictionary<Track, string> SkipExisting(List<Track> tracks, string dir, FileConditions necessaryCond, M3uEditor m3uEditor, SkipMode mode)
        {
            var existing = new Dictionary<Track, string>();

            var checker = GetChecker(mode, dir, necessaryCond, m3uEditor);
            checker.BuildIndex();

            for (int i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].IsNotAudio)
                    continue;

                if (checker.TrackExists(tracks[i], out string? path))
                {
                    existing.TryAdd(tracks[i], path);
                    tracks[i].State = TrackState.AlreadyExists;
                    tracks[i].DownloadPath = path;
                }
            }

            return existing;
        }
    }

    public interface IExistingChecker
    {
        public bool TrackExists(Track track, out string? foundPath);
        public void BuildIndex() { }
    }

    public class NameExistingChecker : IExistingChecker
    {
        readonly string[] ignore = new string[] { " ", "_", "-", ".", "(", ")", "[", "]" };
        readonly string dir;
        readonly List<(string, string, string)> index = new(); // (Path, PreprocessedPath, PreprocessedName)

        public NameExistingChecker(string dir)
        {
            this.dir = dir;
        }

        private string Preprocess(string s, bool removeSlash)
        {
            s = s.ToLower().Replace(ignore, "");
            s = s.ReplaceInvalidChars("", false, removeSlash);
            s = s.RemoveFt();
            s = s.RemoveDiacritics();
            return s;
        }

        public void BuildIndex()
        {
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);

            int removeLen = Preprocess(dir, false).Length + 1;

            foreach (var path in files)
            {
                if (Utils.IsMusicFile(path))
                {
                    string ppath = Preprocess(path[..path.LastIndexOf('.')], false)[removeLen..];
                    string pname = Path.GetFileName(ppath);
                    index.Add((path, ppath, pname));
                }
            }
        }

        public bool TrackExists(Track track, out string? foundPath)
        {
            string title = Preprocess(track.Title, true);
            string artist = Preprocess(track.Artist, true);

            foreach ((var path, var ppath, var pname) in index)
            {
                if (pname.Contains(title) && ppath.Contains(artist))
                {
                    foundPath = path;
                    return true;
                }
            }

            foundPath = null;
            return false;
        }
    }

    public class NameConditionExistingChecker : IExistingChecker
    {
        readonly string[] ignore = new string[] { " ", "_", "-", ".", "(", ")", "[", "]" };
        readonly string dir;
        readonly List<(string, string, SimpleFile)> index = new(); // (PreprocessedPath, PreprocessedName, file)
        FileConditions conditions;

        public NameConditionExistingChecker(string dir, FileConditions conditions)
        {
            this.dir = dir;
            this.conditions = conditions;
        }

        private string Preprocess(string s, bool removeSlash)
        {
            s = s.ToLower().Replace(ignore, "");
            s = s.ReplaceInvalidChars("", false, removeSlash);
            s = s.RemoveFt();
            s = s.RemoveDiacritics();
            return s;
        }

        public void BuildIndex()
        {
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
        }

        public bool TrackExists(Track track, out string? foundPath)
        {
            string title = Preprocess(track.Title, true);
            string artist = Preprocess(track.Artist, true);

            foreach ((var ppath, var pname, var musicFile) in index)
            {
                if (pname.Contains(title) && ppath.Contains(artist) && conditions.FileSatisfies(musicFile, track))
                {
                    foundPath = musicFile.Path;
                    return true;
                }
            }

            foundPath = null;
            return false;
        }
    }

    public class TagExistingChecker : IExistingChecker
    {
        readonly string dir;
        readonly List<(string, string, string)> index = new(); // (Path, PreprocessedArtist, PreprocessedTitle)

        public TagExistingChecker(string dir)
        {
            this.dir = dir;
        }

        private string Preprocess(string s)
        {
            return s.Replace(" ", "").RemoveFt().ToLower();
        }

        public void BuildIndex()
        {
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
        }

        public bool TrackExists(Track track, out string? foundPath)
        {
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

            foundPath = null;
            return false;
        }
    }

    public class TagConditionExistingChecker : IExistingChecker
    {
        readonly string dir;
        readonly List<(string, string, SimpleFile)> index = new(); // (PreprocessedArtist, PreprocessedTitle, file)
        FileConditions conditions;

        public TagConditionExistingChecker(string dir, FileConditions conditions)
        {
            this.dir = dir;
            this.conditions = conditions;
        }

        private string Preprocess(string s)
        {
            return s.Replace(" ", "").RemoveFt().ToLower();
        }

        public void BuildIndex()
        {
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
        }

        public bool TrackExists(Track track, out string? foundPath)
        {
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

            foundPath = null;
            return false;
        }
    }

    public class M3uExistingChecker : IExistingChecker
    {
        M3uEditor m3uEditor;
        bool checkFileExists;
        
        public M3uExistingChecker(M3uEditor m3UEditor, bool checkFileExists) 
        {
            this.m3uEditor = m3UEditor;
            this.checkFileExists = checkFileExists;
        }

        public bool TrackExists(Track track, out string? foundPath)
        {
            foundPath = null;
            var t = m3uEditor.PreviousRunResult(track);
            if (t != null && (t.State == TrackState.Downloaded || t.State == TrackState.AlreadyExists))
            {
                if (checkFileExists && (t.DownloadPath.Length == 0 || !File.Exists(t.DownloadPath)))
                {
                    return false;
                }
                foundPath = t.DownloadPath;
                return true;
            }
            return false;
        }
    }

    public class M3uConditionExistingChecker : IExistingChecker
    {
        M3uEditor m3uEditor;
        FileConditions conditions;

        public M3uConditionExistingChecker(M3uEditor m3UEditor, FileConditions conditions)
        {
            this.m3uEditor = m3UEditor;
            this.conditions = conditions;
        }

        public bool TrackExists(Track track, out string? foundPath)
        {
            foundPath = null;
            var t = m3uEditor.PreviousRunResult(track);
            if (t != null && (t.State == TrackState.Downloaded || t.State == TrackState.AlreadyExists) && t.DownloadPath.Length > 0)
            {
                if (File.Exists(t.DownloadPath))
                {
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
            }
            return false;
        }
    }
}
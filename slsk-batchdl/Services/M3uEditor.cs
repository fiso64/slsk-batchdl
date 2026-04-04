using Jobs;
using Models;
using Enums;
using System.Text;


// Holds the persisted state of one entry read from a prior-run index file.
public class IndexEntry
{
    public string       DownloadPath  { get; set; } = "";
    public string       Artist        { get; set; } = "";
    public string       Album         { get; set; } = "";
    public string       Title         { get; set; } = "";
    public int          Length        { get; set; } = -1;
    public TrackState   State         { get; set; } = TrackState.Initial;
    public FailureReason FailureReason { get; set; } = FailureReason.None;
    // True when a Normal-type entry was promoted to also be keyed as an Album entry.
    public bool         IsAlbum       { get; set; }

    public string ToKey() =>
        $"{Artist.ToLower()}\n{Album.ToLower()}\n{Title.ToLower()}\n{Length}";
}


public class M3uEditor // todo: separate into M3uEditor and IndexEditor
{
    public string path { get; private set; }
    public M3uOption option = M3uOption.Index;
    string parent;
    List<string> lines;
    bool needFirstUpdate = false;
    int offset = 0;
    readonly JobQueue queue;
    readonly Dictionary<string, IndexEntry> previousRunData = new(); // key → IndexEntry

    private readonly object locker = new();
    private readonly Dictionary<Guid, string?> jobDownloadPaths = new();

    public void NotifyJobDownloadPath(Guid jobId, string? path)
    {
        lock (locker) jobDownloadPaths[jobId] = path;
    }

    private M3uEditor(JobQueue queue, M3uOption option, int offset = 0)
    {
        this.queue  = queue;
        this.option = option;
        this.offset = offset;
        this.needFirstUpdate = option == M3uOption.All || option == M3uOption.Playlist;
    }

    public M3uEditor(string path, JobQueue queue, M3uOption option, bool loadPreviousResults) : this(queue, option)
    {
        SetPathAndLoad(path, loadPreviousResults);
    }

    private void SetPathAndLoad(string path, bool loadPreviousResults)
    {
        if (string.IsNullOrEmpty(path))
            return;

        if (this.path != null && Utils.NormalizedPath(this.path) == Utils.NormalizedPath(path))
            return;

        this.path = Utils.GetFullPath(path);
        parent    = Utils.NormalizedPath(Path.GetDirectoryName(this.path));

        lines = ReadAllLines().ToList();

        if (loadPreviousResults)
            LoadPreviousResults();
    }

    private void LoadPreviousResults()
    {
        if (lines.Count == 0 || !lines.Any(x => x.Trim() != ""))
            return;

        bool useOldFormat = lines[0].StartsWith("#SLDL:");

        var indexLines   = useOldFormat ? new string[] { lines[0] } : lines.Skip(1);
        var currentItem  = new StringBuilder();

        if (useOldFormat) lines = lines.Skip(1).ToList();
        int startOffset = useOldFormat ? "#SLDL:".Length : 0;

        foreach (var sldlLine in indexLines)
        {
            if (string.IsNullOrWhiteSpace(sldlLine))
                continue;

            int  k       = startOffset;
            bool inQuotes = false;

            for (; k < sldlLine.Length && sldlLine[k] == ' '; k++) ;

            for (; k < sldlLine.Length; k++)
            {
                var entry = new IndexEntry();
                int field = 0;
                for (int i = k; i < sldlLine.Length; i++)
                {
                    char c = sldlLine[i];

                    if (c == '"' && (i == k || sldlLine[i - 1] != '\\'))
                    {
                        if (inQuotes && i + 1 < sldlLine.Length && sldlLine[i + 1] == '"')
                        {
                            currentItem.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = !inQuotes;
                        }
                    }
                    else if (field <= 6 && c == ',' && !inQuotes)
                    {
                        var x = currentItem.ToString();

                        if (field == 0)
                        {
                            if (x.StartsWith("./"))
                                x = Path.Join(parent, x[2..]);
                            entry.DownloadPath = x;
                        }
                        else if (field == 1) entry.Artist = x;
                        else if (field == 2) entry.Album  = x;
                        else if (field == 3) entry.Title  = x;
                        else if (field == 4) entry.Length = int.Parse(x);
                        else if (field == 5) { /* tracktype — ignored, determined by Title/Album */ }
                        else if (field == 6) entry.State  = (TrackState)int.Parse(x);

                        currentItem.Clear();
                        field++;
                    }
                    else if (field == 7 && c == ';' && useOldFormat)
                    {
                        entry.FailureReason = (FailureReason)int.Parse(currentItem.ToString());
                        currentItem.Clear();
                        k = i;
                        break;
                    }
                    else
                    {
                        currentItem.Append(c);
                    }
                }

                if (!useOldFormat)
                {
                    entry.FailureReason = (FailureReason)int.Parse(currentItem.ToString());
                    currentItem.Clear();
                }

                previousRunData[entry.ToKey()] = entry;

                // When an entry has both album and title set, also register it under the album-only key
                // so AlbumQueryJob lookups (which use empty title) can find it.
                if (entry.Title.Length > 0 && entry.Album.Length > 0)
                {
                    var albumKey = new IndexEntry
                    {
                        Artist = entry.Artist,
                        Album  = entry.Album,
                        Title  = "",
                        Length = -1,
                    }.ToKey();
                    previousRunData.TryAdd(albumKey, entry);
                }

                if (!useOldFormat)
                    break;
            }
        }
    }

    public void Update()
    {
        if (option == M3uOption.None)
            return;

        lock (queue) lock (locker)
        {
            bool needUpdate = false;
            int  index      = 1 + offset;

            bool updateLine(string newLine)
            {
                bool changed = index >= lines.Count || newLine != lines[index];
                while (index >= lines.Count) lines.Add("");
                lines[index] = newLine;
                return changed;
            }

            bool entryChanged(IndexEntry? prev, string downloadPath, TrackState state, FailureReason reason)
            {
                return prev == null
                    || prev.State != state
                    || prev.FailureReason != reason
                    || Utils.NormalizedPath(prev.DownloadPath) != Utils.NormalizedPath(downloadPath ?? "");
            }

            void updateEntryIfNeeded(string key, string downloadPath, string artist, string album,
                string title, int length, TrackState state, FailureReason reason, bool isAlbum = false)
            {
                if (option == M3uOption.Playlist)
                    return;

                previousRunData.TryGetValue(key, out var prev);

                if (!needUpdate)
                    needUpdate = entryChanged(prev, downloadPath, state, reason);

                if (needUpdate)
                {
                    if (prev == null)
                    {
                        previousRunData[key] = new IndexEntry
                        {
                            DownloadPath  = downloadPath ?? "",
                            Artist        = artist,
                            Album         = album,
                            Title         = title,
                            Length        = length,
                            State         = state,
                            FailureReason = reason,
                            IsAlbum       = isAlbum,
                        };
                    }
                    else
                    {
                        prev.State         = state;
                        prev.FailureReason = reason;
                        prev.DownloadPath  = downloadPath ?? "";
                    }
                }
            }

            foreach (var job in queue.Jobs)
            {
                // Job-level entry (for AlbumQueryJob and non-Normal jobs that have their own index row)
                if (job is AlbumQueryJob albumJob && albumJob.State != JobState.Pending)
                {
                    var (state, reason) = JobStateToTrackState(albumJob);
                    string key = MakeAlbumKey(albumJob.Query.Artist, albumJob.Query.Album);
                    jobDownloadPaths.TryGetValue(albumJob.Id, out var downloadPath);
                    updateEntryIfNeeded(key, downloadPath ?? "",
                        albumJob.Query.Artist, albumJob.Query.Album, "", -1, state, reason, isAlbum: true);
                }

                // Per-song entries
                IEnumerable<SongJob> songs = job switch
                {
                    SongListQueryJob slj => slj.Songs,
                    AggregateQueryJob ag => ag.Songs,
                    _               => Enumerable.Empty<SongJob>(),
                };

                foreach (var song in songs)
                {
                    if (song.State == TrackState.Initial)
                    {
                        index++;
                        continue;
                    }

                    string key = MakeSongKey(song);
                    updateEntryIfNeeded(key, song.DownloadPath ?? "",
                        song.Query.Artist, song.Query.Album, song.Query.Title, song.Query.Length,
                        song.State, song.FailureReason);

                    if (option == M3uOption.All || option == M3uOption.Playlist)
                    {
                        needUpdate |= updateLine(SongToLine(song));
                        index++;
                    }
                }
            }

            if (needUpdate || needFirstUpdate)
            {
                needFirstUpdate = false;
                WriteAllLines();
            }
        }
    }

    class Writer
    {
        private readonly StringBuilder sb = new();
        public void Write(string s) => sb.Append(s);
        public void Write(char c)   => sb.Append(c);
        public override string ToString() => sb.ToString();
    }

    private void WriteAllLines()
    {
        if (!Directory.Exists(parent))
            Directory.CreateDirectory(parent);

        var writer = new Writer();

        if (option != M3uOption.Playlist)
            WriteSldlLine(writer);

        if (option != M3uOption.Index)
        {
            foreach (var line in lines)
            {
                writer.Write(line);
                writer.Write('\n');
            }
        }

        File.WriteAllText(path, writer.ToString());
    }

    private void WriteSldlLine(Writer writer)
    {
        void writeCsvLine(string[] items)
        {
            bool comma = false;
            foreach (var item in items)
            {
                if (comma) writer.Write(',');

                if (item.Contains(',') || item.Contains('\"'))
                {
                    writer.Write('"');
                    writer.Write(item.Replace("\"", "\"\""));
                    writer.Write('"');
                }
                else
                {
                    writer.Write(item);
                }
                comma = true;
            }
        }

        writer.Write("filepath,artist,album,title,length,tracktype,state,failurereason\n");

        foreach (var val in previousRunData.Values)
        {
            string p = val.DownloadPath;
            if (Utils.NormalizedPath(p).StartsWith(parent))
                p = "./" + System.IO.Path.GetRelativePath(parent, p);

            // tracktype: 1 for album entries, 0 for song entries (backward-compat with old readers)
            int tracktype = val.IsAlbum || val.Title.Length == 0 ? 1 : 0;

            var items = new string[]
            {
                p,
                val.Artist,
                val.Album,
                val.Title,
                val.Length.ToString(),
                tracktype.ToString(),
                ((int)val.State).ToString(),
                ((int)val.FailureReason).ToString(),
            };

            writeCsvLine(items);
            writer.Write('\n');
        }

        writer.Write('\n');
    }

    private string SongToLine(SongJob song)
    {
        string? failureReason = song.FailureReason != FailureReason.None ? song.FailureReason.ToString() : null;
        if (failureReason == null && song.State == TrackState.NotFoundLastTime)
            failureReason = nameof(FailureReason.NoSuitableFileFound);

        if (failureReason != null)
            return $"#FAIL: {song} [{failureReason}]";

        if (!string.IsNullOrEmpty(song.DownloadPath))
        {
            if (Utils.NormalizedPath(song.DownloadPath).StartsWith(parent))
                return Path.GetRelativePath(parent, song.DownloadPath);
            else
                return song.DownloadPath;
        }

        return $"# {song}";
    }

    // Looks up the persisted state for a SongJob from a prior run.
    public IndexEntry? PreviousRunResult(SongJob song)
    {
        previousRunData.TryGetValue(MakeSongKey(song), out var t);
        return t;
    }

    // Looks up the persisted state for an AlbumQueryJob from a prior run.
    public IndexEntry? PreviousRunResult(AlbumQueryJob job)
    {
        previousRunData.TryGetValue(MakeAlbumKey(job.Query.Artist, job.Query.Album), out var t);
        return t;
    }

    public bool TryGetPreviousRunResult(SongJob song, out IndexEntry? result)
    {
        previousRunData.TryGetValue(MakeSongKey(song), out result);
        return result != null;
    }

    public bool TryGetFailureReason(SongJob song, out FailureReason reason)
    {
        reason = FailureReason.None;
        var t = PreviousRunResult(song);
        if (t != null && t.State == TrackState.Failed)
        {
            reason = t.FailureReason;
            return true;
        }
        return false;
    }

    public IReadOnlyCollection<IndexEntry> GetPreviousRunData() => previousRunData.Values;

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string MakeSongKey(SongJob song)
    {
        return new IndexEntry
        {
            Artist = song.Query.Artist,
            Album  = song.Query.Album,
            Title  = song.Query.Title,
            Length = song.Query.Length,
        }.ToKey();
    }

    private static string MakeAlbumKey(string artist, string album)
    {
        return new IndexEntry { Artist = artist, Album = album, Title = "", Length = -1 }.ToKey();
    }

    // Maps JobState → TrackState for writing album entries to the index.
    private static (TrackState state, FailureReason reason) JobStateToTrackState(Job job)
    {
        return job.State switch
        {
            JobState.Done    => (TrackState.Downloaded,    job.FailureReason),
            JobState.Failed  => (TrackState.Failed,        job.FailureReason),
            JobState.Skipped => job.FailureReason != FailureReason.None
                                    ? (TrackState.NotFoundLastTime, job.FailureReason)
                                    : (TrackState.AlreadyExists,    FailureReason.None),
            _                => (TrackState.Initial,       FailureReason.None),
        };
    }

    private string ReadAllText()
    {
        if (!File.Exists(path))
            return "";
        using var fileStream    = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var streamReader  = new StreamReader(fileStream, encoding: Encoding.UTF8);
        return streamReader.ReadToEnd();
    }

    private string[] ReadAllLines()
    {
        return ReadAllText().TrimEnd().Split('\n');
    }
}

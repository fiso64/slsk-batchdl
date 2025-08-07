using Models;
using Enums;
using System.Text;


public class M3uEditor // todo: separate into M3uEditor and IndexEditor
{
    public string path { get; private set; }
    public M3uOption option = M3uOption.Index;
    string parent;
    List<string> lines;
    bool needFirstUpdate = false;
    int offset = 0;
    readonly TrackLists trackLists;
    readonly Dictionary<string, Track> previousRunData = new(); // { track.ToKey(), track }

    private readonly object locker = new();

    private M3uEditor(TrackLists trackLists, M3uOption option, int offset = 0)
    {
        this.trackLists = trackLists;
        this.option = option;
        this.offset = offset;
        this.needFirstUpdate = option == M3uOption.All || option == M3uOption.Playlist;
    }

    public M3uEditor(string path, TrackLists trackLists, M3uOption option, bool loadPreviousResults) : this(trackLists, option)
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
        parent = Utils.NormalizedPath(Path.GetDirectoryName(this.path));

        lines = ReadAllLines().ToList();

        if (loadPreviousResults)
            LoadPreviousResults();
    }

    private void LoadPreviousResults()
    {
        if (lines.Count == 0 || !lines.Any(x => x.Trim() != ""))
            return;

        bool useOldFormat = lines[0].StartsWith("#SLDL:");

        var indexLines = useOldFormat ? new string[] { lines[0] } : lines.Skip(1);
        var currentItem = new StringBuilder();

        if (useOldFormat) lines = lines.Skip(1).ToList();
        int offset = useOldFormat ? "#SLDL:".Length : 0;

        foreach (var sldlLine in indexLines)
        {
            if (string.IsNullOrWhiteSpace(sldlLine))
                continue;

            int k = offset;
            bool inQuotes = false;

            for (; k < sldlLine.Length && sldlLine[k] == ' '; k++) ;

            for (; k < sldlLine.Length; k++)
            {
                var track = new Track();
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
                            track.DownloadPath = x;
                        }
                        else if (field == 1)
                            track.Artist = x;
                        else if (field == 2)
                            track.Album = x;
                        else if (field == 3)
                            track.Title = x;
                        else if (field == 4)
                            track.Length = int.Parse(x);
                        else if (field == 5)
                            track.Type = (TrackType)int.Parse(currentItem.ToString());
                        else if (field == 6)
                            track.State = (TrackState)int.Parse(x);

                        currentItem.Clear();
                        field++;
                    }
                    else if (field == 7 && c == ';' && useOldFormat)
                    {
                        track.FailureReason = (FailureReason)int.Parse(currentItem.ToString());
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
                    track.FailureReason = (FailureReason)int.Parse(currentItem.ToString());
                    currentItem.Clear();
                }

                previousRunData[track.ToKey()] = track;

                if (track.Type == TrackType.Album && track.Title.Length > 0)
                {
                    previousRunData[track.ToKey(forceNormal: true)] = track;
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

        lock (trackLists) lock (locker)
            {
                bool needUpdate = false;
                int index = 1 + offset;

                bool updateLine(string newLine)
                {
                    bool changed = index >= lines.Count || newLine != lines[index];

                    while (index >= lines.Count) lines.Add("");
                    lines[index] = newLine;

                    return changed;
                }

                bool trackChanged(Track track, Track? indexTrack)
                {
                    return indexTrack == null
                        || indexTrack.State != track.State
                        || indexTrack.FailureReason != track.FailureReason
                        || Utils.NormalizedPath(indexTrack.DownloadPath) != Utils.NormalizedPath(track.DownloadPath);
                }

                void updateIndexTrackIfNeeded(Track track)
                {
                    if (option == M3uOption.Playlist)
                        return;

                    var key = track.ToKey();

                    previousRunData.TryGetValue(key, out Track? indexTrack);

                    if (!needUpdate)
                        needUpdate = trackChanged(track, indexTrack);

                    if (needUpdate)
                    {
                        if (indexTrack == null)
                            previousRunData[key] = new Track(track);
                        else
                        {
                            indexTrack.State = track.State;
                            indexTrack.FailureReason = track.FailureReason;
                            indexTrack.DownloadPath = track.DownloadPath;
                        }
                    }
                }

                foreach (var tle in trackLists.lists)
                {
                    if (tle.source.Type != TrackType.Normal)
                    {
                        if (tle.source.State != TrackState.Initial)
                        {
                            updateIndexTrackIfNeeded(tle.source);
                        }
                    }

                    if (tle.list == null) continue;

                    for (int k = 0; k < tle.list.Count; k++)
                    {
                        for (int j = 0; j < tle.list[k].Count; j++)
                        {
                            var track = tle.list[k][j];

                            if (track.IsNotAudio)
                            {
                                continue;
                            }
                            else if (track.State == TrackState.Initial)
                            {
                                index++;
                                continue;
                            }

                            updateIndexTrackIfNeeded(track);

                            if (option == M3uOption.All || option == M3uOption.Playlist)
                            {
                                needUpdate |= updateLine(TrackToLine(track));
                                index++;
                            }
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

    class Writer // temporary fix because streamwriter sometimes writes garbled text (for unknown reasons)
    {
        private StringBuilder sb = new();
        public void Write(string s) => sb.Append(s);
        public void Write(char c) => sb.Append(c);
        public override string ToString() => sb.ToString();
    }

    private void WriteAllLines()
    {
        if (!Directory.Exists(parent))
            Directory.CreateDirectory(parent);

        //using var fileStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write/*, FileShare.ReadWrite*/);
        //using var writer = new StreamWriter(fileStream, encoding: Encoding.UTF8);
        //using var writer = TextWriter.Synchronized(new StreamWriter(fileStream, encoding: Encoding.UTF8));
        var writer = new Writer();

        if (option != M3uOption.Playlist)
        {
            WriteSldlLine(writer);
        }

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
                if (comma)
                    writer.Write(',');

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

        //writer.Write("#SLDL:");
        writer.Write("filepath,artist,album,title,length,tracktype,state,failurereason\n");

        foreach (var val in previousRunData.Values)
        {
            string p = val.DownloadPath;
            if (Utils.NormalizedPath(p).StartsWith(parent))
                p = "./" + System.IO.Path.GetRelativePath(parent, p); // prepend ./ for LoadPreviousResults to recognize that a rel. path is used

            var items = new string[]
            {
                p,
                val.Artist,
                val.Album,
                val.Title,
                val.Length.ToString(),
                ((int)val.Type).ToString(),
                ((int)val.State).ToString(),
                ((int)val.FailureReason).ToString(),
            };

            writeCsvLine(items);
            //writer.Write(';');
            writer.Write('\n');
        }

        writer.Write('\n');
    }

    private string TrackToLine(Track track)
    {
        string? failureReason = track.FailureReason != FailureReason.None ? track.FailureReason.ToString() : null;
        if (failureReason == null && track.State == TrackState.NotFoundLastTime)
            failureReason = nameof(FailureReason.NoSuitableFileFound);

        if (failureReason != null)
            return $"#FAIL: {track} [{failureReason}]";

        if (track.DownloadPath.Length > 0)
        {
            if (Utils.NormalizedPath(track.DownloadPath).StartsWith(parent))
                return Path.GetRelativePath(parent, track.DownloadPath);
            else
                return track.DownloadPath;
        }

        return $"# {track}";
    }

    public Track? PreviousRunResult(Track track)
    {
        previousRunData.TryGetValue(track.ToKey(), out var t);
        return t;
    }

    public bool TryGetPreviousRunResult(Track track, out Track? result)
    {
        previousRunData.TryGetValue(track.ToKey(), out result);
        return result != null;
    }

    public bool TryGetFailureReason(Track track, out FailureReason reason)
    {
        reason = FailureReason.None;
        var t = PreviousRunResult(track);
        if (t != null && t.State == TrackState.Failed)
        {
            reason = t.FailureReason;
            return true;
        }
        return false;
    }

    private string ReadAllText()
    {
        if (!File.Exists(path))
            return "";
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var streamReader = new StreamReader(fileStream, encoding: Encoding.UTF8);
        return streamReader.ReadToEnd();
    }

    private string[] ReadAllLines()
    {
        return ReadAllText().TrimEnd().Split('\n');
    }

    public IReadOnlyCollection<Track> GetPreviousRunData()
    {
        return previousRunData.Values;
    }
}
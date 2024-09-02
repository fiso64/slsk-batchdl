using Data;
using Enums;
using System.Text;


public class M3uEditor
{
    public string path { get; private set; }
    string parent;
    List<string> lines;
    bool needFirstUpdate = false;
    readonly TrackLists trackLists;
    readonly M3uOption option = M3uOption.Index;
    readonly Dictionary<string, Track> previousRunData = new(); // { track.ToKey(), track }

    public M3uEditor(TrackLists trackLists, M3uOption option)
    {
        this.trackLists = trackLists;
        this.option = option;
        this.needFirstUpdate = option == M3uOption.All;
    }

    public M3uEditor(string path, TrackLists trackLists, M3uOption option) : this(trackLists, option)
    {
        SetPathAndLoad(path);
    }

    public void SetPathAndLoad(string path)
    {
        if (this.path == path)
            return;

        this.path = Path.GetFullPath(path);
        parent = Utils.NormalizedPath(Path.GetDirectoryName(this.path));

        lines = ReadAllLines().ToList();
        LoadPreviousResults();
    }

    private void LoadPreviousResults()
    {
        // Format:
        // #SLDL:<trackinfo>;<trackinfo>; ... 
        // where <trackinfo>  = filepath,artist,album,title,length(int),tracktype(int),state(int),failurereason(int)

        if (lines.Count == 0 || !lines[0].StartsWith("#SLDL:"))
            return;

        string sldlLine = lines[0];
        lines = lines.Skip(1).ToList();

        int k = "#SLDL:".Length;
        var currentItem = new StringBuilder();
        bool inQuotes = false;

        for (; k < sldlLine.Length && sldlLine[k] == ' '; k++);

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
                else if (field == 7 && c == ';')
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

            previousRunData[track.ToKey()] = track;
        }
    }

    public void Update()
    {
        if (option == M3uOption.None)
            return;

        lock (trackLists)
        {
            bool needUpdate = false;
            int index = 1;

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

            void updateTrackIfNeeded(Track track)
            {
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
                        updateTrackIfNeeded(tle.source);
                    }
                }

                for (int k = 0; k < tle.list.Count; k++)
                {
                    for (int j = 0; j < tle.list[k].Count; j++)
                    {
                        var track = tle.list[k][j];

                        if (track.IsNotAudio || track.State == TrackState.Initial)
                            continue;

                        updateTrackIfNeeded(track);

                        if (option == M3uOption.All)
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

    private void WriteAllLines()
    {
        if (!Directory.Exists(parent))
            Directory.CreateDirectory(parent);

        using var fileStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(fileStream);
        WriteSldlLine(writer);
        foreach (var line in lines)
        {
            writer.Write(line);
            writer.Write('\n');
        }
    }

    private void WriteSldlLine(StreamWriter writer)
    {
        // Format:
        // #SLDL:<trackinfo>;<trackinfo>; ... 
        // where <trackinfo>  = filepath,artist,album,title,length(int),tracktype(int),state(int),failurereason(int)

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

        writer.Write("#SLDL:");

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
            writer.Write(';');
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
        var key = track.ToKey();
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
        using var streamReader = new StreamReader(fileStream);
        return streamReader.ReadToEnd();
    }

    private string[] ReadAllLines()
    {
        return ReadAllText().TrimEnd().Split('\n');
    }
}
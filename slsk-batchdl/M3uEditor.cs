using Data;
using Enums;
using System.Text;


public class M3uEditor
{
    List<string> lines;
    TrackLists trackLists;
    string path;
    string parent;
    int offset = 0;
    M3uOption option = M3uOption.Index;
    bool needFirstUpdate = false;
    Dictionary<string, Track> previousRunTracks = new(); // {track.ToKey(), track }

    public M3uEditor(string m3uPath, TrackLists trackLists, M3uOption option, int offset = 0)
    {
        this.trackLists = trackLists;
        this.offset = offset;
        this.option = option;
        this.path = Path.GetFullPath(m3uPath);
        this.parent = Path.GetDirectoryName(path);
        this.lines = ReadAllLines().ToList();
        this.needFirstUpdate = option == M3uOption.All;

        LoadPreviousResults();
    }

    private void LoadPreviousResults() // #SLDL:path,artist,album,title,length(int),state(int),failurereason(int); ... ; ...
    {
        if (lines.Count == 0 || !lines[0].StartsWith("#SLDL:"))
            return;

        string sldlLine = lines[0]["#SLDL:".Length..];
        var currentItem = new StringBuilder();
        bool inQuotes = false;

        lines = lines.Skip(1).ToList();

        for (int k = 0; k < sldlLine.Length; k++)
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
                else if (field <= 5 && c == ',' && !inQuotes)
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
                        track.State = (TrackState)int.Parse(x);

                    currentItem.Clear();
                    field++;
                }
                else if (field == 6 && c == ';')
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
            previousRunTracks[track.ToKey()] = track;
        }
    }

    public void Update()
    {
        if (option == M3uOption.None)
            return;

        lock (trackLists)
        {
            bool needUpdate = false;
            int index = 1 + offset;

            void updateLine(string newLine)
            {
                while (index >= lines.Count) lines.Add("");
                lines[index] = newLine;
            }

            foreach (var tle in trackLists.lists)
            {
                if (tle.type != ListType.Normal)
                {
                    continue;
                }
                //if (option == M3uOption.All && source.State == TrackState.Failed)
                //{
                //    string reason = source.FailureReason.ToString();
                //    updateLine(TrackToLine(source, reason));
                //    index++;
                //}
                else
                {
                    for (int k = 0; k < tle.list.Count; k++)
                    {
                        for (int j = 0; j < tle.list[k].Count; j++)
                        {
                            var track = tle.list[k][j];

                            if (track.IsNotAudio || track.State == TrackState.Initial)
                                continue;

                            string trackKey = track.ToKey();
                            previousRunTracks.TryGetValue(trackKey, out Track? indexTrack);

                            if (!needUpdate)
                            {
                                needUpdate |= indexTrack == null
                                    || indexTrack.State != track.State
                                    || indexTrack.FailureReason != track.FailureReason
                                    || indexTrack.DownloadPath != track.DownloadPath;
                            }

                            previousRunTracks[trackKey] = track;

                            if (option == M3uOption.All)
                            {
                                if (track.State != TrackState.AlreadyExists || k == 0)
                                {
                                    string? reason = track.FailureReason != FailureReason.None ? track.FailureReason.ToString() : null;
                                    if (reason == null && track.State == TrackState.NotFoundLastTime)
                                        reason = nameof(FailureReason.NoSuitableFileFound);

                                    updateLine(TrackToLine(track, reason));
                                    if (tle.type != ListType.Normal)
                                        index++;
                                }
                            }

                            if (tle.type == ListType.Normal)
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

    private void WriteSldlLine(StreamWriter writer)  // #SLDL:path,artist,album,title,length(int),state(int),failurereason(int); ... ; ...
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

        writer.Write("#SLDL:");

        foreach (var val in previousRunTracks.Values)
        {
            string p = val.DownloadPath;
            if (p.StartsWith(parent))
                p = "./" + Path.GetRelativePath(parent, p); // prepend ./ for LoadPreviousResults to recognize that a rel. path is used

            var items = new string[] 
            { 
                p,
                val.Artist,
                val.Album,
                val.Title,
                val.Length.ToString(),
                ((int)val.State).ToString(),
                ((int)val.FailureReason).ToString(),
            };

            writeCsvLine(items);
            writer.Write(';');
        }

        writer.Write('\n');
    }

    private string TrackToLine(Track track, string? failureReason = null)
    {
        if (failureReason != null)
            return $"# Failed: {track} [{failureReason}]";
        if (track.DownloadPath.Length > 0)
        {
            if (track.DownloadPath.StartsWith(parent))
                return Path.GetRelativePath(parent, track.DownloadPath);
            else
                return track.DownloadPath;
        }
        return $"# {track}";
    }

    public Track? PreviousRunResult(Track track)
    {
        previousRunTracks.TryGetValue(track.ToKey(), out var t);
        return t;
    }

    public bool TryGetPreviousRunResult(Track track, out Track? result)
    {
        previousRunTracks.TryGetValue(track.ToKey(), out result);
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
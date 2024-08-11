using Enums;

using SlDictionary = System.Collections.Concurrent.ConcurrentDictionary<string, (Soulseek.SearchResponse, Soulseek.File)>;

namespace Data
{
    public class Track
    {
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public string URI = "";
        public int Length = -1;
        public bool ArtistMaybeWrong = false;
        public bool IsAlbum = false;
        public int MinAlbumTrackCount = -1;
        public int MaxAlbumTrackCount = -1;
        public bool IsNotAudio = false;
        public string DownloadPath = "";
        public string Other = "";
        public int CsvRow = -1;
        public FailureReason FailureReason = FailureReason.None;
        public TrackState State = TrackState.Initial;
        public SlDictionary? Downloads = null;

        public Track() { }

        public Track(Track other)
        {
            Title = other.Title;
            Artist = other.Artist;
            Album = other.Album;
            Length = other.Length;
            URI = other.URI;
            ArtistMaybeWrong = other.ArtistMaybeWrong;
            Downloads = other.Downloads;
            IsAlbum = other.IsAlbum;
            IsNotAudio = other.IsNotAudio;
            State = other.State;
            FailureReason = other.FailureReason;
            DownloadPath = other.DownloadPath;
            Other = other.Other;
            MinAlbumTrackCount = other.MinAlbumTrackCount;
            MaxAlbumTrackCount = other.MaxAlbumTrackCount;
            CsvRow = other.CsvRow;
        }

        public string ToKey()
        {
            return $"{Artist};{Album};{Title};{Length}";
        }

        public override string ToString()
        {
            return ToString(false);
        }

        public string ToString(bool noInfo = false)
        {
            if (IsNotAudio && Downloads != null && !Downloads.IsEmpty)
                return $"{Utils.GetFileNameSlsk(Downloads.First().Value.Item2.Filename)}";

            string str = Artist;
            if (!IsAlbum && Title.Length == 0 && Downloads != null && !Downloads.IsEmpty)
            {
                str = $"{Utils.GetFileNameSlsk(Downloads.First().Value.Item2.Filename)}";
            }
            else if (Title.Length > 0 || Album.Length > 0)
            {
                if (str.Length > 0)
                    str += " - ";
                if (IsAlbum)
                    str += Album;
                else if (Title.Length > 0)
                    str += Title;
                if (!noInfo)
                {
                    if (Length > 0)
                        str += $" ({Length}s)";
                    if (IsAlbum)
                        str += " (album)";
                }
            }
            else if (!noInfo)
            {
                str += " (artist)";
            }

            return str;
        }
    }

    public class TrackListEntry
    {
        public List<List<Track>> list;
        public ListType type;
        public Track source;
        public bool needSearch;
        public bool placeInSubdir;

        public TrackListEntry(List<List<Track>> list, ListType type, Track source)
        {
            this.list = list;
            this.type = type;
            this.source = source;

            needSearch = type != ListType.Normal;
            placeInSubdir = false;
        }

        public TrackListEntry(List<List<Track>> list, ListType type, Track source, bool needSearch, bool placeInSubdir)
        {
            this.list = list;
            this.type = type;
            this.source = source;
            this.needSearch = needSearch;
            this.placeInSubdir = placeInSubdir;
        }
    }

    public class TrackLists
    {
        public List<TrackListEntry> lists = new();

        public TrackLists() { }

        public TrackLists(List<(List<List<Track>> list, ListType type, Track source)> lists)
        {
            foreach (var (list, type, source) in lists)
            {
                var newList = new List<List<Track>>();
                foreach (var innerList in list)
                {
                    var innerNewList = new List<Track>(innerList);
                    newList.Add(innerNewList);
                }
                this.lists.Add(new TrackListEntry(newList, type, source));
            }
        }

        public static TrackLists FromFlattened(IEnumerable<Track> flatList, bool aggregate, bool album)
        {
            var res = new TrackLists();
            using var enumerator = flatList.GetEnumerator();

            while (enumerator.MoveNext())
            {
                var track = enumerator.Current;

                if (album && aggregate)
                {
                    res.AddEntry(ListType.AlbumAggregate, track);
                }
                else if (aggregate)
                {
                    res.AddEntry(ListType.Aggregate, track);
                }
                else if (album || track.IsAlbum)
                {
                    track.IsAlbum = true;
                    res.AddEntry(ListType.Album, track);
                }
                else
                {
                    res.AddEntry(ListType.Normal);
                    res.AddTrackToLast(track);

                    bool hasNext;
                    while (true)
                    {
                        hasNext = enumerator.MoveNext();
                        if (!hasNext || enumerator.Current.IsAlbum)
                            break;
                        res.AddTrackToLast(enumerator.Current);
                    }

                    if (hasNext && enumerator.Current.IsAlbum)
                        res.AddEntry(ListType.Album, track);
                    else if (!hasNext)
                        break;
                }
            }

            return res;
        }

        public TrackListEntry this[int index]
        {
            get { return lists[index]; }
            set { lists[index] = value; }
        }

        public void AddEntry(TrackListEntry tle)
        {
            lists.Add(tle);
        }

        public void AddEntry(List<List<Track>>? list, ListType? type = null, Track? source = null)
        {
            type ??= ListType.Normal;
            source ??= new Track();
            list ??= new List<List<Track>>();
            lists.Add(new TrackListEntry(list, (ListType)type, source));
        }

        public void AddEntry(List<Track> tracks, ListType? type = null, Track? source = null)
        {
            var list = new List<List<Track>>() { tracks };
            AddEntry(list, type, source);
        }

        public void AddEntry(Track track, ListType? type = null, Track? source = null)
        {
            var list = new List<List<Track>>() { new List<Track>() { track } };
            AddEntry(list, type, source);
        }

        public void AddEntry(ListType? type = null, Track? source = null)
        {
            var list = new List<List<Track>>() { new List<Track>() };
            AddEntry(list, type, source);
        }

        public void AddTrackToLast(Track track)
        {
            int i = lists.Count - 1;
            int j = lists[i].list.Count - 1;
            lists[i].list[j].Add(track);
        }

        public void Reverse()
        {
            lists.Reverse();
            foreach (var tle in lists)
            {
                foreach (var ls in tle.list)
                {
                    ls.Reverse();
                }
            }
        }

        public IEnumerable<Track> Flattened(bool addSources, bool addSpecialSourceTracks, bool sourcesOnly = false)
        {
            foreach (var tle in lists)
            {
                if ((addSources || sourcesOnly) && tle.source != null)
                    yield return tle.source;
                if (!sourcesOnly && tle.list.Count > 0 && (tle.type == ListType.Normal || addSpecialSourceTracks))
                {
                    foreach (var t in tle.list[0])
                        yield return t;
                }
            }
        }
    }

    public class TrackStringComparer : IEqualityComparer<Track>
    {
        private bool _ignoreCase = false;
        public TrackStringComparer(bool ignoreCase = false)
        {
            _ignoreCase = ignoreCase;
        }

        public bool Equals(Track a, Track b)
        {
            if (a.Equals(b))
                return true;

            var comparer = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            return string.Equals(a.Title, b.Title, comparer)
                && string.Equals(a.Artist, b.Artist, comparer)
                && string.Equals(a.Album, b.Album, comparer);
        }

        public int GetHashCode(Track a)
        {
            unchecked
            {
                int hash = 17;
                string trackTitle = _ignoreCase ? a.Title.ToLower() : a.Title;
                string artistName = _ignoreCase ? a.Artist.ToLower() : a.Artist;
                string album = _ignoreCase ? a.Album.ToLower() : a.Album;

                hash = hash * 23 + trackTitle.GetHashCode();
                hash = hash * 23 + artistName.GetHashCode();
                hash = hash * 23 + album.GetHashCode();

                return hash;
            }
        }
    }

    public class SimpleFile
    {
        public string Path;
        public string? Artists;
        public string? Title;
        public string? Album;
        public int Length;
        public int Bitrate;
        public int Samplerate;
        public int Bitdepth;

        public SimpleFile(TagLib.File file)
        {
            Path = file.Name;
            Artists = file.Tag.JoinedPerformers;
            Title = file.Tag.Title;
            Album = file.Tag.Album;
            Length = (int)file.Length;
            Bitrate = file.Properties.AudioBitrate;
            Samplerate = file.Properties.AudioSampleRate;
            Bitdepth = file.Properties.BitsPerSample;
        }
    }

    public class ResponseData
    {
        public int lockedFilesCount;
    }
}

using Enums;
using Soulseek;


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
        public int MinAlbumTrackCount = -1;
        public int MaxAlbumTrackCount = -1;
        public bool IsNotAudio = false;
        public string DownloadPath = "";
        public string Other = "";
        public int CsvRow = -1;
        public TrackType Type = TrackType.Normal;
        public FailureReason FailureReason = FailureReason.None;
        public TrackState State = TrackState.Initial;
        public List<(SearchResponse, Soulseek.File)>? Downloads = null;

        public bool OutputsDirectory => Type != TrackType.Normal;
        public Soulseek.File? FirstDownload => Downloads?.FirstOrDefault().Item2;
        public SearchResponse? FirstResponse => Downloads?.FirstOrDefault().Item1;
        public string? FirstUsername => Downloads?.FirstOrDefault().Item1?.Username;

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
            Type = other.Type;
            IsNotAudio = other.IsNotAudio;
            State = other.State;
            FailureReason = other.FailureReason;
            DownloadPath = other.DownloadPath;
            Other = other.Other;
            MinAlbumTrackCount = other.MinAlbumTrackCount;
            MaxAlbumTrackCount = other.MaxAlbumTrackCount;
            //CsvRow = other.CsvRow;
        }

        public string ToKey()
        {
            if (Type == TrackType.Album)
                return $"{Artist};{Album};{(int)Type}";
            else
                return $"{Artist};{Album};{Title};{Length};{(int)Type}";
        }

        public override string ToString()
        {
            return ToString(false);
        }

        public string ToString(bool noInfo = false)
        {
            if (IsNotAudio && Downloads != null && Downloads.Count > 0)
                return $"{Utils.GetFileNameSlsk(Downloads[0].Item2.Filename)}";

            string str = Artist;
            if (Type == TrackType.Normal && Title.Length == 0 && Downloads != null && Downloads.Count > 0)
            {
                str = $"{Utils.GetFileNameSlsk(Downloads[0].Item2.Filename)}";
            }
            else if (Title.Length > 0 || Album.Length > 0)
            {
                if (str.Length > 0)
                    str += " - ";
                if (Type == TrackType.Album)
                    str += Album;
                else if (Title.Length > 0)
                    str += Title;
                if (!noInfo)
                {
                    if (Length > 0)
                        str += $" ({Length}s)";
                    if (Type == TrackType.Album)
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
        public Track source;
        public bool needSourceSearch = false;
        public bool sourceCanBeSkipped = false;
        public bool needSkipExistingAfterSearch = false;
        public bool gotoNextAfterSearch = false;
        public bool placeInSubdir = false;

        public TrackListEntry()
        {
            list = new List<List<Track>>();
            source = new Track();
        }

        public TrackListEntry(Track source)
        {
            list = new List<List<Track>>();
            this.source = source;

            needSourceSearch = source.Type != TrackType.Normal;
            needSkipExistingAfterSearch = source.Type == TrackType.Aggregate;
            gotoNextAfterSearch = source.Type == TrackType.AlbumAggregate;
            sourceCanBeSkipped = source.Type != TrackType.Normal 
                && source.Type != TrackType.Aggregate 
                && source.Type != TrackType.AlbumAggregate;
        }

        public TrackListEntry(List<List<Track>> list, Track source)
        {
            this.list = list;
            this.source = source;

            needSourceSearch = source.Type != TrackType.Normal;
            needSkipExistingAfterSearch = source.Type == TrackType.Aggregate;
            gotoNextAfterSearch = source.Type == TrackType.AlbumAggregate;
            sourceCanBeSkipped = source.Type != TrackType.Normal
                && source.Type != TrackType.Aggregate
                && source.Type != TrackType.AlbumAggregate;
        }

        public TrackListEntry(List<List<Track>> list, Track source, bool needSearch, bool placeInSubdir,
            bool sourceCanBeSkipped, bool needSkipExistingAfterSearch, bool gotoNextAfterSearch)
        {
            this.list = list;
            this.source = source;
            this.needSourceSearch = needSearch;
            this.placeInSubdir = placeInSubdir;
            this.sourceCanBeSkipped = sourceCanBeSkipped;
            this.needSkipExistingAfterSearch = needSkipExistingAfterSearch;
            this.gotoNextAfterSearch = gotoNextAfterSearch;
        }
    }

    public class TrackLists
    {
        public List<TrackListEntry> lists = new();

        public TrackLists() { }

        public static TrackLists FromFlattened(IEnumerable<Track> flatList)
        {
            var res = new TrackLists();
            using var enumerator = flatList.GetEnumerator();

            while (enumerator.MoveNext())
            {
                var track = enumerator.Current;

                if (track.Type != TrackType.Normal)
                {
                    res.AddEntry(new TrackListEntry(track));
                }
                else
                {
                    res.AddEntry(new TrackListEntry());
                    res.AddTrackToLast(track);

                    bool hasNext;
                    while (true)
                    {
                        hasNext = enumerator.MoveNext();
                        if (!hasNext || enumerator.Current.Type != TrackType.Normal)
                            break;
                        res.AddTrackToLast(enumerator.Current);
                    }

                    if (hasNext)
                        res.AddEntry(new TrackListEntry(enumerator.Current));
                    else break;
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

        public void AddTrackToLast(Track track)
        {
            if (lists.Count == 0)
            {
                AddEntry(new TrackListEntry(new List<List<Track>> { new List<Track>() { track } }, new Track()));
                return;
            }

            int i = lists.Count - 1;

            if (lists[i].list.Count == 0)
            {
                lists[i].list.Add(new List<Track>() { track });
                return;
            }

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

        public void UpgradeListTypes(bool aggregate, bool album)
        {
            if (!aggregate && !album)
                return;

            var newLists = new List<TrackListEntry>();

            for (int i = 0; i < lists.Count; i++)
            {
                var tle = lists[i];

                if (tle.source.Type == TrackType.Album && aggregate)
                {
                    tle.source.Type = TrackType.AlbumAggregate;
                    newLists.Add(tle);
                }
                else if (tle.source.Type == TrackType.Aggregate && album)
                {
                    tle.source.Type = TrackType.AlbumAggregate;
                    newLists.Add(tle);
                }
                else if (tle.source.Type == TrackType.Normal && (album || aggregate))
                {
                    foreach (var track in tle.list[0])
                    {
                        if (album && aggregate)
                            track.Type = TrackType.AlbumAggregate;
                        else if (album)
                            track.Type = TrackType.Album;
                        else if (aggregate)
                            track.Type = TrackType.Aggregate;

                        newLists.Add(new TrackListEntry(track));
                    }
                }
                else
                {
                    newLists.Add(tle);
                }
            }

            lists = newLists;
        }

        public void SetListEntryOptions()
        {
            // place downloads in subdirs if there is more than one special (album/aggregate) download
            bool placeInSubdirs = Flattened(true, false, true).Skip(1).Any();

            if (placeInSubdirs)
            {
                foreach(var tle in lists)
                {
                    if (tle.source.Type != TrackType.Normal)
                        tle.placeInSubdir = true;
                }
            }
        }

        public IEnumerable<Track> Flattened(bool addSources, bool addSpecialSourceTracks, bool sourcesOnly = false)
        {
            foreach (var tle in lists)
            {
                if ((addSources || sourcesOnly) && tle.source != null && tle.source.Type != TrackType.Normal)
                    yield return tle.source;
                if (!sourcesOnly && tle.list.Count > 0 && (tle.source.Type == TrackType.Normal || addSpecialSourceTracks))
                {
                    foreach (var t in tle.list[0])
                        yield return t;
                }
            }
        }
    }

    public class TrackComparer : IEqualityComparer<Track>
    {
        private bool _ignoreCase = false;
        private int _lenTol = -1;
        public TrackComparer(bool ignoreCase = false, int lenTol = -1)
        {
            _ignoreCase = ignoreCase;
            _lenTol = lenTol;
        }

        public bool Equals(Track a, Track b)
        {
            if (a.Equals(b))
                return true;

            var comparer = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            return string.Equals(a.Title, b.Title, comparer)
                && string.Equals(a.Artist, b.Artist, comparer)
                && string.Equals(a.Album, b.Album, comparer)
                && _lenTol == -1 || (a.Length == -1 && b.Length == -1) || (a.Length != -1 && b.Length != -1 && Math.Abs(a.Length - b.Length) <= _lenTol);
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

using Enums;
using Soulseek;

namespace Models
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
        public bool IsDirectLink = false;
        public string DownloadPath = "";
        public string Other = "";
        public int ItemNumber = 1; // source item number (1-indexed, including offset)
        public int LineNumber = 1; // line number (1-indexed, for csv or list input only)
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
            ItemNumber = other.ItemNumber;
            LineNumber = other.LineNumber;
        }

        public string ToKey(bool forceNormal = false)
        {
            if (Type == TrackType.Album && !forceNormal)
                return $"{Artist};{Album};{(int)Type}";
            else if (!IsDirectLink)
                return $"{Artist};{Album};{Title};{Length};{(forceNormal ? (int)TrackType.Normal : (int)Type)}";
            else
                return URI;
        }

        public override string ToString()
        {
            return ToString(false);
        }

        public string ToString(bool noInfo = false)
        {
            if (IsNotAudio && Downloads != null && Downloads.Count > 0)
                return $"{Utils.GetFileNameSlsk(Downloads[0].Item2.Filename)}";

            if (IsDirectLink)
                return URI;

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
                {
                    if (Album.Length > 0)
                        str += Album;
                    else
                        str += Title;
                }
                else
                {
                    if (Title.Length > 0)
                        str += Title;
                    else
                        str += Album;
                }
                if (!noInfo)
                {
                    if (Type == TrackType.Album)
                        str += " (album)";
                    else if (Length > 0)
                        str += $" ({Length}s)";
                }
            }
            else if (!noInfo)
            {
                str += " (artist)";
            }

            return str;
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

        public bool Equals(Track? a, Track? b)
        {
            if (a == null || b == null)
                return a == b;

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
}

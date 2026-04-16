namespace Sldl.Core.Models;
    public class AlbumQuery
    {
        public string Artist { get; init; } = "";
        public string Album  { get; init; } = "";
        // Optional song-title hint: used as the network search keyword when Album is empty,
        // but NOT used for folder-name matching. Allows "search albums by song title".
        public string SearchHint { get; init; } = "";
        public string URI    { get; init; } = "";
        public bool   ArtistMaybeWrong { get; init; }
        public bool   IsDirectLink     { get; init; }
        public int    MinTrackCount { get; set; } = -1;  // -1 = no constraint
        public int    MaxTrackCount { get; set; } = -1;  // -1 = no constraint

        public AlbumQuery() { }

        public AlbumQuery(AlbumQuery other)
        {
            Artist         = other.Artist;
            Album          = other.Album;
            SearchHint     = other.SearchHint;
            URI            = other.URI;
            ArtistMaybeWrong = other.ArtistMaybeWrong;
            IsDirectLink   = other.IsDirectLink;
            MinTrackCount  = other.MinTrackCount;
            MaxTrackCount  = other.MaxTrackCount;
        }

        public string ToKey()
        {
            if (IsDirectLink)
                return URI;
            // Keep numeric suffix compatible with old index files (TrackType.Album = 1)
            return $"{Artist};{Album};1";
        }

        public override string ToString() => ToString(noInfo: false);

        public string ToString(bool noInfo)
        {
            if (IsDirectLink)
                return URI;

            string str = Artist;

            if (Album.Length > 0)
            {
                if (str.Length > 0)
                    str += " - ";
                str += Album;
                if (!noInfo)
                    str += " (album)";
            }
            else if (!noInfo)
            {
                str += " (artist)";
            }

            return str;
        }

        public static AlbumQuery FromSongQuery(SongQuery q)
            => new AlbumQuery { Artist = q.Artist, Album = q.Title, URI = q.URI, ArtistMaybeWrong = q.ArtistMaybeWrong, IsDirectLink = q.IsDirectLink };
    }

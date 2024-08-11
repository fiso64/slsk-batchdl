
using Data;
using Enums;

namespace Extractors
{
    public class StringExtractor : IExtractor
    {
        public static bool InputMatches(string input)
        {
            return !input.IsInternetUrl();
        }

        public async Task<TrackLists> GetTracks()
        {
            var trackLists = new TrackLists();
            var music = ParseTrackArg(Config.input, Config.album);
            bool isAlbum = false;

            if (Config.album && Config.aggregate)
            {
                trackLists.AddEntry(ListType.AlbumAggregate, music);
            }
            else if (Config.album)
            {
                music.IsAlbum = true;
                trackLists.AddEntry(ListType.Album, music);
            }
            else if (!Config.aggregate && music.Title.Length > 0)
            {
                trackLists.AddEntry(music);
            }
            else if (Config.aggregate)
            {
                trackLists.AddEntry(ListType.Aggregate, music);
            }
            else if (music.Title.Length == 0 && music.Album.Length > 0)
            {
                isAlbum = true;
                music.IsAlbum = true;
                trackLists.AddEntry(ListType.Album, music);
            }
            else
            {
                throw new ArgumentException("Need track title or album");
            }

            if (Config.aggregate || isAlbum || Config.album)
                Config.defaultFolderName = music.ToString(true).ReplaceInvalidChars(Config.invalidReplaceStr).Trim();
            else
                Config.defaultFolderName = ".";

            return trackLists;
        }

        public Track ParseTrackArg(string input, bool isAlbum)
        {
            input = input.Trim();
            var track = new Track();
            var keys = new string[] { "title", "artist", "length", "album", "artist-maybe-wrong" };

            track.IsAlbum = isAlbum;

            void setProperty(string key, string value)
            {
                switch (key)
                {
                    case "title":
                        track.Title = value;
                        break;
                    case "artist":
                        track.Artist = value;
                        break;
                    case "length":
                        track.Length = int.Parse(value);
                        break;
                    case "album":
                        track.Album = value;
                        break;
                    case "artist-maybe-wrong":
                        if (value == "true") track.ArtistMaybeWrong = true;
                        break;
                }
            }

            var parts = input.Split(',');
            var other = "";
            string? currentKey = null;
            string? currentVal = null;
            bool otherFieldDone = false;

            for (int i = 0; i < parts.Length; i++)
            {
                var x = parts[i];
                bool keyval = false;

                if (x.Contains('='))
                {
                    var lr = x.Split('=', 2);
                    lr[0] = lr[0].Trim();
                    if (lr.Length == 2 && lr[1].Length > 0 && keys.Contains(lr[0]))
                    {
                        if (currentKey != null && currentVal != null)
                            setProperty(currentKey, currentVal.Trim());
                        currentKey = lr[0];
                        currentVal = lr[1];
                        keyval = true;
                        otherFieldDone = true;
                    }
                }

                if (!keyval && currentVal != null)
                {
                    currentVal += ',' + x;
                }

                if (!otherFieldDone)
                {
                    if (i > 0) other += ',';
                    other += x;
                }
            }

            if (currentKey != null && currentVal != null)
                setProperty(currentKey, currentVal.Trim());

            other = other.Trim();
            if (other.Length > 0)
            {
                string artist = "", album = "", title = "";
                parts = other.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1 || parts.Length > 3)
                {
                    if (isAlbum)
                        album = other.Trim();
                    else
                        title = other.Trim();
                }
                else if (parts.Length == 2)
                {
                    artist = parts[0];

                    if (isAlbum)
                        album = parts[1];
                    else
                        title = parts[1];
                }
                else if (parts.Length == 3)
                {
                    artist = parts[0];
                    album = parts[1];
                    title = parts[2];
                }

                if (track.Artist.Length == 0)
                    track.Artist = artist;
                if (track.Album.Length == 0)
                    track.Album = album;
                if (track.Title.Length == 0)
                    track.Title = title;
            }

            if (track.Title.Length == 0 && track.Album.Length == 0 && track.Artist.Length == 0)
                throw new ArgumentException("Track string must contain title, album or artist.");

            return track;
        }
    }
}


using Models;
using Enums;

namespace Extractors
{
    public class StringExtractor : IExtractor
    {
        public static bool InputMatches(string input)
        {
            return !input.IsInternetUrl();
        }

        public Task<TrackLists> GetTracks(string input, int maxTracks, int offset, bool reverse, Config config)
        {
            bool isAlbum = config.album;

            if (input.StartsWith("album://"))
            {
                isAlbum = true;
                input = input[8..];
            }

            var trackLists = new TrackLists();
            var music = ParseTrackArg(input, isAlbum);
            TrackListEntry tle;

            if (isAlbum || (music.Title.Length == 0 && music.Album.Length > 0))
            {
                music.Type = TrackType.Album;
                tle = new TrackListEntry(music);
            }
            else
            {
                tle = new TrackListEntry(TrackType.Normal);
                tle.AddTrack(music);
            }

            trackLists.AddEntry(tle);

            return Task.FromResult(trackLists);
        }

        public static Track ParseTrackArg(string input, bool isAlbum)
        {
            input = input.Trim();
            var track = new Track();
            var keys = new string[] { "title", "artist", "length", "album", "artist-maybe-wrong", "album-track-count" };

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
                    case "album-track-count":
                        if (value == "-1")
                        {
                            track.MinAlbumTrackCount = -1;
                            track.MaxAlbumTrackCount = -1;
                        }
                        else if (value.Last() == '-')
                        {
                            track.MaxAlbumTrackCount = int.Parse(value[..^1]);
                        }
                        else if (value.Last() == '+')
                        {
                            track.MinAlbumTrackCount = int.Parse(value[..^1]);
                        }
                        else
                        {
                            track.MinAlbumTrackCount = int.Parse(value);
                            track.MaxAlbumTrackCount = track.MinAlbumTrackCount;
                        }
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
                    if (lr.Length == 2 && keys.Contains(lr[0]))
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
            if (other.Length > 0 && (isAlbum && track.Album.Length > 0 || !isAlbum && track.Title.Length > 0))
            {
                Logger.Warn($"Warning: Input part '{other}' provided without a property name " +
                    $"and album or title is already set. Ignoring.");
            }
            else if (other.Length > 0)
            {
                string artist = "", album = "", title = "";
                parts = other.Split(" - ", 2, StringSplitOptions.TrimEntries);

                if (parts.Length == 1 || track.Artist.Length > 0)
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

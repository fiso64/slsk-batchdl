using Models;
using System.Text.RegularExpressions;

namespace Extractors
{
    public class CsvExtractor : IExtractor
    {
        string? csvFilePath = null;
        int csvColumnCount = -1;
        /// Async-friendly lock.
        private readonly SemaphoreSlim csvLock = new SemaphoreSlim(1, 1);

        public static bool InputMatches(string input)
        {
            input = input.ToLower();
            return !input.IsInternetUrl() && input.EndsWith(".csv");
        }

        public async Task<TrackLists> GetTracks(string input, int maxTracks, int offset, bool reverse, Config config)
        {
            csvFilePath = Utils.ExpandVariables(input);

            if (!File.Exists(csvFilePath))
                throw new FileNotFoundException($"CSV file '{csvFilePath}' not found");

            var tracks = await ParseCsvIntoTrackInfo(csvFilePath, config.artistCol, config.titleCol, config.lengthCol,
                config.albumCol, config.descCol, config.ytIdCol, config.trackCountCol, config.timeUnit, config.ytParse);

            if (reverse)
                tracks.Reverse();

            var trackLists = TrackLists.FromFlattened(tracks.Skip(offset).Take(maxTracks));
            var csvName = Path.GetFileNameWithoutExtension(csvFilePath);

            foreach (var tle in trackLists.lists)
            {
                tle.itemName = csvName;
                tle.enablesIndexByDefault = true;
            }

            return trackLists;
        }

        public async Task RemoveTrackFromSource(Track track)
        {
            await csvLock.WaitAsync();
            try
            {
                if (File.Exists(csvFilePath))
                {
                    try
                    {
                        string[] lines = await File.ReadAllLinesAsync(csvFilePath, System.Text.Encoding.UTF8);
                        int idx = track.LineNumber - 1;

                        if (idx > -1 && idx < lines.Length)
                        {
                            lines[idx] = new string(',', Math.Max(0, csvColumnCount - 1));
                            await Utils.WriteAllLinesAsync(csvFilePath, lines, '\n');
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error($"Error removing from source: {e}");
                    }
                }
            }

            finally
            {
                csvLock.Release();
            }
        }

        async Task<List<Track>> ParseCsvIntoTrackInfo(string path, string artistCol = "", string trackCol = "",
            string lengthCol = "", string albumCol = "", string descCol = "", string ytIdCol = "", string trackCountCol = "", string timeUnit = "s", bool ytParse = false)
        {
            var tracks = new List<Track>();
            using var sr = new StreamReader(path, System.Text.Encoding.UTF8);
            var parser = new SmallestCSV.SmallestCSVParser(sr);

            int index = 1;
            var header = parser.ReadNextRow();
            while (header == null || header.Count == 0 || !header.Any(t => t.Trim().Length > 0))
            {
                index++;
                header = parser.ReadNextRow();
            }

            string[] cols = { artistCol, albumCol, trackCol, lengthCol, descCol, ytIdCol, trackCountCol };
            string[][] aliases = {
                new[] { "artist", "artist name", "artists", "artist names" },
                new[] { "album", "album name", "album title" },
                new[] { "title", "song", "track title", "track name", "song name", "track" },
                new[] { "length", "duration", "track length", "track duration", "song length", "song duration" },
                new[] { "description", "youtube description" },
                new[] { "url", "track url", "uri", "id", "youtube id" },
                new[] { "track count", "album track count" }
            };

            string usingColumns = "";
            for (int i = 0; i < cols.Length; i++)
            {
                if (string.IsNullOrEmpty(cols[i]))
                {
                    string? res = header.FirstOrDefault(h => Regex.Replace(h, @"\(.*?\)", "").Trim().EqualsAny(aliases[i], StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(res))
                    {
                        cols[i] = res;
                        usingColumns += $"{aliases[i][0]}:\"{res}\", ";
                    }
                }
                else
                {
                    if (header.IndexOf(cols[i]) == -1)
                        throw new Exception($"Column \"{cols[i]}\" not found in CSV file");
                    usingColumns += $"{aliases[i][0]}:\"{cols[i]}\", ";
                }
            }

            int foundCount = cols.Count(col => col.Length > 0);
            if (!string.IsNullOrEmpty(usingColumns))
                Logger.Info($"Using columns: {usingColumns.TrimEnd(' ', ',')}.");
            else if (foundCount == 0)
                throw new Exception("No columns specified and couldn't determine automatically");

            int[] indices = cols.Select(col => col.Length == 0 ? -1 : header.IndexOf(col)).ToArray();
            int artistIndex, albumIndex, trackIndex, lengthIndex, descIndex, ytIdIndex, trackCountIndex;
            (artistIndex, albumIndex, trackIndex, lengthIndex, descIndex, ytIdIndex, trackCountIndex) = (indices[0], indices[1], indices[2], indices[3], indices[4], indices[5], indices[6]);

            while (true)
            {
                index++;
                List<string>? values = null;

                try
                {
                    values = parser.ReadNextRow();
                }
                catch (Exception e)
                {
                    throw new InvalidDataException($"Error parsing CSV at line {index}: {e.Message}", e);
                }

                if (values == null)
                    break;
                if (!values.Any(t => t.Trim().Length > 0))
                    continue;
                while (values.Count < foundCount)
                    values.Add("");

                if (csvColumnCount == -1)
                    csvColumnCount = values.Count;

                var desc = "";
                var track = new Track() { LineNumber = index, ItemNumber = tracks.Count + 1 };

                if (artistIndex >= 0) track.Artist = values[artistIndex];
                if (trackIndex >= 0) track.Title = values[trackIndex];
                if (albumIndex >= 0) track.Album = values[albumIndex];
                if (descIndex >= 0) desc = values[descIndex];
                if (ytIdIndex >= 0) track.URI = values[ytIdIndex];
                if (trackCountIndex >= 0)
                {
                    string a = values[trackCountIndex].Trim();
                    if (a == "-1")
                    {
                        track.MinAlbumTrackCount = -1;
                        track.MaxAlbumTrackCount = -1;
                    }
                    else if (a.Last() == '-' && int.TryParse(a.AsSpan(0, a.Length - 1), out int n))
                    {
                        track.MaxAlbumTrackCount = n;
                    }
                    else if (a.Last() == '+' && int.TryParse(a.AsSpan(0, a.Length - 1), out n))
                    {
                        track.MinAlbumTrackCount = n;
                    }
                    else if (int.TryParse(a, out n))
                    {
                        track.MinAlbumTrackCount = n;
                        track.MaxAlbumTrackCount = n;
                    }
                }
                if (lengthIndex >= 0)
                {
                    try
                    {
                        track.Length = (int)ParseTrackLength(values[lengthIndex], timeUnit);
                    }
                    catch
                    {
                        Logger.Warn($"Couldn't parse track length \"{values[lengthIndex]}\" with format \"{timeUnit}\" for \"{track}\"");
                    }
                }

                if (ytParse)
                    track = await YouTube.ParseTrackInfo(track.Title, track.Artist, track.URI, track.Length, desc);

                if (track.Title.Length == 0 && track.Album.Length > 0)
                    track.Type = Enums.TrackType.Album;

                if (track.Title.Length > 0 || track.Artist.Length > 0 || track.Album.Length > 0)
                    tracks.Add(track);
            }

            if (ytParse)
                YouTube.StopService();

            return tracks;
        }

        static double ParseTrackLength(string duration, string format)
        {
            if (string.IsNullOrEmpty(format))
                throw new ArgumentException("Duration format string empty");
            duration = Regex.Replace(duration, "[a-zA-Z]", "");
            var formatParts = Regex.Split(format, @"\W+");
            var durationParts = Regex.Split(duration, @"\W+").Where(s => !string.IsNullOrEmpty(s)).ToArray();

            double totalSeconds = 0;

            for (int i = 0; i < formatParts.Length; i++)
            {
                switch (formatParts[i])
                {
                    case "h":
                        totalSeconds += double.Parse(durationParts[i]) * 3600;
                        break;
                    case "m":
                        totalSeconds += double.Parse(durationParts[i]) * 60;
                        break;
                    case "s":
                        totalSeconds += double.Parse(durationParts[i]);
                        break;
                    case "ms":
                        totalSeconds += double.Parse(durationParts[i]) / Math.Pow(10, durationParts[i].Length);
                        break;
                }
            }

            return totalSeconds;
        }
    }
}
using Data;
using HtmlAgilityPack;

using Enums;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Extractors
{
    public class BandcampExtractor : IExtractor
    {
        public static bool InputMatches(string input)
        {
            input = input.ToLower();
            return input.IsInternetUrl() && input.Contains("bandcamp.com");
        }

        public async Task<TrackLists> GetTracks(int maxTracks, int offset, bool reverse)
        {
            var trackLists = new TrackLists();
            bool isTrack = Config.input.Contains("/track/");
            bool isAlbum = !isTrack && Config.input.Contains("/album/");
            bool isArtist =!isTrack && !isAlbum;

            if (isArtist)
            {
                string artistUrl = Config.input.TrimEnd('/');

                if (!artistUrl.EndsWith("/music"))
                    artistUrl += "/music";

                using var httpClient = new HttpClient();
                var response = await httpClient.GetStringAsync(artistUrl);

                string idPattern = @"band_id=(\d+)&";
                var match = Regex.Match(response, idPattern);
                var id = match.Groups[1].Value;

                var address = $"http://bandcamp.com/api/mobile/24/band_details?band_id={id}";

                var responseString = await httpClient.GetStringAsync(address);
                var jsonDocument = JsonDocument.Parse(responseString);
                var root = jsonDocument.RootElement;

                string artistName = root.GetProperty("name").GetString();

                var tralbums = new List<Track>();

                foreach (var item in root.GetProperty("discography").EnumerateArray())
                {
                    //ItemType = item.GetProperty("item_type").GetString(),
                    var t = new Track()
                    {
                        Album = item.GetProperty("title").GetString(),
                        Artist = item.GetProperty("artist_name").GetString() ?? item.GetProperty("band_name").GetString(),
                        Type = TrackType.Album,
                    };
                    var tle = new TrackListEntry()
                    {
                        source = t,
                        placeInSubdir = true,
                        subdirOverride = t.ToString(true)
                    };
                    trackLists.AddEntry(tle);
                }
            }
            else
            {
                var web = new HtmlWeb();
                var doc = await web.LoadFromWebAsync(Config.input);

                var nameSection = doc.DocumentNode.SelectSingleNode("//div[@id='name-section']");
                var name = nameSection.SelectSingleNode(".//h2[contains(@class, 'trackTitle')]").InnerText.UnHtmlString().Trim();

                if (isAlbum)
                {
                    var artist = nameSection.SelectSingleNode(".//h3/span/a").InnerText.UnHtmlString().Trim();
                    var track = new Track() { Artist = artist, Album = name, Type = TrackType.Album };
                    trackLists.AddEntry(new TrackListEntry(track));

                    if (Config.setAlbumMinTrackCount || Config.setAlbumMaxTrackCount)
                    {
                        var trackTable = doc.DocumentNode.SelectSingleNode("//*[@id='track_table']");
                        int n = trackTable.SelectNodes(".//tr").Count;

                        if (Config.setAlbumMinTrackCount)
                            track.MinAlbumTrackCount = n;

                        if (Config.setAlbumMaxTrackCount)
                            track.MaxAlbumTrackCount = n;
                    }

                    Config.defaultFolderName = track.ToString(true).ReplaceInvalidChars(Config.invalidReplaceStr).Trim();
                }
                else
                {
                    var album = nameSection.SelectSingleNode(".//h3[contains(@class, 'albumTitle')]/span/a").InnerText.UnHtmlString().Trim();
                    var artist = nameSection.SelectSingleNode(".//h3[contains(@class, 'albumTitle')]/span[last()]/a").InnerText.UnHtmlString().Trim();
                    //var timeParts = doc.DocumentNode.SelectSingleNode("//span[@class='time_total']").InnerText.Trim().Split(':');

                    var track = new Track() { Artist = artist, Title = name, Album = album };
                    trackLists.AddEntry(new());
                    trackLists.AddTrackToLast(track);

                    Config.defaultFolderName = ".";
                }
            }

            if (reverse)
                trackLists.Reverse();

            if (offset > 0 || maxTracks < int.MaxValue)
                trackLists = TrackLists.FromFlattened(trackLists.Flattened(true, false).Skip(offset).Take(maxTracks));

            return trackLists;
        }
    }
}
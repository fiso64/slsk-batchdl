using Models;
using HtmlAgilityPack;

using Enums;
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

        public async Task<TrackLists> GetTracks(string input, int maxTracks, int offset, bool reverse, Config config)
        {
            var trackLists = new TrackLists();
            bool isTrack = input.Contains("/track/");
            bool isAlbum = !isTrack && input.Contains("/album/");
            bool isWishlist = !isTrack && !isAlbum && input.Contains("/wishlist");
            bool isArtist = !isTrack && !isAlbum && !isWishlist;

            if (isWishlist)
            {
                Logger.Info("Retrieving bandcamp wishlist..");
                HtmlDocument doc;

                if (!string.IsNullOrEmpty(config.htmlFromFile))
                {
                    doc = new HtmlDocument();
                    doc.Load(config.htmlFromFile);
                }
                else
                {
                    var web = new HtmlWeb();
                    doc = await web.LoadFromWebAsync(input);
                }

                var items = doc.DocumentNode.SelectNodes("//li[contains(@class, 'collection-item-container')]");

                if (items != null)
                {
                    int num = 1;
                    foreach (var item in items)
                    {
                        var titleNode = item.SelectSingleNode(".//div[contains(@class, 'collection-item-title')]");
                        var artistNode = item.SelectSingleNode(".//div[contains(@class, 'collection-item-artist')]");

                        if (titleNode != null && artistNode != null)
                        {
                            string album = titleNode.InnerText.UnHtmlString().Trim();
                            string artist = artistNode.InnerText.UnHtmlString().Trim();

                            if (artist.StartsWith("by ", StringComparison.OrdinalIgnoreCase))
                                artist = artist.Substring(3).Trim();

                            var track = new Track()
                            {
                                Album = album,
                                Artist = artist,
                                Type = TrackType.Album,
                                ItemNumber = num++,
                            };

                            var tle = new TrackListEntry(track);
                            tle.enablesIndexByDefault = true;
                            trackLists.AddEntry(tle);
                        }
                    }
                }
            }
            else if (isArtist)
            {
                Logger.Info("Retrieving bandcamp artist discography..");
                using var httpClient = new HttpClient();
                string response;

                if (!string.IsNullOrEmpty(config.htmlFromFile))
                {
                    response = await File.ReadAllTextAsync(config.htmlFromFile);
                }
                else
                {
                    string artistUrl = input.TrimEnd('/');

                    if (!artistUrl.EndsWith("/music"))
                        artistUrl += "/music";

                    response = await httpClient.GetStringAsync(artistUrl);
                }

                string idPattern = @"band_id=(\d+)&";
                var match = Regex.Match(response, idPattern);
                var id = match.Groups[1].Value;

                var address = $"http://bandcamp.com/api/mobile/24/band_details?band_id={id}";

                var responseString = await httpClient.GetStringAsync(address);
                var jsonDocument = JsonDocument.Parse(responseString);
                var root = jsonDocument.RootElement;

                string artistName = root.GetProperty("name").GetString();

                var tralbums = new List<Track>();

                int num = 1;
                foreach (var item in root.GetProperty("discography").EnumerateArray())
                {
                    //ItemType = item.GetProperty("item_type").GetString(),
                    var track = new Track()
                    {
                        Album = item.GetProperty("title").GetString(),
                        Artist = item.GetProperty("artist_name").GetString() ?? item.GetProperty("band_name").GetString(),
                        Type = TrackType.Album,
                        ItemNumber = num++,
                    };
                    var tle = new TrackListEntry(track);
                    tle.itemName = track.Artist;
                    tle.enablesIndexByDefault = true;
                    trackLists.AddEntry(tle);
                }
            }
            else
            {
                Logger.Info("Retrieving bandcamp item..");
                HtmlDocument doc;

                if (!string.IsNullOrEmpty(config.htmlFromFile))
                {
                    doc = new HtmlDocument();
                    doc.Load(config.htmlFromFile);
                }
                else
                {
                    var web = new HtmlWeb();
                    doc = await web.LoadFromWebAsync(input);
                }

                var nameSection = doc.DocumentNode.SelectSingleNode("//div[@id='name-section']");
                var name = nameSection.SelectSingleNode(".//h2[contains(@class, 'trackTitle')]").InnerText.UnHtmlString().Trim();

                if (isAlbum)
                {
                    var artist = nameSection.SelectSingleNode(".//h3/span/a").InnerText.UnHtmlString().Trim();
                    var track = new Track() { Artist = artist, Album = name, Type = TrackType.Album };
                    trackLists.AddEntry(new TrackListEntry(track));

                    if (config.setAlbumMinTrackCount || config.setAlbumMaxTrackCount)
                    {
                        var trackTable = doc.DocumentNode.SelectSingleNode("//*[@id='track_table']");
                        int n = trackTable.SelectNodes(".//tr").Count;

                        if (config.setAlbumMinTrackCount)
                            track.MinAlbumTrackCount = n;

                        if (config.setAlbumMaxTrackCount)
                            track.MaxAlbumTrackCount = n;
                    }
                }
                else
                {
                    var album = nameSection.SelectSingleNode(".//h3[contains(@class, 'albumTitle')]/span/a").InnerText.UnHtmlString().Trim();
                    var artist = nameSection.SelectSingleNode(".//h3[contains(@class, 'albumTitle')]/span[last()]/a").InnerText.UnHtmlString().Trim();
                    //var timeParts = doc.DocumentNode.SelectSingleNode("//span[@class='time_total']").InnerText.Trim().Split(':');

                    var track = new Track() { Artist = artist, Title = name, Album = album };
                    trackLists.AddEntry(new TrackListEntry(TrackType.Normal));
                    trackLists.AddTrackToLast(track);
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
using Models;
using Jobs;
using HtmlAgilityPack;
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

        public async Task<JobQueue> GetTracks(string input, int maxTracks, int offset, bool reverse, Config config)
        {
            var queue = new JobQueue();
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
                        var titleNode  = item.SelectSingleNode(".//div[contains(@class, 'collection-item-title')]");
                        var artistNode = item.SelectSingleNode(".//div[contains(@class, 'collection-item-artist')]");

                        if (titleNode != null && artistNode != null)
                        {
                            string album  = titleNode.InnerText.UnHtmlString().Trim();
                            string artist = artistNode.InnerText.UnHtmlString().Trim();

                            if (artist.StartsWith("by ", StringComparison.OrdinalIgnoreCase))
                                artist = artist.Substring(3).Trim();

                            var job = new AlbumJob(new AlbumQuery { Album = album, Artist = artist })
                            {
                                ItemNumber            = num++,
                                EnablesIndexByDefault = true,
                            };
                            queue.Enqueue(job);
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

                int num = 1;
                foreach (var item in root.GetProperty("discography").EnumerateArray())
                {
                    string albumTitle  = item.GetProperty("title").GetString();
                    string albumArtist = item.GetProperty("artist_name").GetString()
                                     ?? item.GetProperty("band_name").GetString();

                    var job = new AlbumJob(new AlbumQuery { Album = albumTitle, Artist = albumArtist })
                    {
                        ItemNumber            = num++,
                        ItemName              = albumArtist,
                        EnablesIndexByDefault = true,
                    };
                    queue.Enqueue(job);
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
                    var query  = new AlbumQuery { Artist = artist, Album = name };

                    if (config.setAlbumMinTrackCount || config.setAlbumMaxTrackCount)
                    {
                        var trackTable = doc.DocumentNode.SelectSingleNode("//*[@id='track_table']");
                        int n = trackTable.SelectNodes(".//tr").Count;

                        if (config.setAlbumMinTrackCount) query.MinTrackCount = n;
                        if (config.setAlbumMaxTrackCount) query.MaxTrackCount = n;
                    }

                    queue.Enqueue(new AlbumJob(query));
                }
                else
                {
                    var album  = nameSection.SelectSingleNode(".//h3[contains(@class, 'albumTitle')]/span/a").InnerText.UnHtmlString().Trim();
                    var artist = nameSection.SelectSingleNode(".//h3[contains(@class, 'albumTitle')]/span[last()]/a").InnerText.UnHtmlString().Trim();
                    var songQuery = new SongQuery { Artist = artist, Title = name, Album = album };
                    var slj = new SongListJob();
                    slj.Songs.Add(new SongJob(songQuery));
                    queue.Enqueue(slj);
                }
            }

            if (reverse)
                queue.Reverse();

            if (offset > 0 || maxTracks < int.MaxValue)
            {
                var kept = queue.Jobs.Skip(offset).Take(maxTracks).ToList();
                queue.Jobs.Clear();
                queue.Jobs.AddRange(kept);
            }

            return queue;
        }
    }
}

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
                Logger.Debug($"Wishlist URL: {input}");

                // Extract fan_id from the URL
                var usernameMatch = Regex.Match(input, @"bandcamp\.com/([^/]+)/wishlist");
                if (!usernameMatch.Success)
                {
                    Logger.Warn("Could not extract fan username from wishlist URL");
                    return trackLists;
                }

                string fanUsername = usernameMatch.Groups[1].Value;
                Logger.Debug($"Extracted fan username: {fanUsername}");

                // Load the page to get fan_id
                var web = new HtmlWeb();
                var doc = await web.LoadFromWebAsync(input);

                // Extract fan_id from the page HTML
                string fanIdPattern = @"fan_id[""']?\s*[:=]\s*(\d+)";
                var pageContent = doc.DocumentNode.OuterHtml;
                var fanIdMatch = Regex.Match(pageContent, fanIdPattern);

                if (!fanIdMatch.Success)
                {
                    Logger.Warn("Could not extract fan_id from page HTML");
                    return trackLists;
                }

                string fanId = fanIdMatch.Groups[1].Value;
                Logger.Info($"Found fan_id: {fanId}");

                // Use the API to fetch all items
                Logger.Info("Getting wishlist items from API...");
                await FetchWishlistItemsFromAPI(fanId, trackLists, fanUsername);
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

        private async Task FetchWishlistItemsFromAPI(string fanId, TrackLists trackLists, string fanUsername = "")
        {
            using var httpClient = new HttpClient();

            string apiUrl = "https://bandcamp.com/api/fancollection/1/wishlist_items";
            int startingItemCount = trackLists.lists.Count;
            int itemCount = startingItemCount;
            string? olderThanToken = null;
            int batchSize = 20;

            while (true)
            {
                try
                {
                    if (olderThanToken == null)
                    {
                        // Generate a token with current timestamp for the initial request
                        long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        olderThanToken = $"{currentTimestamp}:0:a::";
                    }

                    object requestPayload = new
                    {
                        fan_id = int.Parse(fanId),
                        older_than_token = olderThanToken,
                        count = batchSize
                    };

                    var payload = JsonSerializer.Serialize(requestPayload);

                    var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

                    var response = await httpClient.PostAsync(apiUrl, content);

                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.Warn($"API request failed with status: {response.StatusCode}. Response: {await response.Content.ReadAsStringAsync()}");
                        Logger.Info("No additional API items to fetch.");
                        return;
                    }

                    var responseContent = await response.Content.ReadAsStringAsync();
                    Logger.Debug($"API response: {responseContent.Substring(0, Math.Min(200, responseContent.Length))}...");

                    var jsonDoc = JsonDocument.Parse(responseContent);
                    var root = jsonDoc.RootElement;

                    if (!root.TryGetProperty("items", out var itemsArray))
                    {
                        Logger.Info("No additional items found in API response.");
                        return;
                    }

                    var items = itemsArray.EnumerateArray().ToList();
                    if (items.Count == 0)
                    {
                        Logger.Info("No more items to fetch");
                        break;
                    }

                    Logger.Debug($"Processing batch of {items.Count} items...");

                    foreach (var item in items)
                    {
                        try
                        {
                            ProcessWishlistItem(item, trackLists, ++itemCount);
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"Error processing item: {ex.Message}");
                        }
                    }

                    // Check if there are more items
                    if (root.TryGetProperty("more_available", out var moreAvailable) && moreAvailable.GetBoolean())
                    {
                        // Extract the older_than_token for the next request
                        if (root.TryGetProperty("last_token", out var lastToken))
                        {
                            olderThanToken = lastToken.GetString();
                            Logger.Debug($"Fetching next batch with token: {olderThanToken}");
                        }
                        else
                        {
                            Logger.Warn("more_available is true but no last_token found");
                            break;
                        }
                    }
                    else
                    {
                        Logger.Info("All items fetched");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Error fetching additional wishlist items: {ex.Message}");
                    Logger.Info("Stopping API fetch, will use items already collected.");
                    return;
                }
            }

            Logger.Info($"Successfully processed {itemCount - startingItemCount} additional wishlist items from API");
        }

        private void ProcessWishlistItem(JsonElement item, TrackLists trackLists, int itemNumber)
        {
            try
            {
                var title = "";
                var artist = "";
                var itemType = "";

                if (item.TryGetProperty("item_title", out var itemTitle))
                    title = itemTitle.GetString() ?? "";

                if (item.TryGetProperty("band_name", out var bandName))
                    artist = bandName.GetString() ?? "";

                if (item.TryGetProperty("item_type", out var itemTypeProp))
                    itemType = itemTypeProp.GetString() ?? "album";

                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(artist))
                {
                    Logger.Debug($"Skipping item #{itemNumber} due to missing title or artist");
                    return;
                }

                Logger.Debug($"Processing item #{itemNumber}: '{title}' by '{artist}' (type: {itemType})");

                // Determine if it's an album or track based on the item_type field
                bool isAlbumItem = itemType == "album";

                var track = new Track()
                {
                    Artist = artist,
                    Type = isAlbumItem ? TrackType.Album : TrackType.Normal,
                    ItemNumber = itemNumber
                };

                if (isAlbumItem)
                {
                    track.Album = title;
                    var trackListEntry = new TrackListEntry(track);
                    trackListEntry.itemName = artist;
                    trackListEntry.enablesIndexByDefault = true;
                    trackLists.AddEntry(trackListEntry);
                }
                else
                {
                    track.Title = title;
                    var trackListEntry = new TrackListEntry(TrackType.Normal);
                    trackLists.AddEntry(trackListEntry);
                    trackLists.AddTrackToLast(track);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error processing wishlist item: {ex.Message}");
            }
        }


    }
}

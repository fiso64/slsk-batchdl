using Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Extractors.Apis
{
    public static class BandcampApi
    {
        public static async Task<string?> ExtractFanIdFromPage(string wishlistUrl)
        {
            var web = new HtmlAgilityPack.HtmlWeb();
            var doc = await web.LoadFromWebAsync(wishlistUrl);

            // Extract fan_id from the page HTML
            string fanIdPattern = @"fan_id[""']?\s*[:=]\s*(\d+)";
            var pageContent = doc.DocumentNode.OuterHtml;
            var fanIdMatch = Regex.Match(pageContent, fanIdPattern);

            if (!fanIdMatch.Success)
            {
                Logger.Warn("Could not extract fan_id from page HTML");
                return null;
            }

            string fanId = fanIdMatch.Groups[1].Value;
            Logger.Info($"Found fan_id: {fanId}");
            return fanId;
        }



        public static async Task FetchWishlistItemsFromAPI(string fanId, TrackLists trackLists, string fanUsername = "")
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

        private static void ProcessWishlistItem(JsonElement item, TrackLists trackLists, int itemNumber)
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
                    Type = isAlbumItem ? Enums.TrackType.Album : Enums.TrackType.Normal,
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
                    var trackListEntry = new TrackListEntry(Enums.TrackType.Normal);
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

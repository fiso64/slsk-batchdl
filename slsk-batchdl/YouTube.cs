using Google.Apis.YouTube.v3;
using Google.Apis.Services;
using System.Xml;
using YoutubeExplode;
using System.Text.RegularExpressions;
using YoutubeExplode.Common;
using System.Diagnostics;
using HtmlAgilityPack;
using System.Text;
using System.Threading.Channels;
using System.Collections.Concurrent;

public static class YouTube
{
    private static YoutubeClient? youtube = new YoutubeClient();
    private static YouTubeService? youtubeService = null;
    public static string apiKey = "";

    public static async Task<(string, List<Track>)> GetTracksApi(string url, int max = int.MaxValue, int offset = 0)
    {
        StartService();

        string playlistId = await UrlToId(url);

        var playlistRequest = youtubeService.Playlists.List("snippet");
        playlistRequest.Id = playlistId;
        var playlistResponse = playlistRequest.Execute();

        string playlistName = playlistResponse.Items[0].Snippet.Title;

        var playlistItemsRequest = youtubeService.PlaylistItems.List("snippet,contentDetails");
        playlistItemsRequest.PlaylistId = playlistId;
        playlistItemsRequest.MaxResults = Math.Min(max, 100);

        var tracksDict = await GetDictYtExplode(url, max, offset);
        var tracks = new List<Track>();
        int count = 0;

        while (playlistItemsRequest != null && count < max + offset)
        {
            var playlistItemsResponse = playlistItemsRequest.Execute();
            foreach (var playlistItem in playlistItemsResponse.Items)
            {
                if (count >= offset)
                {
                    if (tracksDict.ContainsKey(playlistItem.Snippet.ResourceId.VideoId))
                        tracks.Add(tracksDict[playlistItem.Snippet.ResourceId.VideoId]);
                    else
                    {
                        var title = "";
                        var uploader = "";
                        var length = 0;
                        var desc = "";

                        var videoRequest = youtubeService.Videos.List("contentDetails,snippet");
                        videoRequest.Id = playlistItem.Snippet.ResourceId.VideoId;
                        var videoResponse = videoRequest.Execute();

                        title = playlistItem.Snippet.Title;
                        if (videoResponse.Items.Count == 0)
                            continue;
                        uploader = videoResponse.Items[0].Snippet.ChannelTitle;
                        length = (int)XmlConvert.ToTimeSpan(videoResponse.Items[0].ContentDetails.Duration).TotalSeconds;
                        desc = videoResponse.Items[0].Snippet.Description;

                        Track track = await ParseTrackInfo(title, uploader, playlistItem.Snippet.ResourceId.VideoId, length, false, desc);
                        tracks.Add(track);
                    }
                }

                if (++count >= max + offset)
                    break;
            }

            if (tracksDict.Count >= 200 && !Console.IsOutputRedirected)
            {
                Console.SetCursorPosition(0, Console.CursorTop);
                Console.Write($"Loaded: {tracks.Count}");
            }

            playlistItemsRequest.PageToken = playlistItemsResponse.NextPageToken;
            if (playlistItemsRequest.PageToken == null || count >= max + offset)
                playlistItemsRequest = null;
            else
                playlistItemsRequest.MaxResults = Math.Min(offset + max - count, 100);
        }

        Console.WriteLine();

        return (playlistName, tracks);
    }

    public static async Task<Track> ParseTrackInfo(string title, string uploader, string id, int length, bool requestInfoIfNeeded, string desc = "")
    {
        (string title, string uploader, int length, string desc) info = ("", "", -1, "");
        var track = new Track();
        track.URI = id;

        title = title.Replace("–", "-");

        var trackTitle = title.Trim();
        trackTitle = Regex.Replace(trackTitle, @"\s+", " ");
        var artist = uploader.Trim();

        if (artist.EndsWith("- Topic"))
        {
            artist = artist.Substring(0, artist.Length - 7).Trim();
            trackTitle = title;

            if (artist == "Various Artists")
            {
                if (desc == "" && requestInfoIfNeeded && id != "")
                {
                    info = await GetVideoInfo(id);
                    desc = info.desc;
                }
                
                if (desc != "")
                {
                    var lines = desc.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    var dotLine = lines.FirstOrDefault(line => line.Contains(" · "));

                    if (dotLine != null)
                        artist = dotLine.Split(new[] { " · " }, StringSplitOptions.None)[1];
                }
            }
        }
        else
        {
            int idx = title.IndexOf('-');
            var split = title.Split(new[] { '-' }, 2);
            if (idx > 0 && idx < title.Length - 1 && (title[idx - 1] == ' ' || title[idx + 1] == ' ') && split[0].Trim() != "" && split[1].Trim() != "")
            {
                artist = title.Split(new[] { '-' }, 2)[0].Trim();
                trackTitle = title.Split(new[] { '-' }, 2)[1].Trim();
            }
            else
            {
                track.ArtistMaybeWrong = true;
            }
        }

        if (length <= 0 && id != "" && requestInfoIfNeeded)
        {
            if (info.length > 0)
                length = info.length;
            else
            {
                info = await GetVideoInfo(id);
                length = info.length;
            }
        }

        track.Length = length;
        track.ArtistName = artist;
        track.TrackTitle = trackTitle;

        return track;
    }

    public static async Task<(string title, string uploader, int length, string desc)> GetVideoInfo(string id)
    {
        (string title, string uploader, int length, string desc) o = ("", "", -1, "");

        try
        {
            var vid = await youtube.Videos.GetAsync(id);
            o.title = vid.Title;
            o.uploader = vid.Author.ChannelTitle;
            o.desc = vid.Description;
            o.length = (int)vid.Duration.Value.TotalSeconds;
        }
        catch
        {
            if (apiKey != "")
            {
                try
                {
                    StartService();
                    var videoRequest = youtubeService.Videos.List("contentDetails,snippet");
                    videoRequest.Id = id;
                    var videoResponse = videoRequest.Execute();

                    o.title = videoResponse.Items[0].Snippet.Title;
                    o.uploader = videoResponse.Items[0].Snippet.ChannelTitle;
                    o.length = (int)XmlConvert.ToTimeSpan(videoResponse.Items[0].ContentDetails.Duration).TotalSeconds;
                    o.desc = videoResponse.Items[0].Snippet.Description;
                }
                catch { }
            }
        }

        return o;
    }

    public static void StartService()
    {
        if (youtubeService == null)
        {
            if (apiKey == "")
                throw new Exception("No API key");

            youtubeService = new YouTubeService(new BaseClientService.Initializer()
            {
                ApiKey = apiKey,
                ApplicationName = "slsk-batchdl"
            });
        }
    }

    public static void StopService()
    {
        youtubeService = null;
    }

    public static async Task<Dictionary<string, Track>> GetDictYtExplode(string url, int max = int.MaxValue, int offset = 0)
    {
        var youtube = new YoutubeClient();
        var playlist = await youtube.Playlists.GetAsync(url);

        var tracks = new Dictionary<string, Track>();
        int count = 0;

        await foreach (var video in youtube.Playlists.GetVideosAsync(playlist.Id))
        {
            if (count >= offset && count < offset + max)
            {
                var title = video.Title;
                var uploader = video.Author.Title;
                var ytId = video.Id.Value;
                var length = (int)video.Duration.Value.TotalSeconds;

                var track = await ParseTrackInfo(title, uploader, ytId, length, true);

                tracks[ytId] = track;
            }

            if (count++ >= offset + max)
                break;
        }

        return tracks;
    }

    public static async Task<(string, List<Track>)> GetTracksYtExplode(string url, int max = int.MaxValue, int offset = 0)
    {
        var youtube = new YoutubeClient();
        var playlist = await youtube.Playlists.GetAsync(url);

        var playlistTitle = playlist.Title;
        var tracks = new List<Track>();
        int count = 0;

        await foreach (var video in youtube.Playlists.GetVideosAsync(playlist.Id))
        {
            if (count >= offset && count < offset + max)
            {
                var title = video.Title;
                var uploader = video.Author.Title;
                var ytId = video.Id.Value;
                var length = (int)video.Duration.Value.TotalSeconds;

                var track = await ParseTrackInfo(title, uploader, ytId, length, true);

                tracks.Add(track);
            }

            if (count++ >= offset + max)
                break;
        }

        return (playlistTitle, tracks);
    }


    public static async Task<string> UrlToId(string url)
    {
        var playlist = await youtube.Playlists.GetAsync(url);
        return playlist.Id.ToString();
    }

    public class YouTubeArchiveRetriever
    {
        private HttpClient _client;

        public YouTubeArchiveRetriever()
        {
            _client = new HttpClient();
            _client.Timeout = TimeSpan.FromSeconds(10);
        }

        public async Task<List<Track>> RetrieveDeleted(string url)
        {
            var deletedVideoUrls = new BlockingCollection<string>();
            var tracks = new ConcurrentBag<Track>();

            var process = new Process()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = $"--ignore-no-formats-error --no-warn --match-filter \"!uploader\" --print webpage_url {url}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.EnableRaisingEvents = true;
            bool ok = false;
            process.OutputDataReceived += (sender, e) =>
            {
                if (!ok) { Console.WriteLine("Got first video"); ok = true; }
                deletedVideoUrls.Add(e.Data);
            };
            process.Exited += (sender, e) =>
            {
                deletedVideoUrls.CompleteAdding();
            };

            process.Start();
            process.BeginOutputReadLine();

            List<Task> workers = new List<Task>();
            int workerCount = 4;
            for (int i = 0; i < workerCount; i++)
            {
                workers.Add(Task.Run(async () =>
                {
                    foreach (var videoUrl in deletedVideoUrls.GetConsumingEnumerable())
                    {
                        var waybackUrl = await GetOldestArchiveUrl(videoUrl);
                        if (!string.IsNullOrEmpty(waybackUrl))
                        {
                            var x = await GetVideoDetails(waybackUrl);
                            if (!string.IsNullOrEmpty(x.title))
                            {
                                var track = await ParseTrackInfo(x.title, x.uploader, waybackUrl, x.duration, false);
                                tracks.Add(track);
                                if (!Console.IsOutputRedirected)
                                {
                                    Console.SetCursorPosition(0, Console.CursorTop);
                                    Console.Write($"Deleted videos processed: {tracks.Count}");
                                }
                            }
                        }
                    }
                }));
            }

            await Task.WhenAll(workers);
            process.WaitForExit();
            deletedVideoUrls.CompleteAdding();
            Console.WriteLine();
            return tracks.ToList();
        }

        private async Task<string> GetOldestArchiveUrl(string url)
        {
            var url2 = $"http://web.archive.org/cdx/search/cdx?url={url}&fl=timestamp,original&filter=statuscode:200&sort=timestamp:asc&limit=1";
            HttpResponseMessage response = null;
            for (int i = 0; i < 3; i++)
            {
                try {
                    response = await _client.GetAsync(url2);
                    break;
                }
                catch (Exception e) { }
            }
            if (response == null) return null;
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var lines = content.Split("\n").Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
                if (lines.Any())
                {
                    var parts = lines[0].Split(" ");
                    var timestamp = parts[0];
                    var originalUrl = parts[1];
                    var oldestArchive = $"http://web.archive.org/web/{timestamp}/{originalUrl}";
                    return oldestArchive;
                }
            }
            return null;
        }

        public async Task<(string title, string uploader, int duration)> GetVideoDetails(string url)
        {
            var web = new HtmlWeb();
            var doc = await web.LoadFromWebAsync(url);

            var titlePatterns = new[]
            {
                "//h1[@id='video_title']",
                "//meta[@name='title']",
            };

            var usernamePatterns = new[]
            {
                "//div[@id='userInfoDiv']/b/a",
                "//a[contains(@class, 'contributor')]",
                "//a[@id='watch-username']",
                "//a[contains(@class, 'author')]",
                "//div[@class='yt-user-info']/a",
                "//div[@id='upload-info']//yt-formatted-string/a",
                "//span[@itemprop='author']//link[@itemprop='name']",
                "//a[contains(@class, 'yt-user-name')]",
            };

            string getItem(string[] patterns)
            {
                foreach (var pattern in patterns)
                {
                    var node = doc.DocumentNode.SelectSingleNode(pattern);
                    var res = "";
                    if (node != null)
                    {
                        if (pattern.StartsWith("//meta") || pattern.Contains("@itemprop"))
                            res = node.GetAttributeValue("content", "");
                        else
                            res = node.InnerText;
                        if (!string.IsNullOrEmpty(res)) return res;
                    }
                }
                return "";
            }

            int duration = -1;
            var node = doc.DocumentNode.SelectSingleNode("//meta[@itemprop='duration']");
            if (node != null)
                duration = (int)XmlConvert.ToTimeSpan(node.GetAttributeValue("content", "")).TotalSeconds;
                
            return (getItem(titlePatterns), getItem(usernamePatterns), duration);
        }
    }
}

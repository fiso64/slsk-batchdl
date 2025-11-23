using Google.Apis.YouTube.v3;
using Google.Apis.Services;
using System.Xml;
using YoutubeExplode;
using System.Text.RegularExpressions;
using YoutubeExplode.Common;
using System.Diagnostics;
using HtmlAgilityPack;
using System.Collections.Concurrent;

using Models;
using Enums;

namespace Extractors
{
    public class YouTubeExtractor : IExtractor
    {
        public static bool InputMatches(string input)
        {
            input = input.ToLower();
            return input.IsInternetUrl() && (input.Contains("youtu.be") || input.Contains("youtube.com"));
        }

        public async Task<TrackLists> GetTracks(string input, int maxTracks, int offset, bool reverse, Config config)
        {
            var trackLists = new TrackLists();
            int max = reverse ? int.MaxValue : maxTracks;
            int off = reverse ? 0 : offset;
            YouTube.apiKey = config.ytKey;

            string name;
            List<Track>? deleted = null;
            List<Track> tracks = new();

            if (config.getDeleted)
            {
                Logger.Info("Getting deleted videos..");
                var archive = new YouTube.YouTubeArchiveRetriever();
                deleted = await archive.RetrieveDeleted(input, printFailed: config.deletedOnly);
            }
            if (!config.deletedOnly)
            {
                if (YouTube.apiKey.Length > 0)
                {
                    Logger.Info("Loading YouTube playlist (API)");
                    (name, tracks) = await YouTube.GetTracksApi(input, max, off);
                }
                else
                {
                    Logger.Info("Loading YouTube playlist");
                    (name, tracks) = await YouTube.GetTracksYtExplode(input, max, off);
                }
            }
            else
            {
                name = await YouTube.GetPlaylistTitle(input);
            }
            if (deleted != null)
            {
                tracks.InsertRange(0, deleted);
            }

            YouTube.StopService();

            var tle = new TrackListEntry(TrackType.Normal);

            tle.enablesIndexByDefault = true;
            tle.itemName = name;
            tle.list.Add(tracks);

            trackLists.AddEntry(tle);

            if (reverse)
            {
                trackLists.Reverse();
                trackLists = TrackLists.FromFlattened(trackLists.Flattened(true, false).Skip(offset).Take(maxTracks));
            }

            return trackLists;
        }
    }


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

                            Track track = await ParseTrackInfo(title, uploader, playlistItem.Snippet.ResourceId.VideoId, length, desc);
                            track.ItemNumber = count + 1;
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

        // requestInfoIfNeeded=true is way too slow
        public static async Task<Track> ParseTrackInfo(string title, string uploader, string id, int length, string desc = "", bool requestInfoIfNeeded = false)
        {
            (string title, string uploader, int length, string desc) info = ("", "", -1, "");
            var track = new Track();
            track.URI = id;

            uploader = uploader.Replace("–", "-").Trim().RemoveConsecutiveWs();
            title = title.Replace("–", "-").Replace(" -- ", " - ").Trim().RemoveConsecutiveWs();

            var artist = uploader;
            var trackTitle = title;

            if (artist.EndsWith(" - Topic"))
            {
                artist = artist[..^7].Trim();
                trackTitle = title;

                if (artist == "Various Artists")
                {
                    if (desc.Length == 0 && requestInfoIfNeeded && id.Length > 0)
                    {
                        info = await GetVideoInfo(id);
                        desc = info.desc;
                    }

                    if (desc.Length > 0)
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
                track.ArtistMaybeWrong = !title.ContainsWithBoundary(artist, true) && !desc.ContainsWithBoundary(artist, true);

                var split = title.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (split.Length == 2)
                {
                    artist = split[0];
                    trackTitle = split[1];
                    track.ArtistMaybeWrong = false;
                }
                else if (split.Length > 2)
                {
                    int index = Array.FindIndex(split, s => s.ContainsWithBoundary(artist, true));
                    if (index != -1 && index < split.Length - 1)
                    {
                        artist = split[index];
                        trackTitle = String.Join(" - ", split[(index + 1)..]);
                        track.ArtistMaybeWrong = false;
                    }
                }

                if (track.ArtistMaybeWrong && requestInfoIfNeeded && desc.Length == 0)
                {
                    info = await GetVideoInfo(id);
                    track.ArtistMaybeWrong = !info.desc.ContainsWithBoundary(artist, true);
                }
            }

            if (length <= 0 && id.Length > 0 && requestInfoIfNeeded)
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
            track.Artist = artist;
            track.Title = trackTitle;

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
                if (apiKey.Length > 0)
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
                if (apiKey.Length == 0)
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
                    var uploader = video.Author.ChannelTitle;
                    var ytId = video.Id.Value;
                    var length = (int)video.Duration.Value.TotalSeconds;

                    var track = await ParseTrackInfo(title, uploader, ytId, length);
                    track.ItemNumber = count + 1;

                    tracks[ytId] = track;
                }

                if (count++ >= offset + max)
                    break;
            }

            return tracks;
        }

        public static async Task<string> GetPlaylistTitle(string url)
        {
            var youtube = new YoutubeClient();
            var playlist = await youtube.Playlists.GetAsync(url);
            return playlist.Title;
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
                    var uploader = video.Author.ChannelTitle;
                    var ytId = video.Id.Value;
                    var length = (int)video.Duration.Value.TotalSeconds;

                    var track = await ParseTrackInfo(title, uploader, ytId, length);
                    track.ItemNumber = count + 1;
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

            public async Task<List<Track>> RetrieveDeleted(string url, bool printFailed = true)
            {
                var deletedVideoUrls = new BlockingCollection<string>();

                int totalCount = 0;
                int archivedCount = 0;
                var tracks = new ConcurrentBag<Track>();
                var noArchive = new ConcurrentBag<string>();
                var failRetrieve = new ConcurrentBag<string>();

                int workerCount = 4;
                var workers = new List<Task>();
                var consoleLock = new object();

                void updateInfo()
                {
                    lock (consoleLock)
                    {
                        if (!Console.IsOutputRedirected)
                        {
                            string info = "Deleted metadata total/archived/retrieved: ";
                            Console.SetCursorPosition(0, Console.CursorTop);
                            Console.Write($"{info}{totalCount}/{archivedCount}/{tracks.Count}");
                        }
                    }
                }

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "yt-dlp",
                        Arguments = $"--ignore-no-formats-error --no-warn --match-filter \"!uploader\" --print webpage_url {url}",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                    EnableRaisingEvents = true
                };
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        deletedVideoUrls.Add(e.Data);
                        Interlocked.Increment(ref totalCount);
                        updateInfo();
                    }
                };
                process.Exited += (sender, e) =>
                {
                    deletedVideoUrls.CompleteAdding();
                };

                process.Start();
                process.BeginOutputReadLine();

                for (int i = 0; i < workerCount; i++)
                {
                    workers.Add(Task.Run(async () =>
                    {
                        foreach (var videoUrl in deletedVideoUrls.GetConsumingEnumerable())
                        {
                            var waybackUrls = await GetOldestArchiveUrls(videoUrl, limit: 2);
                            if (waybackUrls != null && waybackUrls.Count > 0)
                            {
                                Interlocked.Increment(ref archivedCount);

                                bool good = false;
                                foreach (var waybackUrl in waybackUrls)
                                {
                                    var (title, uploader, duration) = await GetVideoDetails(waybackUrl);
                                    if (!string.IsNullOrWhiteSpace(title))
                                    {
                                        var track = await ParseTrackInfo(title, uploader, waybackUrl, duration);
                                        track.Other = $"{{\"t\":\"{title.Trim()}\",\"u\":\"{uploader.Trim()}\"}}";
                                        tracks.Add(track);
                                        good = true;
                                        break;
                                    }
                                }

                                if (!good)
                                {
                                    failRetrieve.Add(waybackUrls[0]);
                                }
                            }
                            else
                            {
                                noArchive.Add(videoUrl);
                            }

                            updateInfo();
                        }
                    }));
                }

                await Task.WhenAll(workers);
                process.WaitForExit();
                deletedVideoUrls.CompleteAdding();
                Console.WriteLine();

                if (printFailed)
                {
                    if (archivedCount < totalCount)
                    {
                        Logger.Info("No archived version found for the following:");
                        foreach (var x in noArchive)
                            Logger.Info($"  {x}");
                        Console.WriteLine();

                    }
                    if (tracks.Count < archivedCount)
                    {
                        Logger.Info("Failed to parse archived version for the following:");
                        foreach (var x in failRetrieve)
                            Logger.Info($"  {x}");
                        Console.WriteLine();
                    }
                }

                return tracks.ToList();
            }

            private async Task<List<string>> GetOldestArchiveUrls(string url, int limit)
            {
                var url2 = $"http://web.archive.org/cdx/search/cdx?url={url}&fl=timestamp,original&filter=statuscode:200&sort=timestamp:asc&limit={limit}";
                HttpResponseMessage response = null;
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        response = await _client.GetAsync(url2);
                        break;
                    }
                    catch { }
                }
                if (response == null) return null;

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var lines = content.Split("\n").Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
                    if (lines.Count > 0)
                    {
                        for (int i = 0; i < lines.Count; i++)
                        {
                            var parts = lines[i].Split(" ");
                            var timestamp = parts[0];
                            var originalUrl = parts[1];
                            lines[i] = $"http://web.archive.org/web/{timestamp}/{originalUrl}";
                        }
                        return lines;
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
                        if (node != null)
                        {
                            var res = "";
                            if (pattern.StartsWith("//meta") || pattern.Contains("@itemprop"))
                                res = node.GetAttributeValue("content", "");
                            else
                                res = node.InnerText;
                            if (!string.IsNullOrEmpty(res))
                                return Utils.UnHtmlString(res);
                        }
                    }
                    return "";
                }

                var title = getItem(titlePatterns);
                if (string.IsNullOrEmpty(title))
                {
                    var pattern = @"document\.title\s*=\s*""(.+?) - YouTube"";";
                    var match = Regex.Match(doc.Text, pattern);
                    if (match.Success)
                        title = match.Groups[1].Value;
                }

                var username = getItem(usernamePatterns);

                int duration = -1;
                var node = doc.DocumentNode.SelectSingleNode("//meta[@itemprop='duration']");
                if (node != null)
                {
                    try
                    {
                        duration = (int)XmlConvert.ToTimeSpan(node.GetAttributeValue("content", "")).TotalSeconds;
                    }
                    catch { }
                }

                return (title, username, duration);
            }
        }

        public static async Task<List<(int length, string id, string title)>> YtdlpSearch(Track track)
        {
            Process process = new Process();
            ProcessStartInfo startInfo = new ProcessStartInfo();

            startInfo.FileName = "yt-dlp";
            string search = track.Artist.Length > 0 ? $"{track.Artist} - {track.Title}" : track.Title;
            startInfo.Arguments = $"\"ytsearch3:{search}\" --print \"%(duration>%s)s === %(id)s === %(title)s\"";

            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            process.StartInfo = startInfo;
            process.OutputDataReceived += (sender, e) => { Logger.Info(e.Data ?? ""); };
            process.ErrorDataReceived += (sender, e) => { Logger.Info(e.Data ?? ""); };

            Logger.Debug($"{startInfo.FileName} {startInfo.Arguments}");

            process.Start();

            List<(int, string, string)> results = new List<(int, string, string)>();
            string output;
            Regex regex = new Regex(@"^(\d+) === ([\w-]+) === (.+)$");
            while ((output = process.StandardOutput.ReadLine()) != null)
            {
                Match match = regex.Match(output);
                if (match.Success)
                {
                    int seconds = int.Parse(match.Groups[1].Value);
                    string id = match.Groups[2].Value;
                    string title = match.Groups[3].Value;
                    results.Add((seconds, id, title));
                }
            }

            process.WaitForExit();
            return results;
        }

        public static async Task<string> YtdlpDownload(string id, string savePathNoExt, string ytdlpArgument = "")
        {
            var process = new Process();
            var startInfo = new ProcessStartInfo();

            bool isCustomPath = ytdlpArgument.Length > 0 && !ytdlpArgument.Contains("{savepath-noext}.%(ext)s") && !ytdlpArgument.Contains("{savepath}.%(ext)s");

            if (ytdlpArgument.Length == 0)
                ytdlpArgument = "\"{id}\" -f bestaudio/best -ci -o \"{savepath-noext}.%(ext)s\" -x";

            startInfo.FileName = "yt-dlp";
            startInfo.Arguments = ytdlpArgument
                .Replace("{id}", id)
                .Replace("{savepath}", savePathNoExt)
                .Replace("{savepath-noext}", savePathNoExt)
                .Replace("{savedir}", Path.GetDirectoryName(savePathNoExt));

            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            process.StartInfo = startInfo;
            //process.OutputDataReceived += (sender, e) => { Console.WriteLine(e.Data); };
            //process.ErrorDataReceived += (sender, e) => { Console.WriteLine(e.Data); };

            Logger.Debug($"{startInfo.FileName} {startInfo.Arguments}");

            process.Start();
            process.WaitForExit();

            if (File.Exists(savePathNoExt + ".opus"))
                return savePathNoExt + ".opus";

            string parentDirectory = Path.GetDirectoryName(savePathNoExt);
            string fileName = Path.GetFileName(savePathNoExt);

            var musicFiles = Enumerable.Empty<string>();
            try
            {
                musicFiles = Directory.GetFiles(parentDirectory, fileName + ".*", SearchOption.TopDirectoryOnly)
                    .Where(file => Utils.IsMusicFile(file) || Utils.IsVideoFile(file))
                    .OrderByDescending(file => Utils.IsMusicFile(file))
                    .ThenBy(file => Utils.IsVideoFile(file));
            }
            catch (DirectoryNotFoundException) { }

            if (!musicFiles.Any())
            {
                if (isCustomPath)
                    Logger.Debug($"Could not find yt-dlp output file. This is expected if using a custom output path argument.");
                else
                    throw new FileNotFoundException($"Could not find yt-dlp output file after download in {parentDirectory}/{fileName}.*");
            }

            return musicFiles.FirstOrDefault() ?? "";
        }
    }
}

using Google.Apis.YouTube.v3;
using Google.Apis.Services;
using System.Xml;
using YoutubeExplode;
using System.Text.RegularExpressions;


public static class YouTube
{
    private static YoutubeClient? youtube = new YoutubeClient();
    private static YouTubeService? youtubeService = null;
    public static string apiKey = "";

    public static async Task<(string, List<Track>)> GetTracksApi(string url, StringEdit strEdit)
    {
        StartService();

        string playlistId = await UrlToId(url);

        var playlistRequest = youtubeService.Playlists.List("snippet");
        playlistRequest.Id = playlistId;
        var playlistResponse = playlistRequest.Execute();

        string playlistName = playlistResponse.Items[0].Snippet.Title;

        var playlistItemsRequest = youtubeService.PlaylistItems.List("snippet,contentDetails");
        playlistItemsRequest.PlaylistId = playlistId;
        playlistItemsRequest.MaxResults = 100;

        var tracksDict = await GetDictYtExplode(url, strEdit);
        var tracks = new List<Track>();

        while (playlistItemsRequest != null)
        {
            var playlistItemsResponse = playlistItemsRequest.Execute();

            foreach (var playlistItem in playlistItemsResponse.Items)
            {
                if (tracksDict.ContainsKey(playlistItem.Snippet.ResourceId.VideoId)) 
                {
                    tracks.Add(tracksDict[playlistItem.Snippet.ResourceId.VideoId]);
                }
                else
                {
                    var title = "";
                    var uploader = "";
                    var length = 0;
                    var desc = "";
                    
                    try
                    {
                        var video = await youtube.Videos.GetAsync(playlistItem.Snippet.ResourceId.VideoId);
                        title = video.Title;
                        uploader = video.Author.Title;
                        length = (int)video.Duration.Value.TotalSeconds;
                        desc = video.Description;
                    }
                    catch
                    {
                        var videoRequest = youtubeService.Videos.List("contentDetails,snippet");
                        videoRequest.Id = playlistItem.Snippet.ResourceId.VideoId;
                        var videoResponse = videoRequest.Execute();

                        title = playlistItem.Snippet.Title;
                        if (videoResponse.Items.Count == 0)
                            continue;
                        uploader = videoResponse.Items[0].Snippet.ChannelTitle;
                        length = (int)XmlConvert.ToTimeSpan(videoResponse.Items[0].ContentDetails.Duration).TotalSeconds;
                        desc = videoResponse.Items[0].Snippet.Description;
                    }

                    Track track = await ParseTrackInfo(strEdit.Edit(title), uploader, playlistItem.Snippet.ResourceId.VideoId, length, false, desc);
                    tracks.Add(track);
                }
            }

            if (tracksDict.Count >= 200)
            {
                Console.SetCursorPosition(0, Console.CursorTop);
                Console.Write(tracks.Count);
            }

            playlistItemsRequest.PageToken = playlistItemsResponse.NextPageToken;
            if (playlistItemsRequest.PageToken == null)
            {
                playlistItemsRequest = null;
            }
        }

        Console.WriteLine();

        return (playlistName, tracks);
    }

    public static async Task<Track> ParseTrackInfo(string title, string uploader, string id, int length, bool requestInfoIfNeeded, string desc = "")
    {
        (string title, string uploader, int length, string desc) info = ("", "", -1, "");
        var track = new Track();
        track.YtID = id;

        title = title.Replace("–", "-");

        var stringsToRemove = new string[] { "(Official music video)", "(Official video)", "(Official audio)",
                    "(Lyrics)", "(Official)", "(Lyric Video)", "(Official Lyric Video)", "(Official HD Video)",
                    "(Official 4K Video)", "(Video)", "[HD]", "[4K]", "(Original Mix)", "(Lyric)", "(Music Video)", 
                    "(Visualizer)", "(Audio)", "Official Lyrics" };

        foreach (string s in stringsToRemove)
        {
            var t = title;
            title = Regex.Replace(title, Regex.Escape(s), "", RegexOptions.IgnoreCase);
            if (t == title)
            {
                if (s.Contains("["))
                {
                    string s2 = s.Replace("[", "(").Replace("]", ")");
                    title = Regex.Replace(title, Regex.Escape(s2), "", RegexOptions.IgnoreCase);
                }
                else if (s.Contains("("))
                {
                    string s2 = s.Replace("(", "[").Replace(")", "]");
                    title = Regex.Replace(title, Regex.Escape(s2), "", RegexOptions.IgnoreCase);
                }
            }
        }

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
        //try { youtubeService.Dispose(); }
        //catch { }
        youtubeService = null;
    }

    public static async Task<Dictionary<string, Track>> GetDictYtExplode(string url, StringEdit strEdit)
    {
        var youtube = new YoutubeClient();
        var playlist = await youtube.Playlists.GetAsync(url);

        var tracks = new Dictionary<string, Track>();

        await foreach (var video in youtube.Playlists.GetVideosAsync(playlist.Id))
        {
            var title = strEdit.Edit(video.Title);
            var uploader = video.Author.Title;
            var ytId = video.Id.Value;
            var length = (int)video.Duration.Value.TotalSeconds;

            var track = await ParseTrackInfo(title, uploader, ytId, length, true);

            tracks[ytId] = track;
        }

        return tracks;
    }

    public static async Task<(string, List<Track>)> GetTracksYtExplode(string url, StringEdit strEdit)
    {
        var playlist = await youtube.Playlists.GetAsync(url);

        var playlistTitle = playlist.Title;
        var tracks = new List<Track>();

        await foreach (var video in youtube.Playlists.GetVideosAsync(playlist.Id))
        {
            var title = strEdit.Edit(video.Title);
            var uploader = video.Author.Title;
            var ytId = video.Id.Value;
            var length = (int)video.Duration.Value.TotalSeconds;

            var track = await ParseTrackInfo(title, uploader, ytId, length, true);

            tracks.Add(track);
        }

        return (playlistTitle, tracks);
    }

    public static async Task<string> UrlToId(string url)
    {
        var playlist = await youtube.Playlists.GetAsync(url);
        return playlist.Id.ToString();
    }
}

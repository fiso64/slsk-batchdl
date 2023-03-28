using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YoutubeExplode;


public static class YouTube
{
    public static async Task<(string, List<Track>)> GetTracks(string url)
    {
        var youtube = new YoutubeClient();
        var playlist = await youtube.Playlists.GetAsync(url);

        var playlistTitle = playlist.Title;
        var tracks = new List<Track>();
        var videoTasks = new List<(ValueTask<YoutubeExplode.Videos.Video>, int)>();

        await foreach (var video in youtube.Playlists.GetVideosAsync(playlist.Id))
        {
            var title = video.Title;
            var uploader = video.Author.Title;
            var ytId = video.Id.Value;
            var length = (int)video.Duration.Value.TotalSeconds;

            title = title.Replace("–", "-");

            var trackTitle = title.Trim();
            var artist = uploader.Trim();

            if (artist.EndsWith("- Topic"))
            {
                artist = artist.Substring(0, artist.Length - 7).Trim();
                trackTitle = title;

                if (artist == "Various Artists")
                {
                    //var vid = await youtube.Videos.GetAsync(video.Id);
                    videoTasks.Add((youtube.Videos.GetAsync(video.Id), tracks.Count));
                    //Thread.Sleep(20);
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
                    artist = uploader;
                    trackTitle = title;
                }
            }

            var track = new Track
            {
                UnparsedTitle = video.Title,
                Uploader = uploader,
                TrackTitle = trackTitle,
                ArtistName = artist,
                YtID = ytId,
                Length = length
            };

            tracks.Add(track);
        }

        foreach ((var vidTask, int idx) in videoTasks)
        {
            var vid = await vidTask;

            var lines = vid.Description.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var dotLine = lines.FirstOrDefault(line => line.Contains(" · ")); 

            if (dotLine != null)
            {
                var t = tracks[idx];
                t.ArtistName = dotLine.Split(new[] { " · " }, StringSplitOptions.None)[1]; // can't be asked to do it properly
                tracks[idx] = t;
            }
        }

        return (playlistTitle, tracks);
    }
}

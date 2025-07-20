using Models;
using Enums;
using System.Web;
using Soulseek;

namespace Extractors
{
    public class SoulseekExtractor : IExtractor
    {
        public static bool InputMatches(string input)
        {
            return input.StartsWith("slsk://", StringComparison.OrdinalIgnoreCase);
        }

        public Task<TrackLists> GetTracks(string input, int maxTracks, int offset, bool reverse, Config config)
        {
            var res = new TrackLists();
            var track = new Track()
            {
                URI = HttpUtility.UrlDecode(input),
                IsDirectLink = true,
            };

            if (input.EndsWith('/') || config.album)
            {
                track.Type = TrackType.Album;
                res.AddEntry(new TrackListEntry(track));
                res.lists[0].sourceCanBeSkipped = false;
            }
            else
            {
                var parts = track.URI["slsk://".Length..].Split('/', 2);
                var username = parts[0];
                var path = parts[1].TrimEnd('/').Replace('/', '\\');

                var response = new SearchResponse(username, -1, false, -1, -1, null);
                var file = new Soulseek.File(-1, path, -1, Path.GetExtension(path));

                track.Downloads = new() { (response, file) };

                res.AddTrackToLast(track);
                res.lists[0].sourceCanBeSkipped = false;
            }
            return Task.FromResult(res);
        }
    }
}

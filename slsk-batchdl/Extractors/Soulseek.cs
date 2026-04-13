using Models;
using Jobs;
using System.Web;
using Soulseek;
using Settings;

namespace Extractors
{
    public class SoulseekExtractor : IExtractor, IInputMatcher
    {
        public static bool InputMatches(string input)
        {
            return input.StartsWith("slsk://", StringComparison.OrdinalIgnoreCase);
        }

        public Task<Job> GetTracks(string input, ExtractionSettings extraction)
        {
            var uri = HttpUtility.UrlDecode(input);

            if (input.EndsWith('/') || extraction.IsAlbum)
            {
                // Direct-link album: the URI is the folder path
                var query = new AlbumQuery { URI = uri, IsDirectLink = true };
                return Task.FromResult<Job>(new AlbumJob(query) { CanBeSkippedOverride = false });
            }
            else
            {
                // Direct-link single file: pre-populate Candidates so search is skipped
                var parts = uri["slsk://".Length..].Split('/', 2);
                var username = parts[0];
                var path = parts[1].TrimEnd('/').Replace('/', '\\');

                var response = new SearchResponse(username, -1, false, -1, -1, null);
                var file = new Soulseek.File(-1, path, -1, Path.GetExtension(path));
                var candidate = new FileCandidate(response, file);

                var query = new SongQuery { URI = uri, IsDirectLink = true };
                var song = new SongJob(query) { Candidates = new List<FileCandidate> { candidate } };
                return Task.FromResult<Job>(song);
            }
        }
    }
}

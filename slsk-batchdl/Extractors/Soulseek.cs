using Models;
using Jobs;
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

        public Task<List<QueryJob>> GetTracks(string input, int maxTracks, int offset, bool reverse, Config config)
        {
            var jobs = new List<QueryJob>();
            var uri = HttpUtility.UrlDecode(input);

            if (input.EndsWith('/') || config.album)
            {
                // Direct-link album: the URI is the folder path
                var query = new AlbumQuery { URI = uri, IsDirectLink = true };
                var job = new AlbumQueryJob(query) { CanBeSkippedOverride = false };
                jobs.Add(job);
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

                var slj = new SongListQueryJob();
                slj.Songs.Add(song);
                jobs.Add(slj);
            }

            return Task.FromResult(jobs);
        }
    }
}

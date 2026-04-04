using Jobs;
using Models;
using Swan;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Extractors
{
    public class MusicBrainzExtractor : IExtractor
    {
        public static bool InputMatches(string input)
        {
            return input.IsInternetUrl() && input.ToLower().Contains("musicbrainz.org");
        }

        public async Task<List<QueryJob>> GetTracks(string input, int maxTracks, int offset, bool reverse, Config config)
        {
            var musicBrainzClient = new MusicBrainzClient();

            int max = reverse ? int.MaxValue : maxTracks;
            int off = reverse ? 0 : offset;

            JobQueue queue;

            var match = Regex.Match(input, @"musicbrainz\.org/([a-z\-]+)/([0-9a-f\-]{36})");
            if (!match.Success)
                throw new ArgumentException($"Could not parse MusicBrainz URL: {input}");

            var entityType = match.Groups[1].Value;
            var mbid       = match.Groups[2].Value;

            switch (entityType)
            {
                case "release":
                    queue = await musicBrainzClient.GetReleaseAsAlbum(mbid, max, off, config);
                    break;
                case "release-group":
                    queue = await musicBrainzClient.GetReleaseGroupAsAlbum(mbid, max, off, config);
                    break;
                case "collection":
                    queue = await musicBrainzClient.GetCollectionReleases(mbid, max, off);
                    break;
                case "artist":
                    throw new Exception("MusicBrainz artist download currently not supported.");
                default:
                    throw new ArgumentException($"Unsupported MusicBrainz entity type: {entityType}");
            }

            var jobs = queue.Jobs.Cast<QueryJob>().ToList();

            if (reverse)
            {
                JobQueue.ReverseJobList(jobs);
                jobs = jobs.Skip(offset).Take(maxTracks).ToList();
            }

            return jobs;
        }
    }

    public class MusicBrainzClient
    {
        private readonly HttpClient _httpClient;

        public MusicBrainzClient()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("slsk-batchdl/1.0 ( https://github.com/fiso64/slsk-batchdl )");
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<JobQueue> GetReleaseAsAlbum(string mbid, int max, int offset, Config config, bool fromReleaseGroup = false)
        {
            var queue = new JobQueue();
            if (offset > 0 || max == 0)
                return queue;

            Logger.Info("Loading MusicBrainz release...");
            var url      = $"https://musicbrainz.org/ws/2/release/{mbid}?inc=artist-credits+media&fmt=json";
            var response = await _httpClient.GetStringAsync(url);
            var release  = JsonDocument.Parse(response).RootElement;

            var artistCredit = release.GetProperty("artist-credit")[0].GetProperty("name").GetString();
            var albumTitle   = release.GetProperty("title").GetString();

            int totalTracks = 0;
            if (release.TryGetProperty("media", out var media))
            {
                foreach (var medium in media.EnumerateArray())
                {
                    if (medium.TryGetProperty("track-count", out var trackCount))
                        totalTracks += trackCount.GetInt32();
                }
            }

            var query = new AlbumQuery
            {
                Artist        = artistCredit,
                Album         = albumTitle,
                MinTrackCount = totalTracks,
                MaxTrackCount = (!fromReleaseGroup || config.setAlbumMaxTrackCount) ? totalTracks : -1,
            };

            queue.Enqueue(new AlbumQueryJob(query));
            return queue;
        }

        public async Task<JobQueue> GetReleaseGroupAsAlbum(string mbid, int max, int offset, Config config)
        {
            Logger.Info("Loading MusicBrainz release group...");
            var url          = $"https://musicbrainz.org/ws/2/release-group/{mbid}?inc=releases&fmt=json";
            var response     = await _httpClient.GetStringAsync(url);
            var releaseGroup = JsonDocument.Parse(response).RootElement;

            var releases = releaseGroup.GetProperty("releases").EnumerateArray().ToList();
            if (!releases.Any())
            {
                Logger.Info("Release group contains no releases.");
                return new JobQueue();
            }

            var bestRelease = releases.FirstOrDefault(r => r.TryGetProperty("status", out var s) && s.GetString() == "Official");
            if (bestRelease.ValueKind == JsonValueKind.Undefined)
                bestRelease = releases.FirstOrDefault(r => r.TryGetProperty("release-group", out var rg) && rg.TryGetProperty("primary-type", out var pt) && pt.GetString() == "Album");
            if (bestRelease.ValueKind == JsonValueKind.Undefined)
                bestRelease = releases.First();

            var releaseMbid = bestRelease.GetProperty("id").GetString();
            Logger.Info($"Found release '{bestRelease.GetProperty("title").GetString()}' ({releaseMbid}) in release group. Getting album info...");
            return await GetReleaseAsAlbum(releaseMbid, max, offset, config, true);
        }

        public async Task<JobQueue> GetCollectionReleases(string mbid, int max, int offset)
        {
            var collectionInfoUrl      = $"https://musicbrainz.org/ws/2/collection/{mbid}?fmt=json";
            var collectionInfoResponse = await _httpClient.GetStringAsync(collectionInfoUrl);
            var collectionInfo         = JsonDocument.Parse(collectionInfoResponse).RootElement;
            var collectionName         = collectionInfo.GetProperty("name").GetString();
            Logger.Info($"Loading releases from MusicBrainz collection '{collectionName}'...");

            var queue  = new JobQueue();
            int limit  = Math.Min(max, 100);
            int currentOffset = offset;
            int count  = 0;

            while (count < max)
            {
                var url            = $"https://musicbrainz.org/ws/2/collection/{mbid}/releases?limit={limit}&offset={currentOffset}&fmt=json";
                var response       = await _httpClient.GetStringAsync(url);
                var collectionData = JsonDocument.Parse(response).RootElement;

                var releases = collectionData.GetProperty("releases").EnumerateArray().ToList();
                if (!releases.Any()) break;

                foreach (var release in releases)
                {
                    if (count >= max) break;

                    var artistCredit = release.GetProperty("artist-credit")[0].GetProperty("name").GetString();
                    var albumTitle   = release.GetProperty("title").GetString();
                    var trackCount   = release.GetProperty("track-count").GetInt32();
                    var releaseId    = release.GetProperty("id").GetString();

                    var query = new AlbumQuery
                    {
                        Artist        = artistCredit,
                        Album         = albumTitle,
                        URI           = releaseId,
                        MinTrackCount = trackCount,
                        MaxTrackCount = trackCount,
                    };

                    var job = new AlbumQueryJob(query)
                    {
                        ItemNumber            = offset + count + 1,
                        ItemName              = collectionName,
                        EnablesIndexByDefault = true,
                    };
                    queue.Enqueue(job);
                    count++;
                }

                if (releases.Count < limit) break;
                currentOffset += limit;
            }

            Logger.Info($"Found {queue.Count} releases in MusicBrainz collection '{collectionName}'");
            return queue;
        }
    }
}

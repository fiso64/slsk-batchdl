using Enums;
using Models;
using Swan;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Extractors
{
    public class MusicBrainzExtractor : IExtractor
    {
        public static bool InputMatches(string input)
        {
            return input.IsInternetUrl() && input.ToLower().Contains("musicbrainz.org");
        }

        public async Task<TrackLists> GetTracks(string input, int maxTracks, int offset, bool reverse, Config config)
        {
            var musicBrainzClient = new MusicBrainzClient();

            int max = reverse ? int.MaxValue : maxTracks;
            int off = reverse ? 0 : offset;

            var trackLists = new TrackLists();

            var match = Regex.Match(input, @"musicbrainz\.org/([a-z\-]+)/([0-9a-f\-]{36})");
            if (!match.Success)
            {
                throw new ArgumentException($"Could not parse MusicBrainz URL: {input}");
            }

            var entityType = match.Groups[1].Value;
            var mbid = match.Groups[2].Value;

            switch (entityType)
            {
                case "release":
                    trackLists = await musicBrainzClient.GetReleaseAsAlbum(mbid, max, off, config);
                    break;
                case "release-group":
                    trackLists = await musicBrainzClient.GetReleaseGroupAsAlbum(mbid, max, off, config);
                    break;
                case "collection":
                    trackLists = await musicBrainzClient.GetCollectionReleases(mbid, max, off);
                    break;
                case "artist":
                    Logger.Fatal("Error: MusicBrainz artist download currently not supported.");
                    Environment.Exit(1);
                    break;
                default:
                    throw new ArgumentException($"Unsupported MusicBrainz entity type: {entityType}");
            }


            if (reverse)
            {
                trackLists.Reverse();
                trackLists = TrackLists.FromFlattened(trackLists.Flattened(true, false).Skip(offset).Take(maxTracks));
            }

            return trackLists;
        }
    }

    public class MusicBrainzClient
    {
        private readonly HttpClient _httpClient;

        public MusicBrainzClient()
        {
            _httpClient = new HttpClient();
            // MusicBrainz API requires a user agent.
            // See: https://musicbrainz.org/doc/Development/XML_Web_Service/Rate_Limiting
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("slsk-batchdl/1.0 ( https://github.com/fiso64/slsk-batchdl )");
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<TrackLists> GetReleaseAsAlbum(string mbid, int max, int offset, Config config, bool fromReleaseGroup = false)
        {
            var trackLists = new TrackLists();
            if (offset > 0 || max == 0)
            {
                return trackLists;
            }

            Logger.Info("Loading MusicBrainz release...");
            var url = $"https://musicbrainz.org/ws/2/release/{mbid}?inc=artist-credits+media&fmt=json";
            var response = await _httpClient.GetStringAsync(url);
            var release = JsonDocument.Parse(response).RootElement;

            var artistCredit = release.GetProperty("artist-credit")[0].GetProperty("name").GetString();
            var albumTitle = release.GetProperty("title").GetString();

            int totalTracks = 0;
            if (release.TryGetProperty("media", out var media))
            {
                foreach (var medium in media.EnumerateArray())
                {
                    if (medium.TryGetProperty("track-count", out var trackCount))
                    {
                        totalTracks += trackCount.GetInt32();
                    }
                }
            }

            var albumTrack = new Track
            {
                Artist = artistCredit,
                Album = albumTitle,
                MinAlbumTrackCount = totalTracks,
                MaxAlbumTrackCount = (!fromReleaseGroup || config.setAlbumMaxTrackCount) ? totalTracks : -1,
                Type = TrackType.Album,
            };

            var tle = new TrackListEntry(albumTrack);
            trackLists.AddEntry(tle);

            return trackLists;
        }

        public async Task<TrackLists> GetReleaseGroupAsAlbum(string mbid, int max, int offset, Config config)
        {
            Logger.Info("Loading MusicBrainz release group...");
            var url = $"https://musicbrainz.org/ws/2/release-group/{mbid}?inc=releases&fmt=json";
            var response = await _httpClient.GetStringAsync(url);
            var releaseGroup = JsonDocument.Parse(response).RootElement;

            var releases = releaseGroup.GetProperty("releases").EnumerateArray().ToList();
            if (!releases.Any())
            {
                Logger.Info("Release group contains no releases.");
                return new TrackLists();
            }

            // Heuristic: prefer 'Official' releases, then 'Album' type, then just take the first.
            var bestRelease = releases.FirstOrDefault(r => r.TryGetProperty("status", out var s) && s.GetString() == "Official");
            if (bestRelease.ValueKind == JsonValueKind.Undefined)
            {
                bestRelease = releases.FirstOrDefault(r => r.TryGetProperty("release-group", out var rg) && rg.TryGetProperty("primary-type", out var pt) && pt.GetString() == "Album");
            }
            if (bestRelease.ValueKind == JsonValueKind.Undefined)
            {
                bestRelease = releases.First();
            }

            var releaseMbid = bestRelease.GetProperty("id").GetString();
            Logger.Info($"Found release '{bestRelease.GetProperty("title").GetString()}' ({releaseMbid}) in release group. Getting album info...");
            return await GetReleaseAsAlbum(releaseMbid, max, offset, config, true);
        }

        public async Task<TrackLists> GetCollectionReleases(string mbid, int max, int offset)
        {
            var collectionInfoUrl = $"https://musicbrainz.org/ws/2/collection/{mbid}?fmt=json";
            var collectionInfoResponse = await _httpClient.GetStringAsync(collectionInfoUrl);
            var collectionInfo = JsonDocument.Parse(collectionInfoResponse).RootElement;
            var collectionName = collectionInfo.GetProperty("name").GetString();
            Logger.Info($"Loading releases from MusicBrainz collection '{collectionName}'...");

            var trackLists = new TrackLists();
            int limit = Math.Min(max, 100);
            int currentOffset = offset;
            int count = 0;

            while (count < max)
            {
                var url = $"https://musicbrainz.org/ws/2/collection/{mbid}/releases?limit={limit}&offset={currentOffset}&fmt=json";
                var response = await _httpClient.GetStringAsync(url);
                var collectionData = JsonDocument.Parse(response).RootElement;

                var releases = collectionData.GetProperty("releases").EnumerateArray().ToList();
                if (!releases.Any())
                {
                    break; // No more releases
                }

                foreach (var release in releases)
                {
                    if (count >= max) break;

                    var artistCredit = release.GetProperty("artist-credit")[0].GetProperty("name").GetString();
                    var albumTitle = release.GetProperty("title").GetString();
                    var trackCount = release.GetProperty("track-count").GetInt32();

                    var tle = new TrackListEntry(new Track
                    {
                        Artist = artistCredit,
                        Album = albumTitle,
                        MinAlbumTrackCount = trackCount,
                        MaxAlbumTrackCount = trackCount,
                        ItemNumber = offset + count + 1,
                        Type = TrackType.Album,
                        URI = release.GetProperty("id").GetString(),
                    });

                    tle.itemName = collectionName;
                    tle.enablesIndexByDefault = true;
                    trackLists.AddEntry(tle);

                    count++;
                }

                if (releases.Count < limit)
                {
                    break; // Last page
                }

                currentOffset += limit;
            }

            Logger.Info($"Found {trackLists.lists.Count} releases in MusicBrainz collection '{collectionName}'");
            return trackLists;
        }
    }
}
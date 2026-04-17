using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Soulseek;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Settings;
using SlFile = Soulseek.File;
using SlResponse = Soulseek.SearchResponse;

namespace Sldl.Core.Services;

public static partial class SearchResultProjector
{
    public static List<FileCandidate> SortedTrackCandidates(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> rawResults,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = true,
        bool useLevenshtein = true)
        => ResultSorter.OrderedResults(
                rawResults.Select(x => (x.Response, x.File)),
                query,
                search,
                userSuccessCounts,
                useInfer,
                useLevenshtein)
            .Select(x => new FileCandidate(x.response, x.file))
            .ToList();

    public static List<SongJob> AggregateTracks(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> rawResults,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts)
    {
        var equivalentFiles = Searcher.EquivalentFiles(query, rawResults.Select(x => (x.Response, x.File)), search)
            .Select(x => (x.query, Ordered: ResultSorter.OrderedResults(
                x.candidates.Select(c => (c.Response, c.File)),
                x.query,
                search,
                userSuccessCounts,
                useInfer: false,
                useLevenshtein: false,
                albumMode: false)))
            .ToList();

        if (!search.Relax)
        {
            equivalentFiles = equivalentFiles
                .Where(x => FileConditions.StrictString(x.query.Title, query.Title, ignoreCase: true)
                    && (FileConditions.StrictString(x.query.Artist, query.Artist, ignoreCase: true, boundarySkipWs: false)
                        || FileConditions.StrictString(x.query.Title, query.Artist, ignoreCase: true, boundarySkipWs: false)
                            && x.query.Title.ContainsInBrackets(query.Artist, ignoreCase: true)))
                .ToList();
        }

        return equivalentFiles.Select(x =>
        {
            var song = new SongJob(x.query);
            song.Candidates = x.Ordered.Select(r => new FileCandidate(r.response, r.file)).ToList();
            return song;
        }).ToList();
    }

    public static List<AlbumFolder> AlbumFolders(
        IEnumerable<(SearchResponse Response, Soulseek.File File)> rawResults,
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null)
    {
        bool canMatchDisc = !DiscPatternRegex().IsMatch(query.Album) && !DiscPatternRegex().IsMatch(query.Artist);

        var bridgeQuery = AlbumBridgeQuery(query);
        var orderedResults = ResultSorter.OrderedResults(
            rawResults.Select(x => (x.Response, x.File)),
            bridgeQuery,
            search,
            userSuccessCounts ?? new ConcurrentDictionary<string, int>(),
            useInfer: false,
            useLevenshtein: false,
            albumMode: true);

        var dirStructure = new Dictionary<string, (string username, string folderPath, List<(SlResponse r, SlFile f)> list, int idx)>();
        int idx = 0;

        foreach (var (response, file) in orderedResults)
        {
            string username = response.Username;
            string folderPath = file.Filename[..file.Filename.LastIndexOf('\\')];
            string dirName = folderPath[(folderPath.LastIndexOf('\\') + 1)..];

            if (canMatchDisc && DiscPatternRegex().IsMatch(dirName))
                folderPath = folderPath[..folderPath.LastIndexOf('\\')];

            string key = username + '\\' + folderPath;
            if (!dirStructure.TryGetValue(key, out var value))
                dirStructure[key] = (username, folderPath, new List<(SlResponse, SlFile)> { (response, file) }, idx);
            else
                value.list.Add((response, file));

            idx++;
        }

        MergeChildDirectories(dirStructure);

        int min = query.MinTrackCount;
        int max = query.MaxTrackCount;
        var folders = new List<AlbumFolder>();

        foreach (var (_, (username, folderPath, list, _)) in dirStructure)
        {
            int musicCount = list.Count(x => Utils.IsMusicFile(x.f.Filename));
            if (musicCount == 0) continue;
            if (max != -1 && musicCount > max) continue;
            if (min > 0 && musicCount < min) continue;

            var files = list
                .OrderBy(x => !Utils.IsMusicFile(x.f.Filename))
                .ThenBy(x => x.f.Filename)
                .Select(x =>
                {
                    var info = Searcher.InferSongQuery(x.f.Filename, new SongQuery { Artist = query.Artist, Album = query.Album });
                    return new SongJob(info) { ResolvedTarget = new FileCandidate(x.r, x.f) };
                })
                .ToList();

            if (!search.NecessaryFolderCond.RequiredTrackTitlesSatisfy(files))
                continue;

            folders.Add(new AlbumFolder(username, folderPath, files));
        }

        return folders;
    }

    public static List<AlbumJob> AggregateAlbums(
        IEnumerable<AlbumFolder> albums,
        AlbumQuery query,
        SearchSettings search)
    {
        int maxDiff = search.AggregateLengthTol;

        bool LengthsAreSimilar(int[] s1, int[] s2)
        {
            if (s1.Length != s2.Length) return false;
            for (int i = 0; i < s1.Length; i++)
                if (Math.Abs(s1[i] - s2[i]) > maxDiff) return false;
            return true;
        }

        var byLength = new List<(int[] lengths, List<AlbumFolder> versions, HashSet<string> users)>();

        foreach (var folder in albums)
        {
            if (folder.Files.Count == 0) continue;
            var sortedLengths = folder.Files
                .Where(f => !f.IsNotAudio)
                .Select(f => f.ResolvedTarget!.File.Length ?? -1)
                .OrderBy(x => x)
                .ToArray();

            bool matched = false;
            for (int i = 0; i < byLength.Count; i++)
            {
                if (!LengthsAreSimilar(sortedLengths, byLength[i].lengths)) continue;

                if (sortedLengths.Length == 1 && !SingleTrackAlbumsMatch(byLength[i].versions[0], folder))
                    continue;

                byLength[i].versions.Add(folder);
                byLength[i].users.Add(folder.Username);
                matched = true;
                break;
            }

            if (!matched)
                byLength.Add((sortedLengths, new List<AlbumFolder> { folder }, new HashSet<string> { folder.Username }));
        }

        return byLength
            .Where(x => x.users.Count >= search.MinSharesAggregate)
            .OrderByDescending(x => x.users.Count)
            .Select(x =>
            {
                var newJob = new AlbumJob(query);
                newJob.Results = x.versions;
                return newJob;
            })
            .ToList();
    }

    public static SongQuery AlbumBridgeQuery(AlbumQuery query)
        => new()
        {
            Artist = query.Artist,
            Title = query.Album.Length > 0 ? query.Album : query.SearchHint,
            Album = query.Album,
            ArtistMaybeWrong = query.ArtistMaybeWrong,
            IsDirectLink = query.IsDirectLink,
        };

    private static void MergeChildDirectories(Dictionary<string, (string username, string folderPath, List<(SlResponse r, SlFile f)> list, int idx)> dirStructure)
    {
        var sortedKeys = dirStructure.Keys.OrderBy(k => k).ToList();
        var toRemove = new HashSet<string>();

        for (int i = 0; i < sortedKeys.Count; i++)
        {
            var key = sortedKeys[i];
            if (toRemove.Contains(key)) continue;
            for (int j = i + 1; j < sortedKeys.Count; j++)
            {
                var key2 = sortedKeys[j];
                if (toRemove.Contains(key2)) continue;
                if ((key2 + '\\').StartsWith(key + '\\'))
                {
                    if (dirStructure[key].idx <= dirStructure[key2].idx)
                    {
                        dirStructure[key].list.AddRange(dirStructure[key2].list);
                        toRemove.Add(key2);
                    }
                    else
                    {
                        dirStructure[key2].list.AddRange(dirStructure[key].list);
                        toRemove.Add(key);
                        key = key2;
                    }
                }
                else if (!(key2 + '\\').StartsWith(key)) break;
            }
        }
        foreach (var key in toRemove) dirStructure.Remove(key);
    }

    private static bool SingleTrackAlbumsMatch(AlbumFolder a, AlbumFolder b)
    {
        var rep1 = a.Files.FirstOrDefault(f => !f.IsNotAudio);
        var rep2 = b.Files.FirstOrDefault(f => !f.IsNotAudio);
        if (rep1 == null || rep2 == null)
            return true;

        var q1 = Searcher.InferSongQuery(rep1.ResolvedTarget!.Filename, new SongQuery());
        var q2 = Searcher.InferSongQuery(rep2.ResolvedTarget!.Filename, new SongQuery());
        return (q2.Artist.ContainsIgnoreCase(q1.Artist) || q1.Artist.ContainsIgnoreCase(q2.Artist))
            && (q2.Title.ContainsIgnoreCase(q1.Title) || q1.Title.ContainsIgnoreCase(q2.Title));
    }

    [GeneratedRegex(@"^(?i)(dis[c|k]|cd)\s*\d{1,2}$")]
    private static partial Regex DiscPatternRegex();
}

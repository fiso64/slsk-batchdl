using Sldl.Core.Models;
using Soulseek;
using System.Collections.Concurrent;
using Sldl.Core.Settings;

namespace Sldl.Core.Services;

public static class ResultSorter
{
    public static IEnumerable<(SearchResponse response, Soulseek.File file)> OrderedResults(
        IEnumerable<KeyValuePair<string, (SearchResponse, Soulseek.File)>> results,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = false,
        bool useLevenshtein = true,
        bool albumMode = false)
    {
        return OrderedResults(results.Select(x => x.Value), query, search, userSuccessCounts, useInfer, useLevenshtein, albumMode);
    }

    public static IEnumerable<(SearchResponse response, Soulseek.File file)> OrderedResults(
        IEnumerable<(SearchResponse, Soulseek.File)> results,
        SongQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = false,
        bool useLevenshtein = true,
        bool albumMode = false)
    {
        bool useBracketCheck = !albumMode;
        useLevenshtein = albumMode ? false : useLevenshtein;
        useInfer       = albumMode ? false : useInfer;

        var infQueriesAndCounts = GetInferredQueries(results, query, search, useInfer);
        var random = new Random();

        (SongQuery, int) inferredQuery((SearchResponse response, Soulseek.File file) x)
        {
            string key = $"{x.response.Username}\\{x.file.Filename}";
            if (infQueriesAndCounts != null && infQueriesAndCounts.ContainsKey(key))
                return infQueriesAndCounts[key];
            return (new SongQuery(), 0);
        }

        // TODO: Maybe optimize to only compute the fields when they are needed for sorting (worth it?)
        Func<(SearchResponse response, Soulseek.File file), SortingCriteria> getSortingCriteria =
            result => new SortingCriteria
            {
                UserSuccessAboveDownrank    = userSuccessCounts.GetValueOrDefault(result.response.Username, 0) > search.DownrankOn,
                NecessaryConditionsMet      = search.NecessaryCond.FileSatisfies(result.file, query, result.response),
                PreferredUserConditionsMet  = search.PreferredCond.BannedUsersSatisfies(result.response),
                HasValidLength              = (result.file.Length != null && result.file.Length > 0)
                                              || search.PreferredCond.AcceptNoLength == null
                                              || search.PreferredCond.AcceptNoLength.Value,
                BracketCheckPassed          = !useBracketCheck || FileConditions.BracketCheck(query, inferredQuery((result.response, result.file)).Item1),
                StrictTitleMatch            = search.PreferredCond.StrictTitleSatisfies(result.file.Filename, query.Title),
                AlbumModeStrictAlbumMatch   = !albumMode || search.PreferredCond.StrictAlbumSatisfies(result.file.Filename, query.Album),
                StrictArtistMatch           = search.PreferredCond.StrictArtistSatisfies(result.file.Filename, query.Title),
                LengthToleranceMatch        = search.PreferredCond.LengthToleranceSatisfies(result.file, query.Length),
                FormatMatch                 = search.PreferredCond.FormatSatisfies(result.file.Filename),
                NonAlbumModeStrictAlbumMatch = albumMode || search.PreferredCond.StrictAlbumSatisfies(result.file.Filename, query.Album),
                BitrateMatch                = search.PreferredCond.BitrateSatisfies(result.file),
                SampleRateMatch             = search.PreferredCond.SampleRateSatisfies(result.file),
                BitDepthMatch               = search.PreferredCond.BitDepthSatisfies(result.file),
                FileSatisfies               = search.PreferredCond.FileSatisfies(result.file, query, result.response),
                HasFreeUploadSlot           = result.response.HasFreeUploadSlot,
                NoQueue                     = result.response.QueueLength == 0,
                UploadSpeedFast             = result.response.UploadSpeed / 1024 / 650,
                NonAlbumModeStrictString    = albumMode || FileConditions.StrictString(result.file.Filename, query.Title),
                AlbumModeStrictString       = !albumMode || FileConditions.StrictString(Utils.GetDirectoryNameSlsk(result.file.Filename), query.Album),
                StrictArtistString          = FileConditions.StrictString(result.file.Filename, query.Artist, boundarySkipWs: false),
                InferredTrackCount          = useInfer ? inferredQuery((result.response, result.file)).Item2 : 0,
                UploadSpeedMedium           = result.response.UploadSpeed / 1024 / 350,
                BitRate                     = (result.file.BitRate ?? 0) / 80,
                LevenshteinScore            = useLevenshtein ? CalculateLevenshtein(query, inferredQuery((result.response, result.file)).Item1) / 5 : 0,
                RandomTiebreaker            = random.Next()
            };

        return results
            .Select(x => (response: x.Item1, file: x.Item2))
            .Where(x => userSuccessCounts.GetValueOrDefault(x.response.Username, 0) > search.IgnoreOn)
            .OrderByDescending(x => getSortingCriteria(x));
    }

    private static Dictionary<string, (SongQuery, int)>? GetInferredQueries(
        IEnumerable<(SearchResponse, Soulseek.File)> results,
        SongQuery query,
        SearchSettings search,
        bool useInfer)
    {
        if (!useInfer) return null;

        var equivalentFiles = Searcher.EquivalentFiles(query, results, search, 1);

        return equivalentFiles
            .SelectMany(t => t.candidates, (t, c) => new
            {
                InferredQuery = t.query,
                c.Response.Username,
                c.File.Filename,
                Count = t.candidates.Count()
            })
            .ToSafeDictionary(
                x => $"{x.Username}\\{x.Filename}",
                y => (y.InferredQuery, y.Count));
    }

    private static int CalculateLevenshtein(SongQuery query, SongQuery inferred)
    {
        string t1 = query.Title.RemoveFt().ReplaceSpecialChars("").Replace(" ", "").Replace("_", "").ToLower();
        string t2 = inferred.Title.RemoveFt().ReplaceSpecialChars("").Replace(" ", "").Replace("_", "").ToLower();
        return Utils.Levenshtein(t1, t2);
    }
}

public class SortingCriteria : IComparable<SortingCriteria>
{
    public bool UserSuccessAboveDownrank;
    public bool NecessaryConditionsMet;
    public bool PreferredUserConditionsMet;
    public bool HasValidLength;
    public bool BracketCheckPassed;
    public bool StrictTitleMatch;
    public bool AlbumModeStrictAlbumMatch;
    public bool StrictArtistMatch;
    public bool LengthToleranceMatch;
    public bool FormatMatch;
    public bool NonAlbumModeStrictAlbumMatch;
    public bool BitrateMatch;
    public bool SampleRateMatch;
    public bool BitDepthMatch;
    public bool FileSatisfies;
    public bool HasFreeUploadSlot;
    public bool NoQueue;
    public int  UploadSpeedFast;
    public bool NonAlbumModeStrictString;
    public bool AlbumModeStrictString;
    public bool StrictArtistString;
    public int  InferredTrackCount;
    public int  UploadSpeedMedium;
    public int  BitRate;
    public int  LevenshteinScore;
    public int  RandomTiebreaker;

    public int CompareTo(SortingCriteria? other)
    {
        if (other == null) return 1;

        int comparison;

        comparison = UserSuccessAboveDownrank.CompareTo(other.UserSuccessAboveDownrank);
        if (comparison != 0) return comparison;

        comparison = NecessaryConditionsMet.CompareTo(other.NecessaryConditionsMet);
        if (comparison != 0) return comparison;

        comparison = PreferredUserConditionsMet.CompareTo(other.PreferredUserConditionsMet);
        if (comparison != 0) return comparison;

        comparison = HasValidLength.CompareTo(other.HasValidLength);
        if (comparison != 0) return comparison;

        comparison = BracketCheckPassed.CompareTo(other.BracketCheckPassed);
        if (comparison != 0) return comparison;

        comparison = StrictTitleMatch.CompareTo(other.StrictTitleMatch);
        if (comparison != 0) return comparison;

        comparison = AlbumModeStrictAlbumMatch.CompareTo(other.AlbumModeStrictAlbumMatch);
        if (comparison != 0) return comparison;

        comparison = StrictArtistMatch.CompareTo(other.StrictArtistMatch);
        if (comparison != 0) return comparison;

        comparison = LengthToleranceMatch.CompareTo(other.LengthToleranceMatch);
        if (comparison != 0) return comparison;

        comparison = FormatMatch.CompareTo(other.FormatMatch);
        if (comparison != 0) return comparison;

        comparison = NonAlbumModeStrictAlbumMatch.CompareTo(other.NonAlbumModeStrictAlbumMatch);
        if (comparison != 0) return comparison;

        comparison = BitrateMatch.CompareTo(other.BitrateMatch);
        if (comparison != 0) return comparison;

        comparison = SampleRateMatch.CompareTo(other.SampleRateMatch);
        if (comparison != 0) return comparison;

        comparison = BitDepthMatch.CompareTo(other.BitDepthMatch);
        if (comparison != 0) return comparison;

        comparison = FileSatisfies.CompareTo(other.FileSatisfies);
        if (comparison != 0) return comparison;

        comparison = HasFreeUploadSlot.CompareTo(other.HasFreeUploadSlot);
        if (comparison != 0) return comparison;

        //comparison = NoQueue.CompareTo(other.NoQueue);
        //if (comparison != 0) return comparison;

        comparison = UploadSpeedFast.CompareTo(other.UploadSpeedFast);
        if (comparison != 0) return comparison;

        comparison = NonAlbumModeStrictString.CompareTo(other.NonAlbumModeStrictString);
        if (comparison != 0) return comparison;

        comparison = AlbumModeStrictString.CompareTo(other.AlbumModeStrictString);
        if (comparison != 0) return comparison;

        comparison = StrictArtistString.CompareTo(other.StrictArtistString);
        if (comparison != 0) return comparison;

        comparison = InferredTrackCount.CompareTo(other.InferredTrackCount);
        if (comparison != 0) return comparison;

        comparison = UploadSpeedMedium.CompareTo(other.UploadSpeedMedium);
        if (comparison != 0) return comparison;

        comparison = BitRate.CompareTo(other.BitRate);
        if (comparison != 0) return comparison;

        comparison = LevenshteinScore.CompareTo(other.LevenshteinScore);
        if (comparison != 0) return comparison;

        return RandomTiebreaker.CompareTo(other.RandomTiebreaker);
    }
}

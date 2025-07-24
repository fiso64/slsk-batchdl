using Models;
using Soulseek;
using System.Collections.Concurrent;

public static class ResultSorter
{
    public static IEnumerable<(SearchResponse response, Soulseek.File file)> OrderedResults(
        IEnumerable<KeyValuePair<string, (SearchResponse, Soulseek.File)>> results,
        Track track,
        Config config,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = false,
        bool useLevenshtein = true,
        bool albumMode = false)
    {
        return OrderedResults(results.Select(x => x.Value), track, config, userSuccessCounts, useInfer, useLevenshtein, albumMode);
    }

    public static IEnumerable<(SearchResponse response, Soulseek.File file)> OrderedResults(
        IEnumerable<(SearchResponse, Soulseek.File)> results,
        Track track,
        Config config,
        ConcurrentDictionary<string, int> userSuccessCounts,
        bool useInfer = false,
        bool useLevenshtein = true,
        bool albumMode = false)
    {
        bool useBracketCheck = !albumMode;
        useLevenshtein = albumMode ? false : useLevenshtein;
        useInfer = albumMode ? false : useInfer;

        var infTracksAndCounts = GetInferredTracks(results, track, config, useInfer);
        var random = new Random();

        (Track, int) inferredTrack((SearchResponse response, Soulseek.File file) x)
        {
            string key = $"{x.response.Username}\\{x.file.Filename}";
            if (infTracksAndCounts != null && infTracksAndCounts.ContainsKey(key))
                return infTracksAndCounts[key];
            return (new Track(), 0);
        }

        Func<(SearchResponse response, Soulseek.File file), SortingCriteria> getSortingCriteria =
            result => new SortingCriteria
            {
                UserSuccessAboveDownrank = userSuccessCounts.GetValueOrDefault(result.response.Username, 0) > config.downrankOn,
                NecessaryConditionsMet = config.necessaryCond.FileSatisfies(result.file, track, result.response),
                PreferredUserConditionsMet = config.preferredCond.BannedUsersSatisfies(result.response),
                HasValidLength = (result.file.Length != null && result.file.Length > 0) ||
                                config.preferredCond.AcceptNoLength == null ||
                                config.preferredCond.AcceptNoLength.Value,
                BracketCheckPassed = !useBracketCheck || FileConditions.BracketCheck(track, inferredTrack((result.response, result.file)).Item1),
                StrictTitleMatch = config.preferredCond.StrictTitleSatisfies(result.file.Filename, track.Title),
                AlbumModeStrictAlbumMatch = !albumMode || config.preferredCond.StrictAlbumSatisfies(result.file.Filename, track.Album),
                StrictArtistMatch = config.preferredCond.StrictArtistSatisfies(result.file.Filename, track.Title),
                LengthToleranceMatch = config.preferredCond.LengthToleranceSatisfies(result.file, track.Length),
                FormatMatch = config.preferredCond.FormatSatisfies(result.file.Filename),
                NonAlbumModeStrictAlbumMatch = albumMode || config.preferredCond.StrictAlbumSatisfies(result.file.Filename, track.Album),
                BitrateMatch = config.preferredCond.BitrateSatisfies(result.file),
                SampleRateMatch = config.preferredCond.SampleRateSatisfies(result.file),
                BitDepthMatch = config.preferredCond.BitDepthSatisfies(result.file),
                FileSatisfies = config.preferredCond.FileSatisfies(result.file, track, result.response),
                HasFreeUploadSlot = result.response.HasFreeUploadSlot,
                NoQueue = result.response.QueueLength == 0,
                UploadSpeedFast = result.response.UploadSpeed / 1024 / 650,
                NonAlbumModeStrictString = albumMode || FileConditions.StrictString(result.file.Filename, track.Title),
                AlbumModeStrictString = !albumMode || FileConditions.StrictString(Utils.GetDirectoryNameSlsk(result.file.Filename), track.Album),
                StrictArtistString = FileConditions.StrictString(result.file.Filename, track.Artist, boundarySkipWs: false),
                InferredTrackCount = useInfer ? inferredTrack((result.response, result.file)).Item2 : 0,
                UploadSpeedMedium = result.response.UploadSpeed / 1024 / 350,
                BitRate = (result.file.BitRate ?? 0) / 80,
                LevenshteinScore = useLevenshtein ? CalculateLevenshtein(track, inferredTrack((result.response, result.file)).Item1) / 5 : 0,
                RandomTiebreaker = random.Next()
            };

        return results
            .Select(x => (response: x.Item1, file: x.Item2))
            .Where(x => userSuccessCounts.GetValueOrDefault(x.response.Username, 0) > config.ignoreOn)
            .OrderByDescending(x => getSortingCriteria(x));
    }

    private static Dictionary<string, (Track, int)>? GetInferredTracks(
        IEnumerable<(SearchResponse, Soulseek.File)> results,
        Track track,
        Config config,
        bool useInfer)
    {
        if (!useInfer) return null;

        var equivalentFiles = Searcher.EquivalentFiles(track, results, config, 1);
        return equivalentFiles
            .SelectMany(t => t.Item2, (t, f) => new
            {
                t.Item1,
                f.response.Username,
                f.file.Filename,
                Count = t.Item2.Count()
            })
            .ToSafeDictionary(
                x => $"{x.Username}\\{x.Filename}",
                y => (y.Item1, y.Count));
    }

    private static int CalculateLevenshtein(Track track, Track inferredTrack)
    {
        string t1 = track.Title.RemoveFt().ReplaceSpecialChars("").Replace(" ", "").Replace("_", "").ToLower();
        string t2 = inferredTrack.Title.RemoveFt().ReplaceSpecialChars("").Replace(" ", "").Replace("_", "").ToLower();
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
    public int UploadSpeedFast;
    public bool NonAlbumModeStrictString;
    public bool AlbumModeStrictString;
    public bool StrictArtistString;
    public int InferredTrackCount;
    public int UploadSpeedMedium;
    public int BitRate;
    public int LevenshteinScore;
    public int RandomTiebreaker;

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
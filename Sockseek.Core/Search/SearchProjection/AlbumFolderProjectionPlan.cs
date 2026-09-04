using System.Collections.Concurrent;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;
using SlFile = Soulseek.File;

namespace Sockseek.Core.Services;

internal readonly record struct EvaluatedAlbumProjectionInput(
    SearchProjectionInput Input,
    ResultSorter.SortEntry SortEntry);

// Shared album-folder projection policy. One-shot projection and incremental
// projection should differ only in how raw results are collected; filtering,
// grouping, and ranking must stay here to avoid local/remote/result-view drift.
internal readonly struct AlbumFolderProjectionPlan
{
    private readonly AlbumQuery query;
    private readonly SearchSettings search;
    private readonly ConditionSatisfactionPolicy.AlbumSearchFilter projectionFilter;
    private readonly FolderSortMode sortMode;
    private readonly ResultSorter.SortKeyContext aggregateSortKeyContext;

    public AlbumFolderProjectionPlan(
        AlbumQuery query,
        SearchSettings search,
        ConcurrentDictionary<string, int>? userSuccessCounts = null,
        bool ignoreStringSortConditions = false,
        FolderSortMode sortMode = FolderSortMode.AlbumRanked)
    {
        this.query = query;
        this.search = search;
        this.sortMode = sortMode;
        projectionFilter = ConditionSatisfactionPolicy.CreateAlbumSearchFilter(query, search);
        aggregateSortKeyContext = ResultSorter.CreateSortKeyContext(
            [],
            projectionFilter.SortQuery,
            search,
            userSuccessCounts ?? new ConcurrentDictionary<string, int>(),
            useBracketCheck: false,
            useInfer: false,
            albumMode: true,
            ignoreStringSortConditions: ignoreStringSortConditions,
            necessaryConditionEvaluator: projectionFilter.Satisfies);
    }

    internal bool Includes((SearchResponse Response, SlFile File) result)
        => projectionFilter.Satisfies(result);

    public bool Includes(SearchProjectionInput result)
        => projectionFilter.Satisfies(result);

    public EvaluatedAlbumProjectionInput? Evaluate(
        SearchProjectionInput input,
        int originalIndex)
    {
        ResultSorter.SortEntry? entry = ResultSorter.CreateSortEntry(
            input,
            aggregateSortKeyContext,
            originalIndex);
        return entry is { } admitted
            && admitted.Key.ConditionFacts.NecessaryConditionsSatisfied
                ? new EvaluatedAlbumProjectionInput(input, admitted)
                : null;
    }

    public List<EvaluatedAlbumProjectionInput> EvaluateToList(
        IEnumerable<SearchProjectionInput> results)
    {
        var evaluated = new List<EvaluatedAlbumProjectionInput>();
        int index = 0;
        foreach (SearchProjectionInput input in results)
        {
            if (Evaluate(input, index++) is { } admitted)
                evaluated.Add(admitted);
        }
        return evaluated;
    }

    internal List<(SearchResponse Response, SlFile File)> FilterToList(
        IEnumerable<(SearchResponse Response, SlFile File)> results)
    {
        var filtered = new List<(SearchResponse Response, SlFile File)>();

        foreach (var result in results)
        {
            if (Includes(result))
                filtered.Add(result);
        }

        return filtered;
    }

    public List<SearchProjectionInput> FilterToList(IEnumerable<SearchProjectionInput> results)
        => results.Where(Includes).ToList();

    internal List<AlbumFolder> ProjectFilteredResults(
        IEnumerable<(SearchResponse Response, SlFile File)> filteredResults,
        int capacity)
        => SearchResultProjector.AlbumFoldersFromResults(
            filteredResults,
            query,
            search,
            capacity,
            aggregateSortKeyContext: aggregateSortKeyContext,
            useAlbumFolderQualityRanking: sortMode == FolderSortMode.AlbumRanked);

    public List<AlbumFolder> ProjectFilteredResults(
        IEnumerable<SearchProjectionInput> filteredResults,
        int capacity)
        => SearchResultProjector.AlbumFoldersFromResults(
            filteredResults,
            query,
            search,
            capacity,
            aggregateSortKeyContext: aggregateSortKeyContext,
            useAlbumFolderQualityRanking: sortMode == FolderSortMode.AlbumRanked);

    public List<AlbumFolder> ProjectEvaluatedResults(
        IEnumerable<EvaluatedAlbumProjectionInput> evaluatedResults,
        int capacity)
    {
        IReadOnlyList<EvaluatedAlbumProjectionInput> evaluated = evaluatedResults
            as IReadOnlyList<EvaluatedAlbumProjectionInput>
            ?? evaluatedResults.ToArray();
        return SearchResultProjector.AlbumFoldersFromResults(
            evaluated.Select(row => row.Input),
            query,
            search,
            capacity,
            aggregateSortKeyContext: null,
            useAlbumFolderQualityRanking: sortMode == FolderSortMode.AlbumRanked,
            precomputedSortEntries: evaluated.ToDictionary(
                row => (
                    row.Input.Username,
                    row.Input.Filename,
                    row.Input.Visibility),
                row => row.SortEntry));
    }
}

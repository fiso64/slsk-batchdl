using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Api;

public sealed record CollectionPatchDto<T>(
    IReadOnlyList<T>? Replace = null,
    IReadOnlyList<T>? Append = null);

public sealed record RegexRuleDto(
    RegexFieldsDto Match,
    RegexFieldsDto Replace);

public sealed record RegexFieldsDto(
    string Title,
    string Artist,
    string Album);

public sealed record DownloadSettingsPatchDto(
    OutputSettingsPatchDto? Output = null,
    SearchSettingsPatchDto? Search = null,
    SkipSettingsPatchDto? Skip = null,
    PreprocessSettingsPatchDto? Preprocess = null,
    ExtractionSettingsPatchDto? Extraction = null,
    TransferSettingsPatchDto? Transfer = null,
    SpotifySettingsPatchDto? Spotify = null,
    YouTubeSettingsPatchDto? YouTube = null,
    YtDlpSettingsPatchDto? YtDlp = null,
    CsvSettingsPatchDto? Csv = null,
    BandcampSettingsPatchDto? Bandcamp = null,
    PrintOption? PrintOption = null);

public sealed record OutputSettingsPatchDto(
    string? ParentDir = null,
    string? NameFormat = null,
    string? InvalidReplaceStr = null,
    bool? WritePlaylist = null,
    bool? WriteIndex = null,
    bool? HasConfiguredIndex = null,
    string? M3uFilePath = null,
    string? IndexFilePath = null,
    IncompleteAlbumActionSettingsPatchDto? IncompleteAlbumAction = null,
    CollectionPatchDto<string>? OnComplete = null,
    bool? AlbumArtOnly = null,
    AlbumArtOption? AlbumArtOption = null);

public sealed record IncompleteAlbumActionSettingsPatchDto(
    IncompleteAlbumActionKind? Kind = null,
    string? Path = null);

public sealed record SearchSettingsPatchDto(
    FileConditionsPatchDto? NecessaryCond = null,
    FileConditionsPatchDto? PreferredCond = null,
    FolderConditionsPatchDto? NecessaryFolderCond = null,
    FolderConditionsPatchDto? PreferredFolderCond = null,
    int? SearchTimeout = null,
    int? DownrankOn = null,
    int? IgnoreOn = null,
    bool? FastSearch = null,
    int? FastSearchDelay = null,
    double? FastSearchMinUpSpeed = null,
    bool? DesperateSearch = null,
    bool? NoRemoveSpecialChars = null,
    bool? RemoveSingleCharSearchTerms = null,
    bool? NoBrowseFolder = null,
    bool? Relax = null,
    bool? StrictAlbumQuality = null,
    bool? ArtistMaybeWrong = null,
    bool? IsAggregate = null,
    int? MinSharesAggregate = null,
    int? AggregateLengthTol = null);

public sealed record FileConditionsPatchDto(
    int? LengthTolerance = null,
    int? MinBitrate = null,
    int? MaxBitrate = null,
    int? MinSampleRate = null,
    int? MaxSampleRate = null,
    int? MinBitDepth = null,
    int? MaxBitDepth = null,
    bool? StrictTitle = null,
    bool? StrictArtist = null,
    bool? StrictAlbum = null,
    CollectionPatchDto<string>? Formats = null,
    CollectionPatchDto<string>? BannedUsers = null,
    CollectionPatchDto<string>? AllowedUsers = null,
    bool? AcceptNoLength = null,
    bool? AcceptMissingProps = null);

public sealed record FolderConditionsPatchDto(
    int? MinTrackCount = null,
    int? MaxTrackCount = null,
    CollectionPatchDto<string>? RequiredTrackTitles = null);

public sealed record SkipSettingsPatchDto(
    bool? SkipExisting = null,
    bool? SkipNotFound = null,
    SkipMode? SkipMode = null,
    string? SkipMusicDir = null,
    SkipMode? SkipModeMusicDir = null,
    bool? SkipCheckCond = null,
    bool? SkipCheckPrefCond = null);

public sealed record PreprocessSettingsPatchDto(
    bool? RemoveFt = null,
    bool? RemoveBrackets = null,
    bool? ExtractArtist = null,
    string? ParseTitleTemplate = null,
    CollectionPatchDto<RegexRuleDto>? Regex = null);

public sealed record ExtractionSettingsPatchDto(
    string? Input = null,
    InputType? InputType = null,
    int? MaxTracks = null,
    int? Offset = null,
    bool? Reverse = null,
    bool? RemoveTracksFromSource = null,
    // Nullable by design: null lets the input source decide. String input and string
    // lines inside list files then use the 3.0 album default; explicit Song/Album
    // only affects ambiguous string interpretation.
    ExtractionMode? RequestedMode = null,
    bool? UpgradeToAlbum = null,
    bool? SetAlbumMinTrackCount = null,
    bool? SetAlbumMaxTrackCount = null);

public sealed record TransferSettingsPatchDto(
    int? MaxDownloadRetries = null,
    int? UnknownErrorRetries = null,
    bool? NoIncompleteExt = null,
    int? AlbumTrackCountMaxRetries = null,
    int? MaxStaleTime = null);

public sealed record SpotifySettingsPatchDto(
    string? ClientId = null,
    string? ClientSecret = null,
    string? Token = null,
    string? Refresh = null);

public sealed record YouTubeSettingsPatchDto(
    string? ApiKey = null,
    bool? GetDeleted = null,
    bool? DeletedOnly = null);

public sealed record YtDlpSettingsPatchDto(
    bool? UseYtdlp = null,
    string? YtdlpArgument = null);

public sealed record CsvSettingsPatchDto(
    string? ArtistCol = null,
    string? AlbumCol = null,
    string? TitleCol = null,
    string? YtIdCol = null,
    string? DescCol = null,
    string? TrackCountCol = null,
    string? LengthCol = null,
    string? TimeUnit = null,
    bool? YtParse = null);

public sealed record BandcampSettingsPatchDto(
    string? HtmlFromFile = null);

public static class DownloadSettingsPatchDtoMapper
{
    public static void ApplyTo(DownloadSettings settings, DownloadSettingsPatchDto? patch)
    {
        if (patch == null)
            return;

        ApplyOutput(settings.Output, patch.Output);
        ApplySearch(settings.Search, patch.Search);
        ApplySkip(settings.Skip, patch.Skip);
        ApplyPreprocess(settings.Preprocess, patch.Preprocess);
        ApplyExtraction(settings.Extraction, patch.Extraction);
        ApplyTransfer(settings.Transfer, patch.Transfer);
        ApplySpotify(settings.Spotify, patch.Spotify);
        ApplyYouTube(settings.YouTube, patch.YouTube);
        ApplyYtDlp(settings.YtDlp, patch.YtDlp);
        ApplyCsv(settings.Csv, patch.Csv);
        ApplyBandcamp(settings.Bandcamp, patch.Bandcamp);

        if (patch.PrintOption is { } printOption) settings.PrintOption = printOption;
    }

    public static DownloadSettingsPatchDto? FromDifference(DownloadSettings baseline, DownloadSettings effective)
    {
        var patch = new DownloadSettingsPatchDto(
            Difference(baseline.Output, effective.Output),
            Difference(baseline.Search, effective.Search),
            Difference(baseline.Skip, effective.Skip),
            Difference(baseline.Preprocess, effective.Preprocess),
            Difference(baseline.Extraction, effective.Extraction),
            Difference(baseline.Transfer, effective.Transfer),
            Difference(baseline.Spotify, effective.Spotify),
            Difference(baseline.YouTube, effective.YouTube),
            Difference(baseline.YtDlp, effective.YtDlp),
            Difference(baseline.Csv, effective.Csv),
            Difference(baseline.Bandcamp, effective.Bandcamp),
            Changed(baseline.PrintOption, effective.PrintOption));

        return NullIfEmpty(patch, new DownloadSettingsPatchDto());
    }

    public static DownloadSettingsPatchDto? Combine(
        DownloadSettingsPatchDto? first,
        DownloadSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;

        return new DownloadSettingsPatchDto(
            Combine(first.Output, second.Output),
            Combine(first.Search, second.Search),
            Combine(first.Skip, second.Skip),
            Combine(first.Preprocess, second.Preprocess),
            Combine(first.Extraction, second.Extraction),
            Combine(first.Transfer, second.Transfer),
            Combine(first.Spotify, second.Spotify),
            Combine(first.YouTube, second.YouTube),
            Combine(first.YtDlp, second.YtDlp),
            Combine(first.Csv, second.Csv),
            Combine(first.Bandcamp, second.Bandcamp),
            second.PrintOption ?? first.PrintOption);
    }

    private static OutputSettingsPatchDto? Difference(OutputSettings before, OutputSettings after)
    {
        var incompleteAlbumAction = before.IncompleteAlbumAction.Kind != after.IncompleteAlbumAction.Kind
            || before.IncompleteAlbumAction.Path != after.IncompleteAlbumAction.Path
                ? new IncompleteAlbumActionSettingsPatchDto(
                    after.IncompleteAlbumAction.Kind,
                    after.IncompleteAlbumAction.Path)
                : null;
        var patch = new OutputSettingsPatchDto(
            Changed(before.ParentDir, after.ParentDir),
            Changed(before.NameFormat, after.NameFormat),
            Changed(before.InvalidReplaceStr, after.InvalidReplaceStr),
            Changed(before.WritePlaylist, after.WritePlaylist),
            Changed(before.WriteIndex, after.WriteIndex),
            Changed(before.HasConfiguredIndex, after.HasConfiguredIndex),
            Changed(before.M3uFilePath, after.M3uFilePath),
            Changed(before.IndexFilePath, after.IndexFilePath),
            incompleteAlbumAction,
            Changed(before.OnComplete, after.OnComplete),
            Changed(before.AlbumArtOnly, after.AlbumArtOnly),
            Changed(before.AlbumArtOption, after.AlbumArtOption));
        return NullIfEmpty(patch, new OutputSettingsPatchDto());
    }

    private static SearchSettingsPatchDto? Difference(SearchSettings before, SearchSettings after)
    {
        var patch = new SearchSettingsPatchDto(
            Difference(before.NecessaryCond, after.NecessaryCond),
            Difference(before.PreferredCond, after.PreferredCond),
            Difference(before.NecessaryFolderCond, after.NecessaryFolderCond),
            Difference(before.PreferredFolderCond, after.PreferredFolderCond),
            Changed(before.SearchTimeout, after.SearchTimeout),
            Changed(before.DownrankOn, after.DownrankOn),
            Changed(before.IgnoreOn, after.IgnoreOn),
            Changed(before.FastSearch, after.FastSearch),
            Changed(before.FastSearchDelay, after.FastSearchDelay),
            Changed(before.FastSearchMinUpSpeed, after.FastSearchMinUpSpeed),
            Changed(before.DesperateSearch, after.DesperateSearch),
            Changed(before.NoRemoveSpecialChars, after.NoRemoveSpecialChars),
            Changed(before.RemoveSingleCharSearchTerms, after.RemoveSingleCharSearchTerms),
            Changed(before.NoBrowseFolder, after.NoBrowseFolder),
            Changed(before.Relax, after.Relax),
            Changed(before.StrictAlbumQuality, after.StrictAlbumQuality),
            Changed(before.ArtistMaybeWrong, after.ArtistMaybeWrong),
            Changed(before.IsAggregate, after.IsAggregate),
            Changed(before.MinSharesAggregate, after.MinSharesAggregate),
            Changed(before.AggregateLengthTol, after.AggregateLengthTol));
        return NullIfEmpty(patch, new SearchSettingsPatchDto());
    }

    private static FileConditionsPatchDto? Difference(FileConditions before, FileConditions after)
    {
        var patch = new FileConditionsPatchDto(
            ChangedNullable(before.LengthTolerance, after.LengthTolerance),
            ChangedNullable(before.MinBitrate, after.MinBitrate),
            ChangedNullable(before.MaxBitrate, after.MaxBitrate),
            ChangedNullable(before.MinSampleRate, after.MinSampleRate),
            ChangedNullable(before.MaxSampleRate, after.MaxSampleRate),
            ChangedNullable(before.MinBitDepth, after.MinBitDepth),
            ChangedNullable(before.MaxBitDepth, after.MaxBitDepth),
            Changed(before.StrictTitle, after.StrictTitle),
            Changed(before.StrictArtist, after.StrictArtist),
            Changed(before.StrictAlbum, after.StrictAlbum),
            Changed(before.Formats, after.Formats),
            Changed(before.BannedUsers, after.BannedUsers),
            Changed(before.AllowedUsers, after.AllowedUsers),
            Changed(before.AcceptNoLength, after.AcceptNoLength),
            Changed(before.AcceptMissingProps, after.AcceptMissingProps));
        return NullIfEmpty(patch, new FileConditionsPatchDto());
    }

    private static FolderConditionsPatchDto? Difference(FolderConditions before, FolderConditions after)
    {
        var patch = new FolderConditionsPatchDto(
            ChangedNullable(before.MinTrackCount, after.MinTrackCount),
            ChangedNullable(before.MaxTrackCount, after.MaxTrackCount),
            Changed(before.RequiredTrackTitles, after.RequiredTrackTitles));
        return NullIfEmpty(patch, new FolderConditionsPatchDto());
    }

    private static SkipSettingsPatchDto? Difference(SkipSettings before, SkipSettings after)
    {
        var patch = new SkipSettingsPatchDto(
            Changed(before.SkipExisting, after.SkipExisting),
            Changed(before.SkipNotFound, after.SkipNotFound),
            Changed(before.SkipMode, after.SkipMode),
            Changed(before.SkipMusicDir, after.SkipMusicDir),
            Changed(before.SkipModeMusicDir, after.SkipModeMusicDir),
            Changed(before.SkipCheckCond, after.SkipCheckCond),
            Changed(before.SkipCheckPrefCond, after.SkipCheckPrefCond));
        return NullIfEmpty(patch, new SkipSettingsPatchDto());
    }

    private static PreprocessSettingsPatchDto? Difference(PreprocessSettings before, PreprocessSettings after)
    {
        var beforeRegex = before.Regex?.Select(ToRegexRuleDto);
        var afterRegex = after.Regex?.Select(ToRegexRuleDto);
        var patch = new PreprocessSettingsPatchDto(
            Changed(before.RemoveFt, after.RemoveFt),
            Changed(before.RemoveBrackets, after.RemoveBrackets),
            Changed(before.ExtractArtist, after.ExtractArtist),
            Changed(before.ParseTitleTemplate, after.ParseTitleTemplate),
            Changed(beforeRegex, afterRegex));
        return NullIfEmpty(patch, new PreprocessSettingsPatchDto());
    }

    private static ExtractionSettingsPatchDto? Difference(ExtractionSettings before, ExtractionSettings after)
    {
        var patch = new ExtractionSettingsPatchDto(
            Changed(before.Input, after.Input),
            Changed(before.InputType, after.InputType),
            Changed(before.MaxTracks, after.MaxTracks),
            Changed(before.Offset, after.Offset),
            Changed(before.Reverse, after.Reverse),
            Changed(before.RemoveTracksFromSource, after.RemoveTracksFromSource),
            ChangedNullable(before.RequestedMode, after.RequestedMode),
            Changed(before.UpgradeToAlbum, after.UpgradeToAlbum),
            Changed(before.SetAlbumMinTrackCount, after.SetAlbumMinTrackCount),
            Changed(before.SetAlbumMaxTrackCount, after.SetAlbumMaxTrackCount));
        return NullIfEmpty(patch, new ExtractionSettingsPatchDto());
    }

    private static TransferSettingsPatchDto? Difference(TransferSettings before, TransferSettings after)
    {
        var patch = new TransferSettingsPatchDto(
            Changed(before.MaxDownloadRetries, after.MaxDownloadRetries),
            Changed(before.UnknownErrorRetries, after.UnknownErrorRetries),
            Changed(before.NoIncompleteExt, after.NoIncompleteExt),
            Changed(before.AlbumTrackCountMaxRetries, after.AlbumTrackCountMaxRetries),
            Changed(before.MaxStaleTime, after.MaxStaleTime));
        return NullIfEmpty(patch, new TransferSettingsPatchDto());
    }

    private static SpotifySettingsPatchDto? Difference(SpotifySettings before, SpotifySettings after)
    {
        var patch = new SpotifySettingsPatchDto(
            Changed(before.ClientId, after.ClientId),
            Changed(before.ClientSecret, after.ClientSecret),
            Changed(before.Token, after.Token),
            Changed(before.Refresh, after.Refresh));
        return NullIfEmpty(patch, new SpotifySettingsPatchDto());
    }

    private static YouTubeSettingsPatchDto? Difference(YouTubeSettings before, YouTubeSettings after)
    {
        var patch = new YouTubeSettingsPatchDto(
            Changed(before.ApiKey, after.ApiKey),
            Changed(before.GetDeleted, after.GetDeleted),
            Changed(before.DeletedOnly, after.DeletedOnly));
        return NullIfEmpty(patch, new YouTubeSettingsPatchDto());
    }

    private static YtDlpSettingsPatchDto? Difference(YtDlpSettings before, YtDlpSettings after)
    {
        var patch = new YtDlpSettingsPatchDto(
            Changed(before.UseYtdlp, after.UseYtdlp),
            Changed(before.YtdlpArgument, after.YtdlpArgument));
        return NullIfEmpty(patch, new YtDlpSettingsPatchDto());
    }

    private static CsvSettingsPatchDto? Difference(CsvSettings before, CsvSettings after)
    {
        var patch = new CsvSettingsPatchDto(
            Changed(before.ArtistCol, after.ArtistCol),
            Changed(before.AlbumCol, after.AlbumCol),
            Changed(before.TitleCol, after.TitleCol),
            Changed(before.YtIdCol, after.YtIdCol),
            Changed(before.DescCol, after.DescCol),
            Changed(before.TrackCountCol, after.TrackCountCol),
            Changed(before.LengthCol, after.LengthCol),
            Changed(before.TimeUnit, after.TimeUnit),
            Changed(before.YtParse, after.YtParse));
        return NullIfEmpty(patch, new CsvSettingsPatchDto());
    }

    private static BandcampSettingsPatchDto? Difference(BandcampSettings before, BandcampSettings after)
    {
        var patch = new BandcampSettingsPatchDto(Changed(before.HtmlFromFile, after.HtmlFromFile));
        return NullIfEmpty(patch, new BandcampSettingsPatchDto());
    }

    private static OutputSettingsPatchDto? Combine(OutputSettingsPatchDto? first, OutputSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new OutputSettingsPatchDto(
            second.ParentDir ?? first.ParentDir,
            second.NameFormat ?? first.NameFormat,
            second.InvalidReplaceStr ?? first.InvalidReplaceStr,
            second.WritePlaylist ?? first.WritePlaylist,
            second.WriteIndex ?? first.WriteIndex,
            second.HasConfiguredIndex ?? first.HasConfiguredIndex,
            second.M3uFilePath ?? first.M3uFilePath,
            second.IndexFilePath ?? first.IndexFilePath,
            second.IncompleteAlbumAction ?? first.IncompleteAlbumAction,
            Combine(first.OnComplete, second.OnComplete),
            second.AlbumArtOnly ?? first.AlbumArtOnly,
            second.AlbumArtOption ?? first.AlbumArtOption);
    }

    private static SearchSettingsPatchDto? Combine(SearchSettingsPatchDto? first, SearchSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new SearchSettingsPatchDto(
            Combine(first.NecessaryCond, second.NecessaryCond),
            Combine(first.PreferredCond, second.PreferredCond),
            Combine(first.NecessaryFolderCond, second.NecessaryFolderCond),
            Combine(first.PreferredFolderCond, second.PreferredFolderCond),
            second.SearchTimeout ?? first.SearchTimeout,
            second.DownrankOn ?? first.DownrankOn,
            second.IgnoreOn ?? first.IgnoreOn,
            second.FastSearch ?? first.FastSearch,
            second.FastSearchDelay ?? first.FastSearchDelay,
            second.FastSearchMinUpSpeed ?? first.FastSearchMinUpSpeed,
            second.DesperateSearch ?? first.DesperateSearch,
            second.NoRemoveSpecialChars ?? first.NoRemoveSpecialChars,
            second.RemoveSingleCharSearchTerms ?? first.RemoveSingleCharSearchTerms,
            second.NoBrowseFolder ?? first.NoBrowseFolder,
            second.Relax ?? first.Relax,
            second.StrictAlbumQuality ?? first.StrictAlbumQuality,
            second.ArtistMaybeWrong ?? first.ArtistMaybeWrong,
            second.IsAggregate ?? first.IsAggregate,
            second.MinSharesAggregate ?? first.MinSharesAggregate,
            second.AggregateLengthTol ?? first.AggregateLengthTol);
    }

    private static FileConditionsPatchDto? Combine(FileConditionsPatchDto? first, FileConditionsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new FileConditionsPatchDto(
            second.LengthTolerance ?? first.LengthTolerance,
            second.MinBitrate ?? first.MinBitrate,
            second.MaxBitrate ?? first.MaxBitrate,
            second.MinSampleRate ?? first.MinSampleRate,
            second.MaxSampleRate ?? first.MaxSampleRate,
            second.MinBitDepth ?? first.MinBitDepth,
            second.MaxBitDepth ?? first.MaxBitDepth,
            second.StrictTitle ?? first.StrictTitle,
            second.StrictArtist ?? first.StrictArtist,
            second.StrictAlbum ?? first.StrictAlbum,
            Combine(first.Formats, second.Formats),
            Combine(first.BannedUsers, second.BannedUsers),
            Combine(first.AllowedUsers, second.AllowedUsers),
            second.AcceptNoLength ?? first.AcceptNoLength,
            second.AcceptMissingProps ?? first.AcceptMissingProps);
    }

    private static FolderConditionsPatchDto? Combine(FolderConditionsPatchDto? first, FolderConditionsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new FolderConditionsPatchDto(
            second.MinTrackCount ?? first.MinTrackCount,
            second.MaxTrackCount ?? first.MaxTrackCount,
            Combine(first.RequiredTrackTitles, second.RequiredTrackTitles));
    }

    private static SkipSettingsPatchDto? Combine(SkipSettingsPatchDto? first, SkipSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new SkipSettingsPatchDto(
            second.SkipExisting ?? first.SkipExisting,
            second.SkipNotFound ?? first.SkipNotFound,
            second.SkipMode ?? first.SkipMode,
            second.SkipMusicDir ?? first.SkipMusicDir,
            second.SkipModeMusicDir ?? first.SkipModeMusicDir,
            second.SkipCheckCond ?? first.SkipCheckCond,
            second.SkipCheckPrefCond ?? first.SkipCheckPrefCond);
    }

    private static PreprocessSettingsPatchDto? Combine(PreprocessSettingsPatchDto? first, PreprocessSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new PreprocessSettingsPatchDto(
            second.RemoveFt ?? first.RemoveFt,
            second.RemoveBrackets ?? first.RemoveBrackets,
            second.ExtractArtist ?? first.ExtractArtist,
            second.ParseTitleTemplate ?? first.ParseTitleTemplate,
            Combine(first.Regex, second.Regex));
    }

    private static ExtractionSettingsPatchDto? Combine(ExtractionSettingsPatchDto? first, ExtractionSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new ExtractionSettingsPatchDto(
            second.Input ?? first.Input,
            second.InputType ?? first.InputType,
            second.MaxTracks ?? first.MaxTracks,
            second.Offset ?? first.Offset,
            second.Reverse ?? first.Reverse,
            second.RemoveTracksFromSource ?? first.RemoveTracksFromSource,
            second.RequestedMode ?? first.RequestedMode,
            second.UpgradeToAlbum ?? first.UpgradeToAlbum,
            second.SetAlbumMinTrackCount ?? first.SetAlbumMinTrackCount,
            second.SetAlbumMaxTrackCount ?? first.SetAlbumMaxTrackCount);
    }

    private static TransferSettingsPatchDto? Combine(TransferSettingsPatchDto? first, TransferSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new TransferSettingsPatchDto(
            second.MaxDownloadRetries ?? first.MaxDownloadRetries,
            second.UnknownErrorRetries ?? first.UnknownErrorRetries,
            second.NoIncompleteExt ?? first.NoIncompleteExt,
            second.AlbumTrackCountMaxRetries ?? first.AlbumTrackCountMaxRetries,
            second.MaxStaleTime ?? first.MaxStaleTime);
    }

    private static SpotifySettingsPatchDto? Combine(SpotifySettingsPatchDto? first, SpotifySettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new SpotifySettingsPatchDto(
            second.ClientId ?? first.ClientId,
            second.ClientSecret ?? first.ClientSecret,
            second.Token ?? first.Token,
            second.Refresh ?? first.Refresh);
    }

    private static YouTubeSettingsPatchDto? Combine(YouTubeSettingsPatchDto? first, YouTubeSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new YouTubeSettingsPatchDto(
            second.ApiKey ?? first.ApiKey,
            second.GetDeleted ?? first.GetDeleted,
            second.DeletedOnly ?? first.DeletedOnly);
    }

    private static YtDlpSettingsPatchDto? Combine(YtDlpSettingsPatchDto? first, YtDlpSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new YtDlpSettingsPatchDto(
            second.UseYtdlp ?? first.UseYtdlp,
            second.YtdlpArgument ?? first.YtdlpArgument);
    }

    private static CsvSettingsPatchDto? Combine(CsvSettingsPatchDto? first, CsvSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new CsvSettingsPatchDto(
            second.ArtistCol ?? first.ArtistCol,
            second.AlbumCol ?? first.AlbumCol,
            second.TitleCol ?? first.TitleCol,
            second.YtIdCol ?? first.YtIdCol,
            second.DescCol ?? first.DescCol,
            second.TrackCountCol ?? first.TrackCountCol,
            second.LengthCol ?? first.LengthCol,
            second.TimeUnit ?? first.TimeUnit,
            second.YtParse ?? first.YtParse);
    }

    private static BandcampSettingsPatchDto? Combine(BandcampSettingsPatchDto? first, BandcampSettingsPatchDto? second)
        => second ?? first;

    private static CollectionPatchDto<T>? Combine<T>(CollectionPatchDto<T>? first, CollectionPatchDto<T>? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        IReadOnlyList<T>? append = first.Append == null && second.Append == null
            ? null
            : [.. first.Append ?? [], .. second.Append ?? []];
        return new CollectionPatchDto<T>(
            second.Replace ?? first.Replace,
            append);
    }

    private static T? Changed<T>(T before, T after) where T : struct
        => EqualityComparer<T>.Default.Equals(before, after) ? null : after;

    private static T? ChangedNullable<T>(T? before, T? after) where T : struct
        => EqualityComparer<T?>.Default.Equals(before, after) ? null : after;

    private static string? Changed(string? before, string? after)
        => before == after ? null : after;

    private static CollectionPatchDto<T>? Changed<T>(IEnumerable<T>? before, IEnumerable<T>? after)
    {
        var beforeItems = before?.ToArray() ?? [];
        var afterItems = after?.ToArray() ?? [];
        return beforeItems.SequenceEqual(afterItems)
            ? null
            : new CollectionPatchDto<T>(Replace: afterItems);
    }

    private static T? NullIfEmpty<T>(T value, T empty) where T : class
        => EqualityComparer<T>.Default.Equals(value, empty) ? null : value;

    private static void ApplyOutput(OutputSettings target, OutputSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.ParentDir is { } parentDir) target.ParentDir = parentDir;
        if (patch.NameFormat is { } nameFormat) target.NameFormat = nameFormat;
        if (patch.InvalidReplaceStr is { } invalidReplaceStr) target.InvalidReplaceStr = invalidReplaceStr;
        if (patch.WritePlaylist is { } writePlaylist) target.WritePlaylist = writePlaylist;
        if (patch.WriteIndex is { } writeIndex) target.WriteIndex = writeIndex;
        if (patch.HasConfiguredIndex is { } hasConfiguredIndex) target.HasConfiguredIndex = hasConfiguredIndex;
        if (patch.M3uFilePath is { } m3uFilePath) target.M3uFilePath = m3uFilePath;
        if (patch.IndexFilePath is { } indexFilePath) target.IndexFilePath = indexFilePath;
        if (patch.IncompleteAlbumAction is { } incompleteAlbumAction)
        {
            target.IncompleteAlbumAction.Kind = incompleteAlbumAction.Kind;
            target.IncompleteAlbumAction.Path = incompleteAlbumAction.Path;
        }
        if (patch.OnComplete is { } onComplete)
        {
            ValidateOnCompletePatch(onComplete);
            target.OnComplete ??= [];
            onComplete.ApplyTo(target.OnComplete);
        }
        if (patch.AlbumArtOnly is { } albumArtOnly) target.AlbumArtOnly = albumArtOnly;
        if (patch.AlbumArtOption is { } albumArtOption) target.AlbumArtOption = albumArtOption;
    }

    private static void ApplySearch(SearchSettings target, SearchSettingsPatchDto? patch)
    {
        if (patch == null) return;
        ApplyFileConditions(target.NecessaryCond, patch.NecessaryCond);
        ApplyFileConditions(target.PreferredCond, patch.PreferredCond);
        ApplyFolderConditions(target.NecessaryFolderCond, patch.NecessaryFolderCond);
        ApplyFolderConditions(target.PreferredFolderCond, patch.PreferredFolderCond);
        if (patch.SearchTimeout is { } searchTimeout) target.SearchTimeout = searchTimeout;
        if (patch.DownrankOn is { } downrankOn) target.DownrankOn = downrankOn;
        if (patch.IgnoreOn is { } ignoreOn) target.IgnoreOn = ignoreOn;
        if (patch.FastSearch is { } fastSearch) target.FastSearch = fastSearch;
        if (patch.FastSearchDelay is { } fastSearchDelay) target.FastSearchDelay = fastSearchDelay;
        if (patch.FastSearchMinUpSpeed is { } fastSearchMinUpSpeed) target.FastSearchMinUpSpeed = fastSearchMinUpSpeed;
        if (patch.DesperateSearch is { } desperateSearch) target.DesperateSearch = desperateSearch;
        if (patch.NoRemoveSpecialChars is { } noRemoveSpecialChars) target.NoRemoveSpecialChars = noRemoveSpecialChars;
        if (patch.RemoveSingleCharSearchTerms is { } removeSingleCharSearchTerms) target.RemoveSingleCharSearchTerms = removeSingleCharSearchTerms;
        if (patch.NoBrowseFolder is { } noBrowseFolder) target.NoBrowseFolder = noBrowseFolder;
        if (patch.Relax is { } relax) target.Relax = relax;
        if (patch.StrictAlbumQuality is { } strictAlbumQuality) target.StrictAlbumQuality = strictAlbumQuality;
        if (patch.ArtistMaybeWrong is { } artistMaybeWrong) target.ArtistMaybeWrong = artistMaybeWrong;
        if (patch.IsAggregate is { } isAggregate) target.IsAggregate = isAggregate;
        if (patch.MinSharesAggregate is { } minSharesAggregate) target.MinSharesAggregate = minSharesAggregate;
        if (patch.AggregateLengthTol is { } aggregateLengthTol) target.AggregateLengthTol = aggregateLengthTol;
    }

    private static void ApplyFileConditions(FileConditions target, FileConditionsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.LengthTolerance is { } lengthTolerance) target.LengthTolerance = lengthTolerance;
        if (patch.MinBitrate is { } minBitrate) target.MinBitrate = minBitrate;
        if (patch.MaxBitrate is { } maxBitrate) target.MaxBitrate = maxBitrate;
        if (patch.MinSampleRate is { } minSampleRate) target.MinSampleRate = minSampleRate;
        if (patch.MaxSampleRate is { } maxSampleRate) target.MaxSampleRate = maxSampleRate;
        if (patch.MinBitDepth is { } minBitDepth) target.MinBitDepth = minBitDepth;
        if (patch.MaxBitDepth is { } maxBitDepth) target.MaxBitDepth = maxBitDepth;
        if (patch.StrictTitle is { } strictTitle) target.StrictTitle = strictTitle;
        if (patch.StrictArtist is { } strictArtist) target.StrictArtist = strictArtist;
        if (patch.StrictAlbum is { } strictAlbum) target.StrictAlbum = strictAlbum;
        if (patch.Formats is { } formats) target.Formats = formats.ApplyTo(target.Formats.ToList()).ToArray();
        if (patch.BannedUsers is { } bannedUsers) target.BannedUsers = bannedUsers.ApplyTo(target.BannedUsers.ToList()).ToArray();
        if (patch.AllowedUsers is { } allowedUsers) target.AllowedUsers = allowedUsers.ApplyTo(target.AllowedUsers.ToList()).ToArray();
        if (patch.AcceptNoLength is { } acceptNoLength) target.AcceptNoLength = acceptNoLength;
        if (patch.AcceptMissingProps is { } acceptMissingProps) target.AcceptMissingProps = acceptMissingProps;
    }

    private static void ApplyFolderConditions(FolderConditions target, FolderConditionsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.MinTrackCount is { } minTrackCount) target.MinTrackCount = minTrackCount;
        if (patch.MaxTrackCount is { } maxTrackCount) target.MaxTrackCount = maxTrackCount;
        patch.RequiredTrackTitles?.ApplyTo(target.RequiredTrackTitles);
    }

    private static void ApplySkip(SkipSettings target, SkipSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.SkipExisting is { } skipExisting) target.SkipExisting = skipExisting;
        if (patch.SkipNotFound is { } skipNotFound) target.SkipNotFound = skipNotFound;
        if (patch.SkipMode is { } skipMode) target.SkipMode = skipMode;
        if (patch.SkipMusicDir is { } skipMusicDir) target.SkipMusicDir = skipMusicDir;
        if (patch.SkipModeMusicDir is { } skipModeMusicDir) target.SkipModeMusicDir = skipModeMusicDir;
        if (patch.SkipCheckCond is { } skipCheckCond) target.SkipCheckCond = skipCheckCond;
        if (patch.SkipCheckPrefCond is { } skipCheckPrefCond) target.SkipCheckPrefCond = skipCheckPrefCond;
    }

    private static void ApplyPreprocess(PreprocessSettings target, PreprocessSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.RemoveFt is { } removeFt) target.RemoveFt = removeFt;
        if (patch.RemoveBrackets is { } removeBrackets) target.RemoveBrackets = removeBrackets;
        if (patch.ExtractArtist is { } extractArtist) target.ExtractArtist = extractArtist;
        if (patch.ParseTitleTemplate is { } parseTitleTemplate) target.ParseTitleTemplate = parseTitleTemplate;
        if (patch.Regex is { } regex)
        {
            var current = target.Regex?.Select(ToRegexRuleDto).ToList();
            var updated = regex.ApplyTo(current);
            target.Regex = updated.Select(ToRegexRule).ToList();
        }
    }

    private static void ApplyExtraction(ExtractionSettings target, ExtractionSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.Input is { } input) target.Input = input;
        if (patch.InputType is { } inputType) target.InputType = inputType;
        if (patch.MaxTracks is { } maxTracks) target.MaxTracks = maxTracks;
        if (patch.Offset is { } offset) target.Offset = offset;
        if (patch.Reverse is { } reverse) target.Reverse = reverse;
        if (patch.RemoveTracksFromSource is { } removeTracksFromSource) target.RemoveTracksFromSource = removeTracksFromSource;
        if (patch.RequestedMode is { } requestedMode) target.RequestedMode = requestedMode;
        if (patch.UpgradeToAlbum is { } upgradeToAlbum) target.UpgradeToAlbum = upgradeToAlbum;
        if (patch.SetAlbumMinTrackCount is { } setAlbumMinTrackCount) target.SetAlbumMinTrackCount = setAlbumMinTrackCount;
        if (patch.SetAlbumMaxTrackCount is { } setAlbumMaxTrackCount) target.SetAlbumMaxTrackCount = setAlbumMaxTrackCount;
    }

    private static void ApplyTransfer(TransferSettings target, TransferSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.MaxDownloadRetries is { } maxDownloadRetries) target.MaxDownloadRetries = maxDownloadRetries;
        if (patch.UnknownErrorRetries is { } unknownErrorRetries) target.UnknownErrorRetries = unknownErrorRetries;
        if (patch.NoIncompleteExt is { } noIncompleteExt) target.NoIncompleteExt = noIncompleteExt;
        if (patch.AlbumTrackCountMaxRetries is { } albumTrackCountMaxRetries) target.AlbumTrackCountMaxRetries = albumTrackCountMaxRetries;
        if (patch.MaxStaleTime is { } maxStaleTime) target.MaxStaleTime = maxStaleTime;
    }

    private static void ApplySpotify(SpotifySettings target, SpotifySettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.ClientId is { } clientId) target.ClientId = clientId;
        if (patch.ClientSecret is { } clientSecret) target.ClientSecret = clientSecret;
        if (patch.Token is { } token) target.Token = token;
        if (patch.Refresh is { } refresh) target.Refresh = refresh;
    }

    private static void ApplyYouTube(YouTubeSettings target, YouTubeSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.ApiKey is { } apiKey) target.ApiKey = apiKey;
        if (patch.GetDeleted is { } getDeleted) target.GetDeleted = getDeleted;
        if (patch.DeletedOnly is { } deletedOnly) target.DeletedOnly = deletedOnly;
    }

    private static void ApplyYtDlp(YtDlpSettings target, YtDlpSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.UseYtdlp is { } useYtdlp) target.UseYtdlp = useYtdlp;
        if (patch.YtdlpArgument is { } ytdlpArgument) target.YtdlpArgument = ytdlpArgument;
    }

    private static void ApplyCsv(CsvSettings target, CsvSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.ArtistCol is { } artistCol) target.ArtistCol = artistCol;
        if (patch.AlbumCol is { } albumCol) target.AlbumCol = albumCol;
        if (patch.TitleCol is { } titleCol) target.TitleCol = titleCol;
        if (patch.YtIdCol is { } ytIdCol) target.YtIdCol = ytIdCol;
        if (patch.DescCol is { } descCol) target.DescCol = descCol;
        if (patch.TrackCountCol is { } trackCountCol) target.TrackCountCol = trackCountCol;
        if (patch.LengthCol is { } lengthCol) target.LengthCol = lengthCol;
        if (patch.TimeUnit is { } timeUnit) target.TimeUnit = timeUnit;
        if (patch.YtParse is { } ytParse) target.YtParse = ytParse;
    }

    private static void ApplyBandcamp(BandcampSettings target, BandcampSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.HtmlFromFile is { } htmlFromFile) target.HtmlFromFile = htmlFromFile;
    }

    private static List<T> ApplyTo<T>(this CollectionPatchDto<T> patch, List<T>? target)
    {
        target ??= [];
        if (patch.Replace != null)
        {
            target.Clear();
            target.AddRange(patch.Replace);
        }
        if (patch.Append != null)
            target.AddRange(patch.Append);
        return target;
    }

    private static RegexRuleDto ToRegexRuleDto((RegexFields Match, RegexFields Replace) rule)
        => new(ToRegexFieldsDto(rule.Match), ToRegexFieldsDto(rule.Replace));

    private static RegexFieldsDto ToRegexFieldsDto(RegexFields fields)
        => new(fields.Title, fields.Artist, fields.Album);

    private static (RegexFields, RegexFields) ToRegexRule(RegexRuleDto rule)
        => (ToRegexFields(rule.Match), ToRegexFields(rule.Replace));

    private static RegexFields ToRegexFields(RegexFieldsDto fields)
        => new() { Title = fields.Title, Artist = fields.Artist, Album = fields.Album };

    private static void ValidateOnCompletePatch(CollectionPatchDto<string> patch)
    {
        OnCompleteExecutor.ValidateCommands(patch.Replace);
        OnCompleteExecutor.ValidateCommands(patch.Append);
    }

}

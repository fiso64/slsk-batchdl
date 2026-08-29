using System.Text.Json;
using Sockseek.Api;
using Sockseek.Persistence.Read;

namespace Sockseek.Server.Persistence;

internal static class HistoricalJobDtoMapper
{
    public static FileSearchProjectionRequestDto? DefaultFileProjection(PersistedJob job)
    {
        if (string.IsNullOrWhiteSpace(job.PayloadJson))
            return null;
        using var document = JsonDocument.Parse(job.PayloadJson);
        var projection = Child(document.RootElement, "DefaultFileProjection");
        if (projection.ValueKind != JsonValueKind.Object)
            return null;
        var query = Child(projection, "Query");
        return new FileSearchProjectionRequestDto(
            new SongQueryDto(
                Text(query, "Artist"), Text(query, "Title"), Text(query, "Album"), Text(query, "URI"),
                NullableInt(query, "Length"), Bool(query, "ArtistMaybeWrong")),
            Bool(projection, "IncludeFullResults"));
    }

    public static FolderSearchProjectionRequestDto? DefaultFolderProjection(PersistedJob job)
    {
        if (string.IsNullOrWhiteSpace(job.PayloadJson))
            return null;
        using var document = JsonDocument.Parse(job.PayloadJson);
        var projection = Child(document.RootElement, "DefaultFolderProjection");
        if (projection.ValueKind != JsonValueKind.Object)
            return null;
        var query = Child(projection, "Query");
        return new FolderSearchProjectionRequestDto(
            new AlbumQueryDto(
                Text(query, "Artist"), Text(query, "Album"), Text(query, "SearchHint"), Text(query, "URI"),
                Bool(query, "ArtistMaybeWrong")),
            Bool(projection, "IncludeFiles"));
    }

    public static JobSummaryDto ToSummary(PersistedJob job)
        => new(
            job.Id,
            checked((int)job.DisplayId),
            job.WorkflowId,
            Parse(job.Kind, ServerJobKind.Generic),
            Parse(job.LifecycleState, ServerJobLifecycleState.Pending),
            Parse(job.ActivityPhase, ServerJobActivityPhase.None),
            job.ActivityUntilUtc,
            Parse(job.TerminalOutcome, ServerJobTerminalOutcome.None),
            Parse(job.SkipReason, ServerJobSkipReason.None),
            job.ItemName,
            job.QueryText,
            job.FailureReason == "None" ? null : Parse(job.FailureReason, ServerJobFailureReason.Other),
            job.FailureMessage,
            job.ParentJobId,
            job.ResultJobId,
            job.SourceJobId,
            null,
            null,
            [],
            [],
            job.FailureDetail,
            Parse(job.CancellationSource, ServerJobCancellationSource.None));

    public static JobPayloadDto ToPayload(PersistedJob job)
    {
        if (string.IsNullOrWhiteSpace(job.PayloadJson))
            return new GenericJobPayloadDto(job.Kind);
        using var document = JsonDocument.Parse(job.PayloadJson);
        var root = document.RootElement;
        return Parse(job.Kind, ServerJobKind.Generic) switch
        {
            ServerJobKind.Extract => new ExtractJobPayloadDto(
                Text(root, "Input") ?? "",
                Text(root, "InputType"),
                job.ResultJobId),
            ServerJobKind.Search => new SearchJobPayloadDto(
                Text(root, "QueryText") ?? job.QueryText ?? "",
                DefaultFileProjection(job),
                DefaultFolderProjection(job),
                Int(root, "ResultCount"),
                Int(root, "Revision"),
                Bool(root, "IsComplete")),
            ServerJobKind.Song => new SongJobPayloadDto(
                SongQuery(root),
                null,
                FileState(root),
                JobId: job.Id,
                DisplayId: checked((int)job.DisplayId),
                LifecycleState: Parse(job.LifecycleState, ServerJobLifecycleState.Pending),
                ActivityPhase: Parse(job.ActivityPhase, ServerJobActivityPhase.None),
                ActivityUntilUtc: job.ActivityUntilUtc,
                TerminalOutcome: Parse(job.TerminalOutcome, ServerJobTerminalOutcome.None),
                SkipReason: Parse(job.SkipReason, ServerJobSkipReason.None),
                FailureReason: job.FailureReason == "None" ? null : Parse(job.FailureReason, ServerJobFailureReason.Other),
                FailureMessage: job.FailureMessage,
                AvailableActions: [],
                CancellationSource: Parse(job.CancellationSource, ServerJobCancellationSource.None),
                DownloadSource: Parse(Text(root, "DownloadSource"), ServerSongDownloadSource.None),
                ExactTarget: NullablePeerTarget(Child(root, "ExactTarget"))),
            ServerJobKind.Album => new AlbumJobPayloadDto(
                AlbumQuery(root),
                Int(root, "ResultCount"),
                DirectoryState(root),
                null,
                null),
            ServerJobKind.RemoteFile => RemoteFilePayload(root),
            ServerJobKind.RemoteDirectory => RemoteDirectoryPayload(root),
            ServerJobKind.Aggregate => new AggregateJobPayloadDto(
                SongQuery(root),
                Int(root, "SongCount"),
                0,
                0,
                0),
            ServerJobKind.AlbumAggregate => new AlbumAggregateJobPayloadDto(
                AlbumQuery(root),
                Int(root, "AlbumCount")),
            ServerJobKind.JobList => new JobListPayloadDto(Int(root, "Count"), 0, 0, 0, 0),
            ServerJobKind.RetrieveFolder => new RetrieveFolderJobPayloadDto(
                Text(root, "FolderPath") ?? "",
                Text(root, "Username") ?? "",
                Int(root, "NewFilesFoundCount"),
                Parse(Text(root, "RetrievalOutcome"), ServerFolderRetrievalOutcome.None),
                Bool(root, "RetrievalCancelled")),
            _ => new GenericJobPayloadDto(Text(root, "Text") ?? job.Kind),
        };
    }

    private static SongQueryDto SongQuery(JsonElement root)
    {
        var query = Child(root, "Query");
        return new SongQueryDto(
            Text(query, "Artist"),
            Text(query, "Title"),
            Text(query, "Album"),
            Text(query, "URI"),
            NullableInt(query, "Length"),
            Bool(query, "ArtistMaybeWrong"));
    }

    private static AlbumQueryDto AlbumQuery(JsonElement root)
    {
        var query = Child(root, "Query");
        return new AlbumQueryDto(
            Text(query, "Artist"),
            Text(query, "Album"),
            Text(query, "SearchHint"),
            Text(query, "URI"),
            Bool(query, "ArtistMaybeWrong"));
    }

    private static FileDownloadStateDto FileState(JsonElement root)
    {
        var file = Child(root, "File");
        if (file.ValueKind != JsonValueKind.Object)
            return new FileDownloadStateDto(Text(root, "DownloadPath"), 0, null, null);

        long transferred = Long(file, "BytesTransferred");
        long? size = NullableLong(file, "FileSize");
        return new FileDownloadStateDto(
            Text(file, "DownloadPath"),
            transferred,
            size,
            size > 0 ? Math.Round((double)transferred / size.Value * 100, 2) : null);
    }

    private static DirectoryDownloadStateDto DirectoryState(JsonElement root)
    {
        var directory = Child(root, "Directory");
        if (directory.ValueKind != JsonValueKind.Object)
        {
            return new DirectoryDownloadStateDto(
                "unresolved", null, Text(root, "DownloadPath"), 0, 0, 0, 0, 0, 0, null);
        }

        long transferred = Long(directory, "BytesTransferred");
        long total = Long(directory, "TotalKnownBytes");
        return new DirectoryDownloadStateDto(
            Text(directory, "Phase") ?? "unresolved",
            NullableInt(directory, "AttemptNumber"),
            Text(directory, "DownloadPath"),
            Int(directory, "FileCount"),
            Int(directory, "TerminalFileCount"),
            Int(directory, "SuccessfulFileCount"),
            Int(directory, "FailedFileCount"),
            transferred,
            total,
            total > 0 ? Math.Round((double)transferred / total * 100, 2) : null);
    }

    private static RemoteFileJobPayloadDto RemoteFilePayload(JsonElement root)
    {
        var output = Child(root, "OutputPath");
        return new RemoteFileJobPayloadDto(
            PeerTarget(Child(root, "Target")),
            StringArray(output, "Components"),
            FileState(root));
    }

    private static RemoteDirectoryJobPayloadDto RemoteDirectoryPayload(JsonElement root)
    {
        var source = Child(root, "DirectorySource");
        return new RemoteDirectoryJobPayloadDto(
            Parse(Text(root, "SourceKind"), RemoteDirectorySourceKindDto.PeerDirectory),
            Text(source, "Username"),
            Text(source, "FolderPath"),
            DirectoryState(root));
    }

    private static PeerFileTargetDto PeerTarget(JsonElement element)
    {
        var identity = Child(element, "Identity");
        var attributes = ChildArray(element, "Attributes")
            .Select(attribute => new FileAttributeDto(
                Text(attribute, "Type") ?? "Unknown",
                Int(attribute, "Value")))
            .ToArray();
        return new PeerFileTargetDto(
            Text(identity, "Username") ?? "",
            Text(identity, "Filename") ?? "",
            NullableLong(element, "Size"),
            Text(element, "Extension"),
            NullableInt(element, "BitRate"),
            NullableInt(element, "BitDepth"),
            NullableInt(element, "SampleRate"),
            NullableInt(element, "Length"),
            attributes.Length == 0 ? null : attributes);
    }

    private static PeerFileTargetDto? NullablePeerTarget(JsonElement element)
        => element.ValueKind == JsonValueKind.Object ? PeerTarget(element) : null;

    private static JsonElement Child(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static string? Text(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
                : null;

    private static int Int(JsonElement root, string name)
        => NullableInt(root, name) ?? 0;

    private static int? NullableInt(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int result)
                ? result
                : null;

    private static long Long(JsonElement root, string name)
        => NullableLong(root, name) ?? 0;

    private static long? NullableLong(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long result)
                ? result
                : null;

    private static IReadOnlyList<JsonElement> ChildArray(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().ToArray()
                : Array.Empty<JsonElement>();

    private static IReadOnlyList<string> StringArray(JsonElement root, string name)
        => ChildArray(root, name)
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? "")
            .ToArray();

    private static bool Bool(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;

    private static T Parse<T>(string? value, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}

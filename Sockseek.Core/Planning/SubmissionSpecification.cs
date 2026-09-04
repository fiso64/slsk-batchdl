using System.Text.Json;
using System.Text.Json.Serialization;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Planning;

public sealed record SubmissionSourceRevision(
    string Kind,
    string Identity,
    string? Digest,
    long? Length,
    DateTimeOffset? LastModifiedUtc);

/// <summary>
/// Versioned, transport-independent command shape. Optional members are
/// discriminated by Kind; exact peer usernames and paths are never normalized.
/// </summary>
public sealed record NormalizedJobCommand
{
    public required JobSnapshotKind Kind { get; init; }
    public string? ItemName { get; init; }
    public int ItemNumber { get; init; } = 1;
    public int LineNumber { get; init; }
    public SourceMutation? SourceMutation { get; init; }
    public InputType? SourceInputType { get; init; }
    public bool EnablesIndexByDefault { get; init; }
    public DownloadBehaviorPolicy DownloadBehavior { get; init; } = new();
    public FileConditionPatch? ExtractorConditions { get; init; }
    public FileConditionPatch? ExtractorPreferredConditions { get; init; }
    public FolderConditionPatch? ExtractorFolderConditions { get; init; }
    public FolderConditionPatch? ExtractorPreferredFolderConditions { get; init; }
    public string? ArtifactId { get; init; }
    public string? PlannedEffectiveSettingsJson { get; init; }
    public IReadOnlyList<string>? PlannedCredentialBindings { get; init; }

    public string? Input { get; init; }
    public InputType? InputType { get; init; }
    public ExtractionMode? RequestedMode { get; init; }
    public DownloadBehaviorPolicy? ResultDownloadBehavior { get; init; }
    public string? QueryText { get; init; }
    public SongQueryDefinition? SongQuery { get; init; }
    public AlbumQueryDefinition? AlbumQuery { get; init; }
    public bool IncludeFullResults { get; init; }
    public IReadOnlyList<NormalizedJobCommand>? Children { get; init; }
    public PeerFileTargetSnapshot? PeerFile { get; init; }
    public IReadOnlyList<string>? OutputPathComponents { get; init; }
    public PeerDirectoryIdentitySnapshot? PeerDirectory { get; init; }
    public DirectoryTransferPlanSnapshot? DirectoryPlan { get; init; }
    public bool HasPlannedExtraction { get; init; }
    public string? PlannedExtractionFailure { get; init; }
    public NormalizedJobCommand? ExtractedResult { get; init; }
    public SearchDefinition? SearchDefinition { get; init; }
}

/// <summary>
/// Durable accepted intent. EffectiveSettingsJson contains the complete
/// execution settings except configured credentials; credential binding names
/// record which current operator-owned secret slots execution may require.
/// </summary>
public sealed record SubmissionSpecification(
    int SchemaVersion,
    NormalizedJobCommand Command,
    string EffectiveSettingsJson,
    IReadOnlyList<string> CredentialBindings,
    SearchDefinition? Search,
    SubmissionSourceRevision? SourceRevision)
{
    public const int CurrentSchemaVersion = 2;

    public static SubmissionSpecification Create(
        Job job,
        DownloadSettings effectiveSettings,
        SubmissionSourceRevision? sourceRevision = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(effectiveSettings);
        sourceRevision ??= job.PlannedSourceRevision;
        RetainedSettings retained = RetainSettings(effectiveSettings);

        SearchDefinition? search = job switch
        {
            SearchJob searchJob => searchJob.Definition
                ?? SearchDefinition.Create(searchJob, effectiveSettings.Search),
            SongJob or AlbumJob or AggregateJob or AlbumAggregateJob =>
                SearchDefinition.Create(job, effectiveSettings.Search),
            _ => null,
        };
        return new SubmissionSpecification(
            CurrentSchemaVersion,
            CommandFrom(job),
            retained.Json,
            retained.CredentialBindings,
            search,
            sourceRevision);
    }

    public DownloadSettings MaterializeSettings(DownloadSettings? credentialSource = null)
    {
        Validate();
        return RestoreSettings(EffectiveSettingsJson, CredentialBindings, credentialSource);
    }

    public Job MaterializeJob(DownloadSettings? credentialSource = null)
    {
        Validate();
        Job job = JobFrom(Command, credentialSource);
        if (job is SearchJob searchJob && Search != null)
            searchJob.Definition = Search;
        return job;
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Unsupported submission-specification schema version {SchemaVersion}.");
        ArgumentNullException.ThrowIfNull(Command);
        ValidateCommand(Command);
        _ = RetainedSettingsCodec.Deserialize(EffectiveSettingsJson);
        Search?.Validate();
    }

    private static NormalizedJobCommand CommandFrom(Job job)
    {
        var command = new NormalizedJobCommand
        {
            Kind = Kind(job),
            ItemName = job.ItemName,
            ItemNumber = job.ItemNumber,
            LineNumber = job.LineNumber,
            SourceMutation = job.SourceMutation,
            SourceInputType = job.SourceInputType,
            EnablesIndexByDefault = job.EnablesIndexByDefault,
            DownloadBehavior = job.DownloadBehaviorPolicy,
            ExtractorConditions = job.ExtractorCond,
            ExtractorPreferredConditions = job.ExtractorPrefCond,
            ExtractorFolderConditions = job.ExtractorFolderCond,
            ExtractorPreferredFolderConditions = job.ExtractorPrefFolderCond,
            ArtifactId = job.ArtifactId,
        };
        NormalizedJobCommand normalized = job switch
        {
            ExtractJob extract => command with
            {
                Input = extract.Input,
                InputType = extract.InputType,
                RequestedMode = extract.RequestedModeOverride,
                ResultDownloadBehavior = extract.ResultDownloadBehaviorPolicy,
                HasPlannedExtraction = extract.HasPlannedExtraction,
                PlannedExtractionFailure = extract.PlannedExtractionFailure,
                ExtractedResult = extract.HasPlannedExtraction && extract.Result != null
                    ? CommandFrom(extract.Result)
                    : null,
            },
            SearchJob search when search.DefaultFolderProjection is { } folder => command with
            {
                QueryText = search.QueryText,
                AlbumQuery = AlbumQueryDefinition.From(folder.Query),
                SearchDefinition = search.Definition,
            },
            SearchJob search when search.DefaultFileProjection is { } file => command with
            {
                QueryText = search.QueryText,
                SongQuery = SongQueryDefinition.From(file.Query),
                IncludeFullResults = file.IncludeFullResults,
                SearchDefinition = search.Definition,
            },
            SearchJob search => command with
            {
                QueryText = search.QueryText,
                SearchDefinition = search.Definition,
            },
            SongJob song => command with { SongQuery = SongQueryDefinition.From(song.Query) },
            AlbumJob album => command with { AlbumQuery = AlbumQueryDefinition.From(album.Query) },
            AggregateJob aggregate => command with { SongQuery = SongQueryDefinition.From(aggregate.Query) },
            AlbumAggregateJob aggregate => command with { AlbumQuery = AlbumQueryDefinition.From(aggregate.Query) },
            JobList list => command with { Children = list.Jobs.Select(CommandFrom).ToArray() },
            RemoteFileJob remote => command with
            {
                PeerFile = PeerFile(remote.Target),
                OutputPathComponents = remote.OutputPath.Components.ToArray(),
            },
            RemoteDirectoryJob remote when remote.Source is RemoteDirectorySource.PeerDirectory peer => command with
            {
                PeerDirectory = new(peer.Directory.Username, peer.Directory.FolderPath),
            },
            RemoteDirectoryJob remote when remote.Source is RemoteDirectorySource.Resolved resolved => command with
            {
                DirectoryPlan = DirectoryPlan(resolved.Plan),
            },
            RetrieveFolderJob retrieve => command with
            {
                PeerDirectory = new(
                    retrieve.Directory.Username,
                    retrieve.Directory.FolderPath),
            },
            _ => throw new NotSupportedException($"Job type '{job.GetType().Name}' has no normalized command."),
        };
        if (job.PlannedEffectiveSettings is { } planned)
        {
            RetainedSettings retained = RetainSettings(planned);
            normalized = normalized with
            {
                PlannedEffectiveSettingsJson = retained.Json,
                PlannedCredentialBindings = retained.CredentialBindings,
            };
        }
        return normalized;
    }

    private static Job JobFrom(
        NormalizedJobCommand command,
        DownloadSettings? credentialSource)
    {
        Job job = command.Kind switch
        {
            JobSnapshotKind.Extract => new ExtractJob(
                command.Input!,
                command.InputType)
            {
                RequestedModeOverride = command.RequestedMode,
                ResultDownloadBehaviorPolicy = command.ResultDownloadBehavior,
                HasPlannedExtraction = command.HasPlannedExtraction,
                PlannedExtractionFailure = command.PlannedExtractionFailure,
                Result = command.ExtractedResult == null
                    ? null
                    : JobFrom(command.ExtractedResult, credentialSource),
            },
            JobSnapshotKind.Search when command.AlbumQuery != null =>
                new SearchJob(command.AlbumQuery.ToQuery()),
            JobSnapshotKind.Search when command.SongQuery != null =>
                new SearchJob(command.SongQuery.ToQuery(), command.IncludeFullResults),
            JobSnapshotKind.Search => new SearchJob(command.QueryText!),
            JobSnapshotKind.Song => new SongJob(command.SongQuery!.ToQuery()),
            JobSnapshotKind.Album => new AlbumJob(command.AlbumQuery!.ToQuery()),
            JobSnapshotKind.Aggregate => new AggregateJob(command.SongQuery!.ToQuery()),
            JobSnapshotKind.AlbumAggregate => new AlbumAggregateJob(command.AlbumQuery!.ToQuery()),
            JobSnapshotKind.JobList => new JobList(
                command.ItemName,
                command.Children!.Select(child => JobFrom(child, credentialSource))),
            JobSnapshotKind.RetrieveFolder => new RetrieveFolderJob(
                new PeerDirectoryIdentity(
                    command.PeerDirectory!.Username,
                    command.PeerDirectory.FolderPath)),
            JobSnapshotKind.RemoteFile => new RemoteFileJob(
                PeerFile(command.PeerFile!),
                new RelativeOutputPath(command.OutputPathComponents!)),
            JobSnapshotKind.RemoteDirectory when command.PeerDirectory != null =>
                new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(
                    new PeerDirectoryIdentity(
                        command.PeerDirectory.Username,
                        command.PeerDirectory.FolderPath))),
            JobSnapshotKind.RemoteDirectory => new RemoteDirectoryJob(
                new RemoteDirectorySource.Resolved(DirectoryPlan(command.DirectoryPlan!))),
            _ => throw new NotSupportedException($"Normalized command kind '{command.Kind}' is not executable."),
        };
        job.ItemName = command.ItemName;
        job.ItemNumber = command.ItemNumber;
        job.LineNumber = command.LineNumber;
        job.SourceMutation = command.SourceMutation;
        job.SourceInputType = command.SourceInputType;
        job.EnablesIndexByDefault = command.EnablesIndexByDefault;
        job.DownloadBehaviorPolicy = command.DownloadBehavior;
        job.ExtractorCond = command.ExtractorConditions;
        job.ExtractorPrefCond = command.ExtractorPreferredConditions;
        job.ExtractorFolderCond = command.ExtractorFolderConditions;
        job.ExtractorPrefFolderCond = command.ExtractorPreferredFolderConditions;
        job.ArtifactId = command.ArtifactId;
        if (command.PlannedEffectiveSettingsJson != null)
        {
            job.PlannedEffectiveSettings = RestoreSettings(
                command.PlannedEffectiveSettingsJson,
                command.PlannedCredentialBindings ?? [],
                credentialSource);
        }
        if (job is SearchJob searchJob && command.SearchDefinition != null)
            searchJob.Definition = command.SearchDefinition;
        return job;
    }

    private static void ValidateCommand(NormalizedJobCommand command)
    {
        bool valid = command.Kind switch
        {
            JobSnapshotKind.Extract => !string.IsNullOrEmpty(command.Input),
            JobSnapshotKind.Search => !string.IsNullOrWhiteSpace(command.QueryText),
            JobSnapshotKind.Song or JobSnapshotKind.Aggregate => command.SongQuery != null,
            JobSnapshotKind.Album or JobSnapshotKind.AlbumAggregate => command.AlbumQuery != null,
            JobSnapshotKind.JobList => command.Children != null,
            JobSnapshotKind.RetrieveFolder => command.PeerDirectory != null,
            JobSnapshotKind.RemoteFile => command.PeerFile != null
                && command.OutputPathComponents?.Count > 0,
            JobSnapshotKind.RemoteDirectory => (command.PeerDirectory != null)
                != (command.DirectoryPlan != null),
            _ => false,
        };
        if (!valid)
            throw new InvalidDataException($"Normalized {command.Kind} command is incomplete.");
        if (command.Children != null)
        {
            foreach (NormalizedJobCommand child in command.Children)
                ValidateCommand(child);
        }
        if (command.ExtractedResult != null)
            ValidateCommand(command.ExtractedResult);
        if (command.HasPlannedExtraction
            && command.PlannedExtractionFailure == null
            && command.ExtractedResult == null)
        {
            throw new InvalidDataException(
                "A successfully planned extraction has no retained result command.");
        }
        if (command.PlannedEffectiveSettingsJson != null)
            _ = RetainedSettingsCodec.Deserialize(command.PlannedEffectiveSettingsJson);
        command.SearchDefinition?.Validate();
    }

    private static RetainedSettings RetainSettings(DownloadSettings effectiveSettings)
    {
        var bindings = new List<string>();
        DownloadSettings retained = SettingsCloner.Clone(effectiveSettings);
        if (!string.IsNullOrEmpty(retained.Spotify.ClientId)) bindings.Add("spotify.client-id");
        if (!string.IsNullOrEmpty(retained.Spotify.ClientSecret)) bindings.Add("spotify.client-secret");
        if (!string.IsNullOrEmpty(retained.Spotify.Token)) bindings.Add("spotify.token");
        if (!string.IsNullOrEmpty(retained.Spotify.Refresh)) bindings.Add("spotify.refresh");
        if (!string.IsNullOrEmpty(retained.YouTube.ApiKey)) bindings.Add("youtube.api-key");
        retained.Spotify.ClientId = null;
        retained.Spotify.ClientSecret = null;
        retained.Spotify.Token = null;
        retained.Spotify.Refresh = null;
        retained.YouTube.ApiKey = null;
        return new(RetainedSettingsCodec.Serialize(retained), bindings);
    }

    private static DownloadSettings RestoreSettings(
        string json,
        IReadOnlyList<string> credentialBindings,
        DownloadSettings? credentialSource)
    {
        DownloadSettings settings = RetainedSettingsCodec.Deserialize(json);
        if (credentialSource == null)
            return settings;
        if (credentialBindings.Contains("spotify.client-id"))
            settings.Spotify.ClientId = credentialSource.Spotify.ClientId;
        if (credentialBindings.Contains("spotify.client-secret"))
            settings.Spotify.ClientSecret = credentialSource.Spotify.ClientSecret;
        if (credentialBindings.Contains("spotify.token"))
            settings.Spotify.Token = credentialSource.Spotify.Token;
        if (credentialBindings.Contains("spotify.refresh"))
            settings.Spotify.Refresh = credentialSource.Spotify.Refresh;
        if (credentialBindings.Contains("youtube.api-key"))
            settings.YouTube.ApiKey = credentialSource.YouTube.ApiKey;
        return settings;
    }

    private sealed record RetainedSettings(
        string Json,
        IReadOnlyList<string> CredentialBindings);

    private static JobSnapshotKind Kind(Job job) => job switch
    {
        ExtractJob => JobSnapshotKind.Extract,
        SearchJob => JobSnapshotKind.Search,
        SongJob => JobSnapshotKind.Song,
        AlbumJob => JobSnapshotKind.Album,
        AggregateJob => JobSnapshotKind.Aggregate,
        AlbumAggregateJob => JobSnapshotKind.AlbumAggregate,
        JobList => JobSnapshotKind.JobList,
        RetrieveFolderJob => JobSnapshotKind.RetrieveFolder,
        RemoteFileJob => JobSnapshotKind.RemoteFile,
        RemoteDirectoryJob => JobSnapshotKind.RemoteDirectory,
        _ => JobSnapshotKind.Generic,
    };

    private static PeerFileTargetSnapshot PeerFile(PeerFileTarget target)
        => new(
            new(target.Username, target.Filename),
            target.Size,
            target.Extension,
            target.BitRate,
            target.BitDepth,
            target.SampleRate,
            target.Length,
            target.Attributes?.ToArray());

    private static PeerFileTarget PeerFile(PeerFileTargetSnapshot target)
        => new(
            new PeerFileIdentity(target.Identity.Username, target.Identity.Filename),
            target.Size,
            target.Extension,
            target.BitRate,
            target.BitDepth,
            target.SampleRate,
            target.Length,
            target.Attributes);

    private static DirectoryTransferPlanSnapshot DirectoryPlan(DirectoryTransferPlan plan)
        => new(
            plan.DisplayRoot,
            plan.Entries.Select(entry => new DirectoryTransferEntrySnapshot(
                PeerFile(entry.Target),
                entry.RelativeDirectoryComponents.ToArray())).ToArray(),
            plan.TotalKnownBytes);

    private static DirectoryTransferPlan DirectoryPlan(DirectoryTransferPlanSnapshot plan)
        => new(
            plan.DisplayRoot,
            plan.Entries.Select(entry => new DirectoryTransferEntry(
                PeerFile(entry.Target),
                entry.RelativeDirectoryComponents)).ToArray());
}

public static class SubmissionSpecificationCodec
{
    private static readonly JsonSerializerOptions Options = RetainedSettingsCodec.CreateOptions();

    public static string Serialize(SubmissionSpecification specification)
    {
        specification.Validate();
        return JsonSerializer.Serialize(specification, Options);
    }

    public static SubmissionSpecification Deserialize(string json)
    {
        SubmissionSpecification specification =
            JsonSerializer.Deserialize<SubmissionSpecification>(json, Options)
            ?? throw new InvalidDataException("Submission specification is empty.");
        specification.Validate();
        return specification;
    }
}

internal static class RetainedSettingsCodec
{
    public static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true,
        IgnoreReadOnlyProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(DownloadSettings settings)
        => JsonSerializer.Serialize(settings, CreateOptions());

    public static DownloadSettings Deserialize(string json)
        => JsonSerializer.Deserialize<DownloadSettings>(json, CreateOptions())
            ?? throw new InvalidDataException("Retained effective settings are empty.");
}

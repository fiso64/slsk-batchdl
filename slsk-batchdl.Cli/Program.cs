using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Sldl.Server;
using Soulseek;

namespace Sldl.Cli;

internal static partial class Program
{
    public static async Task Main(string[] args)
    {
        Console.ResetColor();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (Help.PrintAndExitIfNeeded(args))
            return;

        bool daemonMode = args.Length > 0 && string.Equals(args[0], "daemon", StringComparison.OrdinalIgnoreCase);
        var bindArgs = daemonMode ? args.Skip(1).ToArray() : args;

        Logger.SetupExceptionHandling();
        Logger.AddConsole(writer: (msg, color) => Printing.WriteLine(msg, color));

        string configPath = ConfigManager.ExtractConfigPath(bindArgs);
        var configFile = ConfigManager.Load(configPath);
        var (engineSettings, rootSettings, cliSettings, daemonSettings, remoteSettings) = ConfigManager.BindAll(configFile, bindArgs);
        ConfigManager.ApplyAutoProfileCliSettings(configFile, rootSettings, cliSettings);

        if (!string.IsNullOrWhiteSpace(engineSettings.LogFilePath))
            Logger.AddOrReplaceFile(engineSettings.LogFilePath);

        Logger.SetConsoleLogLevel(rootSettings.NonVerbosePrint ? Logger.LogLevel.Error : engineSettings.LogLevel);

        if (daemonMode)
        {
            await RunDaemonAsync(bindArgs, configFile, engineSettings, rootSettings, daemonSettings);
            return;
        }

        var cts = new CancellationTokenSource();

        if (remoteSettings.IsEnabled)
        {
            await RunRemoteAsync(bindArgs, rootSettings, cliSettings, remoteSettings, cts);
            return;
        }

        var clientManager = new SoulseekClientManager(engineSettings);

        if (string.IsNullOrEmpty(rootSettings.Extraction.Input))
        {
            var diagnostic = new DiagnosticService(clientManager);
            try {
                await diagnostic.PerformNoInputActions(rootSettings.PrintOption, rootSettings.Output.IndexFilePath, cts.Token);
            } catch (Exception ex) {
                Logger.Fatal($"Diagnostic action failed: {ex.Message}");
            }
            return;
        }

        var jobSettingsResolver = ConfigManager.CreateJobSettingsResolver(configFile, bindArgs, cliSettings);
        var engine = new DownloadEngine(engineSettings, clientManager, jobSettingsResolver);
        var backend = new LocalCliBackend(engine, rootSettings);

        CliProgressReporter? cliReporter = null;
        if (cliSettings.ProgressJson)
            new JsonStreamProgressReporter(Console.Out).Attach(backend);
        else
        {
            cliReporter = new CliProgressReporter(cliSettings);
            cliReporter.Attach(backend);
        }
        backend.EventReceived += envelope =>
        {
            if (envelope.Type == "track-batch.resolved"
                && envelope.Payload is TrackBatchResolvedEventDto batch
                && !batch.PrintOption.HasFlag(PrintOption.Tracks))
            {
                Printing.PrintTracksTbd(
                    batch.Pending.Select(ToSongJob).ToList(),
                    batch.Existing.Select(ToSongJob).ToList(),
                    batch.NotFound.Select(ToSongJob).ToList(),
                    batch.IsNormal,
                    batch.PrintOption);
            }
        };

        if (cliSettings.InteractiveMode)
        {
            var interactiveCoordinator = new InteractiveCliCoordinator(engine, cliSettings, cts.Token, backend);
            interactiveCoordinator.Start(
                new ExtractJob(rootSettings.Extraction.Input, rootSettings.Extraction.InputType),
                rootSettings);
        }
        else
        {
            await backend.SubmitJobAsync(
                new SubmitJobRequestDto(
                    new JobSpecDto
                    {
                        Kind = "extract",
                        Input = rootSettings.Extraction.Input,
                        InputType = rootSettings.Extraction.InputType.ToString(),
                    }),
                cts.Token);
            engine.CompleteEnqueue();
        }

        ConsoleInputManager.Reporter = cliReporter;
        ConsoleInputManager.OnCancelRequested = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Console.WriteLine();
                Printing.Write("Cancel job ID or all jobs? id/[A]ll/n=Esc: ", ConsoleColor.Yellow, force: true);
            }

            var result = ConsoleInputManager.ReadCancelPromptResult();

            if (result.Action == ConsoleInputManager.CancelPromptAction.Abort)
                return;

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelAll)
            {
                Logger.LogNonConsole(Logger.LogLevel.Info, "Cancelling all jobs...");
                Printing.WriteLine("Cancelling all jobs...", ConsoleColor.Gray, force: true);
                engine.Cancel();
                return;
            }

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob && result.JobId is int id)
            {
                if (await backend.CancelJobByDisplayIdAsync(id, cts.Token))
                {
                    Logger.Info($"Cancelling job [{id}]...");
                }
                else
                {
                    Logger.Error($"Job ID [{id}] not found.");
                }
            }
            else
            {
                Logger.Error($"Invalid input '{result.Input}'.");
            }
        };

        _ = Task.Run(() => ConsoleInputManager.RunLoopAsync(cts.Token), cts.Token);

        try
        {
            await engine.RunAsync(cts.Token);
            Printing.PrintComplete(engine.Queue);

            if (rootSettings.DoNotDownload)
                Printing.PrintPlannedOutput(engine.Queue);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            engine.Cancel();
            cts.Cancel();
            Printing.SetBuffering(false);
            Printing.Flush();
        }
    }

    private static async Task RunRemoteAsync(
        string[] args,
        DownloadSettings rootSettings,
        CliSettings cliSettings,
        RemoteSettings remoteSettings,
        CancellationTokenSource cts)
    {
        if (string.IsNullOrWhiteSpace(rootSettings.Extraction.Input))
        {
            Logger.Fatal("Remote mode requires an input.");
            return;
        }

        await using var backend = new RemoteCliBackend(remoteSettings.ServerUrl!);
        await backend.StartAsync(cts.Token);

        CliProgressReporter? cliReporter = null;
        if (cliSettings.ProgressJson)
            new JsonStreamProgressReporter(Console.Out).Attach(backend);
        else
        {
            cliReporter = new CliProgressReporter(cliSettings);
            cliReporter.Attach(backend);
        }

        backend.EventReceived += envelope =>
        {
            if (envelope.Type == "track-batch.resolved"
                && envelope.Payload is TrackBatchResolvedEventDto batch
                && !batch.PrintOption.HasFlag(PrintOption.Tracks))
            {
                Printing.PrintTracksTbd(
                    batch.Pending.Select(ToSongJob).ToList(),
                    batch.Existing.Select(ToSongJob).ToList(),
                    batch.NotFound.Select(ToSongJob).ToList(),
                    batch.IsNormal,
                    batch.PrintOption);
            }
        };

        var request = new SubmitJobRequestDto(
            new JobSpecDto
            {
                Kind = "extract",
                Input = rootSettings.Extraction.Input,
                InputType = rootSettings.Extraction.InputType.ToString(),
            },
            BuildRemoteSubmissionOptions(args, cliSettings));

        RemoteInteractiveCliCoordinator? interactiveCoordinator = null;
        JobSummaryDto submission;
        if (cliSettings.InteractiveMode)
        {
            interactiveCoordinator = new RemoteInteractiveCliCoordinator(backend, cliSettings, cts.Token);
            submission = await interactiveCoordinator.StartAsync(request, cts.Token);
        }
        else
        {
            submission = await backend.SubmitJobAsync(request, cts.Token);
        }

        ConsoleInputManager.Reporter = cliReporter;
        ConsoleInputManager.OnCancelRequested = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Console.WriteLine();
                Printing.Write("Cancel job ID or all jobs? id/[A]ll/n=Esc: ", ConsoleColor.Yellow, force: true);
            }

            var result = ConsoleInputManager.ReadCancelPromptResult();

            if (result.Action == ConsoleInputManager.CancelPromptAction.Abort)
                return;

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelAll)
            {
                Logger.LogNonConsole(Logger.LogLevel.Info, "Cancelling workflow...");
                Printing.WriteLine("Cancelling workflow...", ConsoleColor.Gray, force: true);
                await backend.CancelWorkflowAsync(submission.WorkflowId, cts.Token);
                return;
            }

            if (result.Action == ConsoleInputManager.CancelPromptAction.CancelJob && result.JobId is int id)
            {
                if (await backend.CancelJobByDisplayIdAsync(id, cts.Token))
                    Logger.Info($"Cancelling job [{id}]...");
                else
                    Logger.Error($"Job ID [{id}] not found.");
            }
            else
            {
                Logger.Error($"Invalid input '{result.Input}'.");
            }
        };

        _ = Task.Run(() => ConsoleInputManager.RunLoopAsync(cts.Token), cts.Token);

        try
        {
            if (interactiveCoordinator != null)
                await interactiveCoordinator.RunUntilCompleteAsync(submission.WorkflowId, cts.Token);
            else
                await WaitForRemoteWorkflowAsync(backend, submission.WorkflowId, cts.Token);

            if (!rootSettings.DoNotDownload)
                await PrintRemoteCompleteAsync(backend, submission.WorkflowId, cts.Token);

            if (rootSettings.PrintResults)
                await PrintRemoteResultsAsync(backend, submission.WorkflowId, rootSettings, cts.Token);
            else if (rootSettings.PrintTracks)
                await PrintRemotePlannedOutputAsync(backend, submission.WorkflowId, rootSettings, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            cts.Cancel();
            cliReporter?.Stop();
            Printing.SetBuffering(false);
            Printing.Flush();
        }
    }

    private static async Task WaitForRemoteWorkflowAsync(ICliBackend backend, Guid workflowId, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var workflow = await backend.GetWorkflowAsync(workflowId, ct);
            if (workflow?.Summary.State is "completed" or "failed")
                return;

            await Task.Delay(200, ct);
        }
    }

    internal static async Task PrintRemoteCompleteAsync(
        ICliBackend backend,
        Guid workflowId,
        CancellationToken ct)
    {
        var workflow = await backend.GetWorkflowAsync(workflowId, ct);
        if (workflow == null)
            return;

        int successes = 0;
        int fails = 0;

        foreach (var summary in workflow.Jobs.OrderBy(job => job.DisplayId))
        {
            var detail = await backend.GetJobDetailAsync(summary.JobId, ct);
            switch (detail?.Payload)
            {
                case SongJobPayloadDto song:
                    CountSong(song, summary, ref successes, ref fails);
                    break;

                case AggregateJobPayloadDto aggregate:
                    foreach (var aggregateSong in aggregate.Songs)
                        CountSong(aggregateSong, null, ref successes, ref fails);
                    break;

                case AlbumJobPayloadDto album:
                    foreach (var albumSong in ResolvedAlbumSongs(album))
                        CountSong(albumSong, null, ref successes, ref fails);
                    break;
            }
        }

        Printing.PrintComplete(successes, fails);
    }

    private static void CountSong(SongJobPayloadDto song, JobSummaryDto? summary, ref int successes, ref int fails)
    {
        string? stateText = song.State ?? summary?.State;
        if (!Enum.TryParse<JobState>(stateText, out var state))
            return;

        if (Printing.IsSuccessfulCompletion(state))
            successes++;
        else if (state == JobState.Failed)
            fails++;
    }

    private static IEnumerable<SongJobPayloadDto> ResolvedAlbumSongs(AlbumJobPayloadDto album)
    {
        if (album.Results == null)
            return [];

        AlbumFolderDto? resolvedFolder = null;
        if (!string.IsNullOrWhiteSpace(album.ResolvedFolderUsername)
            && !string.IsNullOrWhiteSpace(album.ResolvedFolderPath))
        {
            resolvedFolder = album.Results.FirstOrDefault(folder =>
                string.Equals(folder.Username, album.ResolvedFolderUsername, StringComparison.Ordinal)
                && string.Equals(folder.FolderPath, album.ResolvedFolderPath, StringComparison.Ordinal));
        }

        resolvedFolder ??= album.Results.Count == 1 ? album.Results[0] : null;
        return resolvedFolder?.Files?.Where(song => Utils.IsMusicFile(song.ResolvedFilename ?? "")) ?? [];
    }

    internal static async Task PrintRemoteResultsAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        CancellationToken ct)
    {
        var workflow = await backend.GetWorkflowAsync(workflowId, ct);
        if (workflow == null)
            return;

        bool nonVerbose = (settings.PrintOption & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;
        bool printedAny = false;

        foreach (var summary in workflow.Jobs.OrderBy(job => job.DisplayId))
        {
            var detail = await backend.GetJobDetailAsync(summary.JobId, ct);
            if (detail?.Payload == null)
                continue;

            Job? job = detail.Payload switch
            {
                SongJobPayloadDto song when song.CandidateCount.GetValueOrDefault() > 0
                    => ToSongJob(song),
                AlbumJobPayloadDto album when album.ResultCount > 0
                    => ToAlbumJob(album),
                AggregateJobPayloadDto aggregate when aggregate.Songs.Count > 0
                    => ToAggregateJob(aggregate),
                _ => null,
            };

            if (job == null)
                continue;

            if (printedAny && !nonVerbose)
                Console.WriteLine();

            Printing.PrintResults(job, settings.PrintOption, settings.Search);
            printedAny = true;
        }
    }

    internal static async Task PrintRemotePlannedOutputAsync(
        ICliBackend backend,
        Guid workflowId,
        DownloadSettings settings,
        CancellationToken ct)
    {
        var workflow = await backend.GetWorkflowAsync(workflowId, ct);
        if (workflow == null)
            return;

        var details = new Dictionary<Guid, JobDetailDto>();
        foreach (var summary in workflow.Jobs)
        {
            var detail = await backend.GetJobDetailAsync(summary.JobId, ct);
            if (detail != null)
                details[summary.JobId] = detail;
        }

        var roots = details.Values
            .Where(detail => detail.Summary.ParentJobId == null)
            .OrderBy(detail => detail.Summary.DisplayId)
            .ToList();

        var visited = new HashSet<Guid>();
        var plannedJobs = new List<Job>();
        foreach (var root in roots)
            CollectRemotePlannedDownloads(root, details, plannedJobs, visited);

        if (plannedJobs.Count > 0 && settings.PrintTracks)
            Printing.PrintPlannedDownloads(plannedJobs, settings);
    }

    private static void CollectRemotePlannedDownloads(
        JobDetailDto detail,
        IReadOnlyDictionary<Guid, JobDetailDto> details,
        List<Job> plannedJobs,
        HashSet<Guid> visited)
    {
        if (!visited.Add(detail.Summary.JobId))
            return;

        if (detail.Payload is ExtractJobPayloadDto extract
            && extract.ResultJobId is Guid resultJobId
            && details.TryGetValue(resultJobId, out var resultDetail))
        {
            CollectRemotePlannedDownloads(resultDetail, details, plannedJobs, visited);
            return;
        }

        switch (detail.Payload)
        {
            case SongJobPayloadDto song:
                plannedJobs.Add(ToSongJob(song));
                break;

            case AlbumJobPayloadDto album:
                plannedJobs.Add(ToAlbumJob(album, detail.Summary));
                break;

            case AggregateJobPayloadDto aggregate:
                plannedJobs.Add(ToAggregateJob(aggregate));
                break;

            case AlbumAggregateJobPayloadDto albumAggregate:
                plannedJobs.Add(ToAlbumAggregateJob(albumAggregate, detail.Summary));
                break;

            case JobListPayloadDto jobList:
                var directSongIds = new HashSet<Guid>();
                if (jobList.DirectSongs != null)
                {
                    foreach (var song in jobList.DirectSongs)
                    {
                        plannedJobs.Add(ToSongJob(song));
                        if (song.JobId is Guid jobId)
                            directSongIds.Add(jobId);
                    }
                }

                var children = details.Values
                    .Where(candidate => candidate.Summary.ParentJobId == detail.Summary.JobId)
                    .OrderBy(candidate => candidate.Summary.DisplayId)
                    .ToList();

                foreach (var child in children)
                {
                    if (directSongIds.Contains(child.Summary.JobId))
                        continue;

                    CollectRemotePlannedDownloads(child, details, plannedJobs, visited);
                }
                break;
        }
    }

    internal static SubmissionOptionsDto BuildRemoteSubmissionOptions(
        string[] args,
        CliSettings cliSettings)
        => new(
            ProfileNames: SplitProfileNames(ConfigManager.ExtractProfileName(args)),
            ProfileContext: new Dictionary<string, bool>
            {
                ["interactive"] = cliSettings.InteractiveMode,
                ["progress-json"] = cliSettings.ProgressJson,
                ["no-progress"] = cliSettings.NoProgress,
            },
            DownloadSettings: ConfigManager.CreateCliDownloadSettingsDelta(args));

    private static IReadOnlyList<string>? SplitProfileNames(string? names)
        => string.IsNullOrWhiteSpace(names)
            ? null
            : names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static async Task RunDaemonAsync(
        string[] args,
        ConfigFile configFile,
        EngineSettings engineSettings,
        DownloadSettings rootSettings,
        DaemonSettings daemonSettings)
    {
        var url = $"http://{daemonSettings.ListenIp}:{daemonSettings.ListenPort}";
        var options = new ServerOptions
        {
            Engine = SettingsCloner.Clone(engineSettings),
            DefaultDownload = SettingsCloner.Clone(rootSettings),
            Profiles = ConfigManager.CreateProfileCatalog(configFile),
        };

        Logger.Info($"Starting sldl daemon on {url}");
        Logger.Info("Press Ctrl+C to stop.");

        var app = ServerHost.Build(args, options, url);
        await app.RunAsync();
    }

    private static SongJob ToSongJob(SongJobPayloadDto song)
        => ToSongJob(song, null);

    private static SongJob ToSongJob(SongJobPayloadDto song, JobSummaryDto? summary)
    {
        var job = new SongJob(new SongQuery
        {
            Artist = song.Query.Artist,
            Title = song.Query.Title,
            Album = song.Query.Album,
            URI = song.Query.Uri,
            Length = song.Query.Length,
            ArtistMaybeWrong = song.Query.ArtistMaybeWrong,
            IsDirectLink = song.Query.IsDirectLink,
        })
        {
            DownloadPath = song.DownloadPath,
            Candidates = song.Candidates?.Select(ToFileCandidate).ToList(),
        };

        ApplyJobOutcome(job, song.State, song.FailureReason, song.FailureMessage);

        if (summary != null)
        {
            ApplyJobOutcome(job, summary.State, summary.FailureReason, summary.FailureMessage);
        }

        if (!string.IsNullOrWhiteSpace(song.ResolvedUsername)
            && !string.IsNullOrWhiteSpace(song.ResolvedFilename))
        {
            job.ResolvedTarget = ToFileCandidate(new FileCandidateDto(
                new FileCandidateRefDto(song.ResolvedUsername, song.ResolvedFilename),
                song.ResolvedUsername,
                song.ResolvedFilename,
                song.ResolvedSize ?? 0,
                null,
                null,
                song.ResolvedHasFreeUploadSlot,
                song.ResolvedUploadSpeed,
                song.ResolvedExtension,
                song.ResolvedAttributes));
        }

        return job;
    }

    private static AlbumJob ToAlbumJob(AlbumJobPayloadDto album)
        => ToAlbumJob(album, null);

    private static AlbumJob ToAlbumJob(AlbumJobPayloadDto album, JobSummaryDto? summary)
    {
        var job = new AlbumJob(ToAlbumQuery(album.Query))
        {
            Results = album.Results?.Select(ToAlbumFolder).ToList() ?? [],
            DownloadPath = album.DownloadPath,
        };

        if (summary != null)
            ApplyJobOutcome(job, summary.State, summary.FailureReason, summary.FailureMessage);

        return job;
    }

    private static AggregateJob ToAggregateJob(AggregateJobPayloadDto aggregate)
        => new(ToSongQuery(aggregate.Query))
        {
            Songs = aggregate.Songs.Select(ToSongJob).ToList(),
        };

    private static AlbumAggregateJob ToAlbumAggregateJob(AlbumAggregateJobPayloadDto albumAggregate, JobSummaryDto? summary = null)
    {
        var job = new AlbumAggregateJob(ToAlbumQuery(albumAggregate.Query));
        if (summary != null)
            ApplyJobOutcome(job, summary.State, summary.FailureReason, summary.FailureMessage);
        return job;
    }

    private static void ApplyJobOutcome(Job job, string? state, string? failureReason, string? failureMessage)
    {
        if (!string.IsNullOrWhiteSpace(state) && Enum.TryParse<JobState>(state, out var parsedState))
            job.State = parsedState;
        if (!string.IsNullOrWhiteSpace(failureReason) && Enum.TryParse<FailureReason>(failureReason, out var parsedFailureReason))
            job.FailureReason = parsedFailureReason;
        job.FailureMessage = failureMessage;
    }

    private static AlbumFolder ToAlbumFolder(AlbumFolderDto folder)
        => new(
            folder.Username,
            folder.FolderPath,
            folder.Files?.Select(ToSongJob).ToList() ?? []);

    private static SongQuery ToSongQuery(SongQueryDto query)
        => new()
        {
            Artist = query.Artist,
            Title = query.Title,
            Album = query.Album,
            URI = query.Uri,
            Length = query.Length,
            ArtistMaybeWrong = query.ArtistMaybeWrong,
            IsDirectLink = query.IsDirectLink,
        };

    private static AlbumQuery ToAlbumQuery(AlbumQueryDto query)
        => new()
        {
            Artist = query.Artist,
            Album = query.Album,
            SearchHint = query.SearchHint,
            URI = query.Uri,
            ArtistMaybeWrong = query.ArtistMaybeWrong,
            IsDirectLink = query.IsDirectLink,
            MinTrackCount = query.MinTrackCount,
            MaxTrackCount = query.MaxTrackCount,
        };

    private static FileCandidate ToFileCandidate(FileCandidateDto candidate)
        => new(
            new SearchResponse(
                candidate.Username,
                -1,
                candidate.HasFreeUploadSlot ?? false,
                candidate.UploadSpeed ?? -1,
                -1,
                null),
            new Soulseek.File(
                0,
                candidate.Filename,
                candidate.Size,
                candidate.Extension ?? Path.GetExtension(candidate.Filename),
                candidate.Attributes?.Select(x => new Soulseek.FileAttribute(Enum.Parse<Soulseek.FileAttributeType>(x.Type), x.Value))));
}

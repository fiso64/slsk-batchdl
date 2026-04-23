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

        Logger.SetupExceptionHandling();
        Logger.AddConsole(writer: (msg, color) => Printing.WriteLine(msg, color));

        string configPath = ConfigManager.ExtractConfigPath(args);
        var configFile = ConfigManager.Load(configPath);
        var (engineSettings, rootSettings, cliSettings) = ConfigManager.Bind(configFile, args);
        ConfigManager.ApplyAutoProfileCliSettings(configFile, rootSettings, cliSettings);

        if (!string.IsNullOrWhiteSpace(engineSettings.LogFilePath))
            Logger.AddOrReplaceFile(engineSettings.LogFilePath);

        Logger.SetConsoleLogLevel(rootSettings.NonVerbosePrint ? Logger.LogLevel.Error : engineSettings.LogLevel);
        
        var cts = new CancellationTokenSource();
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

        var jobSettingsResolver = ConfigManager.CreateJobSettingsResolver(configFile, args, cliSettings);
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
            if (envelope.Type == "track-batch.resolved" && envelope.Payload is TrackBatchResolvedEventDto batch)
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

    private static SongJob ToSongJob(SongJobPayloadDto song)
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
        };

        if (!string.IsNullOrWhiteSpace(song.ResolvedUsername)
            && !string.IsNullOrWhiteSpace(song.ResolvedFilename))
        {
            job.ResolvedTarget = new FileCandidate(
                new SearchResponse(
                    song.ResolvedUsername,
                    -1,
                    song.ResolvedHasFreeUploadSlot ?? false,
                    song.ResolvedUploadSpeed ?? -1,
                    -1,
                    null),
                new Soulseek.File(
                    0,
                    song.ResolvedFilename,
                    song.ResolvedSize ?? 0,
                    song.ResolvedExtension ?? Path.GetExtension(song.ResolvedFilename),
                    song.ResolvedAttributes?.Select(x => new Soulseek.FileAttribute(Enum.Parse<Soulseek.FileAttributeType>(x.Type), x.Value))));
        }

        return job;
    }
}

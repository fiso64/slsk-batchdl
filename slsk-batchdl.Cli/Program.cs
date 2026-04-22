using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Sldl.Server;

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
            new JsonStreamProgressReporter(Console.Out).Attach(engine.Events);
        else
        {
            cliReporter = new CliProgressReporter(cliSettings);
            cliReporter.Attach(engine.Events);
        }
        engine.Events.EngineCompleted += queue => Printing.PrintComplete(queue);
        engine.Events.TrackBatchResolved += (job, pending, existing, notFound) =>
            Printing.PrintTracksTbd(
                pending.ToList(),
                existing.ToList(),
                notFound.ToList(),
                job is JobList,
                job.Config.PrintOption);

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
                Logger.Info("Cancelling all jobs...");
                engine.Cancel();
                cts.Cancel();
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

}

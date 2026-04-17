using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Services;
using Sldl.Core.Settings;

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

        var engine = new DownloadEngine(engineSettings, clientManager);

        CliProgressReporter? cliReporter = null;
        if (cliSettings.ProgressJson)
            new JsonStreamProgressReporter(Console.Out).Attach(engine.Events);
        else
        {
            cliReporter = new CliProgressReporter(cliSettings);
            cliReporter.Attach(engine.Events);
        }

        engine.DisplayTracksTbd      = (pending, existing, notFound, isNormal, printOption) =>
            Printing.PrintTracksTbd(pending, existing, notFound, isNormal, printOption);
        engine.DisplayResultsCallback = async (job, existing, notFound, printOption, search, searcher) =>
            await Printing.PrintResults(job, existing, notFound, printOption, search, searcher);
        engine.DisplayComplete        = queue => Printing.PrintComplete(queue);

        engine.Enqueue(new ExtractJob(rootSettings.Extraction.Input, rootSettings.Extraction.InputType), rootSettings);
        engine.CompleteEnqueue();

        if (cliSettings.InteractiveMode)
        {
            string? filterStr = null;
            engine.SelectAlbumVersion = async (job) =>
            {
                var retrievedFolders = new HashSet<string>();
                var interactive = new InteractiveModeManager(
                    job, engine.Queue, job.Results,
                    canRetrieve: true,
                    retrievedFolders: retrievedFolders,
                    retrieveFolderCallback: async (f) => await engine.ProcessFolderRetrieval(f, job),
                    filterStr: filterStr);

                var result = await interactive.Run();
                filterStr = result.FilterStr;

                if (result.Index == -1 || result.Index == -2 || result.Folder == null)
                    return null;

                if (result.ExitInteractiveMode)
                    cliSettings.InteractiveMode = false;

                return result.Folder;
            };
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
                var jobToCancel = engine.GetJob(id);
                if (jobToCancel != null)
                {
                    Logger.Info($"Cancelling job [{id}]...");
                    jobToCancel.Cancel();
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

using Utilities;
using Services;

internal static partial class Program
{
    public static async Task Main(string[] args)
    {
        Console.ResetColor();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (Help.PrintAndExitIfNeeded(args))
            return;

        Logger.SetupExceptionHandling();
        Logger.AddConsole();

        var config = new Config(args);
        Logger.SetConsoleLogLevel(config.GetConsoleLogLevel());
        
        var cts = new CancellationTokenSource();
        var clientManager = soulseekClientManager(config);

        if (!config.RequiresInput)
        {
            var diagnostic = new DiagnosticService(clientManager);
            try {
                await diagnostic.PerformNoInputActions(config, cts.Token);
            } catch (Exception ex) {
                Logger.Fatal($"Diagnostic action failed: {ex.Message}");
            }
            return;
        }

        IProgressReporter reporter;
        if (config.progressJson)
            reporter = new JsonStreamProgressReporter(Console.Out);
        else
            reporter = new CliProgressReporter(config);

        var engine = new DownloadEngine(config, clientManager, progressReporter: reporter);

        if (config.interactiveMode)
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
                    config.interactiveMode = false;

                return result.Folder;
            };
        }

        ConsoleInputManager.Reporter = reporter as CliProgressReporter;
        ConsoleInputManager.OnCancelRequested = async () =>
        {
            lock (Printing.ConsoleLock)
            {
                Console.WriteLine();
                Printing.Write("Cancel Job ID (or 'all', Enter to abort): ", ConsoleColor.Yellow, force: true);
            }

            string? input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input)) return;

            if (input.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                cts.Cancel();
                return;
            }

            if (int.TryParse(input, out int id))
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
                Logger.Error($"Invalid input '{input}'.");
            }
        };

        _ = Task.Run(() => ConsoleInputManager.RunLoopAsync(cts.Token), cts.Token);

        try
        {
            await engine.RunAsync(cts.Token);
        }
        finally
        {
            Printing.SetBuffering(false);
            Printing.Flush();
        }
    }

    private static SoulseekClientManager soulseekClientManager(Config config)
    {
        return new SoulseekClientManager(config);
    }
}
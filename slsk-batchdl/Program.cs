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
        
        if (reporter is CliProgressReporter cli)
            cli.OnKeyPressed = key => engine.OnKeyPressed(key);

        await engine.RunAsync(cts.Token);
    }

    private static SoulseekClientManager soulseekClientManager(Config config)
    {
        return new SoulseekClientManager(config);
    }
}
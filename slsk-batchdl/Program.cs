using Utilities;

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

        IProgressReporter reporter;
        DownloadEngine app;

        if (config.progressJson)
        {
            reporter = new JsonStreamProgressReporter(Console.Out);
            app = new DownloadEngine(config, progressReporter: reporter);
        }
        else
        {
            var cliReporter = new CliProgressReporter(config);
            app = new DownloadEngine(config, progressReporter: cliReporter);
            cliReporter.OnKeyPressed = key => app.OnKeyPressed(key);
        }

        await app.RunAsync();
    }
}
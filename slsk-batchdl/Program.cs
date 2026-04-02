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

        IProgressReporter? reporter = null;
        if (config.progressJson)
        {
            reporter = new JsonStreamProgressReporter(Console.Out);
        }

        var app = new DownloaderApplication(config, progressReporter: reporter);
        await app.RunAsync();
    }
}
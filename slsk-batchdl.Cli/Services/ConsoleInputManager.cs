using System.Threading.Channels;

namespace Sldl.Cli;

public static class ConsoleInputManager
{
    private static readonly Channel<ConsoleKeyInfo> _keyChannel = Channel.CreateUnbounded<ConsoleKeyInfo>();

    public static bool GlobalCancelEnabled { get; set; } = true;
    public static Func<Task>? OnCancelRequested { get; set; }
    public static CliProgressReporter? Reporter { get; set; }

    public static async Task RunLoopAsync(CancellationToken ct)
    {
        if (Console.IsInputRedirected)
            return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);

                    if (GlobalCancelEnabled && key.KeyChar == 'c')
                    {
                        if (OnCancelRequested != null)
                        {
                            if (Reporter != null) Reporter.IsPaused = true;
                            Printing.SetBuffering(true);
                            await OnCancelRequested();
                            Printing.SetBuffering(false);
                            Printing.Flush();
                            if (Reporter != null) Reporter.IsPaused = false;
                        }
                    }
                    else
                    {
                        _keyChannel.Writer.TryWrite(key);
                    }
                }
                else
                {
                    await Task.Delay(50, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public static async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct = default)
    {
        return await _keyChannel.Reader.ReadAsync(ct);
    }
}

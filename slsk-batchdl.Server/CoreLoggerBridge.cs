using Microsoft.Extensions.Logging;
using CoreLogger = Sldl.Core.Logger;

namespace Sldl.Server;

public static class CoreLoggerBridge
{
    public static void Configure(IServiceProvider services, CoreLogger.LogLevel minimumLevel)
    {
        var logger = services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Sldl.Core");

        CoreLogger.RemoveNonFileOutputs();
        CoreLogger.AddSink(
            (level, message) => logger.Log(ToMicrosoftLevel(level), "{Message}", message),
            minimumLevel,
            prependDate: false,
            prependLogLevel: false);
    }

    private static LogLevel ToMicrosoftLevel(CoreLogger.LogLevel level)
        => level switch
        {
            CoreLogger.LogLevel.Trace => LogLevel.Trace,
            CoreLogger.LogLevel.Debug => LogLevel.Debug,
            CoreLogger.LogLevel.DebugError => LogLevel.Warning,
            CoreLogger.LogLevel.Info => LogLevel.Information,
            CoreLogger.LogLevel.Warn => LogLevel.Warning,
            CoreLogger.LogLevel.Error => LogLevel.Error,
            CoreLogger.LogLevel.Fatal => LogLevel.Critical,
            _ => LogLevel.Information,
        };
}

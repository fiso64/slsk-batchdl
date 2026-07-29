using Microsoft.Extensions.Logging;
using Sockseek.Core;

namespace Sockseek.Server;

public static class CoreLoggerBridge
{
    public static void Configure(LogLevel configuredMinimumLevel)
    {
        // TODO [LOGGING]: Revisit severity assignments and output defaults together.
        // Once Information logs describe daemon work adequately, consider restoring
        // both daemon stdout and the default --log-file threshold to Information.
        var minimumLevel = configuredMinimumLevel < LogLevel.Debug
            ? configuredMinimumLevel
            : LogLevel.Debug;

        SockseekLog.RemoveNonFileOutputs();
        SockseekLog.AddSink(
            (_, message) => Console.WriteLine(message),
            minimumLevel,
            prependDate: true,
            prependLogLevel: true);
    }

    public static void Configure(IServiceProvider _, LogLevel configuredMinimumLevel)
        => Configure(configuredMinimumLevel);
}

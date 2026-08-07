using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using Sockseek.Core;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
[DoNotParallelize]
public class CoreLoggerBridgeTests
{
    [TestCleanup]
    public void Cleanup()
    {
        SockseekLog.RemoveNonFileOutputs();
        SockseekLog.RemoveFileOutputs();
    }

    [TestMethod]
    public void Configure_DefaultInformationLevel_RoutesDebugDaemonLogsToTimestampedStdout()
    {
        var originalOut = Console.Out;
        using var output = new StringWriter();

        try
        {
            Console.SetOut(output);
            CoreLoggerBridge.Configure(LogLevel.Information);

            SockseekLog.Debug("download started", categoryName: SockseekLog.Categories.Daemon);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var line = output.ToString().Trim();
        StringAssert.Contains(line, "[debug] [daemon] download started");
        StringAssert.Matches(line, new System.Text.RegularExpressions.Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} "));
    }

    [TestMethod]
    public async Task ServerHostBuild_DoesNotReplaceProcessLogRouting()
    {
        var messages = new List<string>();
        SockseekLog.AddSink((_, message) => messages.Add(message), LogLevel.Debug);

        await using var app = ServerHost.Build([], new ServerOptions());
        SockseekLog.Debug("existing sink remains active");

        Assert.IsTrue(messages.Any(message => message.Contains("existing sink remains active")));
    }
}

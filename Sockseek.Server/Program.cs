using Sockseek.Api;
using Sockseek.Server;
using Microsoft.Extensions.Options;

Sockseek.Core.SockseekLog.SetupExceptionHandling();
Sockseek.Core.SockseekLog.AddConsole();

try
{
    var app = ServerHost.Build(args);
    var options = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
    CoreLoggerBridge.Configure(options.Engine.LogLevel);
    app.Run();
}
catch (Exception ex)
{
    Sockseek.Core.SockseekLog.Fatal(ex, "Unhandled server error");
}

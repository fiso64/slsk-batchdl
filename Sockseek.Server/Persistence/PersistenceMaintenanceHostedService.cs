using Microsoft.Extensions.Options;
using Sockseek.Core;

namespace Sockseek.Server.Persistence;

public sealed class PersistenceMaintenanceHostedService(
    PersistenceCoordinator coordinator,
    IOptions<ServerOptions> serverOptions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = serverOptions.Value.Persistence;
        if (!coordinator.IsEnabled || !options.RetentionEnabled)
            return;
        if (options.RetentionInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Persistence retention interval must be positive.");

        using var timer = new PeriodicTimer(options.RetentionInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var result = await coordinator.RunRetentionAsync(stoppingToken).ConfigureAwait(false);
                    SockseekLog.Daemon.Info(
                        $"Persistence retention pruned {result.PrunedJobs} jobs, {result.PrunedSearchResults} raw search results, and {result.PrunedChatMessages} chat messages in {result.DurationMilliseconds} ms.");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    SockseekLog.Daemon.Error(ex, "Scheduled persistence retention failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // PeriodicTimer cancellation is the expected hosted-service stop
            // path, not a background-service failure.
        }
    }
}

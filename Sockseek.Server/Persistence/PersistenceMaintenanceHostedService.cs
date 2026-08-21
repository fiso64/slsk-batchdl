using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Sockseek.Server.Persistence;

public sealed class PersistenceMaintenanceHostedService(
    PersistenceCoordinator coordinator,
    IOptions<ServerOptions> serverOptions,
    ILogger<PersistenceMaintenanceHostedService> logger) : BackgroundService
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
                    ServerLogMessages.PersistenceRetentionCompleted(
                        logger,
                        result.PrunedJobs,
                        result.PrunedSearchResults,
                        result.PrunedChatMessages,
                        result.DurationMilliseconds);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    ServerLogMessages.PersistenceRetentionFailed(logger, ex);
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

using Bashkiria.Events.Interaction.Configuration;
using Microsoft.Extensions.Options;

namespace Bashkiria.Events.Interaction.Services;

public sealed class NotificationWorker(NotificationService notifications, IOptions<InteractionOptions> options,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await notifications.CleanupAsync(stoppingToken);
                await notifications.GenerateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Deliberately omit exception messages and database parameters: they can contain user input.
                logger.LogError("Interaction worker failed: {ErrorType}", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), stoppingToken);
        }
    }
}

using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class NotificationWorker(NotificationRepository repository, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await repository.ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Notification worker failed while processing queued mail.");
            }

            try
            {
                await repository.SyncIncomingMailAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "Incoming mail sync failed; it will retry without advancing the failed message cursor."); }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }
}

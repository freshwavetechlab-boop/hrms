using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class PayrollRunWorker(PayRunRepository repository, ILogger<PayrollRunWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await repository.ProcessQueuedAsync(1);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Payroll run worker failed while processing queued payroll.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

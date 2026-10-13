using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class PayrollAdvisorWorker(PayrollAdvisorRepository repository, ILogger<PayrollAdvisorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = false;
            try { processed = await repository.ProcessNextAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Payroll advisor queue unavailable."); }
            try { await Task.Delay(processed ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class RecruitmentEmployeeSalarySyncWorker(IServiceScopeFactory scopes, ILogger<RecruitmentEmployeeSalarySyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cursor = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                cursor = await scope.ServiceProvider.GetRequiredService<RecruitmentDocumentRepository>().SyncEmptyEmployeeSalariesAsync(cursor, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning(error, "Automatic recruitment salary synchronization will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

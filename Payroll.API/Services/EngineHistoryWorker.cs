namespace Payroll.API.Services;

// Separate from business workers: pausing payroll/ATS must not pause monitoring.
public sealed class EngineHistoryWorker(EngineHistoryCollector collector, EngineHistoryStore store,
    ILogger<EngineHistoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!store.Enabled) return;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await FlushAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    private async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var rows = collector.Checkpoint();
            await store.SaveAsync(rows, timeout.Token);
            collector.Acknowledge(rows);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            store.SaveUnavailable = true;
            logger.LogWarning("Engine history checkpoint unavailable; numeric metrics will retry. Business engines are unaffected.");
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (store.Enabled && !cancellationToken.IsCancellationRequested) await FlushAsync(cancellationToken);
    }
}

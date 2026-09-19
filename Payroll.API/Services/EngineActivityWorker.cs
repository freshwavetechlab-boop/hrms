namespace Payroll.API.Services;

public sealed class EngineActivityWorker(EngineActivityBuffer buffer,EngineActivityStore store,ILogger<EngineActivityWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!store.Enabled) return;
        try
        {
            while(!stoppingToken.IsCancellationRequested)
            {
                await Flush(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken);
            }
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
    }
    private async Task Flush(CancellationToken ct)
    {
        try
        {
            var rows=buffer.Checkpoint();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await store.SaveAsync(rows,timeout.Token);buffer.Acknowledge(rows);
        }
        catch(Exception) when(!ct.IsCancellationRequested)
        {
            if(!store.SaveUnavailable) logger.LogWarning("Engine activity saving unavailable; bounded metadata will retry. Business work is unaffected.");
            store.SaveUnavailable=true;
        }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        if(store.Enabled && !ct.IsCancellationRequested) await Flush(ct);
    }
}

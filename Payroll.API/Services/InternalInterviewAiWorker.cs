using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class InternalInterviewAiWorker(IConfiguration configuration, InternalInterviewRepository repository,
    InternalInterviewAiService ai, ILogger<InternalInterviewAiWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!configuration.GetValue("InternalInterviews:Enabled", false)) continue;
            try
            {
                foreach (var id in await repository.PendingAiAsync())
                {
                    try { await repository.ProcessAiAsync(id, ai, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception error) { logger.LogWarning("Internal interview {InterviewId} AI work failed ({ErrorType}); no hiring result changed.", id, error.GetType().Name); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Internal interview AI queue unavailable ({ErrorType}).", error.GetType().Name); }
        }
    }
}

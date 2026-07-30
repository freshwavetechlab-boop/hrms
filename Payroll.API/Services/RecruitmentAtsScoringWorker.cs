using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class RecruitmentAtsScoringWorker(
    RecruitmentTalentRepository repository,
    ILogger<RecruitmentAtsScoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = false;
            var failed = false;
            try
            {
                processed = await repository.ProcessNextAtsScoringJobAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                failed = true;
                logger.LogError(exception, "Recruitment ATS worker failed while processing a queued score.");
            }

            try
            {
                await Task.Delay(failed ? TimeSpan.FromSeconds(10) : processed ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

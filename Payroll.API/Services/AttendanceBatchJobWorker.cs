using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class AttendanceBatchJobWorker(
    LeaveAttendanceRepository repository,
    ILogger<AttendanceBatchJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = false;
            var failed = false;
            try
            {
                processed = await repository.ProcessNextDailyAttendanceBatchJobAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                failed = true;
                logger.LogError(exception, "Attendance batch worker failed while processing a queued job.");
            }

            try
            {
                await Task.Delay(failed ? TimeSpan.FromSeconds(15) : processed ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

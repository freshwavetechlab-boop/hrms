using System.Globalization;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class AttendanceApiAdapterWorker(AttendanceIntegrationRepository repository,
    AttendanceApiAdapterService service, ILogger<AttendanceApiAdapterWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var clientId in await repository.AdapterClientIdsAsync())
                foreach (var row in await repository.GetAdaptersAsync(clientId))
                {
                    var c = row.Configuration;
                    if (!c.IsActive || c.SyncIntervalMinutes == 0 || c.LastAttemptAt?.AddMinutes(c.SyncIntervalMinutes) > DateTime.UtcNow) continue;
                    try
                    {
                        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(c.TimeZoneId)).Date;
                        var dates = new RunAttendanceApiAdapter(today.AddDays(1 - c.LookbackDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        var run = await service.RunAsync(clientId, c.Id, dates, false, stoppingToken, scheduled: true);
                        if (!run.Success) logger.LogWarning("Attendance adapter {AdapterId}, client {ClientId}: {Message}", c.Id, clientId, run.Message);
                    }
                    catch (InvalidOperationException) { /* A manual run/edit may already hold the adapter lock. */ }
                    catch (Exception e) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning("Attendance adapter {AdapterId} could not run ({ErrorType}).", c.Id, e.GetType().Name); }
                }
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning("Attendance adapter schedule unavailable ({ErrorType}).", e.GetType().Name); }
        }
    }
}

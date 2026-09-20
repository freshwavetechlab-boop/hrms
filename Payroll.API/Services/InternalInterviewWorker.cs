using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class InternalInterviewWorker(IConfiguration configuration, InternalInterviewRepository repository,
    InternalInterviewTransport transport, InternalInterviewMediaStore media, AuthRepository auth, ILogger<InternalInterviewWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!InternalInterviewRuntimeState.Read(configuration).Maintenance) continue;
            try
            {
                await Parallel.ForEachAsync(await repository.PendingLifecycleAsync(), new ParallelOptions
                { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken }, async (id, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await repository.ReconcileMediaAsync(id, transport, media, ct);
                        await repository.ReconcileParticipantsAsync(id, transport, auth.GetUserByIdAsync, ct);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception error) { logger.LogWarning("Internal interview {InterviewId} lifecycle retry failed ({ErrorType}); no evidence assumed saved.", id, error.GetType().Name); }
                });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Internal interview maintenance unavailable ({ErrorType}); check feature migration and private services.", error.GetType().Name); }
        }
    }
}

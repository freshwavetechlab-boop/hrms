using Payroll.API.Repositories;

namespace Payroll.API.Services;

public sealed class RecruitmentAtsScoringWorker(
    RecruitmentTalentRepository repository,
    RecruitmentPipelineRepository pipelines,
    RecruitmentPipelineActionService actions,
    RecruitmentCandidateActionRepository candidateActions,
    RecruitmentCaseRepository hiringCases,
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
                var work = await repository.ProcessNextAtsScoringJobAsync(stoppingToken);
                processed = work.Processed;
                if (work.ApplicationId.HasValue && work.User is not null)
                {
                    var (transition, _) = await pipelines.EvaluateAtsStageAutomationAsync(work.ApplicationId.Value, work.User);
                    if (transition?.Status == "Applied")
                    {
                        await actions.ExecuteAsync(work.ApplicationId.Value, "OnExit", work.User);
                        var entry = await actions.ExecuteAsync(work.ApplicationId.Value, "OnEntry", work.User);
                        if (!entry.Executions.Any(item => item.ActionCode == "GENERATE_ACTION_LINK"))
                            await candidateActions.EnsureForCurrentStageAsync(work.ApplicationId.Value, work.User);
                        await hiringCases.AdvanceHiringCaseForCandidateMilestoneAsync(work.ApplicationId.Value, "ProfilesSelected", work.User);
                    }
                }
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

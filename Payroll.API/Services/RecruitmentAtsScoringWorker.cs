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
                var resumeWork = await repository.ProcessNextPendingPublicResumeAsync(stoppingToken);
                processed = resumeWork.Processed;
                if (!string.IsNullOrWhiteSpace(resumeWork.Warning))
                    logger.LogWarning("Public resume processing for application {ApplicationId}: {Warning}", resumeWork.ApplicationId, resumeWork.Warning);

                var work = await repository.ProcessNextAtsScoringJobAsync(stoppingToken);
                processed = processed || work.Processed;
                if (work.ApplicationId.HasValue && work.User is not null
                    && await actions.PrepareCurrentAtsEntryAsync(work.ApplicationId.Value, work.User))
                {
                    var (transition, automationError) = await pipelines.EvaluateAtsStageAutomationAsync(work.ApplicationId.Value, work.User, work.HumanConfirmed);
                    if (!string.IsNullOrWhiteSpace(automationError))
                        logger.LogWarning("ATS automation for application {ApplicationId} stopped: {Error}", work.ApplicationId.Value, automationError);
                    if (transition?.Status == "Applied")
                    {
                        await actions.ExecuteAsync(work.ApplicationId.Value, "OnExit", work.User);
                        var entry = await actions.ExecuteAsync(work.ApplicationId.Value, "OnEntry", work.User);
                        if (!entry.Executions.Any(item => item.ActionCode == "GENERATE_ACTION_LINK"))
                            await candidateActions.EnsureForCurrentStageAsync(work.ApplicationId.Value, work.User);
                        var hiringError = await hiringCases.AdvanceHiringCaseForCandidateMilestoneAsync(work.ApplicationId.Value, "ProfilesSelected", work.User);
                        if (!string.IsNullOrWhiteSpace(hiringError))
                            logger.LogWarning("Hiring-case automation for application {ApplicationId} stopped: {Error}", work.ApplicationId.Value, hiringError);
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

using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class RecruitmentTalentRepository
{
    private async Task<string> ResumePreviewKeyAsync(IFormFile file, AuthUser user, int clientId,
        long positionId, long? postingId, bool talentPoolOnly, ResumeIntakeParsingContext context, CancellationToken ct)
    {
        if (previewCache is null || !context.Policy.ParsingEnabled) return "";
        try
        {
            var providers = "";
            var references = "";
            if (context.Policy.AiEnabled)
            {
                // Independent read-only probes use separate connections. Await both
                // before proceeding so neither is abandoned on a cache miss/error.
                var providerTask = aiScoring.ResumeConfigurationFingerprintAsync(clientId, ct);
                var referenceTask = resumeParser.ReviewDataFingerprintAsync(clientId, ct);
                await Task.WhenAll(providerTask, referenceTask);
                providers = providerTask.Result;
                references = referenceTask.Result;
            }
            return await ResumePreviewCache.KeyAsync(file, new
            {
                user.Id, ActorClientId = user.ClientId, ClientId = clientId, PositionId = positionId,
                PostingId = postingId, TalentPoolOnly = talentPoolOnly, context.Policy,
                context.JobContext, Providers = providers, ParserVersion = ResumeParsingService.ParserVersion,
                References = references
            }, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A cache/configuration probe must not turn a previously supported upload into a failure.
            logger.LogWarning("Resume preview reuse unavailable; normal parsing and AI review will run.");
            return "";
        }
    }
}

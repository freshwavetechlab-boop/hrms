using Payroll.API.Models;

namespace Payroll.API.Services;

public interface IResumeDocumentSuggestionService
{
    Task<RecruitmentAiResumeDocumentSuggestion> SuggestResumeDocumentAsync(int clientId, string sourceText,
        byte[]? sourceDocument = null, string sourceContentType = "",
        RecruitmentDocumentRagContext? retrievalContext = null, CancellationToken cancellationToken = default);
}

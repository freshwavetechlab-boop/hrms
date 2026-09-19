using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public class AtsResumeDiagnosticsTests
{
    [Theory]
    [InlineData("NeedsReview", "Resume parsing was skipped because the file exceeds the 10 MB parser limit.")]
    [InlineData("NeedsReview", "Text could not be extracted reliably. The resume remains available for manual review.")]
    [InlineData("NeedsReview", "Force uploaded without extracted email or mobile. Complete the candidate identity manually before ATS scoring.")]
    [InlineData("Disabled", "Resume parsing is disabled for this job.")]
    [InlineData("Failed", "The stored resume could not be opened or parsed.")]
    public void Ats_PreservesActualParserReasonAndTerminalStatusPrefix(string status, string reason)
    {
        var error = RecruitmentTalentRepository.AtsResumePreconditionError(status, reason);
        Assert.StartsWith($"Resume parsing status is {status}.", error);
        Assert.Contains(reason, error);
        Assert.DoesNotContain("A parsed resume is required for ATS scoring.", error);
    }

    [Theory]
    [InlineData("Disabled", "Enable Resume Parsing")]
    [InlineData("Pending", "Wait for it to finish")]
    [InlineData("Processing", "Wait for it to finish")]
    [InlineData("Failed", "Re-upload a readable PDF or DOCX")]
    [InlineData("NeedsReview", "Review the resume and required candidate details")]
    public void Ats_LegacyEmptyReasonProvidesAnActionWithoutInventingACause(string status, string expected)
    {
        Assert.Contains(expected, RecruitmentTalentRepository.AtsResumePreconditionError(status));
        Assert.Contains(expected, RecruitmentTalentRepository.AtsResumePreconditionError(status, "  "));
    }

    [Fact]
    public void Ats_ParsedResumeIsNotBlockedByStaleParserWarning()
    {
        Assert.Empty(RecruitmentTalentRepository.AtsResumePreconditionError("Parsed", "Previous extraction warning"));
        Assert.Equal("Upload or select a resume before scoring the application.",
            RecruitmentTalentRepository.AtsResumePreconditionError(null, "Warning from another resume must not leak"));
    }

    [Fact]
    public void Ats_ErrorFitsExistingQueueColumnAndKeepsRootCauseFirst()
    {
        var error = RecruitmentTalentRepository.AtsResumePreconditionError("NeedsReview", "File too large. " + new string('x', 2000));
        Assert.True(error.Length <= 1000);
        Assert.StartsWith("Resume parsing status is NeedsReview. File too large.", error);
    }
}

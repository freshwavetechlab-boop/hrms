using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class RecruitmentResumeRecoveryValidationTests
{
    [Theory]
    [InlineData("candidate+job@gmail.com", "candidate@gmail.com", true)]
    [InlineData("candidate.name@gmail.com", "candidatename@gmail.com", true)]
    [InlineData("candidate+job@example.com", "candidate@example.com", false)]
    [InlineData("candidate.name@example.com", "candidatename@example.com", false)]
    [InlineData("candidate@gmail.com", "different@gmail.com", false)]
    [InlineData("", "", false)]
    public void ReplySenderAliasesNeverCrossDifferentOrCorporateMailboxes(string candidate, string sender, bool expected) =>
        Assert.Equal(expected, RecruitmentResumeRecoveryIdentity.SameMailbox(candidate, sender));

    private static RecruitmentTalentRepository Repository() => new(null!, null!, null!, null!, null!, null!,
        null!, null!, null!, null!, null!, NullLogger<RecruitmentTalentRepository>.Instance);

    [Fact]
    public async Task MissingOrOversizedReplyIsRejectedBeforeAnyDatabaseOrStorageAccess()
    {
        var repository = Repository();
        var absent = await repository.RecoverApplicationResumeAsync(1, new(), new AuthUser(), "", "", default);
        Assert.Null(absent.Row);
        using var source = new MemoryStream([1]);
        var oversized = await repository.RecoverApplicationResumeAsync(1, new()
        {
            File = new FormFile(source, 0, 11 * 1024 * 1024, "File", "resume.pdf")
        }, new AuthUser(), "", "", default);
        Assert.Null(oversized.Row);
        Assert.Contains("10 MB", oversized.Error);
    }

    [Theory]
    [InlineData(0, 2, "reply@example.test")]
    [InlineData(9, 0, "reply@example.test")]
    [InlineData(9, 2, "")]
    public async Task MissingSourceEvidenceIsRejectedBeforeAnyDatabaseOrStorageAccess(long resume, long field, string messageId)
    {
        using var source = new MemoryStream([1]);
        var result = await Repository().RecoverApplicationResumeAsync(1, new()
        {
            File = new FormFile(source, 0, 1, "File", "resume.pdf"), ExpectedResumeId = resume,
            FieldConfigurationId = field, SourceMessageId = messageId, ReceivedAt = DateTimeOffset.UtcNow
        }, new AuthUser(), "", "", default);
        Assert.Null(result.Row);
        Assert.Contains("evidence", result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sha256")]
    [InlineData("aaaaaaaa")]
    public async Task InvalidHashIsRejectedBeforeAnyDatabaseOrStorageAccess(string hash)
    {
        using var source = new MemoryStream([1]);
        var result = await Repository().RecoverApplicationResumeAsync(1, new()
        {
            File = new FormFile(source, 0, 1, "File", "resume.pdf"), ExpectedResumeId = 9,
            FieldConfigurationId = 2, SourceMessageId = "reply@example.test", ReceivedAt = DateTimeOffset.UtcNow,
            Sha256 = hash
        }, new AuthUser(), "", "", default);
        Assert.Null(result.Row);
        Assert.Contains("SHA-256", result.Error);
    }
}

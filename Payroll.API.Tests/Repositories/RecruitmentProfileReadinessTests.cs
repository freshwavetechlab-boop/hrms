using Dapper;
using MySqlConnector;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class RecruitmentProfileReadinessTests
{
    [Fact]
    public void Profile_values_complete_mapped_fields_without_inventing_certification_proof()
    {
        var row = new RecruitmentCandidate { CurrentCompany = "Employer", ExpectedCtc = 1500000m, CurrentCtc = 0, NoticePeriodDays = 0 };
        bool Supplies(string code, string type = "TEXT", bool certificate = false) =>
            RecruitmentProfileReadiness.ProfileSupplies(new() { SemanticCode = code, FieldTypeCode = type }, row, certificate);
        Assert.True(Supplies("CURRENT_COMPANY"));
        Assert.True(Supplies("EXPECTED_CTC", "NUMBER"));
        Assert.True(Supplies("CURRENT_CTC", "NUMBER"));
        Assert.True(Supplies("NOTICE_PERIOD_DAYS", "NUMBER"));
        Assert.False(Supplies("CERTIFICATIONS"));
        Assert.True(Supplies("CERTIFICATIONS", certificate: true));
        Assert.False(Supplies("CERTIFICATIONS", "UPLOAD", true));
        Assert.False(Supplies("CUSTOM_DECLARATION", "CHECKBOX"));
        row.ExpectedCtc = null;
        Assert.False(Supplies("EXPECTED_CTC", "NUMBER"));
    }

    [Theory]
    [InlineData("", true, true)]
    [InlineData("Draft", true, true)]
    [InlineData("Negotiation", true, true)]
    [InlineData("Rejected", true, false)]
    [InlineData("Withdrawn", true, false)]
    [InlineData("Approved", false, false)]
    public void Mom_requires_interview_selection_and_does_not_require_negotiation(string offerStatus, bool selected, bool expected)
    {
        Assert.Equal(expected, RecruitmentHiringProgress.MoMReady(new()
        {
            HasCompletedInterviewDecision = selected, LatestOfferStatus = offerStatus,
            LatestInterviewResult = selected ? "Selected" : "Pending"
        }));
    }

    [Fact]
    public void Rejected_or_pending_extra_candidates_do_not_hold_selected_cohort()
    {
        RecruitmentHiringProgress.Candidate selected = new() { HasCompletedInterviewDecision = true };
        RecruitmentHiringProgress.Candidate rejected = new() { HasCompletedInterviewDecision = true, LatestInterviewResult = "Rejected" };
        Assert.True(RecruitmentHiringProgress.Enough(2, [selected, selected, rejected, new()], RecruitmentHiringProgress.MoMReady));
        Assert.False(RecruitmentHiringProgress.Enough(2, [selected, rejected, new()], RecruitmentHiringProgress.MoMReady));
    }

    [HiringProgressReadOnlyDatabaseFact]
    public async Task Required_field_query_reads_only_active_mandatory_fields_and_batch_check_is_read_only()
    {
        await using var db = new MySqlConnection(Environment.GetEnvironmentVariable("HRMS_HIRING_PROGRESS_READONLY_CONNECTION"));
        await db.OpenAsync();
        var versionId = await db.ExecuteScalarAsync<long>(@"SELECT FormVersionId FROM form_fields
GROUP BY FormVersionId HAVING SUM(IsRequired=FALSE)>0 AND SUM(IsRequired=TRUE)>0 LIMIT 1");
        Assert.True(versionId > 0);
        var requiredIds = (await db.QueryAsync<long>("SELECT Id FROM form_fields WHERE FormVersionId=@Id AND IsActive=TRUE AND IsRequired=TRUE", new { Id = versionId })).ToHashSet();
        var missing = await RecruitmentFormRepository.RequiredSubmissionMissingFieldsAsync(db, null, 0, versionId);
        Assert.NotEmpty(missing);
        Assert.All(missing, field => Assert.Contains(field.Id, requiredIds));
        var ids = await db.QueryAsync<long>("SELECT DISTINCT ApplicationId FROM recruitment_profile_submission_batch_items ORDER BY ApplicationId LIMIT 8");
        foreach (var id in ids)
        {
            var first = await RecruitmentProfileReadiness.MissingAsync(db, id);
            Assert.Equal(first, await RecruitmentProfileReadiness.MissingAsync(db, id));
        }
    }

    [HiringProgressReadOnlyDatabaseFact]
    public async Task Batch_refresh_uses_configured_fields_and_preserves_stored_records()
    {
        var connection = Environment.GetEnvironmentVariable("HRMS_HIRING_PROGRESS_READONLY_CONNECTION")!;
        await using var db = new MySqlConnection(connection);
        await db.OpenAsync();
        var source = await db.QueryFirstAsync<(long HiringCaseId, int ClientId)>(
            "SELECT HiringCaseId,ClientId FROM recruitment_profile_submission_batches ORDER BY Id DESC LIMIT 1");
        var before = (await db.QueryAsync<string>(@"SELECT CONCAT(item.Id,':',item.ReadinessStatus)
FROM recruitment_profile_submission_batch_items item JOIN recruitment_profile_submission_batches batch ON batch.Id=item.BatchId
WHERE batch.HiringCaseId=@HiringCaseId ORDER BY item.Id", new { source.HiringCaseId })).ToArray();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
        var repository = new RecruitmentCaseRepository(configuration, null!, null!, null!);
        var batches = await repository.ListProfileBatchesAsync(source.HiringCaseId, new AuthUser { ClientId = source.ClientId });
        Assert.NotEmpty(batches);
        foreach (var item in batches.SelectMany(batch => batch.Items))
            Assert.Equal(item.MissingFields.Length == 0 ? "Ready" : "Incomplete", item.ReadinessStatus);
        var after = (await db.QueryAsync<string>(@"SELECT CONCAT(item.Id,':',item.ReadinessStatus)
FROM recruitment_profile_submission_batch_items item JOIN recruitment_profile_submission_batches batch ON batch.Id=item.BatchId
WHERE batch.HiringCaseId=@HiringCaseId ORDER BY item.Id", new { source.HiringCaseId })).ToArray();
        Assert.Equal(before, after);
        Assert.Empty(await repository.ListProfileBatchesAsync(source.HiringCaseId, new AuthUser { ClientId = source.ClientId + 1 }));
        var documents = await repository.ListProcessDocumentsAsync(new AuthUser { ClientId = source.ClientId }, null, null);
        Assert.All(documents.Where(document => RecruitmentCaseRepository.IsMoM(document.DocumentType)), document =>
        {
            Assert.Equal(1, document.RequiredSignatureCount);
            Assert.Equal(document.SignatureCount >= 1, document.CapturedSignaturesComplete);
        });
        var fixturePath = Environment.GetEnvironmentVariable("HRMS_PROFILE_REVIEW_FIXTURE_PATH");
        if (!string.IsNullOrWhiteSpace(fixturePath))
            await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(new { source.HiringCaseId, source.ClientId, Batches = batches }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}

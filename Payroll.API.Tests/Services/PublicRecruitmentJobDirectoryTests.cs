using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public class PublicRecruitmentJobDirectoryTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
    private static RecruitmentFormRepository.PublicJobDirectoryRow Job() => new()
    {
        PostingId = 901, PositionId = 800, JobDescriptionVersionId = 700,
        PublicSlug = new string('a', 32), PublicTitle = "Research specialist", PositionTitle = "Specialist",
        PositionCode = "OPEN-019", ClientName = "Example institute", Department = "Research",
        PostingStatus = "Published", ClientActive = true, SourceScopeValid = true, FormScopeValid = true,
        PositionStatus = "Published", RequisitionStatus = "Approved", JobDescriptionStatus = "Approved",
        FormDefinitionStatus = "Active", FormStatus = "Published", RemainingPositions = 2, NumberOfPositions = 3,
        ApplicationCount = 4, MaximumApplications = 10, OpensAtUtc = Now.AddDays(-1), ClosesAtUtc = Now.AddDays(1),
        PublishedAtUtc = Now.AddDays(-2), SalaryMin = 35000.25m, SalaryMax = 50000.75m, Currency = "INR",
        JobLocation = "Pune", WorkMode = "Hybrid", EmploymentType = "Contractual", ExperienceRange = "3–5 years",
        Summary = "Research operations and project coordination.", RolePurpose = "Support the research team.",
    };

    [Fact]
    public void ACurrentPublishedOpeningIsEligibleIncludingAPinnedRetiredForm()
    {
        var row = Job();
        Assert.True(RecruitmentFormRepository.IsPublicDirectoryJob(row, Now));
        row.FormStatus = "Retired";
        Assert.True(RecruitmentFormRepository.IsPublicDirectoryJob(row, Now));
        row.OpensAtUtc = Now; row.ClosesAtUtc = Now;
        Assert.True(RecruitmentFormRepository.IsPublicDirectoryJob(row, Now));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("archived")]
    [InlineData("inactive-client")]
    [InlineData("not-yet-open")]
    [InlineData("expired")]
    [InlineData("full")]
    [InlineData("filled-position")]
    [InlineData("cancelled-position")]
    [InlineData("closed-position")]
    [InlineData("on-hold-position")]
    [InlineData("no-vacancies")]
    [InlineData("draft-jd")]
    [InlineData("unapproved-requisition")]
    [InlineData("draft-form")]
    [InlineData("inactive-form")]
    [InlineData("wrong-form-client")]
    [InlineData("wrong-jd-client")]
    [InlineData("invalid-slug")]
    public void NonPublicOrUnavailableOpeningsDoNotEnterTheDirectory(string scenario)
    {
        var row = Job();
        switch (scenario)
        {
            case "draft": row.PostingStatus = "Draft"; break;
            case "archived": row.PostingStatus = "Closed"; break;
            case "inactive-client": row.ClientActive = false; break;
            case "not-yet-open": row.OpensAtUtc = Now.AddTicks(1); break;
            case "expired": row.ClosesAtUtc = Now.AddTicks(-1); break;
            case "full": row.ApplicationCount = row.MaximumApplications!.Value; break;
            case "filled-position": row.PositionStatus = "Filled"; break;
            case "cancelled-position": row.PositionStatus = "Cancelled"; break;
            case "closed-position": row.PositionStatus = "Closed"; break;
            case "on-hold-position": row.PositionStatus = "On Hold"; break;
            case "no-vacancies": row.RemainingPositions = 0; break;
            case "draft-jd": row.JobDescriptionStatus = "Draft"; break;
            case "unapproved-requisition": row.RequisitionStatus = "Pending Approval"; break;
            case "draft-form": row.FormStatus = "Draft"; break;
            case "inactive-form": row.FormDefinitionStatus = "Inactive"; break;
            case "wrong-form-client": row.FormScopeValid = false; break;
            case "wrong-jd-client": row.SourceScopeValid = false; break;
            case "invalid-slug": row.PublicSlug = "https://untrusted.invalid/redirect"; break;
        }
        Assert.False(RecruitmentFormRepository.IsPublicDirectoryJob(row, Now));
    }

    [Fact]
    public void ThePublicProjectionRetainsAdvertisedAmountsAndUsesTheExistingApplyUrl()
    {
        var row = Job();
        var card = RecruitmentFormRepository.ToPublicDirectoryJob(row, "https://careers.example.test/recruitment");
        Assert.Equal("https://careers.example.test/recruitment/careers/" + row.PublicSlug, card.PublicUrl);
        Assert.Equal(row.SalaryMin, card.SalaryMin); Assert.Equal(row.SalaryMax, card.SalaryMax);
        Assert.Equal(row.Currency, card.Currency); Assert.Equal(row.ExperienceRange, card.ExperienceRange);
        Assert.Equal(row.RemainingPositions, card.RemainingPositions);
        Assert.Equal(DateTimeKind.Utc, card.ClosesAtUtc!.Value.Kind);
        Assert.Equal(Now.AddDays(1).Ticks, card.ClosesAtUtc.Value.Ticks);
        Assert.Equal("", RecruitmentFormRepository.ToPublicDirectoryJob(row, "javascript:alert(1)").PublicUrl);
    }

    [Fact]
    public void InternalPostingKeysFormsAndHiringMetricsCannotLeakThroughTheProjection()
    {
        var publicRow = RecruitmentFormRepository.ToPublicDirectoryJob(Job(), "https://jobs.example.test");
        var properties = JsonSerializer.SerializeToDocument(publicRow).RootElement.EnumerateObject().Select(property => property.Name).ToHashSet();
        Assert.Equal(new HashSet<string> {
            "PublicSlug", "PublicUrl", "PublicTitle", "PositionCode", "PositionTitle", "ClientName", "Department", "JobLocation",
            "EmploymentType", "WorkMode", "ExperienceRange", "SalaryMin", "SalaryMax", "Currency", "NumberOfPositions",
            "RemainingPositions", "PublishedAtUtc", "OpensAtUtc", "ClosesAtUtc", "Summary", "RolePurpose", "Responsibilities",
            "Skills", "Qualifications", "Certifications", "Languages", "Benefits",
        }.Order(), properties.Order());
        Assert.DoesNotContain("PostingId", properties); Assert.DoesNotContain("ClientId", properties);
        Assert.DoesNotContain("ApplicationCount", properties); Assert.DoesNotContain("ApplicationForm", properties);
        Assert.DoesNotContain("BudgetAmount", properties); Assert.DoesNotContain("SalaryPeriod", properties);
    }

    [Fact]
    public void CorruptSourceNarrativeFallsBackToThePublicRolePurpose()
    {
        var row = Job(); row.Summary = "\uFFFD\u0001";
        Assert.Equal(row.RolePurpose, RecruitmentFormRepository.ToPublicDirectoryJob(row, "https://jobs.example.test").Summary);
        row.RolePurpose = "\uFFFD\u0001";
        Assert.Empty(RecruitmentFormRepository.ToPublicDirectoryJob(row, "https://jobs.example.test").Summary);
    }
}

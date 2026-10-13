using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class PayrollAdvisorTests
{
    private static PayrollReviewComponent Wage => new("1", "WAGES", "Earning", "Regular Earning", "None", "Fixed Amount", "18000", "", "", true, true, true, 10, "Fixed Pay");
    private static PayrollReviewComponent Fee => new("2", "FEE", "Deduction", "Regular Deduction", "None", "Formula", "", "WAGES_EARNED*5%", "", false, false, true, 20, "Fixed Pay");
    private static PayrollReviewSnapshot Template => new("Template", 11, "", 216000, [Wage, Fee], [new("1"), new("2")]);

    [Fact]
    public void UsesPayrollEngineForSixScenariosWithoutApproval()
    {
        var report = PayrollAdvisorService.Check(Template);
        Assert.Equal("NeedsReview", report.Status);
        Assert.Equal(6, report.Scenarios.Count);
        Assert.All(report.Scenarios, s => Assert.Null(s.Error));
        Assert.Equal(9000, report.Scenarios.Single(s => s.Label == "Partial month").Amounts.Single(a => a.Code == "WAGES").Earned);
        Assert.Equal(450, report.Scenarios.Single(s => s.Label == "Partial month").Amounts.Single(a => a.Code == "FEE").Earned);
        Assert.All(report.Scenarios.Single(s => s.Label == "Zero payable days").Amounts, a => Assert.Equal(0, a.Earned));
        Assert.Contains("No verified statutory rule pack", report.Coverage);
    }

    [Fact]
    public void OnlyEngineFindingCanSuggestProrationDraftPatch()
    {
        var report = PayrollAdvisorService.Check(Template with { Components = [Wage, Fee with { ProRata = true }] });
        Assert.True(Assert.Single(report.Findings).CanDisableProration);
        using var json = JsonDocument.Parse("""{"status":"Approved","findings":[{"code":"FEE","title":"Suggestion","explanation":"Review this","example":"Example","suggestion":"Look at the result","canDisableProration":true,"severity":"Passed"}]}""");
        var result = Assert.Single(PayrollAdvisorService.ReadAiFindings(json.RootElement, Template));
        Assert.False(result.CanDisableProration);
        Assert.Equal("Review", result.Severity);
    }

    [Fact]
    public void InheritedProrationOverrideIsRespected()
    {
        var report = PayrollAdvisorService.Check(Template with { Components = [Wage, Fee with { ProRata = true }], Lines = [new("1"), new("2", ProRataOverride: "false")] });
        Assert.DoesNotContain(report.Findings, f => f.Id.Contains("double-proration"));
    }

    [Fact]
    public void InvalidFormulaRemainsFailedEvenIfAiHasNoFindings()
    {
        var report = PayrollAdvisorService.Check(Template with { Components = [Wage, Fee with { Formula = "WAGES/0" }] });
        Assert.All(report.Scenarios, s => Assert.NotNull(s.Error));
        using var json = JsonDocument.Parse("{\"findings\":[]}");
        Assert.Empty(PayrollAdvisorService.ReadAiFindings(json.RootElement, Template));
        Assert.Equal("NeedsReview", report.Status);
    }

    [Theory]
    [InlineData("0-10000:0;20000+:200")]
    [InlineData("0-20000:0;15000+:200")]
    [InlineData("0-20000:0")]
    [InlineData("bad slab")]
    public void SlabGapsOverlapsAndMissingFinalRangeAreFlagged(string formula)
    {
        var report = PayrollAdvisorService.Check(Template with { Components = [Wage, Fee with { CalculationType = "Slab Based", Formula = formula }] });
        Assert.Contains(report.Findings, f => f.Id == "FEE:slabs");
    }

    [Fact]
    public void ContinuousSlabsDoNotRaiseCoverageWarning()
    {
        var report = PayrollAdvisorService.Check(Template with { Components = [Wage, Fee with { CalculationType = "Slab Based", Formula = "0-15000:0;15000.01+:200" }] });
        Assert.DoesNotContain(report.Findings, f => f.Id == "FEE:slabs");
    }

    [Fact]
    public void StatutoryTagsRequireReviewRegardlessOfSampleMath()
    {
        var report = PayrollAdvisorService.Check(Template with { Components = [Wage, Fee with { StatutoryType = "PF Employee" }] });
        Assert.Contains(report.Findings, f => f.Id == "FEE:statutory" && f.Severity == "Review");
        Assert.Equal("NeedsReview", report.Status);
    }

    [Fact]
    public void EmployerCostMisclassifiedAsDeductionIsAnError()
    {
        var report = PayrollAdvisorService.Check(Template with { Components = [Wage, Fee with { ComponentRole = "Employer Contribution" }] });
        Assert.Contains(report.Findings, f => f.Id == "FEE:employer-deduction" && f.Severity == "Error");
    }

    [Fact]
    public void DailyRateSimulationDoesNotDoubleProrate()
    {
        var report = PayrollAdvisorService.Check(Template with { AnnualCtc = 0, Components = [Wage with { CalculationType = "Daily Rate", Value = "700" }, Fee] });
        Assert.Equal(15750, report.Scenarios.Single(s => s.PayableDays == 22.5m).Amounts.Single(a => a.Code == "WAGES").Earned);
    }

    [Theory]
    [InlineData(false, null, "settings.manage", 11, false)]
    [InlineData(true, 11, "client.settings.manage", 11, true)]
    [InlineData(true, 11, "client.settings.manage", 12, false)]
    [InlineData(true, 11, "settings.manage", 12, false)]
    [InlineData(true, 11, "client.settings.manage", 0, false)]
    [InlineData(true, null, "settings.manage", 0, true)]
    [InlineData(true, 11, "ess.profile", 11, false)]
    public void ScopeAndPermissionAreBothRequired(bool active, int? userClient, string permission, int requested, bool allowed) =>
        Assert.Equal(allowed, PayrollAdvisorService.CanAccess(new AuthUser { Id = 1, IsActive = active, ClientId = userClient, Permissions = [permission] }, requested));

    [Fact]
    public void ChangedDraftHasDifferentHash()
    {
        Assert.NotEqual(PayrollAdvisorService.Hash(Template), PayrollAdvisorService.Hash(Template with { Lines = [new("1"), new("2", ProRataOverride: "false")] }));
        Assert.NotEqual(PayrollAdvisorService.Hash(Template), PayrollAdvisorService.Hash(Template with { ClientId = 12 }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"findings\":\"Approved\"}")]
    [InlineData("{\"findings\":[{\"code\":\"OTHER_CLIENT\"}]}")]
    [InlineData("{\"findings\":[{\"code\":\"WAGES\"}]}")]
    public void InvalidAiOutputFailsClosed(string value)
    {
        using var json = JsonDocument.Parse(value);
        Assert.Throws<InvalidOperationException>(() => PayrollAdvisorService.ReadAiFindings(json.RootElement, Template));
    }

    [Fact]
    public void WhitelistDropsEmployeeDataBeforeProviderInput()
    {
        var json = PayrollAdvisorService.Serialize(Template).TrimEnd('}') + ",\"employees\":[{\"pan\":\"SECRET\",\"bankAccount\":\"SECRET\"}]}";
        var snapshot = JsonSerializer.Deserialize<PayrollReviewSnapshot>(json, PayrollAdvisorService.Json)!;
        Assert.DoesNotContain("SECRET", PayrollAdvisorService.Serialize(snapshot));
    }

    [Fact]
    public void InvalidSnapshotIsRejectedBeforeQueueing()
    {
        Assert.Throws<InvalidOperationException>(() => PayrollAdvisorService.Validate(Template with { Components = [Wage, Wage] }));
        Assert.Throws<InvalidOperationException>(() => PayrollAdvisorService.Validate(Template with { Lines = [new("unknown")] }));
        Assert.Throws<InvalidOperationException>(() => PayrollAdvisorService.Validate(Template with { ClientId = 0 }));
    }
}

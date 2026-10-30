using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollLocationAllocationImportTests
{
    private sealed record Fixture(ExcelPayrollSetupRequest Request, ExcelPayslipBatch Batch, ExcelPayslipCalculationSource Source,
        string Settings, List<ExcelPayrollSetupRepository.Identity> Identities, Dictionary<int, Employee> Employees,
        List<ExcelPayrollSetupRepository.LocationAllocationMaster> Locations);

    private static Fixture Create()
    {
        var component = new ExcelPayrollSetupComponent { Key = "basic", ExistingComponentId = "1", Code = "BASIC", Name = "Basic",
            SourceColumnIndex = 2, ExpectedColumnIndex = 3, Priority = 10, PayType = "Fixed Pay" };
        var request = new ExcelPayrollSetupRequest { BatchId = new string('a',32), RowIds = ["one","two"], TemplateName = "Reviewed existing location salary",
            EffectiveFrom = "2026-09-01", WorkWeek = "Sunday off", PayrollDaysColumnIndex = 0, PayableDaysColumnIndex = 1, PresentDaysColumnIndex = 4,
            TransferReviewVersion = 1, ReviewedPolicyScope = true, TemplateScopeReviewVersion = 1,
            TemplateScope = new() { Reviewed = true, OpenEnded = true, LocationMode = "Listed", WorkLocationIds = [1,2], CategoryKind = "AllClientCategories",
                DivisorMeaning = "ContractFixedDays", PayableMeaning = "AlreadyPayable", PolicyBasis = "SavedSourceRules" }, Components = [component],
            LocationAllocations = new() { PayrollPeriod = "2026-09", ConfigurationEffectiveFrom = "2026-10-28", Reviewed = true, ReviewedSettingsApplicability = true,
                Groups = [new() { EmployeeId = 10, Parts = [new() { RowId = "one", WorkLocationId = 1, TemplateId = "500", ComponentScopes = new() { ["1"] = "SourceFormulaPerPart" } },
                    new() { RowId = "two", WorkLocationId = 2, TemplateId = "500", ComponentScopes = new() { ["1"] = "SourceFormulaPerPart" } }] }] } };
        var batch = new ExcelPayslipBatch { Id = request.BatchId, ClientId = 42, Month = "2026-09", Rows = [
            new() { Id = "one", SourceRow = 3, EmployeeCode = "SYN-1", EmployeeName = "Synthetic Employee", NetPay = 17000,
                Information = [new() { Label = "Location", Value = "Source A" }, new() { Label = "PAN", Value = "ABCDE1234F" }], Earnings = [new() { Label = "Basic", Amount = 17000 }] },
            new() { Id = "two", SourceRow = 4, EmployeeCode = "SYN-1", EmployeeName = "Synthetic Employee", NetPay = 13000,
                Information = [new() { Label = "Location", Value = "Source B" }, new() { Label = "PAN", Value = "ABCDE1234F" }], Earnings = [new() { Label = "Basic", Amount = 13000 }] }
        ] };
        var source = new ExcelPayslipCalculationSource { SheetName = "Salary", HeaderRow = 2, ColumnCount = 5,
            Columns = Enumerable.Range(0,5).Select(index => new ExcelPayslipCalculationColumn { ColumnIndex = index, Kind = index == 3 ? "earning" : "info" }).ToList(),
            Rows = [new() { SourceRow = 3, Cells = [new() { Value = "30" },new() { Value = "17" },new() { Value = "30000" },new() { Value = "17000", FormulaText = "C3/A3*B3" },new() { Value = "17" }] },
                new() { SourceRow = 4, Cells = [new() { Value = "30" },new() { Value = "13" },new() { Value = "30000" },new() { Value = "13000", FormulaText = "C4/A4*B4" },new() { Value = "13" }] }] };
        request.AttendanceReview = new() { Mode = "SourceSeed", Reviewed = true, SourceMonth = batch.Month, CycleStart = "2026-09-01", CycleEnd = "2026-09-30",
            PayableMeaning = "AlreadyPayable", PresentMeaning = "ActualWorked",
            SourceHash = ExcelPayrollSetupRepository.Hash(JsonSerializer.Serialize(new { batch, source }, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        request.AttendanceReview.BoundSourceInputs = ExcelPayrollSetupRepository.AttendanceBinding(request);
        var settings = """
            {"salaryComponents":[{"id":"1","code":"BASIC","name":"Basic","category":"Earning","componentRole":"Regular Earning","statutoryType":"None",
            "calculationType":"Fixed Amount","value":"0","formula":"","baseComponent":"","payType":"Fixed Pay","proRata":true,"taxable":true,"active":true,"priority":"10"}],
            "salaryStructures":[{"id":"500","clientId":"42","name":"Existing reviewed location template","active":true,"annualCtc":"0","lines":[
            {"componentId":"1","value":"0","calculationType":"Fixed Amount","formula":"","baseComponent":"","proRataOverride":"true","roundingMode":""}]}]}
            """;
        return new(request, batch, source, settings,
            [new() { Id = 10, ClientId = 42, EmployeeCode = "SYN-1", Name = "Synthetic Employee", Pan = "ABCDE1234F", IsActive = true, EligibleForMonth = true }],
            new() { [10] = new() { Id = 10, ClientId = 42, EmployeeCode = "SYN-1", FirstName = "Synthetic", LastName = "Employee", WorkLocationId = 99, SalaryStructureId = "old" } },
            [new(1,"Master A","Delhi"),new(2,"Master B","Uttar Pradesh")]);
    }

    private static ExcelPayrollSetupRepository.LocationAllocationPlan Review(Fixture fixture) =>
        ExcelPayrollSetupRepository.ReviewLocationAllocationPlan(42, fixture.Request, fixture.Batch, fixture.Source, fixture.Settings,
            fixture.Identities, fixture.Employees, fixture.Locations);

    [Fact]
    public void CompleteExistingEmployeePartsPrepareOneIdentityAndTwoSettingsAssignmentsWithoutInventedDates()
    {
        var fixture = Create();
        var before = JsonSerializer.Serialize(new { fixture.Batch, fixture.Source, fixture.Employees });
        ExcelPayrollSetupRepository.ValidateRequest(fixture.Request);
        var plan = Review(fixture);
        // Different Master states require an explicit monthly projection
        // reference. The choice is made from the server's stable preview IDs.
        fixture.Request.LocationAllocations!.Groups[0].ReferenceAssignmentId = plan.Periods[0].Parts[0].AssignmentId;
        plan = Review(fixture);
        Assert.True(plan.Evidence.Preview.CanApply, string.Join(" | ",plan.Evidence.Preview.Issues.Where(issue => issue.Severity == "Error").Select(issue => issue.Message)));
        var period = Assert.Single(plan.Periods);
        Assert.Equal(10, period.EmployeeId); Assert.Equal("2026-09", period.PayPeriod);
        Assert.Equal(2,period.Parts.Count); Assert.Equal(30,period.Parts.Sum(part => part.PayableDays));
        Assert.Equal("2026-10-28",plan.Evidence.Preview.LocationAllocations!.ConfigurationEffectiveFrom);
        Assert.All(period.Parts,part => { Assert.Equal("500",part.SalaryStructureId); Assert.Equal(30000,part.SalaryInputs["1"]); });
        Assert.All(plan.Evidence.Preview.Transfer!.AttendanceProofs,proof => Assert.Equal("Reviewed source allocation - aggregate pending",proof.Status));
        var actual = PayRunRepository.SimulateLocationParts(period,fixture.Settings,30,30,30);
        Assert.True(actual.Complete,string.Join(" | ",actual.Issues.Select(issue => issue.Message))); Assert.Equal(30000,actual.Net);
        Assert.Equal(before,JsonSerializer.Serialize(new { fixture.Batch, fixture.Source, fixture.Employees }));
    }

    [Fact]
    public void PartialGroupIsBlockedEvenWhenTheOtherPartIsOutsideTheSelection()
    {
        var fixture = Create(); fixture.Request.RowIds = ["one"]; fixture.Request.LocationAllocations!.Groups[0].Parts.RemoveAt(1);
        Assert.Contains(ExcelPayrollSetupRepository.LocationAllocationGroupIssues(fixture.Batch,fixture.Request),issue => issue.Contains("every saved source part"));
        Assert.False(Review(fixture).Evidence.Preview.CanApply);
    }

    [Fact]
    public void DifferentSourcePartsCanShareOneImmutableAssignmentWithoutLosingPartIdentity()
    {
        var fixture = Create(); fixture.Request.LocationAllocations!.Groups[0].Parts[1].WorkLocationId = 1;
        var period = Assert.Single(Review(fixture).Periods);
        Assert.Equal(2,period.Parts.Count); Assert.Single(period.Parts.Select(part => part.AssignmentId).Distinct());
        Assert.Equal(2,period.Parts.Select(part => part.PartKey).Distinct().Count());
    }

    [Theory]
    [InlineData("foreignTemplate")]
    [InlineData("missingLocation")]
    [InlineData("formulaDifference")]
    [InlineData("scopeMissing")]
    [InlineData("identityMismatch")]
    [InlineData("payPeriodChanged")]
    public void InvalidSettingsIdentityLocationScopeOrSourcePeriodCannotPrepareApply(string change)
    {
        var fixture = Create(); var part = fixture.Request.LocationAllocations!.Groups[0].Parts[0];
        if (change == "foreignTemplate") part.TemplateId = "999";
        if (change == "missingLocation") part.WorkLocationId = 999;
        if (change == "formulaDifference") fixture.Request.Components[0].Value = "1";
        if (change == "scopeMissing") part.ComponentScopes = new() { ["2"] = "Monthly" };
        if (change == "identityMismatch") fixture.Identities[0].Pan = "BCDEF1234G";
        if (change == "payPeriodChanged") fixture.Request.LocationAllocations.PayrollPeriod = "2026-10";
        Assert.False(Review(fixture).Evidence.Preview.CanApply);
    }

    [Fact]
    public void SavedSourceAndMasterSelectionHaveSeparateStableVersionHashes()
    {
        var fixture = Create(); var first = Review(fixture).Periods[0].Parts[0].AssignmentId;
        fixture.Request.LocationAllocations!.Groups[0].ReferenceAssignmentId = first;
        Assert.Equal(first,Review(fixture).Periods[0].Parts[0].AssignmentId);
        fixture.Request.LocationAllocations.ConfigurationEffectiveFrom = "2026-10-29";
        Assert.NotEqual(first,Review(fixture).Periods[0].Parts[0].AssignmentId);
        Assert.Equal("2026-09",fixture.Batch.Month);
    }

    [Fact]
    public void SettingsHashDetectsChangedPersistedFormulaTaxabilityAndOrder()
    {
        var fixture = Create(); var settings = JsonNode.Parse(fixture.Settings)!.AsObject();
        var template = settings["salaryStructures"]![0]!.AsObject(); var component = settings["salaryComponents"]![0]!.AsObject();
        var original = ExcelPayrollSetupRepository.LocationSettingsRulesHash(template,[component]);
        component["taxable"] = false;
        Assert.NotEqual(original,ExcelPayrollSetupRepository.LocationSettingsRulesHash(template,[component]));
    }

    [Fact]
    public void NewReceiptKeepsEverySourcePartAppliedWhileRetainingOneEmployeeId()
    {
        var fixture = Create(); var result = new ExcelPayrollSetupResult { EmployeeIds = [10], LocationAllocations = Review(fixture).Evidence.Preview.LocationAllocations };
        var rows = ExcelPayrollSetupRepository.AppliedReceiptRows("[\"one\",\"two\"]",JsonSerializer.Serialize(result,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(new[] {"one","two"},rows.Order());
        Assert.Single(result.EmployeeIds);
    }
}

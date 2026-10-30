using System.Text.Json.Nodes;
using Payroll.API.Repositories;
using Allocation = Payroll.API.Repositories.PayRunRepository.LocationPartStoreRow;
using Assignment = Payroll.API.Repositories.PayRunRepository.LocationPartAssignmentStoreRow;

namespace Payroll.API.Tests.Services;

public sealed class PayrollLocationPartStoreTests
{
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly IReadOnlyDictionary<int, int> Employees = new Dictionary<int, int> { [7] = 42 };
    private static Assignment Salary() => new()
    {
        AssignmentId = Id, ClientId = 42, EmployeeId = 7, WorkLocationId = 3,
        WorkState = "Synthetic State", SalaryStructureId = "12", AnnualCtc = 240000,
        SalaryInputsJson = "{\"1\":20000}", ComponentScopesJson = "{\"1\":\"SourceFormulaPerPart\",\"2\":\"Monthly\"}",
        RulesHash = Hash, SourceHash = Hash, ImportId = Id, BatchId = Id, RowId = "source-one", Status = "Active",
        ConfigurationEffectiveFrom = "2026-10-29"
    };
    private static Allocation Part(string part, decimal days) => new()
    {
        ClientId = 42, EmployeeId = 7, PayPeriod = "2026-09", PartKey = part,
        AssignmentId = Id, PayrollDays = 30, PresentDays = days, PayableDays = days,
        AllocationImportId = Id, AllocationBatchId = Id, AllocationRowId = part, AllocationSourceHash = Hash
    };

    [Fact]
    public void NoAllocationLeavesLegacyEmployeeWithoutAnEnvelope()
    {
        Assert.Empty(PayRunRepository.ReadLocationPartPeriods([], [], Employees, "2026-09"));
    }

    [Fact]
    public void FutureMonthRequiresNewAttendanceAllocationForReviewedLocationAssignments()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PayRunRepository.ReadLocationPartPeriods([], [Salary()], Employees, "2026-11"));
        Assert.Contains("attendance allocations for this payroll month", error.Message);
        Assert.Empty(PayRunRepository.ReadLocationPartPeriods([], [Salary()], Employees, "2026-08"));
    }

    [Fact]
    public void ExplicitSavedSourceMonthMayReferenceLaterImportConfigurationDate()
    {
        var result = PayRunRepository.ReadLocationPartPeriods([Part("source-one", 30)], [Salary()], Employees, "2026-09");
        Assert.Equal("2026-09", result[7].PayPeriod);
    }

    [Fact]
    public void TwoActualPartsMayShareOneImmutableAssignmentAndAreNotCollapsed()
    {
        var result = PayRunRepository.ReadLocationPartPeriods([Part("source-one", 17), Part("source-two", 13)],
            [Salary()], Employees, "2026-09");
        var period = Assert.Single(result).Value;
        Assert.Equal(42, period.ClientId); Assert.Equal(7, period.EmployeeId); Assert.Equal("2026-09", period.PayPeriod);
        Assert.Equal(2, period.Parts.Count); Assert.Equal(30, period.Parts.Sum(part => part.PayableDays));
        Assert.All(period.Parts, part => Assert.Equal(Id, part.AssignmentId));
        Assert.Equal(20000, period.Parts[0].SalaryInputs["1"]);
        Assert.Equal("Monthly", period.Parts[0].ComponentScopes["2"]);
    }

    [Theory]
    [InlineData("employee")]
    [InlineData("client")]
    [InlineData("period")]
    [InlineData("missing")]
    [InlineData("inactive")]
    [InlineData("source")]
    public void AllocatedInvalidStateNeverFallsBackToLegacy(string invalid)
    {
        var row = Part("source-one", 17); var assignment = Salary();
        switch (invalid)
        {
            case "employee": assignment.EmployeeId = 8; break;
            case "client": row.ClientId = 43; break;
            case "period": row.PayPeriod = "2026-10"; break;
            case "missing": row.AssignmentId = new string('c', 32); break;
            case "inactive": assignment.Status = "Inactive"; break;
            case "source": row.AllocationSourceHash = ""; break;
        }
        var error = Assert.Throws<InvalidOperationException>(() =>
            PayRunRepository.ReadLocationPartPeriods([row], [assignment], Employees, "2026-09"));
        Assert.StartsWith("Location payroll allocation:", error.Message);
    }

    [Fact]
    public void DuplicatePartCannotDoubleCountEmployeeDays()
    {
        Assert.Throws<InvalidOperationException>(() => PayRunRepository.ReadLocationPartPeriods(
            [Part("source-one", 17), Part("source-one", 13)], [Salary()], Employees, "2026-09"));
    }

    [Fact]
    public void AllPartsMustAgreeOnAReferencePresentInThisExactPeriod()
    {
        var first = Part("source-one", 17); var second = Part("source-two", 13);
        first.ReferenceAssignmentId = Id;
        Assert.Throws<InvalidOperationException>(() => PayRunRepository.ReadLocationPartPeriods([first, second], [Salary()], Employees, "2026-09"));
        second.ReferenceAssignmentId = Id;
        Assert.Equal(Id, PayRunRepository.ReadLocationPartPeriods([first, second], [Salary()], Employees, "2026-09")[7].ReferenceAssignmentId);
        first.ReferenceAssignmentId = second.ReferenceAssignmentId = new string('d', 32);
        Assert.Throws<InvalidOperationException>(() => PayRunRepository.ReadLocationPartPeriods([first, second], [Salary()], Employees, "2026-09"));
    }

    [Theory]
    [InlineData("{\"1\":\"20000\"}")]
    [InlineData("{\"1\":20000,\"1\":20001}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("{\"1\":-1}")]
    public void RecurringInputsRejectTextDuplicatesAndMalformedValues(string json)
    {
        var assignment = Salary(); assignment.SalaryInputsJson = json;
        Assert.Throws<InvalidOperationException>(() => PayRunRepository.ReadLocationPartPeriods(
            [Part("source-one", 30)], [assignment], Employees, "2026-09"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"1\":\"Auto\"}")]
    [InlineData("{\"1\":\"Monthly\",\"1\":\"SourceFormulaPerPart\"}")]
    public void ComponentScopeMustBeExplicitAndUnique(string json)
    {
        var assignment = Salary(); assignment.ComponentScopesJson = json;
        Assert.Throws<InvalidOperationException>(() => PayRunRepository.ReadLocationPartPeriods(
            [Part("source-one", 30)], [assignment], Employees, "2026-09"));
    }

    [Fact]
    public void RuleVersionIsCheckedAgainstActualSettingsBeforePayroll()
    {
        var template = JsonNode.Parse("{\"id\":12,\"clientId\":\"42\",\"active\":true,\"lines\":[{\"componentId\":\"1\",\"value\":\"0\",\"calculationType\":\"Formula\",\"formula\":\"BASE*PAYABLE_DAYS/PAYROLL_DAYS\"}]}")!.AsObject();
        var component = JsonNode.Parse("{\"id\":1,\"code\":\"BASE\",\"active\":true,\"category\":\"Earning\",\"calculationType\":\"Fixed Amount\",\"value\":\"0\"}")!.AsObject();
        var assignment = Salary();
        assignment.RulesHash = ExcelPayrollSetupRepository.LocationSettingsRulesHash(template, [component]);
        var setup = new JsonObject { ["salaryStructures"] = new JsonArray(template), ["salaryComponents"] = new JsonArray(component) };
        PayRunRepository.ValidateLocationAssignmentRules([assignment], setup.ToJsonString());
        component["value"] = "999";
        var error = Assert.Throws<InvalidOperationException>(() => PayRunRepository.ValidateLocationAssignmentRules([assignment], setup.ToJsonString()));
        Assert.Contains("changed since", error.Message);
    }
}

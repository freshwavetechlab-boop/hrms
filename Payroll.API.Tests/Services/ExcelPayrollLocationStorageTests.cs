using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollLocationStorageTests
{
    private static ExcelPayrollSetupRepository.LocationAssignmentStorageRow Assignment() => new(
        new string('a', 32), 42, 100, 3, "Assam", "500", 1200,
        "{\"1\":100,\"2\":0}", "{\"1\":\"SourceFormulaPerPart\",\"2\":\"Monthly\"}",
        new string('b', 64), new string('c', 64), new(2026, 10, 28), new string('d', 32),
        new string('e', 32), "row-53", "reviewer@example.test");

    private static ExcelPayrollSetupRepository.LocationAllocationStorageRow Allocation(string sourcePartKey = "row-53") => new(
        42, 100, "2026-09", sourcePartKey, new string('a', 32), 30, 17, 17, null,
        new string('d', 32), new string('e', 32), sourcePartKey, new string('c', 64));

    [Fact]
    public void RepeatedAssignmentDeduplicatesIdenticalContentWithoutChangingItsAuditDate()
    {
        var assignment = Assignment();
        var stored = assignment with { CreatedAt = new(2026, 10, 28, 12, 30, 0) };
        var distinct = ExcelPayrollSetupRepository.DistinctLocationAssignmentStorageRows([assignment, stored]);
        Assert.Single(distinct);
        Assert.Equal(new DateTime(2026, 10, 28), distinct[0].ConfigurationEffectiveFrom);
        Assert.Equal("2026-09", Allocation().PayPeriod);
    }

    [Fact]
    public void StoredJsonObjectOrderDoesNotTurnAnIdenticalImmutableAssignmentIntoAChange()
    {
        var assignment = Assignment();
        var normalized = assignment with { SalaryInputsJson = "{ \"2\": 0, \"1\": 100 }", ComponentScopesJson = "{\"2\":\"Monthly\",\"1\":\"SourceFormulaPerPart\"}" };
        Assert.True(ExcelPayrollSetupRepository.SameLocationAssignmentContent(assignment, normalized));
        Assert.Single(ExcelPayrollSetupRepository.DistinctLocationAssignmentStorageRows([assignment, normalized]));
    }

    [Theory]
    [InlineData("input")]
    [InlineData("scope")]
    [InlineData("rules")]
    [InlineData("location")]
    [InlineData("employee")]
    public void RepeatedAssignmentIdCannotOverwriteDifferentReviewedContent(string change)
    {
        var assignment = Assignment();
        var changed = change switch
        {
            "input" => assignment with { SalaryInputsJson = "{\"1\":200,\"2\":0}" },
            "scope" => assignment with { ComponentScopesJson = "{\"1\":\"Monthly\",\"2\":\"Monthly\"}" },
            "rules" => assignment with { RulesHash = new string('f', 64) },
            "location" => assignment with { WorkLocationId = 4 },
            _ => assignment with { EmployeeId = 101 }
        };
        Assert.False(ExcelPayrollSetupRepository.SameLocationAssignmentContent(assignment, changed));
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.DistinctLocationAssignmentStorageRows([assignment, changed]));
        Assert.Equal(100, assignment.EmployeeId);
        Assert.Equal(3, assignment.WorkLocationId);
    }

    [Fact]
    public void TwoSourcePartsMayReferenceOneAssignmentWithoutLosingTheirAllocationIdentity()
    {
        var first = Allocation();
        var second = Allocation("row-54") with { PresentDays = 13, PayableDays = 13 };
        ExcelPayrollSetupRepository.ValidateLocationAllocationStorageKeys([first, second]);
        Assert.Equal(first.AssignmentId, second.AssignmentId);
        Assert.NotEqual(first.SourcePartKey, second.SourcePartKey);
        Assert.Equal(30, first.PayableDays + second.PayableDays);
    }

    [Fact]
    public void RepeatedSourcePartKeyWithinAnEmployeeMonthIsRejected()
    {
        var first = Allocation();
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ValidateLocationAllocationStorageKeys([first, first with { AssignmentId = new string('f', 32) }]));
    }

    [Fact]
    public void SourcePartKeysAreScopedToTheClientEmployeeAndSavedPayrollPeriod()
    {
        var first = Allocation();
        ExcelPayrollSetupRepository.ValidateLocationAllocationStorageKeys([
            first, first with { EmployeeId = 101 }, first with { PayPeriod = "2026-10" }, first with { ClientId = 43 }
        ]);
    }
}

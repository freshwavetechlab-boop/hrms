using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class HistoricalPeriodSalaryTests
{
    [Fact]
    public void Historical_shared_divisor_cannot_bind_an_unselected_employee_body_cell()
    {
        var selected=new ExcelPayslipRow{Id="one",SourceRow=2};var other=new ExcelPayslipRow{Id="two",SourceRow=3};
        var request=new ExcelPayrollSetupRequest{PayrollDaysColumnIndex=-1,FixedPayrollDays=30,PayrollDaysCell="B3",PayableDaysColumnIndex=1,
            Components=[new(){Category="Earning",ProRata=true,ExpectedColumnIndex=2}]};
        var source=new ExcelPayslipCalculationSource{HeaderRow=1,Columns=[new(){ColumnIndex=1,Kind="info"}],Rows=[new(){SourceRow=3,Cells=[null,new(){Value="30"}]}]};
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateSharedPayrollDaysBinding(request,source,[selected],[selected,other]));
    }

    [Theory]
    [InlineData("2026-09", "2026-09-01", "2026-09-30")]
    [InlineData("2024-02", "2024-02-01", "2024-02-29")]
    public void OnlyCompletePastCalendarMonthsAreAccepted(string month, string first, string last)
    {
        var range = ExcelPayrollSetupRepository.ClosedHistoricalPeriod(month, new(2026, 11, 14));
        Assert.Equal(first, range.From.ToString("yyyy-MM-dd")); Assert.Equal(last, range.To.ToString("yyyy-MM-dd"));
    }
    [Theory]
    [InlineData("2026-11")][InlineData("2026-12")][InlineData("2026-13")][InlineData("1899-12")]
    public void CurrentFutureOrInvalidPeriodsStayInExistingWorkflow(string month) =>
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.ClosedHistoricalPeriod(month, new(2026, 11, 14)));

    [Fact]
    public void EffectiveTargetAndAllFutureHistoryAreRetainedRatherThanAnnualizingEarnedPay()
    {
        var old = new EmployeeRepository.ClosedSalaryHistoryRow { Id = 1, EmployeeId = 10, ClientId = 1, EffectiveFrom = new(2026, 1, 1), AnnualCtc = 120000 };
        var future = new EmployeeRepository.ClosedSalaryHistoryRow { Id = 2, EmployeeId = 10, ClientId = 1, EffectiveFrom = new(2026, 10, 1), AnnualCtc = 900000 };
        var state = new EmployeeRepository.ClosedSalaryHistoryState(1, "current", [old, future], new Dictionary<long, string> { [1] = "old", [2] = "future" });
        Assert.Same(old, EmployeeRepository.RequireClosedSalaryBasis(state, 10, new(2026, 9, 1), new(2026, 9, 30)));
        var after = state with { OriginalRowHashes = new Dictionary<long, string> { [1] = "old", [2] = "future", [3] = "closed" } };
        EmployeeRepository.RequireClosedHistoryPreserved(state, after, [3]);
        Assert.Throws<InvalidOperationException>(() => EmployeeRepository.RequireClosedHistoryPreserved(state, after with { CurrentMasterHash = "changed" }, [3]));
        Assert.Throws<InvalidOperationException>(() => EmployeeRepository.RequireClosedHistoryPreserved(state, after with { OriginalRowHashes = new Dictionary<long, string> { [1] = "old", [2] = "changed", [3] = "closed" } }, [3]));
    }

    [Fact]
    public void AnIntraMonthHistoryChangeCannotBeHiddenByAWholeMonthAssignment()
    {
        var state = new EmployeeRepository.ClosedSalaryHistoryState(1, "current", [new() { EmployeeId = 10, ClientId = 1, EffectiveFrom = new(2026, 9, 15), AnnualCtc = 120000 }], new Dictionary<long, string>());
        Assert.Throws<InvalidOperationException>(() => EmployeeRepository.RequireClosedSalaryBasis(state, 10, new(2026, 9, 1), new(2026, 9, 30)));
    }

    [Fact]
    public void AHistoryBasisEndingInsideTheMonthCannotCoverTheWholeMonth()
    {
        var state = new EmployeeRepository.ClosedSalaryHistoryState(1, "current", [new() { EmployeeId = 10, ClientId = 1, EffectiveFrom = new(2026, 1, 1), EffectiveTo = new(2026, 9, 15), AnnualCtc = 120000 }], new Dictionary<long, string>());
        Assert.Throws<InvalidOperationException>(() => EmployeeRepository.RequireClosedSalaryBasis(state, 10, new(2026, 9, 1), new(2026, 9, 30)));
    }

    [Theory]
    [InlineData("UAN", "000000000000")][InlineData("UAN", "111111111111")]
    [InlineData("Aadhaar", "111111111111")][InlineData("Aadhaar", "123456789012")][InlineData("Aadhaar", "012345678901")]
    public void PlaceholderOrInvalidIdentityCannotAuthorizeMoneyWithoutADurableLink(string header, string value)
    {
        var row = new ExcelPayslipRow { Id = "source", SourceRow = 2, EmployeeCode = "SYN-1", EmployeeName = "Synthetic One" };
        var batch = new ExcelPayslipBatch { Id = new string('a', 32), ClientId = 1, Month = "2026-09", Rows = [row] };
        var source = new ExcelPayslipCalculationSource { Columns = [new() { ColumnIndex = 0, SourceHeader = header, Label = header }], Rows = [new() { SourceRow = 2, Cells = [new() { Value = value }] }] };
        var target = new ExcelPayrollSetupRepository.Identity { Id = 10, ClientId = 1, EmployeeCode = "SYN-1", Name = "Synthetic One", Uan = value, Aadhaar = value, EligibleForMonth = true };
        Assert.Contains(ExcelPayrollSetupRepository.HistoricalIdentityIssues(batch, source, row, target, [target]), error => error.Contains("source-verified"));
    }

    [Fact]
    public void StrongSourceOwnershipDoesNotIgnoreConflictingOrSharedGovernmentFields()
    {
        var source = new ExcelPayslipCalculationSource { Columns = [new() { ColumnIndex = 0, SourceHeader = "UAN", Label = "UAN" }, new() { ColumnIndex = 1, SourceHeader = "Adhaar Number", Label = "Adhaar Number" }],
            Rows = [new() { SourceRow = 2, Cells = [new() { Value = "101234567891" }, new() { Value = "222233334444" }] }] };
        var row = new ExcelPayslipRow { Id = "source", SourceRow = 2, EmployeeCode = "SYN-1", EmployeeName = "Synthetic One", NetPay = 0 };
        var batch = new ExcelPayslipBatch { Id = new string('a', 32), ClientId = 1, Month = "2026-09", Rows = [row] };
        var target = new ExcelPayrollSetupRepository.Identity { Id = 10, ClientId = 1, EmployeeCode = "SYN-1", Name = "Synthetic One", Uan = "101234567891", Aadhaar = "222233334444", EligibleForMonth = true };
        Assert.Empty(ExcelPayrollSetupRepository.HistoricalIdentityIssues(batch, source, row, target, [target]));
        var other = new ExcelPayrollSetupRepository.Identity { Id = 11, ClientId = 1, EmployeeCode = "SYN-2", Name = "Synthetic Two", Uan = "101234567892", Aadhaar = "222233334444", EligibleForMonth = true };
        Assert.Contains(ExcelPayrollSetupRepository.HistoricalIdentityIssues(batch, source, row, target, [target, other]), error => error.StartsWith("aadhaar:"));
        source.Columns.Add(new() { ColumnIndex = 2, SourceHeader = "UAN", Label = "UAN" }); source.Rows[0].Cells.Add(new() { Value = "101234567892" });
        Assert.Contains(ExcelPayrollSetupRepository.HistoricalIdentityIssues(batch, source, row, target, [target, other]), error => error.Contains("conflicting identity columns"));
    }

    [Fact]
    public void ACodeOnlyMatchCannotAuthorizeHistoricalMoney()
    {
        var row = new ExcelPayslipRow { Id = "source", SourceRow = 2, EmployeeCode = "SYN-1", EmployeeName = "Synthetic One", NetPay = 0 };
        var batch = new ExcelPayslipBatch { Id = new string('a', 32), ClientId = 1, Month = "2026-09", Rows = [row] };
        var source = new ExcelPayslipCalculationSource { Rows = [new() { SourceRow = 2 }] };
        var target = new ExcelPayrollSetupRepository.Identity { Id = 10, ClientId = 1, EmployeeCode = "SYN-1", Name = "Synthetic One", EligibleForMonth = true };
        Assert.Contains(ExcelPayrollSetupRepository.HistoricalIdentityIssues(batch, source, row, target, [target]), error => error.Contains("source-verified"));
    }

    [Fact]
    public void DurableRenamedEmployeeLinkIsAcceptedButDuplicateContradictoryLinksAreNot()
    {
        var row = new ExcelPayslipRow { Id = "source", SourceRow = 2, EmployeeCode = "OLD-SYN-1", EmployeeName = "Old synthetic name" };
        var batch = new ExcelPayslipBatch { Id = new string('a', 32), ClientId = 1, Month = "2026-09", Rows = [row] };
        var source = new ExcelPayslipCalculationSource { Rows = [new() { SourceRow = 2 }] };
        var target = new ExcelPayrollSetupRepository.Identity { Id = 10, ClientId = 1, EmployeeCode = "NEW-SYN-1", Name = "Current synthetic name", EligibleForMonth = true };
        var witness = Payroll.API.Services.ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch, row, source);
        batch.ProfileLinks = [new(row.Id, row.SourceRow, target.Id, witness)];
        Assert.Empty(ExcelPayrollSetupRepository.HistoricalIdentityIssues(batch, source, row, target, [target]));
        batch.ProfileLinks.Add(new(row.Id, row.SourceRow, 11, witness));
        Assert.Contains(ExcelPayrollSetupRepository.HistoricalIdentityIssues(batch, source, row, target, [target]), error => error.Contains("source-verified"));
    }

    [Theory]
    [InlineData(false, "All", true)][InlineData(true, "All", false)][InlineData(false, "Selected", false)]
    public void HistoricalManagementRequiresTheExistingGlobalOperatorAndClientScope(bool changePassword, string scope, bool expected)
    {
        var actor = new AuthUser { Id = 1, IsActive = true, MustChangePassword = changePassword, ClientAccessMode = scope,
            Roles = ["super_admin"], Permissions = ["payroll.run", "employees.manage"] };
        Assert.Equal(expected, ExcelPayrollSetupRepository.CanManageHistoricalSalary(actor, 10));
        actor.Permissions.Remove("employees.manage"); Assert.False(ExcelPayrollSetupRepository.CanManageHistoricalSalary(actor, 10));
    }
}

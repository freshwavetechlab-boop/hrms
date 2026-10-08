using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipVarianceTests
{
    private static ExcelPayslipRow Row(string id, string code = "E001", string name = "Aman Singh", string location = "Ludhiana", decimal amount = 1000) => new()
    {
        Id = id, EmployeeCode = code, EmployeeName = name,
        Information = location.Length == 0 ? [] : [new() { Label = "Work location", Value = location }],
        Earnings = [new() { Label = "Basic", Amount = amount }],
        Deductions = [new() { Label = "PF", Amount = 120 }],
        EmployerContributions = [new() { Label = "Employer PF", Amount = 130 }],
        NetPay = amount - 120
    };

    private static ExcelPayslipBatch Batch(string month, params ExcelPayslipRow[] rows) => new()
    {
        Id = "batch-" + month, ClientId = 20, Month = month, Rows = rows.ToList()
    };

    private static ExcelPayslipVarianceResult Compare(ExcelPayslipRow current, ExcelPayslipRow previous) =>
        ExcelPayslipVarianceService.Compare(Batch("2026-10", current), Batch("2026-09", previous));

    [Fact]
    public void ComparesEveryFinancialCategoryAndPreservesRawPrecisionWithoutMutatingSources()
    {
        var current = Row("current", amount: 1000.011m); var previous = Row("previous");
        current.Deductions[0].Amount = 125;
        current.EmployerContributions[0].Amount = 135;
        var snapshot = JsonSerializer.Serialize(new[] { current, previous });
        var result = Compare(current, previous);
        Assert.Equal("batch-2026-10", result.CurrentBatchId);
        Assert.Equal("batch-2026-09", result.PreviousBatchId);
        Assert.Equal(1, result.MatchedEmployees);
        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(0.011m, result.Rows.Single(x => x.Category == "Earnings").Difference);
        Assert.Equal(5, result.Rows.Single(x => x.Category == "Deductions").Difference);
        Assert.Equal(5, result.Rows.Single(x => x.Category == "Employer contributions").Difference);
        Assert.Equal(0.011m, result.Rows.Single(x => x.Category == "Net pay").Difference);
        Assert.All(result.Rows, x => { Assert.Equal("previous", x.PreviousRowId); Assert.Equal("current", x.CurrentRowId); });
        Assert.Equal(snapshot, JsonSerializer.Serialize(new[] { current, previous }));
    }

    [Fact]
    public void UniqueCodeMatchesChangedNameOrLocationBeforeFallbackMatching()
    {
        var result = Compare(Row("now", " e001 ", "Updated name", "Chandigarh"), Row("old"));
        Assert.Equal(1, result.MatchedEmployees);
        Assert.Equal(0, result.ReviewEmployees);
        Assert.Contains(result.Rows, x => x.Category == "Employee" && x.Status == "Changed");
        Assert.Contains(result.Rows, x => x.Category == "Net pay" && x.Status == "Unchanged");
    }

    [Fact]
    public void NameAndWorkLocationIdentifySameNamesInDifferentLocations()
    {
        var current = Batch("2026-10", Row("a", "", location: "Amritsar", amount: 2000), Row("b", "", location: "Ludhiana", amount: 1000));
        var previous = Batch("2026-09", Row("b-old", "", location: "Ludhiana", amount: 1000), Row("a-old", "", location: "Amritsar", amount: 2000));
        current.Rows[0].Information[0].Label = "Location Name (Tehsil/Sub Tehsil)";
        current.Rows[0].Information[0].Value = "  amritsar  ";
        current.Rows[0].EmployeeName = "  aman   singh ";
        var result = ExcelPayslipVarianceService.Compare(current, previous);
        Assert.Equal(2, result.MatchedEmployees);
        Assert.Equal("a-old", result.Rows.Single(x => x.CurrentRowId == "a").PreviousRowId);
        Assert.All(result.Rows, x => Assert.Equal("Unchanged", x.Status));
    }

    [Fact]
    public void NameAloneNeverMatchesOrClaimsANewEmployee()
    {
        var result = Compare(Row("now", "", location: ""), Row("old", "", location: ""));
        Assert.Equal(0, result.MatchedEmployees);
        Assert.Equal(2, result.ReviewEmployees);
        Assert.Equal(0, result.NewEmployees);
        Assert.Equal(0, result.MissingEmployees);
        Assert.All(result.Rows, x => Assert.Null(x.Difference));
    }

    [Fact]
    public void FallbackCanMatchWhenOnlyOneMonthHasAnEmployeeCode()
    {
        var result = Compare(Row("now"), Row("old", ""));
        Assert.Equal(1, result.MatchedEmployees);
        Assert.Equal("Unchanged", Assert.Single(result.Rows).Status);
    }

    [Fact]
    public void ConflictingCodesAreNotMergedByMatchingNameAndLocation()
    {
        var result = Compare(Row("now", "E002"), Row("old", "E001"));
        Assert.Equal(0, result.MatchedEmployees);
        Assert.Equal(2, result.ReviewEmployees);
        Assert.All(result.Rows, x => Assert.Contains("codes differ", x.Message));
    }

    [Fact]
    public void DuplicateCodesNeverFallBackToAnApparentlyMatchingName()
    {
        var result = ExcelPayslipVarianceService.Compare(
            Batch("2026-10", Row("a"), Row("b", name: "Another employee", location: "Another place")),
            Batch("2026-09", Row("old")));
        Assert.Equal(0, result.MatchedEmployees);
        Assert.Equal(3, result.ReviewEmployees);
        Assert.All(result.Rows, x => Assert.Equal("Review", x.Status));
    }

    [Fact]
    public void DuplicateNameAndLocationAreAmbiguousWithoutCodes()
    {
        var result = ExcelPayslipVarianceService.Compare(
            Batch("2026-10", Row("a", ""), Row("b", "")), Batch("2026-09", Row("old", "")));
        Assert.Equal(0, result.MatchedEmployees);
        Assert.Equal(3, result.ReviewEmployees);
    }

    [Fact]
    public void CodeMatchedRowsStillParticipateInFallbackAmbiguityDetection()
    {
        var result = ExcelPayslipVarianceService.Compare(
            Batch("2026-10", Row("coded"), Row("uncoded", "")), Batch("2026-09", Row("old")));
        Assert.Equal(1, result.MatchedEmployees);
        Assert.Equal(1, result.ReviewEmployees);
        Assert.Equal("uncoded", result.Rows.Single(x => x.Status == "Review").CurrentRowId);
    }

    [Fact]
    public void NewAndMissingEmployeesAreSeparateAndDoNotInventZeroSalary()
    {
        var result = Compare(Row("new", "E002", "New person", "Patiala"), Row("old"));
        Assert.Equal(1, result.NewEmployees); Assert.Equal(1, result.MissingEmployees);
        Assert.Null(result.Rows.Single(x => x.Status == "New employee").PreviousAmount);
        Assert.Null(result.Rows.Single(x => x.Status == "Missing employee").CurrentAmount);
        Assert.All(result.Rows, x => Assert.Null(x.Difference));
    }

    [Fact]
    public void FlagsNewAndRemovedComponentsEvenWhenAmountsAreZero()
    {
        var current = Row("now"); var previous = Row("old");
        current.Earnings.Add(new() { Label = "Allowance", Amount = 0 });
        previous.Deductions.Add(new() { Label = "Recovery", Amount = 0 });
        var result = Compare(current, previous);
        Assert.Contains(result.Rows, x => x.Component == "Allowance" && x.Status == "Added component" && x.Difference == 0);
        Assert.Contains(result.Rows, x => x.Component == "Recovery" && x.Status == "Removed component" && x.Difference == 0);
    }

    [Fact]
    public void NormalizesComponentLabelsAndReportsCategoryChangeExplicitly()
    {
        var current = Row("now"); var previous = Row("old");
        current.Deductions.RemoveAt(0);
        current.Earnings.Add(new() { Label = " P.F. ", Amount = 120 });
        var result = Compare(current, previous);
        var change = result.Rows.Single(x => x.Status == "Category changed");
        Assert.Equal("Deductions → Earnings", change.Category);
        Assert.Equal(0, change.Difference);
    }

    [Fact]
    public void DoesNotSumDuplicateNormalizedComponentLabels()
    {
        var current = Row("now"); var previous = Row("old");
        current.Deductions.Add(new() { Label = "P.F.", Amount = 10 });
        var result = Compare(current, previous);
        var review = result.Rows.Single(x => x.Status == "Review");
        Assert.Equal("PF", review.Component);
        Assert.Null(review.PreviousAmount); Assert.Null(review.CurrentAmount); Assert.Null(review.Difference);
        Assert.Equal(1, result.ReviewEmployees);
    }

    [Fact]
    public void SameLabelInDistinctCategoriesCanCompareByExactCategory()
    {
        var current = Row("now"); var previous = Row("old");
        current.EmployerContributions[0].Label = previous.EmployerContributions[0].Label = "PF";
        current.Deductions[0].Amount = 121;
        current.EmployerContributions[0].Amount = 131;
        var result = Compare(current, previous);
        Assert.Equal(0, result.ReviewEmployees);
        Assert.Equal(2, result.Rows.Count(x => x.Component == "PF" && x.Status == "Changed"));
    }

    [Fact]
    public void ConflictingLocationsDoNotFallBackToName()
    {
        var current = Row("now", ""); var previous = Row("old", "");
        current.Information.Add(new() { Label = "Location", Value = "Another location" });
        var result = Compare(current, previous);
        Assert.Equal(0, result.MatchedEmployees);
        Assert.Contains(result.Rows, x => x.CurrentRowId == "now" && x.Status == "Review");
    }

    [Fact]
    public void InformationChangesIncludeRatesAndAttendanceEvenWhenNetPayIsUnchanged()
    {
        var current = Row("now"); var previous = Row("old");
        previous.Information.Add(new() { Label = "Monthly rate", Value = "18000" });
        previous.Information.Add(new() { Label = "Days attended", Value = "30" });
        current.Information.Add(new() { Label = "Monthly rate", Value = "20000" });
        current.Information.Add(new() { Label = "Days attended", Value = "27.5" });
        var result = Compare(current, previous);
        Assert.Equal(2000, result.Rows.Single(x => x.Component == "Monthly rate").Difference);
        var attendance = result.Rows.Single(x => x.Component == "Days attended");
        Assert.Equal("Information", attendance.Category); Assert.Equal(-2.5m, attendance.Difference);
        Assert.Equal("Previous: 30. Current: 27.5.", attendance.Message);
        Assert.Equal("Unchanged", result.Rows.Single(x => x.Category == "Net pay").Status);
    }

    [Fact]
    public void NewRemovedAndChangedTextInformationPreservesExactValues()
    {
        var current = Row("now"); var previous = Row("old");
        previous.Information.Add(new() { Label = "Account No", Value = "00123456789" });
        current.Information.Add(new() { Label = "Account No", Value = "00987654321" });
        previous.Information.Add(new() { Label = "Old department", Value = "Operations" });
        current.Information.Add(new() { Label = "New department", Value = "Finance" });
        var result = Compare(current, previous);
        var account = result.Rows.Single(x => x.Component == "Account No");
        Assert.Null(account.PreviousAmount); Assert.Null(account.CurrentAmount); Assert.Null(account.Difference);
        Assert.Equal("Previous: 00123456789. Current: 00987654321.", account.Message);
        Assert.Contains(result.Rows, x => x.Component == "Old department" && x.Status == "Removed component");
        Assert.Contains(result.Rows, x => x.Component == "New department" && x.Status == "Added component");
    }

    [Fact]
    public void DuplicateInformationLabelsNeedReviewRatherThanChoosingOneValue()
    {
        var current = Row("now"); var previous = Row("old");
        current.Information.Add(new() { Label = "Rate", Value = "18000" });
        current.Information.Add(new() { Label = " RATE ", Value = "20000" });
        previous.Information.Add(new() { Label = "Rate", Value = "17000" });
        var result = Compare(current, previous);
        var review = result.Rows.Single(x => x.Status == "Review");
        Assert.Equal("Information", review.Category); Assert.Null(review.Difference);
        Assert.Equal(1, result.ReviewEmployees);
    }

    [Fact]
    public void UnchangedInformationAndLocationAliasChangesDoNotAddNoise()
    {
        var current = Row("now"); var previous = Row("old");
        current.Information[0].Label = "Location Name (Tehsil/Sub Tehsil)";
        current.Information.Add(new() { Label = "Rate", Value = " 18000 " });
        previous.Information.Add(new() { Label = "Rate", Value = "18000" });
        Assert.Equal("Net pay", Assert.Single(Compare(current, previous).Rows).Category);
    }

    [Theory]
    [InlineData("14129.499999999998", "14129.5")]
    [InlineData("1000.004999999999", "1000.005")]
    [InlineData("1000.001", "1000.002")]
    public void FloatingCacheTailsAndSubPaiseChangesDoNotCreateVisibleZeroVariances(string cached, string calculated)
    {
        var oldAmount = decimal.Parse(cached, System.Globalization.CultureInfo.InvariantCulture);
        var newAmount = decimal.Parse(calculated, System.Globalization.CultureInfo.InvariantCulture);
        var current = Row("now", amount: newAmount); var previous = Row("old", amount: oldAmount);
        current.NetPay = newAmount; previous.NetPay = oldAmount;
        var net = Assert.Single(Compare(current, previous).Rows);
        Assert.Equal("Unchanged", net.Status); Assert.Equal("Net pay", net.Category);
        Assert.Equal(oldAmount, net.PreviousAmount); Assert.Equal(newAmount, net.CurrentAmount);
        Assert.Equal(newAmount - oldAmount, net.Difference);
        Assert.Equal(oldAmount, previous.NetPay); Assert.Equal(newAmount, current.NetPay);
    }

    [Fact]
    public void AVisiblePaiseChangeStillProducesFinancialVariance()
    {
        var result = Compare(Row("now", amount: 1000.01m), Row("old", amount: 1000m));
        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, row => { Assert.Equal("Changed", row.Status); Assert.Equal(0.01m, row.Difference); });
    }

    [Fact]
    public void NumericInformationFormattingAndBinaryTailsDoNotHideRealAttendanceChanges()
    {
        var current = Row("now"); var previous = Row("old");
        current.Information.Add(new() { Label = "Days", Value = "20.0" });
        previous.Information.Add(new() { Label = "Days", Value = "20" });
        current.Information.Add(new() { Label = "Bonus", Value = "14129.5" });
        previous.Information.Add(new() { Label = "Bonus", Value = "14129.499999999998" });
        Assert.Equal("Net pay", Assert.Single(Compare(current, previous).Rows).Category);
        current.Information[1].Value = "20.001";
        Assert.Contains(Compare(current, previous).Rows, row => row.Component == "Days" && row.Difference == .001m);
    }

    [Theory]
    [InlineData("2027-01", "2026-12")]
    [InlineData("2026-03", "2026-02")]
    public void PriorMonthCrossesYearAndMonthBoundaries(string current, string previous)
    {
        var result = ExcelPayslipVarianceService.Compare(Batch(current, Row("now")), Batch(previous, Row("old")));
        Assert.Equal(previous, result.PreviousMonth); Assert.Equal(current, result.CurrentMonth);
        Assert.Equal(1, result.MatchedEmployees);
    }

    [Theory]
    [InlineData("2026-10", "2026-08")]
    [InlineData("2026-10", "2026-10")]
    [InlineData("2026-10", "2026-11")]
    [InlineData("2026-13", "2026-12")]
    [InlineData("0001-01", "0001-01")]
    public void RejectsAnythingExceptPriorCalendarMonth(string current, string previous) =>
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipVarianceService.Compare(Batch(current), Batch(previous)));

    [Fact]
    public void RejectsCrossClientComparison()
    {
        var current = Batch("2026-10"); var previous = Batch("2026-09"); previous.ClientId = 21;
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipVarianceService.Compare(current, previous));
    }
}

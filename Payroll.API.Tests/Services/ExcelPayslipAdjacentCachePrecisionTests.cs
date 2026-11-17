using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipAdjacentCachePrecisionTests
{
    private static (ExcelPayslipCalculationSource Source, ExcelPayslipBatch Batch) Fixture(int sign = 1)
    {
        var cache = sign * 9289.4516129032254m;
        var saved = sign * 9289.451612903224m;
        ExcelPayslipCalculationCell Cell(string value, string? formula = null, string type = "Number") =>
            new() { Value = value, FormulaText = formula, DataType = type };
        ExcelPayslipCalculationColumn Col(int index, string label, string kind) =>
            new() { ColumnIndex = index, Label = label, SourceHeader = label, Kind = kind };
        var source = new ExcelPayslipCalculationSource
        {
            SheetName = "Salary", HeaderRow = 1, ColumnCount = 7,
            Columns = [Col(0, "Name", "employeeName"), Col(1, "Wages", "earning"), Col(2, "Net", "netPay"),
                Col(3, "UAN", "info"), Col(4, "Payable", "info"), Col(5, "Monthly rate", "ignore"), Col(6, "Divisor", "ignore")],
            Rows = [new() { SourceRow = 2, Cells = [Cell("Anonymous employee", type: "Text"),
                Cell(cache.ToString(CultureInfo.InvariantCulture), "F2/G2*E2"), Cell(cache.ToString(CultureInfo.InvariantCulture), "B2"),
                Cell("001234567890", type: "Text"), Cell("30"), Cell(cache.ToString(CultureInfo.InvariantCulture)), Cell("30")] }]
        };
        var batch = new ExcelPayslipBatch
        {
            Id = new string('a', 32), ClientId = 12, ClientName = "Synthetic client", Month = "2026-09",
            SheetName = "Salary", HeaderRow = 1,
            Rows = [new() { Id = "source-2", SourceRow = 2, EmployeeName = "Anonymous employee", NetPay = saved,
                Earnings = [new() { Label = "Wages", Amount = saved }],
                Information = [new() { Label = "UAN", Value = "001234567890" }, new() { Label = "Payable", Value = "30" }] }]
        };
        return (source, batch);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void ActualSourceValidatorAcceptsOneIeeeStepAcrossG15WithoutChangingSourceOrSalary(int sign)
    {
        var (source, batch) = Fixture(sign); var before = JsonSerializer.Serialize(new { source, batch });
        var cache = decimal.Parse(source.Rows[0].Cells[1]!.Value, CultureInfo.InvariantCulture);
        var saved = batch.Rows[0].Earnings[0].Amount;
        Assert.NotEqual(saved, cache);
        Assert.NotEqual(saved.ToString("G15", CultureInfo.InvariantCulture), cache.ToString("G15", CultureInfo.InvariantCulture));
        Assert.NotEqual(double.Parse(saved.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            double.Parse(cache.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        Assert.True(ExcelPayslipCalculationService.SameAdjacentCachedAmount(saved, cache));
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source, batch));
        Assert.Equal(before, JsonSerializer.Serialize(new { source, batch }));
    }

    [Fact]
    public void NextMonthCalculationUsesOriginalFormulaAndChangedDaysWithoutRewritingSavedBatch()
    {
        var (source, batch) = Fixture(); var before = JsonSerializer.Serialize(new { source, batch });
        var result = ExcelPayslipCalculationService.Calculate(source, batch, new()
        {
            Month = "2026-10", AttendanceColumnIndex = 4,
            Attendance = [new() { RowId = "source-2", Days = 15 }]
        });
        Assert.Equal("2026-10", result.Month);
        Assert.Equal(4644.73m, decimal.Round(result.Rows[0].NetPay, 2, MidpointRounding.AwayFromZero));
        Assert.Equal("001234567890", result.Rows[0].Information.Single(i => i.Label == "UAN").Value);
        Assert.Equal(before, JsonSerializer.Serialize(new { source, batch }));
    }

    [Theory]
    [InlineData("9289.461612903224")]
    [InlineData("9289.451612913224")]
    [InlineData("-9289.451612903224")]
    [InlineData("0")]
    public void ActualSourceValidatorRejectsRealMoneyDifferencesAndSignOrMissingValueSubstitution(string value)
    {
        var (source, batch) = Fixture(); var number = decimal.Parse(value, CultureInfo.InvariantCulture);
        batch.Rows[0].Earnings[0].Amount = number; batch.Rows[0].NetPay = number;
        Assert.Contains("does not match its saved amount", ExcelPayslipCalculationService.ValidateSource(source, batch));
    }

    [Fact]
    public void AmountRepresentationDoesNotNormalizeOrChangeIdentifierInformation()
    {
        var (source, batch) = Fixture(); batch.Rows[0].Information[0].Value = "1234567890";
        Assert.Contains("UAN does not match its saved information", ExcelPayslipCalculationService.ValidateSource(source, batch));
    }

    [Fact]
    public void NewAdjacentBranchRejectsCurrencyBoundaryWiderStepsAndLargeAbsoluteDifferences()
    {
        decimal D(double number) => decimal.Parse(number.ToString("G17", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        Assert.False(ExcelPayslipCalculationService.SameAdjacentCachedAmount(D(Math.BitDecrement(10.005)), D(10.005)));
        var cache = double.Parse("9289.4516129032254", CultureInfo.InvariantCulture);
        Assert.False(ExcelPayslipCalculationService.SameAdjacentCachedAmount(D(Math.BitDecrement(Math.BitDecrement(cache))), D(cache)));
        Assert.False(ExcelPayslipCalculationService.SameAdjacentCachedAmount(D(Math.BitIncrement(1_000_000_000d)), D(1_000_000_000d)));
        Assert.False(ExcelPayslipCalculationService.SameAdjacentCachedAmount(0m, 0.000000000000001m));
    }
}

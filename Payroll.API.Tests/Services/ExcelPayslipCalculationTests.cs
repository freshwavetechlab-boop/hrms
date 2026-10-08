using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipCalculationTests
{
    private static (ExcelPayslipCalculationSource Source, ExcelPayslipBatch Batch, ExcelPayslipCalculateRequest Request) Example()
    {
        var source = new ExcelPayslipCalculationSource
        {
            SheetName = "Salary", HeaderRow = 2, ColumnCount = 11,
            Columns = [Column(0, "Name", "employeeName"), Column(1, "Rate", "info"), Column(2, "Attendance", "info"),
                Column(3, "Wages", "earning"), Column(4, "PF", "deduction"), Column(5, "ESI", "deduction"),
                Column(6, "Net", "netPay"), Column(7, "Employer PF", "employer"), Column(8, "Employer ESI", "employer"), Column(9, "Reference", "ignore")],
            Rows = [new() { SourceRow = 1, Cells = [Cell("30")] }, new() { SourceRow = 3,
                Cells = [Cell("Person"), Cell("18000"), Cell("30"), Cell("18000", "B3/$A$1*C3"), Cell("2160", "D3*12%"),
                    Cell("135", "D3*0.75%"), Cell("15705", "D3-E3-F3"), Cell("2340", "D3*13%"), Cell("585", "D3*3.25%"),
                    new() { Value = "#REF!", FormulaText = "VLOOKUP(A3,[1]Other!A1:A9,1,0)", Error = "#REF!" }, Cell("0") ] }]
        };
        var batch = new ExcelPayslipBatch
        {
            Id = "0123456789abcdef0123456789abcdef", ClientId = 12, ClientName = "Client", Month = "2026-09", SheetName = "Salary", HeaderRow = 2, SourceFileName = "Salary.xlsx",
            Rows = [new() { Id = "row-3", SourceRow = 3, EmployeeName = "Person", NetPay = 15705,
                Information = [new() { Label = "Rate", Value = "18000" }, new() { Label = "Attendance", Value = "30" }],
                Earnings = [new() { Label = "Wages", Amount = 18000 }], Deductions = [new() { Label = "PF", Amount = 2160 }, new() { Label = "ESI", Amount = 135 }],
                EmployerContributions = [new() { Label = "Employer PF", Amount = 2340 }, new() { Label = "Employer ESI", Amount = 585 }] }]
        };
        var request = new ExcelPayslipCalculateRequest { Month = "2026-10", AttendanceColumnIndex = 2, MonthDaysCell = "A1", MonthDays = 31,
            Attendance = [new() { RowId = "row-3", Days = 15.5m }] };
        return (source, batch, request);
    }
    private static ExcelPayslipCalculationCell Cell(string value, string? formula = null) => new() { Value = value, FormulaText = formula };
    private static ExcelPayslipCalculationColumn Column(int index, string label, string kind) => new() { ColumnIndex = index, Label = label, SourceHeader = label, Kind = kind };

    [Fact]
    public void PlrsArithmeticHalfDaysAndContributionsRecalculateWithoutChangingTheOriginal()
    {
        var (source, batch, request) = Example(); var original = JsonSerializer.Serialize(new { source, batch });
        var result = ExcelPayslipCalculationService.Calculate(source, batch, request);
        var row = Assert.Single(result.Rows);
        Assert.Equal(9000, row.Earnings[0].Amount); Assert.Equal(1080, row.Deductions[0].Amount);
        Assert.Equal(67.5m, row.Deductions[1].Amount); Assert.Equal(7852.5m, row.NetPay);
        Assert.Equal(1170m, row.EmployerContributions[0].Amount); Assert.Equal(292.5m, row.EmployerContributions[1].Amount);
        Assert.Equal("15.5", row.Information[1].Value); Assert.Equal("2026-10", result.Month); Assert.Empty(result.Id);
        Assert.Equal(batch.Id, result.CalculationSource!.SourceBatchId);
        Assert.Equal(original, JsonSerializer.Serialize(new { source, batch }));
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(result.CalculationSource, result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SimpleLayoutIsInheritedByAnotherMonthAndLegacySerializationStaysUnchanged(bool simpleLayout)
    {
        var (source, batch, request) = Example();
        batch.SimpleLayout = simpleLayout;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var serialized = JsonSerializer.Serialize(batch, options);
        Assert.Equal(simpleLayout, serialized.Contains("\"simpleLayout\":true", StringComparison.Ordinal));
        if (!simpleLayout) Assert.DoesNotContain("simpleLayout", serialized, StringComparison.Ordinal);
        var result = ExcelPayslipCalculationService.Calculate(source, batch, request);
        Assert.Equal(simpleLayout, result.SimpleLayout);
        Assert.Equal(serialized, JsonSerializer.Serialize(batch, options));
    }

    [Fact]
    public void APreviousMonthOrExplicitZeroAttendanceIsAllowedAndStaticOverridesArePreserved()
    {
        var (source, batch, request) = Example(); request.Month = "2026-08"; request.Attendance[0].Days = 0;
        request.Overrides["B3"] = 20000;
        source.Rows[1].Cells[4] = Cell("25"); batch.Rows[0].Deductions[0].Amount = 25;
        var result = ExcelPayslipCalculationService.Calculate(source, batch, request);
        Assert.Equal(0, result.Rows[0].Earnings[0].Amount); Assert.Equal(25, result.Rows[0].Deductions[0].Amount);
        Assert.Equal(-25, result.Rows[0].NetPay); Assert.Equal("20000", result.Rows[0].Information[0].Value);
    }

    [Theory]
    [InlineData("ROUND(12.345,2)", "12.35")]
    [InlineData("ROUNDDOWN(-12.345,2)", "-12.34")]
    [InlineData("ROUNDUP(-12.341,2)", "-12.35")]
    [InlineData("ROUND(125,-1)", "130")]
    [InlineData("IF(C3>0,MIN(B3,100)+MAX(2,4),1/0)", "104")]
    [InlineData("IF(AND(C3>0,OR(FALSE,TRUE)),SUM(1,2,3),0)", "6")]
    [InlineData("SUM(B3:C3)", "18015.5")]
    [InlineData("'Salary'!B3*1.2E-2", "216")]
    [InlineData("ABS(-17.5)", "17.5")]
    public void SupportedExpressionsUseExcelNumericSemantics(string formula, string expected)
    {
        var (source, batch, request) = Example(); source.Rows[1].Cells[3]!.FormulaText = $"({formula})+C3*0";
        var result = ExcelPayslipCalculationService.Calculate(source, batch, request);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result.Rows[0].Earnings[0].Amount);
    }

    [Theory]
    [InlineData("D3+1", "Circular")]
    [InlineData("VLOOKUP(B3,B3:C3,1,0)", "Unsupported function")]
    [InlineData("Other!B3", "Cross-worksheet")]
    [InlineData("[1]Salary!B3", "Unsupported")]
    [InlineData("B3/0", "cannot be calculated")]
    [InlineData("B99", "missing")]
    [InlineData("B3 garbage", "Unsupported formula syntax")]
    [InlineData("UNKNOWN", "Unsupported cell reference")]
    [InlineData("ROUND(B3,9)", "precision")]
    [InlineData("B3*9999999999999", "limit")]
    [InlineData("AND(FALSE,1/0)", "cannot be calculated")]
    [InlineData("OR(TRUE,1/0)", "cannot be calculated")]
    public void InvalidDependenciesNeverFallBackToCachedSalary(string formula, string error)
    {
        var (source, batch, request) = Example(); source.Rows[1].Cells[3]!.FormulaText = formula;
        Assert.Contains(error, Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.Calculate(source, batch, request)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASharedFormulaImportErrorIsBlockedOnlyWhenItIsASalaryDependency()
    {
        var (source, batch, request) = Example(); source.Rows[1].Cells[9]!.CalculationError = "Shared formula could not be expanded";
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source, batch));
        ExcelPayslipCalculationService.Calculate(source, batch, request);
        source.Rows[1].Cells[3]!.CalculationError = "Shared formula could not be expanded";
        Assert.Contains("Shared formula", Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.Calculate(source, batch, request)).Message);
    }

    [Fact]
    public void AFormulaCannotBeReplacedThroughInputOverrides()
    {
        var (source, batch, request) = Example(); request.Overrides["D3"] = 1;
        Assert.Contains("input cell", Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.Calculate(source, batch, request)).Message);
        request.Overrides.Clear(); request.Overrides["C3"] = 1;
        Assert.Contains("more than once", Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.Calculate(source, batch, request)).Message);
    }

    [Fact]
    public void AttendanceMustIncludeEveryUniqueKnownEmployeeAndBeWithinTheDivisor()
    {
        var (source, batch, request) = Example(); request.Attendance.Clear();
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.Calculate(source, batch, request));
        request.Attendance = [new() { RowId = "not-an-employee", Days = 1 }];
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.Calculate(source, batch, request));
        request.Attendance = [new() { RowId = "row-3", Days = 30.5m }]; request.MonthDays = 30;
        Assert.Contains("exceed", Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.Calculate(source, batch, request)).Message);
    }

    [Fact]
    public void AnUnrelatedSourceOrChangedMappingCannotBeAttachedToAnExistingBatch()
    {
        var (source, batch, _) = Example(); source.Rows[1].Cells[0]!.Value = "Different employee";
        Assert.Contains("employee", ExcelPayslipCalculationService.ValidateSource(source, batch));
        source.Rows[1].Cells[0]!.Value = "Person"; source.Rows[1].Cells[3]!.Value = "999";
        Assert.Contains("amount", ExcelPayslipCalculationService.ValidateSource(source, batch));
        source.Rows[1].Cells[3]!.Value = "18000"; source.Columns.RemoveAt(8);
        Assert.Contains("component mapping", ExcelPayslipCalculationService.ValidateSource(source, batch));
    }

    [Fact]
    public void RecalculatedSourceCanBeUsedAgainWithoutReuploadingTheWorkbook()
    {
        var (source, batch, request) = Example(); var first = ExcelPayslipCalculationService.Calculate(source, batch, request);
        first.Id = "11111111111111111111111111111111";
        request.Month = "2026-11"; request.MonthDays = 30; request.Attendance[0].Days = 30;
        var second = ExcelPayslipCalculationService.Calculate(first.CalculationSource!, first, request);
        Assert.Equal(18000, second.Rows[0].Earnings[0].Amount); Assert.Equal(15705, second.Rows[0].NetPay);
    }

    [Fact]
    public void NumericInformationFormulasAlsoUpdateAndUnsupportedBonusFormulasFailClearly()
    {
        var (source, batch, request) = Example();
        source.Columns[9] = Column(9, "Bonus", "info"); source.Rows[1].Cells[9] = Cell("180", "D3*1%");
        batch.Rows[0].Information.Add(new() { Label = "Bonus", Value = "180" });
        var result = ExcelPayslipCalculationService.Calculate(source, batch, request);
        Assert.Equal("90", result.Rows[0].Information[2].Value);
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(result.CalculationSource!, result));
        source.Rows[1].Cells[9]!.FormulaText = "VLOOKUP(B3,B3:C3,1,0)";
        Assert.Contains("Unsupported function", Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.Calculate(source, batch, request)).Message);
    }

    [Theory]
    [InlineData("Bank Account Number")]
    [InlineData("UAN")]
    [InlineData("Aadhaar Number")]
    [InlineData("ESIC No")]
    [InlineData("Mobile")]
    public void NumericPersonalIdentifiersCannotBeOverridden(string label)
    {
        var (source, batch, request) = Example(); source.Columns[9] = Column(9, label, "info"); source.Rows[1].Cells[9] = Cell("001234567890");
        batch.Rows[0].Information.Add(new() { Label = label, Value = "001234567890" }); request.Overrides["J3"] = 12;
        Assert.Contains("identity", Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.Calculate(source, batch, request)).Message);
    }

    [Fact]
    public void OptionalBlankColumnsCanBeMissingFromTheSparseExcelSource()
    {
        var (source, batch, request) = Example(); source.Columns.Add(Column(10, "Email", "email")); source.Rows[1].Cells[10] = null;
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source, batch));
        Assert.Empty(ExcelPayslipCalculationService.Calculate(source, batch, request).Rows[0].Email);
        source.Columns[10] = Column(10, "Note", "info"); batch.Rows[0].Information.Add(new() { Label = "Note", Value = "" });
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source, batch));
    }

    [Fact]
    public void SourceCacheSpellingCanDifferOnlyAtTheSameIeeeNumber()
    {
        var (source, batch, _) = Example(); source.Rows[1].Cells[3]!.Value = "12.000000000000001"; batch.Rows[0].Earnings[0].Amount = 12.000000000000002m;
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source, batch));
        batch.Rows[0].Earnings[0].Amount = 12.00000001m;
        Assert.Contains("amount", ExcelPayslipCalculationService.ValidateSource(source, batch));
    }

    [Fact]
    public void DuplicateComponentLabelsStillMatchEveryOrderedComponentExactlyOnce()
    {
        var (source, batch, _) = Example(); source.Columns[5].Label = "PF"; batch.Rows[0].Deductions[1].Label = "PF";
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source, batch));
        source.Rows[1].Cells[5]!.Value = "2160";
        Assert.Contains("amount", ExcelPayslipCalculationService.ValidateSource(source, batch));
    }

    [Fact]
    public void CachedOnlyImportsAndUnrelatedAttendanceColumnsCannotPretendToRecalculateSalary()
    {
        var (source, batch, request) = Example();
        foreach (var column in source.Columns.Where(c => c.Kind is "earning" or "deduction" or "employer" or "netPay")) source.Rows[1].Cells[column.ColumnIndex]!.FormulaText = null;
        Assert.Contains("does not drive", Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.Calculate(source, batch, request)).Message);
        (source, batch, request) = Example(); request.AttendanceColumnIndex = 10;
        Assert.Contains("does not drive", Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.Calculate(source, batch, request)).Message);
    }

    [Fact]
    public void RepeatedRangesAndCachedReadsCountTowardTheCalculationWorkLimit()
    {
        var (source, batch, request) = Example();
        source.Rows[1].Cells[3]!.FormulaText = "SUM(" + string.Join(',', Enumerable.Repeat("C3:C9999", 20)) + ")";
        Assert.Contains("limit", Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.Calculate(source, batch, request)).Message);
    }
}

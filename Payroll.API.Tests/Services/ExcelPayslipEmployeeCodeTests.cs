using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipEmployeeCodeTests
{
    private static ExcelPayslipBatch Batch(params string[] codes) => new()
    {
        Id = "0123456789abcdef0123456789abcdef", ClientId = 11, Month = "2026-09", SourceFileName = "Salary.xlsx", SheetName = "Salary", HeaderRow = 2,
        Rows = codes.Select((code, index) => new ExcelPayslipRow
        {
            Id = $"row-{index + 3}", SourceRow = index + 3, EmployeeCode = code, EmployeeName = "Same Name",
            Information = [new() { Label = "Days", Value = "30" }], Earnings = [new() { Label = "Wages", Amount = 3000 }], NetPay = 3000
        }).ToList()
    };

    private static ExcelPayslipCalculationSource Source(ExcelPayslipBatch batch, bool codeColumn = false) => new()
    {
        SheetName = batch.SheetName, HeaderRow = batch.HeaderRow, ColumnCount = codeColumn ? 5 : 4,
        Columns = new List<ExcelPayslipCalculationColumn>
        {
            new() { ColumnIndex = 0, SourceHeader = "Name", Label = "Name", Kind = "employeeName" },
            new() { ColumnIndex = 1, SourceHeader = "Days", Label = "Days", Kind = "info" },
            new() { ColumnIndex = 2, SourceHeader = "Wages", Label = "Wages", Kind = "earning" },
            new() { ColumnIndex = 3, SourceHeader = "Net", Label = "Net", Kind = "netPay" }
        }.Concat(codeColumn ? [new ExcelPayslipCalculationColumn { ColumnIndex = 4, SourceHeader = "Code", Label = "Code", Kind = "employeeCode" }] : []).ToList(),
        Rows = batch.Rows.Select(row => new ExcelPayslipCalculationSourceRow
        {
            SourceRow = row.SourceRow,
            Cells = new List<ExcelPayslipCalculationCell?>
            {
                new() { Value = row.EmployeeName }, new() { Value = "30" },
                new() { Value = "3000", FormulaText = $"B{row.SourceRow}*100" }, new() { Value = "3000", FormulaText = $"C{row.SourceRow}" }
            }.Concat(codeColumn ? [new ExcelPayslipCalculationCell { Value = row.EmployeeCode }] : []).ToList()
        }).ToList()
    };

    [Fact]
    public void MissingCodesFollowExistingBulkImportSequenceAndDoNotMergeIdenticalNames()
    {
        var batch = Batch("PLRS003", "", "", "another-code");
        ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "plrs", ["plrs007", "PLRS002", "OTHER99999"]);
        Assert.Equal(new[] { "PLRS003", "PLRS008", "PLRS009", "another-code" }, batch.Rows.Select(row => row.EmployeeCode));
        Assert.All(batch.Rows, row => Assert.Equal(3000, row.NetPay));
        // A later code-less import gets new IDs; it cannot silently claim the previous same-name people.
        var later = Batch("");
        ExcelPayslipRepository.AssignEmployeeCodes(later, null, "PLRS", batch.Rows.Select(row => row.EmployeeCode));
        Assert.Equal("PLRS010", later.Rows[0].EmployeeCode);
    }

    [Theory]
    [InlineData("PLRS", "PLRS00001")]
    [InlineData("", "00001")]
    public void FirstImportUsesTheBulkImportDefaultWidth(string prefix, string expected)
    {
        var batch = Batch("");
        ExcelPayslipRepository.AssignEmployeeCodes(batch, null, prefix, []);
        Assert.Equal(expected, batch.Rows[0].EmployeeCode);
    }

    [Theory]
    [InlineData("plrs001", "PLRS001")]
    [InlineData("AB  001", "ab 001")]
    [InlineData("ＰＬＲＳ００１", "PLRS001")]
    public void DuplicateProvidedCodesUseTheSameIdentityRulesAsVariance(string first, string second)
    {
        var batch = Batch(first, second);
        Assert.Contains("more than once", Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "PLRS", [])).Message);
    }

    [Fact]
    public void AppendedCodeColumnPreservesFormulaAddressesAndSurvivesNextMonthCalculation()
    {
        var batch = Batch("", ""); var source = Source(batch);
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source, batch));
        ExcelPayslipRepository.AssignEmployeeCodes(batch, source, "PLRS", []);
        Assert.Equal(5, source.ColumnCount);
        Assert.Equal(4, Assert.Single(source.Columns, column => column.Kind == "employeeCode").ColumnIndex);
        Assert.Equal("Employee Code", source.Rows.Single(row => row.SourceRow == 2).Cells[4]!.Value);
        Assert.Equal("B3*100", source.Rows.Single(row => row.SourceRow == 3).Cells[2]!.FormulaText);
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source, batch));
        var next = ExcelPayslipCalculationService.Calculate(source, batch, new()
        {
            Month = "2026-10", AttendanceColumnIndex = 1,
            Attendance = batch.Rows.Select(row => new ExcelPayslipAttendanceInput { RowId = row.Id, Days = 15.5m }).ToList()
        });
        Assert.Equal(batch.Rows.Select(row => row.EmployeeCode), next.Rows.Select(row => row.EmployeeCode));
        Assert.All(next.Rows, row => Assert.Equal(1550, row.NetPay));
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(next.CalculationSource!, next));
        var before = JsonSerializer.Serialize(next.CalculationSource);
        ExcelPayslipRepository.AssignEmployeeCodes(next, next.CalculationSource, "PLRS", batch.Rows.Select(row => row.EmployeeCode));
        Assert.Equal(before, JsonSerializer.Serialize(next.CalculationSource));
    }

    [Fact]
    public void ExistingCodeColumnOnlyReceivesMissingCodesAndKeepsLeadingZeros()
    {
        var batch = Batch("000004", ""); var source = Source(batch, codeColumn: true);
        ExcelPayslipRepository.AssignEmployeeCodes(batch, source, "", ["000008"]);
        Assert.Equal("000004", batch.Rows[0].EmployeeCode);
        Assert.Equal("000009", batch.Rows[1].EmployeeCode);
        Assert.Equal(5, source.ColumnCount);
        Assert.Equal("000009", source.Rows.Single(row => row.SourceRow == 4).Cells[4]!.Value);
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source, batch));
    }

    [Fact]
    public void CodesProvidedWithoutSourceMappingAreIncludedInTheExportableSource()
    {
        var batch = Batch("PLRS040"); var source = Source(batch);
        ExcelPayslipRepository.AssignEmployeeCodes(batch, source, "PLRS", []);
        Assert.Equal("PLRS040", source.Rows.Single(row => row.SourceRow == 3).Cells[4]!.Value);
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source, batch));
    }

    [Fact]
    public void FullWidthWorksheetRequiresAnExistingCodeColumn()
    {
        var batch = Batch(""); var source = Source(batch); source.ColumnCount = 512;
        Assert.Contains("512 columns", Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.AssignEmployeeCodes(batch, source, "PLRS", [])).Message);
    }
}

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
    [InlineData(3, "PLRS011")]
    [InlineData(6, "PLRS000011")]
    [InlineData(1, "PLRS11")]
    public void SelectedPaddingPreservesSuppliedCodesAndUsesNextReservedCounter(int digits, string expected)
    {
        var batch = Batch("original-code", "");
        batch.EmployeeCodeGeneration = new(true, digits);
        var source = Source(batch, codeColumn: true);
        ExcelPayslipRepository.AssignEmployeeCodes(batch, source, "plrs", ["PLRS00010", "OTHER99999"]);
        Assert.Equal("original-code", batch.Rows[0].EmployeeCode);
        Assert.Equal(expected, batch.Rows[1].EmployeeCode);
        Assert.Equal(expected, source.Rows.Single(r => r.SourceRow == batch.Rows[1].SourceRow).Cells[4]!.Value);
        Assert.Equal("B4*100", source.Rows[1].Cells[2]!.FormulaText);
        Assert.All(batch.Rows, row => Assert.Equal(3000m, row.NetPay));
    }

    [Fact]
    public void DisabledGenerationStillReusesVerifiedMasterButDoesNotAllocateAnUnmatchedCode()
    {
        var matched = Batch(""); matched.EmployeeCodeGeneration = new(false, 5);
        matched.Rows[0].Information.Add(new() { Label="PAN",Value="ABCDE1234F" });
        ExcelPayslipRepository.AssignEmployeeCodes(matched, null, "PLRS", [],
            [new() { Id=2, ClientId=11, EmployeeCode="PLRS00018", EmployeeName="Same Name", Pan="ABCDE1234F" }]);
        Assert.Equal("PLRS00018",matched.Rows[0].EmployeeCode);
        var unmatched=Batch("kept-code",""); unmatched.EmployeeCodeGeneration=new(false,5);
        Assert.Contains("Enable Generate blanks",Assert.Throws<InvalidOperationException>(()=>ExcelPayslipRepository.AssignEmployeeCodes(unmatched,null,"PLRS",[])).Message);
        Assert.Equal("kept-code",unmatched.Rows[0].EmployeeCode);
        Assert.Equal("",unmatched.Rows[1].EmployeeCode);
    }

    [Fact]
    public void LongCounterStaysExactAndPaddingNeverTruncatesIt()
    {
        var batch=Batch(""); batch.EmployeeCodeGeneration=new(true,3);
        ExcelPayslipRepository.AssignEmployeeCodes(batch,null,"PLRS",["PLRS9007199254740993"]);
        Assert.Equal("PLRS9007199254740994",batch.Rows[0].EmployeeCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void InvalidRequestedPaddingIsRejectedBeforeSaving(int digits)
    {
        var batch=Batch(""); batch.EmployeeCodeGeneration=new(true,digits);
        Assert.Contains("between 1 and 12",ExcelPayslipRepository.ValidateBatch(batch));
        Assert.Equal("",batch.Rows[0].EmployeeCode);
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
    public void OneVerifiedEmployeeCanKeepSeparateLocationPayslipsAndTheirOriginalAmounts()
    {
        var batch = Batch("NH001", "NH001");
        for (var i = 0; i < 2; i++)
        {
            batch.Rows[i].Information.AddRange([new() { Label = "UAN", Value = "101555921202" },
                new() { Label = "Work location", Value = i == 0 ? "SO-Lada" : "SO TUTING" }]);
            batch.Rows[i].NetPay = i == 0 ? 42749 : 33860;
        }
        var before = JsonSerializer.Serialize(batch);
        ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "NH", []);
        Assert.Equal(before, JsonSerializer.Serialize(batch));
        Assert.Equal(2, batch.Rows.Select(row => row.Id).Distinct().Count());
        var blank = JsonSerializer.Deserialize<ExcelPayslipBatch>(before)!;
        blank.Rows.ForEach(row => row.EmployeeCode = "");
        ExcelPayslipRepository.AssignEmployeeCodes(blank, null, "NH", ["NH00009"]);
        Assert.All(blank.Rows, row => Assert.Equal("NH00010", row.EmployeeCode));
        foreach (var invalid in new[] { "same-location", "different-name", "different-uan", "no-identifier", "conflicting-pan" })
        {
            var copy = JsonSerializer.Deserialize<ExcelPayslipBatch>(before)!;
            switch (invalid)
            {
                case "same-location": copy.Rows[1].Information[^1].Value = "SO-Lada"; break;
                case "different-name": copy.Rows[1].EmployeeName = "Another Person"; break;
                case "different-uan": copy.Rows[1].Information[^2].Value = "101555921203"; break;
                case "no-identifier": copy.Rows.ForEach(row => row.Information.RemoveAt(row.Information.Count - 2)); break;
                case "conflicting-pan":
                    copy.Rows[0].Information.Add(new() { Label = "PAN", Value = "ABCDE1234F" });
                    copy.Rows[1].Information.Add(new() { Label = "PAN", Value = "ABCDE1234G" }); break;
            }
            Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.AssignEmployeeCodes(copy, null, "NH", []));
        }
    }

    [Fact]
    public void CopiedWorkLocationNeedsReviewedSourceUnitsAndValidCombinedAttendance()
    {
        var batch = Batch("NH001", "NH001");
        for (var i = 0; i < 2; i++)
        {
            batch.Rows[i].Information = [new() { Label = "UAN", Value = "101123456789" },
                new() { Label = "Work location", Value = "Previous location" },
                new() { Label = "Payroll unit", Value = i == 0 ? "Region A" : "Region B" },
                new() { Label = "Source workbook", Value = i == 0 ? "Region A.xlsx" : "Region B.xlsx" },
                new() { Label = "Source Excel row", Value = "2" },
                new() { Label = "Attendance", Value = i == 0 ? "11" : "15" },
                new() { Label = "Reviewed source split", Value = "Confirmed" }];
            batch.Rows[i].NetPay = i == 0 ? 33584 : 45796;
        }
        var before = JsonSerializer.Serialize(batch);
        ExcelPayslipRepository.AssignEmployeeCodes(batch, null, "NH", []);
        Assert.Equal(before, JsonSerializer.Serialize(batch));
        var blank = JsonSerializer.Deserialize<ExcelPayslipBatch>(before)!;
        blank.Rows.ForEach(r => r.EmployeeCode = "");
        ExcelPayslipRepository.AssignEmployeeCodes(blank, null, "NH", ["NH00009"]);
        Assert.All(blank.Rows, r => Assert.Equal("NH00010", r.EmployeeCode));
        foreach (var invalid in new[] { "unreviewed", "same-unit", "same-workbook", "missing-reference", "over-month", "blank-days", "invalid-month", "conflicting-identity" })
        {
            var copy = JsonSerializer.Deserialize<ExcelPayslipBatch>(before)!;
            ExcelPayslipText Field(string label) => copy.Rows[1].Information.Single(i => i.Label == label);
            switch (invalid)
            {
                case "unreviewed": Field("Reviewed source split").Value = ""; break;
                case "same-unit": Field("Payroll unit").Value = "Region A"; break;
                case "same-workbook": Field("Source workbook").Value = "Region A.xlsx"; break;
                case "missing-reference": Field("Source Excel row").Value = ""; break;
                case "over-month": Field("Attendance").Value = "20"; break;
                case "blank-days": Field("Attendance").Value = ""; break;
                case "invalid-month": copy.Month = "2026-00"; break;
                case "conflicting-identity": Field("UAN").Value = "101123456780"; break;
            }
            Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.AssignEmployeeCodes(copy, null, "NH", []));
        }
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
        var batch = Batch(""); var source = Source(batch); source.ColumnCount = 1500;
        Assert.Contains("1500 columns", Assert.Throws<InvalidOperationException>(() => ExcelPayslipRepository.AssignEmployeeCodes(batch, source, "PLRS", [])).Message);
    }

    [Fact]
    public void EmployeeCodeCanBeAppendedAsColumn1500()
    {
        var batch = Batch(""); var source = Source(batch); source.ColumnCount = 1499;
        ExcelPayslipRepository.AssignEmployeeCodes(batch, source, "PLRS", []);
        Assert.Equal(1500, source.ColumnCount);
        Assert.Equal(batch.Rows[0].EmployeeCode, source.Rows.Single(row => row.SourceRow == 3).Cells[1499]!.Value);
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source, batch));
    }
}

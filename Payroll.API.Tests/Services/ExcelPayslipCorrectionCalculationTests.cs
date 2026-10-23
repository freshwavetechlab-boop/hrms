using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipCorrectionCalculationTests
{
    private static ExcelPayslipCorrectionPreview Fixture(decimal correction)
    {
        var cells = new[] { "Aman", "23868", "-1200", "1988.2044", "24656.2044", "1260", "859.248", "167", "", "22369.9564", "1950", "26606.2044", "1117.4605848", "4990.259697264", "32713.924682064", "-1200" }
            .Select(value => new ExcelPayslipCalculationCell { Value = value }).ToList();
        cells[2].FormulaText = "0-1200"; cells[3].FormulaText = "B2*8.33%"; cells[4].FormulaText = "SUM(B2:D2)";
        cells[9].FormulaText = "E2-F2-G2-H2+I2"; cells[11].FormulaText = "E2+K2+I2";
        cells[12].FormulaText = "L2*4.2%"; cells[13].FormulaText = "(L2+M2)*18%"; cells[14].FormulaText = "SUM(L2:N2)";
        cells[15].FormulaText = cells[2].FormulaText;
        var kinds = new[] { "employeeName", "earning", "ignore", "earning", "grossTotal", "deduction", "deduction", "deduction", "ignore", "netPay", "employer", "ignore", "ignore", "ignore", "ignore", "earning" };
        var labels = new[] { "Employee", "Wages", "Original C", "Bonus", "Gross", "PF1", "PF2", "PT", "Blank", "Net", "Employer PF", "Billing", "Fee", "GST", "Invoice", "Allowance" };
        var columns = labels.Select((label, i) => new ExcelPayslipCalculationColumn { ColumnIndex = i, Kind = kinds[i], Label = label, SourceHeader = label }).ToList();
        var source = new ExcelPayslipCalculationSource { SheetName = "Salary", HeaderRow = 1, ColumnCount = 16, Columns = columns,
            Rows = [new() { SourceRow = 1, Cells = labels.Select(value => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value = value }).ToList() }, new() { SourceRow = 2, Cells = cells.Select(c => (ExcelPayslipCalculationCell?)c).ToList() }] };
        return new() { Batch = new() { ClientId = 54, Month = "2026-09", SourceFileName = "Test.xlsx", SheetName = "Salary", HeaderRow = 1, CalculationSource = source,
            Rows = [new() { Id = "aman", SourceRow = 2, EmployeeName = "Aman", NetPay = 22369.9564m,
                Earnings = [new() { Label = "Wages", Amount = 23868 }, new() { Label = "Bonus", Amount = 1988.2044m }, new() { Label = "Allowance", Amount = -1200 }],
                Deductions = [new() { Label = "PF1", Amount = 1260 }, new() { Label = "PF2", Amount = 859.248m }, new() { Label = "PT", Amount = 167 }],
                EmployerContributions = [new() { Label = "Employer PF", Amount = 1950 }], DeclaredGross = 24656.2044m }] },
            Edits = [new() { SourceRow = 2, ColumnIndex = 15, Amount = correction }] };
    }

    [Theory]
    [InlineData(0, 23569.9564, 25856.2044, 34189.396682064)]
    [InlineData(1200, 24769.9564, 27056.2044, 35664.868682064)]
    public void RecalculatesRawMirrorsNetAndBillingFromExcelDependencies(decimal allowance, decimal net, decimal gross, decimal invoice)
    {
        var request = Fixture(allowance); var before = JsonSerializer.Serialize(request);
        var result = ExcelPayslipCalculationService.RecalculateCorrections(request);
        Assert.Equal(allowance.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Rows[1].Cells[2]!.Value);
        Assert.Equal(net, decimal.Parse(result.Rows[1].Cells[9]!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(gross, decimal.Parse(result.Rows[1].Cells[4]!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(invoice, decimal.Parse(result.Rows[1].Cells[14]!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("1260", result.Rows[1].Cells[5]!.Value); Assert.Equal("", result.Rows[1].Cells[8]!.Value);
        Assert.NotNull(result.Rows[1].Cells[15]!.CalculationError); Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public void PreviouslyManuallyCorrectedZeroCanRepairItsStillOldNetAndRawMirror()
    {
        var request = Fixture(0);
        request.Batch.CalculationSource!.Rows[1].Cells[15]!.Value = "0";
        request.Batch.CalculationSource.Rows[1].Cells[15]!.CalculationError = "This formula amount was manually corrected for this batch. Re-import a reviewed salary formula before creating another month.";
        request.Batch.Rows[0].Earnings[2].Amount = 0;
        var result = ExcelPayslipCalculationService.RecalculateCorrections(request);
        Assert.Equal("23569.9564", result.Rows[1].Cells[9]!.Value);
    }

    [Fact]
    public void IgnoredReferencedErrorIsNotTreatedAsAnApprovedNumericCache()
    {
        var request = Fixture(0); var cell = request.Batch.CalculationSource!.Rows[1].Cells[8]!;
        cell.Value = "100"; cell.Error = "#REF!";
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.RecalculateCorrections(request));
    }

    [Fact]
    public void UnsupportedDependentFormulaBlocksPreviewAndIdentityEditsAreRejected()
    {
        var request = Fixture(0); request.Batch.CalculationSource!.Rows[1].Cells[9]!.FormulaText = "UNKNOWN(C2)";
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.RecalculateCorrections(request));
        request = Fixture(0); request.Edits[0].ColumnIndex = 0;
        Assert.Throws<InvalidOperationException>(() => ExcelPayslipCalculationService.RecalculateCorrections(request));
    }
}

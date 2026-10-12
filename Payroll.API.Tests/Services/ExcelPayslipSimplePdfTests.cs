using System.Text;
using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class ExcelPayslipSimplePdfTests
{
    private const string UnsignedNotice = "This is a system-generated payslip and does not require a signature.";

    private static ExcelPayslipRow Sample() => new()
    {
        Id = "source-row-6", SourceRow = 6, EmployeeName = "Sample Employee", EmployeeCode = "HIDDEN-CODE-001",
        Information = [
            new() { Label = "Location Name (Tehsil/Sub Tehsil)", Value = "Sample Tehsil" },
            new() { Label = "UAN (E)", Value = "100000000001" },
            new() { Label = "UAN (AB)", Value = "100000000001" },
            new() { Label = "ESIC No", Value = "Exempt" },
            new() { Label = "Designation_Manpower Category", Value = "Operator" },
            new() { Label = "Attendance", Value = "30" },
            new() { Label = "Bank Account Number", Value = "12345678901" },
            new() { Label = "IFSC", Value = "BANK0000001" },
            new() { Label = "Phone", Value = "9999900001" },
            new() { Label = "Rate (Monthly)", Value = "15414" }
        ],
        Earnings = [new() { Label = "Wages", Amount = 15413.999999999998m }, new() { Label = "Bonus", Amount = 0 }],
        Deductions = [new() { Label = "PF@12%", Amount = 1849.6799999999996m }, new() { Label = "ESIC@0.75%", Amount = 115.60499999999998m }],
        EmployerContributions = [new() { Label = "PF@13%", Amount = 2003.82m }, new() { Label = "ESIC@3.25%", Amount = 500.95499999999998m }],
        NetPay = 13448.714999999998m
    };

    private static ExcelPayslipBatch Batch(params ExcelPayslipRow[] rows) => new()
    {
        Id = "synthetic-simple", ClientId = 11, ClientName = "Selected Client", Month = "2026-09",
        SourceFileName = "synthetic.xlsx", SheetName = "Salary", SimpleLayout = true, Rows = rows.ToList()
    };

    [Fact]
    public void ReferenceIdentityProjectionHasExactlyEightFieldsAndKeepsSourceAliases()
    {
        var row = Sample();
        var before = JsonSerializer.Serialize(row);
        var information = ExcelPayslipPdfService.SimpleInformation(Batch(row), row);
        Assert.Equal(new[] { "Employee Name", "Department", "UAN", "Esic No", "Designation",
            "Number of Working Days Attended", "Account No", "IFSC Code" }, information.Select(field => field.Label));
        Assert.Equal(new[] { "Sample Employee", "Selected Client", "100000000001", "Exempt", "Operator", "30", "12345678901", "BANK0000001" },
            information.Select(field => field.Value));
        Assert.Equal(before, JsonSerializer.Serialize(row));
        var empty = ExcelPayslipPdfService.SimpleInformation(new(), new() { EmployeeName = "Only Name" });
        Assert.Equal(8, empty.Count);
        Assert.All(empty.Skip(1), field => Assert.Equal("", field.Value));
    }

    [Theory]
    [InlineData("ESI Number", "Esic No")]
    [InlineData("Manpower Category", "Designation")]
    [InlineData("Paid Days", "Number of Working Days Attended")]
    [InlineData("Account No", "Account No")]
    [InlineData("IFSC Code", "IFSC Code")]
    public void CommonInformationAliasesPopulateTheReferenceField(string inputLabel, string outputLabel)
    {
        var row = new ExcelPayslipRow { Information = [new() { Label = inputLabel, Value = "Source value" }] };
        Assert.Equal("Source value", ExcelPayslipPdfService.SimpleInformation(Batch(row), row).Single(field => field.Label == outputLabel).Value);
    }

    [Theory]
    [InlineData("Department")]
    [InlineData("Work Location")]
    public void DepartmentAlwaysUsesSelectedBatchClientInsteadOfMappedLocationOrDepartment(string inputLabel)
    {
        var row = Sample(); row.Information.Insert(0, new() { Label = inputLabel, Value = "Ignored source department" });
        Assert.Equal("Selected Client", ExcelPayslipPdfService.SimpleInformation(Batch(row), row).Single(field => field.Label == "Department").Value);
    }

    [Fact]
    public void SimplePdfHidesExtraIdentityAndHeadingsWithoutChangingSnapshotOrFinancialValues()
    {
        var row = Sample(); var batch = Batch(row); var before = JsonSerializer.Serialize(batch);
        var service = new ExcelPayslipPdfService();
        service.ValidateLayout(batch, [row]);
        using var pdf = Read(service.Create(batch, [row], false, 0));
        var text = Content(pdf.Pages[0]);
        foreach (var expected in new[] { "Salary Slip", "Employee Name", "Department", "Selected Client", "UAN", "Esic No", "Designation", "Account No",
            "IFSC Code", "Income", "Deductions", "Rate (Monthly)", "Earn Salary", "PF@12%", "ESIC@0.75%", "Deduction", "In Hand Salary",
            "Employer Contributions", "Employer EPF (13%)", "Employer ESIC (3.25%)", "Total Employer Contributions",
            "2,004", "501", "2,505", "Paid by the employer; does not reduce your In Hand Salary.",
            "15,414", "1,850", "116", "1,965", "13,449",
            "GA DIGITAL WEB WORD (P) LTD", "HARGOBIND ENCLAVE", "DELHI-110092" })
            Assert.Contains(expected, text);
        foreach (var hidden in new[] { "HIDDEN-CODE-001", "Sample Tehsil", "Phone", "9999900001", "Bonus", "UAN (E)", "UAN (AB)",
            "Total Earnings", "PF@13%:", "ESIC@3.25%", "Rupees", "Employer Signature", "Rate/Day" })
            Assert.DoesNotContain(hidden, text);
        Assert.Equal(15413.999999999998m, ExcelPayslipPdfService.TotalsFor(row).Gross); // Rate is informational, never a second earning.
        Assert.Equal(before, JsonSerializer.Serialize(batch));
        Assert.Single(pdf.Pages);
        Assert.InRange(pdf.Pages[0].Width.Point, 595, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public void EarningsAndDeductionsRemainVisibleAndFooterListsSavedEmployerContributions()
    {
        var row = Sample();
        row.Earnings.Add(new() { Label = "Arrears", Amount = -123.45m });
        row.Deductions.Add(new() { Label = "Recovery", Amount = 56.78m });
        row.EmployerContributions.Add(new() { Label = "Insurance", Amount = 89.12m });
        using var pdf = Read(new ExcelPayslipPdfService().Create(Batch(row), [row], false, 2));
        var text = Content(pdf.Pages[0]);
        foreach (var expected in new[] { "Arrears", "-123.45", "Recovery", "56.78", "Employer EPF (13%)", "2,003.82",
            "Employer ESIC (3.25%)", "500.96", "Insurance", "89.12", "Total Employer Contributions", "2,593.90" })
            Assert.Contains(expected, text);
        Assert.DoesNotContain("Bonus", text);
        Assert.DoesNotContain("ESIC@3.25%", text);
        Assert.Equal(500.95499999999998m, row.EmployerContributions[1].Amount);
        Assert.Equal(89.12m, row.EmployerContributions[2].Amount);
    }

    [Theory]
    [InlineData("100000000001", "100000000001")]
    [InlineData("NEW", "100000000001")]
    [InlineData("100000000001", "N/A")]
    [InlineData("'100000000001", "100000000001")]
    public void DuplicateOrPlaceholderUanUsesTheSingleValidNumber(string first, string second)
    {
        var row = Sample(); row.Information[1].Value = first; row.Information[2].Value = second;
        var original = JsonSerializer.Serialize(row);
        Assert.Equal("100000000001", ExcelPayslipPdfService.SimpleInformation(Batch(row), row).Single(field => field.Label == "UAN").Value);
        new ExcelPayslipPdfService().ValidateLayout(Batch(row), [row]);
        Assert.Equal(original, JsonSerializer.Serialize(row));
    }

    [Fact]
    public void ConflictingValidUanStopsSimpleValidationAndExportInsteadOfChoosingAnIdentifier()
    {
        var row = Sample(); row.Information[2].Value = "100000000002";
        var before = JsonSerializer.Serialize(row); var service = new ExcelPayslipPdfService();
        var validation = Assert.Throws<InvalidOperationException>(() => service.ValidateLayout(Batch(row), [row]));
        var rendering = Assert.Throws<InvalidOperationException>(() => service.Create(Batch(row), [row], false));
        Assert.Contains("row 6", validation.Message);
        Assert.Contains("different UAN", validation.Message);
        Assert.Equal(validation.Message, rendering.Message);
        Assert.Equal(before, JsonSerializer.Serialize(row));
    }

    [Theory]
    [InlineData("100000000001", "100000000002")]
    [InlineData("NEW", "100000000002")]
    [InlineData("100000000001", "NEW")]
    public void ExplicitCanonicalUanMappingIsAuthoritativeOverOtherSourceUanColumns(string chosen, string secondary)
    {
        var row = Sample(); row.Information[1].Label = "UAN"; row.Information[1].Value = chosen;
        row.Information[2].Value = secondary;
        var before = JsonSerializer.Serialize(row);
        Assert.Equal(chosen, ExcelPayslipPdfService.SimpleInformation(Batch(row), row).Single(field => field.Label == "UAN").Value);
        new ExcelPayslipPdfService().ValidateLayout(Batch(row), [row]);
        Assert.Equal(before, JsonSerializer.Serialize(row));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrandingIsAlwaysIncludedWhileSignatureChoiceControlsSealAndUnsignedNotice(bool includeSeal)
    {
        var row = Sample();
        using var pdf = Read(new ExcelPayslipPdfService().Create(Batch(row), [row], includeSeal));
        Assert.Equal(!includeSeal, Content(pdf.Pages[0]).Contains(UnsignedNotice));
        Assert.Contains("GA DIGITAL WEB WORD (P) LTD", Content(pdf.Pages[0]));
        Assert.Equal(includeSeal ? 2 : 1, pdf.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")?.Elements.Count ?? 0);
    }

    [Fact]
    public void LayoutIsOptInAndDoesNotRemoveFieldsFromExistingDetailedPayslips()
    {
        var row = Sample(); var batch = Batch(row); batch.SimpleLayout = false;
        using var pdf = Read(new ExcelPayslipPdfService().Create(batch, [row], false));
        var text = Content(pdf.Pages[0]);
        Assert.Contains("HIDDEN-CODE-001", text);
        Assert.Contains("9999900001", text);
        Assert.Contains("Total Earnings", text);
        Assert.Contains("Employer Contributions", text);
        Assert.Contains("Rupees", text);
    }

    [Fact]
    public void ExcessiveVisibleTextIsRejectedWithSourceRowRatherThanSilentlyClipped()
    {
        var row = Sample(); var batch = Batch(row); batch.ClientName = new string('W', 6000);
        var service = new ExcelPayslipPdfService();
        var validation = Assert.Throws<InvalidOperationException>(() => service.ValidateLayout(batch, [row]));
        var rendering = Assert.Throws<InvalidOperationException>(() => service.Create(batch, [row], false));
        Assert.Contains("row 6", validation.Message);
        Assert.Contains("too much text", validation.Message);
        Assert.Equal(validation.Message, rendering.Message);
    }

    [Fact]
    public void LargeSupportedFinancialValuesFitAfterSuccessfulLayoutValidation()
    {
        var row = Sample();
        row.Earnings[0].Amount = 10000000000m;
        row.Deductions[0].Amount = 10000000000m;
        var service = new ExcelPayslipPdfService(); var batch = Batch(row);
        service.ValidateLayout(batch, [row]);
        using var pdf = Read(service.Create(batch, [row], false, 2));
        Assert.Single(pdf.Pages);
    }

    private static PdfDocument Read(byte[] bytes) => PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);

    private static ExcelPayslipCalculationSource RateSource(decimal days = 30, string attendance = "30", string formula = "K6/$L$4*L6") => new()
    {
        Columns = [new() { ColumnIndex = 10, Kind = "info", Label = "Rate (Monthly)" },
            new() { ColumnIndex = 11, Kind = "info", Label = "Attendance" },
            new() { ColumnIndex = 12, Kind = "earning", Label = "Earn Salary" }],
        Rows = [new() { SourceRow = 4, Cells = Enumerable.Range(0, 12).Select(i =>
                (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value = i == 11 ? days.ToString(System.Globalization.CultureInfo.InvariantCulture) : "" }).ToList() },
            new() { SourceRow = 6, Cells = Enumerable.Range(0, 13).Select(i =>
                (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value = i == 10 ? "15414" : i == 11 ? attendance : "",
                    FormulaText = i == 12 ? formula : null }).ToList() }]
    };

    [Theory]
    [InlineData(30, "30", "513.80")]
    [InlineData(31, "15", "497.23")]
    [InlineData(28, "0", "550.50")]
    public void DailyRateUsesSavedCalculationDaysRatherThanAttendance(int days, string attendance, string expected)
    {
        var row = Sample(); var source = RateSource(days, attendance);
        var before = JsonSerializer.Serialize(source);
        var rates = ExcelPayslipPdfService.SimpleRates(row, source, 2);
        Assert.Equal("Rate/Day", Assert.Single(rates).Label);
        Assert.Equal(expected, rates[0].Value);
        Assert.Equal(before, JsonSerializer.Serialize(source));
    }

    [Theory]
    [InlineData("K6/30*L6")]
    [InlineData("=(K6/$L$4)*L6")]
    [InlineData("$K$6 / $L$4 * $L$6")]
    public void EquivalentDirectSalaryFormulasSupplyDailyRate(string formula)
    {
        Assert.Equal("513.80", Assert.Single(ExcelPayslipPdfService.SimpleRates(Sample(), RateSource(formula: formula), 2)).Value);
    }

    [Theory]
    [InlineData(0, "K6/$L$4*L6")]
    [InlineData(32, "K6/$L$4*L6")]
    [InlineData(30, "K6/($L$4*L6)")]
    [InlineData(30, "K7/$L$4*L6")]
    [InlineData(30, "K6/$L$4*L7")]
    public void UnverifiedDivisorKeepsHonestMonthlyLabel(int days, string formula)
    {
        var rate = Assert.Single(ExcelPayslipPdfService.SimpleRates(Sample(), RateSource(days, formula: formula), 2));
        Assert.Equal("Rate (Monthly)", rate.Label);
        Assert.Equal("15,414.00", rate.Value);
    }

    [Fact]
    public void ExistingDailyRateIsNotDividedAgain()
    {
        var row = Sample(); row.Information[^1] = new() { Label = "Rate per day", Value = "513.8" };
        var rate = Assert.Single(ExcelPayslipPdfService.SimpleRates(row, RateSource(), 2));
        Assert.Equal("Rate/Day", rate.Label); Assert.Equal("513.80", rate.Value);
    }

    [Fact]
    public void PdfShowsDailyRateAndPreservesSourceFinancialAmounts()
    {
        var row = Sample(); var batch = Batch(row); var source = RateSource();
        var before = JsonSerializer.Serialize(batch);
        using var pdf = Read(new ExcelPayslipPdfService().Create(batch, [row], false, 2, source));
        var text = Content(pdf.Pages[0]);
        Assert.Contains("Rate/Day", text); Assert.Contains("513.80", text);
        Assert.DoesNotContain("Rate (Monthly)", text);
        Assert.Contains("15,414.00", text); Assert.Contains("13,448.72", text);
        Assert.Equal(before, JsonSerializer.Serialize(batch)); Assert.Single(pdf.Pages);
        using var whole = Read(new ExcelPayslipPdfService().Create(batch, [row], false, 0, source));
        Assert.Contains("514", Content(whole.Pages[0]));
    }
    private static string Content(PdfPage page) => Encoding.Latin1.GetString(page.Contents.CreateSingleContent().Stream.UnfilteredValue)
        .Replace("\\(", "(").Replace("\\)", ")");
}

using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public class ExcelPayslipPdfServiceTests
{
    private static ExcelPayslipRow Sample(string name = "Sample Employee") => new()
    {
        Id = "source-row-6", SourceRow = 6, EmployeeName = name, EmployeeCode = "SOURCE-001",
        Information = [new() { Label = "Work Location", Value = "Baba Bakala Sahib" }, new() { Label = "Attendance", Value = "27.5" }],
        Earnings = [new() { Label = "Basic", Amount = 15414 }, new() { Label = "Adjustment", Amount = -125.5m }, new() { Label = "Bonus", Amount = 0 }],
        Deductions = [new() { Label = "Employee PF", Amount = 1800 }, new() { Label = "Employee ESI", Amount = 115.61m }],
        EmployerContributions = [new() { Label = "Employer PF", Amount = 1950 }, new() { Label = "Employer ESI", Amount = 500.96m }],
        NetPay = 13372.89m
    };
    private static ExcelPayslipBatch Batch(params ExcelPayslipRow[] rows) => new()
    { Id = "synthetic", ClientId = 11, Month = "2026-09", SourceFileName = "synthetic.xlsx", SheetName = "Salary", Rows = rows.ToList() };

    [Fact]
    public void DeclaredTotalsAndNetAreSourceValuesEvenWhenComponentsDiffer()
    {
        var row = Sample(); row.DeclaredGross = 15000; row.DeclaredDeductions = 0; row.NetPay = -250.75m;
        var totals = ExcelPayslipPdfService.TotalsFor(row);
        Assert.Equal(15000, totals.Gross); Assert.Equal(0, totals.Deductions); Assert.Equal(-250.75m, totals.Net);
        row.DeclaredGross = row.DeclaredDeductions = null;
        totals = ExcelPayslipPdfService.TotalsFor(row);
        Assert.Equal(15288.5m, totals.Gross); Assert.Equal(1915.61m, totals.Deductions); Assert.Equal(-250.75m, totals.Net);
        Assert.Equal("0.00", ExcelPayslipPdfService.Amount(0));
        Assert.Equal("-1,234.50", ExcelPayslipPdfService.Amount(-1234.5m));
    }

    [Fact]
    public void CreatesOneSelectableA4PagePerSelectedSnapshotRowWithoutMutatingInput()
    {
        var one = Sample(); var two = Sample("Second Employee"); two.Id = "source-row-7"; two.NetPay = 0;
        var unselected = Sample("Not selected"); var batch = Batch(one, two, unselected);
        var before = JsonSerializer.Serialize(batch);
        var bytes = new ExcelPayslipPdfService().Create(batch, [one, two]);
        using var document = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        Assert.Equal(2, document.PageCount);
        Assert.Equal(before, JsonSerializer.Serialize(batch));
        foreach (var page in document.Pages)
        {
            Assert.InRange(page.Width.Point, 595, 596); Assert.InRange(page.Height.Point, 841, 843);
            var resources = page.Elements.GetDictionary("/Resources")!;
            Assert.NotNull(resources.Elements.GetDictionary("/Font"));
            Assert.Equal(2, resources.Elements.GetDictionary("/XObject")!.Elements.Count);
            Assert.True(page.Contents.Elements.Count > 0);
        }
        // Shared image objects prevent hundreds of copies of the same signature/logo.
        Assert.Equal(ImageIds(document.Pages[0]), ImageIds(document.Pages[1]));
    }

    [Fact]
    public void ExcludingSealKeepsLogoAndSupportsNegativeInHand()
    {
        var row = Sample(); row.NetPay = -500.25m;
        using var pdf = PdfReader.Open(new MemoryStream(new ExcelPayslipPdfService().Create(Batch(row), [row], false)), PdfDocumentOpenMode.Import);
        Assert.Single(ImageIds(pdf.Pages[0]));
        Assert.Equal(1, pdf.PageCount);
    }

    [Fact]
    public void LongNamesLocationsAndUnspacedLabelsWrapWithinOnePage()
    {
        var row = Sample("An Employee With A Long Multi Part Name For The District And Sub Tehsil Office");
        row.Information = Enumerable.Range(1, 13).Select(i => new ExcelPayslipText { Label = i == 1 ? "Residence district" : "Detail " + i,
            Value = i == 1 ? "A detailed office address with a long branch and sub tehsil name" : "Source value " + i }).ToList();
        row.Earnings[0].Label = "Basic salary and an additional description that wraps to a second line";
        row.Deductions[0].Label = new string('X', 64);
        var bytes = new ExcelPayslipPdfService().Create(Batch(row), [row]);
        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        Assert.Equal(1, pdf.PageCount);
    }

    [Fact]
    public void ExcessiveTextIsRejectedInsteadOfClippingOrDroppingFields()
    {
        var row = Sample();
        row.Information = Enumerable.Range(0, 100).Select(i => new ExcelPayslipText { Label = "Field " + i, Value = new string('W', 200) }).ToList();
        var error = Assert.Throws<InvalidOperationException>(() => new ExcelPayslipPdfService().Create(Batch(row), [row], false));
        Assert.Contains("too much text", error.Message);
        Assert.Contains("row 6", error.Message);
        var validation = Assert.Throws<InvalidOperationException>(() => new ExcelPayslipPdfService().ValidateLayout(Batch(row), [row]));
        Assert.Equal(error.Message, validation.Message);
    }

    [Fact]
    public void PreSaveLayoutIncludesServerDerivedClientAndPreservesMappedClient()
    {
        var row = Sample(); var batch = Batch(row); batch.ClientName = "Another Client With A Long Office Name";
        var service = new ExcelPayslipPdfService();
        service.ValidateLayout(batch, [row]);
        batch.ClientName = string.Join(" ", Enumerable.Repeat("An exceptionally long client label", 200));
        Assert.Throws<InvalidOperationException>(() => service.ValidateLayout(batch, [row]));
        row.Information.Add(new() { Label = "Client", Value = "Source client label" });
        service.ValidateLayout(batch, [row]);
        Assert.Equal("Source client label", row.Information.Last().Value);
    }

    [Fact]
    public void ClientMetadataCannotCauseAnExcelMappedEmployeeCodeToBeDropped()
    {
        var row = Sample(); row.EmployeeCode = "";
        row.Information = [new() { Label = "Employee Code", Value = new string('W', 6000) }];
        var batch = Batch(row); batch.ClientName = "Another client";
        // The mapped code must participate in layout, even when Client is already present.
        Assert.Throws<InvalidOperationException>(() => new ExcelPayslipPdfService().ValidateLayout(batch, [row]));
    }

    [Fact]
    public void EmptySelectionCannotProduceAnInvalidEmptyPdf() => Assert.Throws<InvalidOperationException>(
        () => new ExcelPayslipPdfService().Create(Batch(), [], false));

    [Fact]
    public void DisplayChoiceChangesFiguresAndWordsWithoutChangingSourcePrecision()
    {
        var row = Sample(); row.NetPay = 13448.714999999998m;
        var batch = Batch(row); var original = JsonSerializer.Serialize(batch);
        var service = new ExcelPayslipPdfService();
        Assert.Equal("13,449", ExcelPayslipPdfService.Amount(row.NetPay, 0));
        Assert.Equal("13,448.72", ExcelPayslipPdfService.Amount(row.NetPay, 2));
        Assert.Equal("Rupees Thirteen thousand four hundred forty nine only", ExcelPayslipPdfService.NetWords(row.NetPay, 0));
        Assert.Equal("Rupees Thirteen thousand four hundred forty eight and seventy two paise only", ExcelPayslipPdfService.NetWords(row.NetPay, 2));
        foreach (var decimals in new[] { 0, 2 })
        {
            using var pdf = PdfReader.Open(new MemoryStream(service.Create(batch, [row], false, decimals)), PdfDocumentOpenMode.Import);
            Assert.Equal(1, pdf.PageCount);
        }
        Assert.Equal(original, JsonSerializer.Serialize(batch));
        Assert.Equal("-501", ExcelPayslipPdfService.Amount(-500.5m, 0));
        Assert.Equal("Rupees minus Five hundred one only", ExcelPayslipPdfService.NetWords(-500.5m, 0));
        Assert.Equal("0", ExcelPayslipPdfService.Amount(-.1m, 0));
        Assert.Equal("Rupees Zero only", ExcelPayslipPdfService.NetWords(-.1m, 0));
    }

    [Fact]
    public void ExcelSignificantDigitDisplayMatchesCachedHalfRupeeAndHalfPaisaValues()
    {
        // The workbook's actual K135.Value2 is below; Excel K135.Text is "14130".
        Assert.Equal("14,130", ExcelPayslipPdfService.Amount(14129.499999999998m, 0));
        Assert.Equal("-14,130", ExcelPayslipPdfService.Amount(-14129.499999999998m, 0));
        Assert.Equal("Rupees Fourteen thousand one hundred thirty only", ExcelPayslipPdfService.NetWords(14129.499999999998m, 0));
        Assert.Equal("115.61", ExcelPayslipPdfService.Amount(115.60499999999998m, 2));
        Assert.Equal("13,448.72", ExcelPayslipPdfService.Amount(13448.714999999998m, 2));
        // A meaningful value below the midpoint must not be rounded up as a binary tail.
        Assert.Equal("14,129", ExcelPayslipPdfService.Amount(14129.4999m, 0));
        var row = Sample(); row.Earnings[0].Amount = 14129.499999999998m;
        var total = row.Earnings.Sum(x => x.Amount);
        _ = ExcelPayslipPdfService.Amount(total, 0);
        Assert.Equal(total, ExcelPayslipPdfService.TotalsFor(row).Gross);
        Assert.Equal(14129.499999999998m, row.Earnings[0].Amount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(3)]
    public void UnsupportedAmountPrecisionIsRejected(int decimals)
    {
        var row = Sample();
        Assert.Throws<InvalidOperationException>(() => new ExcelPayslipPdfService().Create(Batch(row), [row], false, decimals));
    }

    private static string[] ImageIds(PdfPage page) => page.Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!
        .Elements.Values.OfType<PdfReference>().Select(r => r.ObjectID.ToString()).Order().ToArray();
}

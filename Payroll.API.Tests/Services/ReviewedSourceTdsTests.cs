using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ReviewedSourceTdsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal sealed record Case(ExcelPayrollSetupRequest Request, ExcelPayrollSetupResult Receipt,
        ExcelPayslipBatch Batch, ExcelPayslipCalculationSource Source, Employee Employee, ReviewedSourceTdsRule Rule, decimal Amount);

    internal static Case Fixture(decimal amount = 2500m, bool blank = false)
    {
        var scope = new ExcelPayrollTemplateScope { Reviewed = true, EffectiveTo = "2026-10-31", OpenEnded = false,
            LocationMode = "Listed", WorkLocationIds = [975], CategoryKind = "AllClientCategories",
            DivisorMeaning = "ContractFixedDays", PayableMeaning = "AlreadyPayable", PolicyBasis = "SavedSourceRules" };
        var row = new ExcelPayslipRow { Id = "synthetic-row", SourceRow = 2, EmployeeCode = "SYNTH-TDS-001", EmployeeName = "Synthetic Person", NetPay = 30000 - amount };
        var batch = new ExcelPayslipBatch { Id = new string('a', 32), ClientId = 975, Month = "2026-10", Rows = [row] };
        var source = new ExcelPayslipCalculationSource { SourceBatchId = batch.Id, SheetName = "Synthetic salary", HeaderRow = 1,
            ColumnCount = 1, Columns = [new() { ColumnIndex = 0, SourceHeader = "TDS", Label = "TDS", Kind = "deduction" }],
            Rows = [new() { SourceRow = 2, Cells = [new() { DataType = blank ? "Blank" : "Number", Value = blank ? "" : amount.ToString(CultureInfo.InvariantCulture) }] }] };
        var request = new ExcelPayrollSetupRequest { BatchId = batch.Id, TransferReviewVersion = 1, TemplateScopeReviewVersion = 1,
            TemplateScope = scope, ReviewedFixedInputs = true, ReviewedPolicyScope = true, EffectiveFrom = "2026-10-01", RowIds = [row.Id],
            Components = [new() { Code = "TDS", ExistingComponentId = "97502", Category = "Deduction", StatutoryType = "TDS",
                CalculationType = "Fixed Amount", ProRata = false, SourceColumnIndex = 0, ExpectedColumnIndex = 0, BlankInputAsZero = true }] };
        var transfer = new ExcelPayrollTransferReview { Version = 1, SourceBatchId = batch.Id, SourceMonth = batch.Month,
            Components = [new("TDS", "Deduction", "Reuse unchanged", "97502")],
            Templates = [new() { TemplateId = "97500", RowIds = [row.Id], Applicability = new(975, "97500", new string('b', 64),
                "2026-10-01", "SOURCE_RULES:" + new string('b', 64), scope, batch.Id, batch.Month) }] };
        var receipt = new ExcelPayrollSetupResult { ImportId = new string('c', 32), EmployeeIds = [97501], Transfer = transfer,
            MasterLinks = [new(row.Id, 97501, 2, new string('d', 64), new string('e', 64), 31, null, 31, false)],
            TransferRows = [new(row.Id, 97501, 31, null, 31, row.NetPay, [new("TDS", amount, amount, 0)])] };
        var employee = new Employee { Id = 97501, ClientId = 975, EmployeeCode = row.EmployeeCode, WorkLocationId = 975, SalaryStructureId = "97500" };
        var test = new Case(request, receipt, batch, source, employee, new("97502", "TDS", "Deduction", "TDS", "Fixed Amount", false, ""), amount);
        Seal(test);
        return test;
    }

    internal static void Seal(Case value)
    {
        value.Receipt.Transfer!.SourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { value.Batch, value.Source }, Json)))).ToLowerInvariant();
        value.Receipt.Transfer.Formulas = [ExcelPayrollSetupRepository.InspectTransferFormula(value.Request, value.Source, value.Batch.Rows[0], value.Request.Components[0])];
    }

    internal static ReviewedSourceTdsEvidence? Resolve(Case value, decimal? input = null, string period = "2026-10") => ReviewedSourceTds.Resolve(
        value.Receipt.ImportId, value.Request, value.Receipt, value.Batch, value.Source, value.Employee, period, value.Rule, input ?? value.Amount);

    [Theory]
    [InlineData(2500, false, "ReviewedLiteral")]
    [InlineData(0, false, "ReviewedLiteral")]
    [InlineData(0, true, "ReviewedBlankZero")]
    public void ExactClosedSourceWithholdingIncludesReviewedZeroWithoutEditingInputs(decimal amount, bool blank, string kind)
    {
        var value = Fixture(amount, blank); var before = JsonSerializer.Serialize(value, Json);
        var proof = Assert.IsType<ReviewedSourceTdsEvidence>(Resolve(value));
        Assert.Equal(amount, proof.Amount); Assert.Equal(kind, proof.InputKind);
        Assert.Equal(value.Receipt.Transfer!.SourceHash, proof.SourceHash);
        Assert.Equal("2026-10", proof.PayPeriod); Assert.Equal(97501, proof.EmployeeId);
        Assert.Equal(before, JsonSerializer.Serialize(value, Json));
    }

    [Theory]
    [InlineData("open-ended")]
    [InlineData("dated-policy")]
    [InlineData("unreviewed-input")]
    [InlineData("legacy")]
    [InlineData("other-location")]
    [InlineData("other-client")]
    [InlineData("other-template")]
    [InlineData("other-code")]
    [InlineData("wrong-start")]
    public void UnqualifiedSourceCannotSuppressNormalProjectionEvenForZero(string mode)
    {
        var value = Fixture(0);
        switch (mode)
        {
            case "open-ended": value.Request.TemplateScope!.OpenEnded = true; break;
            case "dated-policy": value.Request.TemplateScope!.PolicyBasis = "DatedPolicy"; break;
            case "unreviewed-input": value.Request.ReviewedFixedInputs = false; break;
            case "legacy": value.Request.TemplateScopeReviewVersion = 0; break;
            case "other-location": value.Employee.WorkLocationId++; break;
            case "other-client": value.Employee.ClientId++; break;
            case "other-template": value.Employee.SalaryStructureId = "97599"; break;
            case "other-code": value.Employee.EmployeeCode = "OTHER"; break;
            case "wrong-start": value.Request.EffectiveFrom = "2026-09-01"; break;
        }
        Assert.Null(Resolve(value));
    }

    [Fact]
    public void PreviousMonthSourceDoesNotBecomeANewMonthTaxExemption() => Assert.Null(Resolve(Fixture(0), period: "2026-11"));

    [Fact]
    public void TemplateDefaultZeroWithoutExplicitSourceFixedInputIsNotAuthority()
    {
        var value = Fixture(0); value.Request.Components[0].SourceColumnIndex = null;
        Assert.Null(Resolve(value));
    }

    [Theory]
    [InlineData("changed-source")]
    [InlineData("changed-input")]
    [InlineData("changed-proof")]
    [InlineData("missing-zero-review")]
    [InlineData("formula-cache")]
    [InlineData("numeric-error")]
    public void StaleOrUnsupportedCashEvidenceRequiresReviewInsteadOfSubstitutingTax(string mode)
    {
        var value = Fixture(0, true);
        switch (mode)
        {
            case "changed-source": value.Batch.Rows[0].NetPay++; break;
            case "changed-input": Assert.Throws<InvalidOperationException>(() => Resolve(value, 1)); return;
            case "changed-proof": value.Receipt.Transfer!.Formulas = [value.Receipt.Transfer.Formulas[0] with { SourceHash = new string('f', 64) }]; break;
            case "missing-zero-review": value.Request.Components[0].BlankInputAsZero = false; break;
            case "formula-cache": value.Source.Rows[0].Cells[0]!.FormulaText = "=A3"; Seal(value); break;
            case "numeric-error": value.Source.Rows[0].Cells[0]!.Error = "#VALUE!"; Seal(value); break;
        }
        Assert.Throws<InvalidOperationException>(() => Resolve(value));
    }

    [Fact]
    public void FormulaTdsAndPeriodAdjustmentsAreNotFrozenIntoLiteralWithholding()
    {
        var formula = Fixture(); formula.Request.Components[0].CalculationType = "Formula";
        Assert.Null(Resolve(formula));
        var adjustment = Fixture(); adjustment.Request.Components[0].IsPeriodAdjustment = true;
        Assert.Null(Resolve(adjustment));
    }

    [Fact]
    public void MultipleSourcePartsCannotElectOneArbitraryWithholding()
    {
        var value = Fixture(); value.Receipt.MasterLinks.Add(value.Receipt.MasterLinks[0]);
        Assert.Null(Resolve(value));
    }
}

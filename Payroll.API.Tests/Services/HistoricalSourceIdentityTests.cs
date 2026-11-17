using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class HistoricalSourceIdentityTests
{
    private const string SourceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class Case
    {
        public ExcelPayslipRow Row = new() { Id = "source-one", SourceRow = 2, EmployeeCode = "SYN-1", EmployeeName = "Synthetic One" };
        public ExcelPayrollSetupRepository.Identity Target = new() { Id = 10, ClientId = 1, EmployeeCode = "SYN-1", Name = "Synthetic One", EligibleForMonth = true };
        public ExcelPayslipBatch Batch;
        public ExcelPayslipCalculationSource Source = new() { Columns = [new() { ColumnIndex = 0, SourceHeader = "Account. No.", Label = "Account No", Kind = "info" }],
            Rows = [new() { SourceRow = 2, Cells = [new() { DataType = "Text", Value = "001234567890" }] }] };
        public HistoricalPeriodSalaryRequest Request;
        public List<ExcelPayrollSetupRepository.Identity> Identities;
        public List<ExcelPayrollSetupRepository.HistoricalBankIdentity> Banks = [new(10, 1, "001234567890")];
        public Case()
        {
            Batch = new() { Id = new string('b', 32), ClientId = 1, Month = "2026-09", Rows = [Row] };
            Identities = [Target];
            Request = new() { Assignments = [new(Row.Id, Target.Id, "100")], SourceIdentityReview = new() { Version = 1, Reviewed = true, SourceHash = SourceHash,
                Reason = "Reviewed exact source bank ownership and unchanged employee code/name.", Bindings = [new(Row.Id, Target.Id, 0, Row.EmployeeName, Target.Name, false)] } };
        }
        public ExcelPayrollSetupRepository.HistoricalVerifiedSourceIdentity Review(string? hash = null) =>
            ExcelPayrollSetupRepository.ReviewHistoricalSourceIdentity(Request, Batch, Source, Row, Target, Identities, Banks, hash ?? SourceHash);
    }

    [Fact]
    public void ExplicitBankReviewIsBoundedAndDefaultIdentityGateStaysUnchanged()
    {
        var data = new Case(); var before = JsonSerializer.Serialize(new { data.Batch, data.Source, data.Target }, Json);
        Assert.Contains(ExcelPayrollSetupRepository.HistoricalIdentityIssues(data.Batch, data.Source, data.Row, data.Target, data.Identities), issue => issue.Contains("source-verified"));
        var reviewed = data.Review();
        Assert.Empty(ExcelPayrollSetupRepository.HistoricalIdentityIssues(data.Batch, data.Source, data.Row, data.Target, data.Identities, reviewed));
        Assert.Equal(before, JsonSerializer.Serialize(new { data.Batch, data.Source, data.Target }, Json));
        Assert.Equal(SourceHash, reviewed.Evidence.SourceHash); Assert.Equal(64, reviewed.Evidence.SourceCellHash.Length);
        Assert.Equal(64, reviewed.Evidence.BankOwnershipHash.Length); Assert.False(reviewed.ReviewedNameChange);
        Assert.DoesNotContain("001234567890", JsonSerializer.Serialize(reviewed.Evidence, Json));
        Assert.Contains(ExcelPayrollSetupRepository.HistoricalIdentityIssues(data.Batch, data.Source, data.Row, data.Target, data.Identities,
            reviewed with { Evidence = reviewed.Evidence with { EmployeeId = 11 } }), issue => issue.Contains("source-verified"));
    }

    [Fact]
    public void NullOptInDoesNotChangeLegacyRequestJson()
    {
        var request = new HistoricalPeriodSalaryRequest();
        Assert.DoesNotContain("sourceIdentityReview", JsonSerializer.Serialize(request, Json));
        ExcelPayrollSetupRepository.ValidateHistoricalSourceIdentityReview(request);
    }

    [Theory]
    [InlineData("1.23456789E+11", "Text", false)]
    [InlineData("1234567890123456", "Number", false)]
    [InlineData("1234567890123456", "Text", true)]
    [InlineData(",001234567890", "Text", true)]
    [InlineData(",001234567890", "Number", false)]
    [InlineData("'001234567890", "Text", true)]
    [InlineData("001,234567890", "Text", false)]
    [InlineData("1234567890.0", "Text", false)]
    [InlineData("000000000000", "Text", false)]
    [InlineData("111111111111", "Text", false)]
    public void LiteralAccountsKeepExactDigitsAndRejectUnreliableRepresentations(string value, string type, bool accepted)
    {
        var data = new Case(); data.Source.Rows[0].Cells[0]!.Value = value; data.Source.Rows[0].Cells[0]!.DataType = type;
        data.Banks = [new(10, 1, ExcelPayrollSetupRepository.HistoricalBankAccount(value))];
        if (accepted) Assert.NotNull(data.Review()); else Assert.Throws<InvalidOperationException>(() => data.Review());
    }

    [Theory]
    [InlineData("formula")][InlineData("error")][InlineData("calculation")][InlineData("missing")][InlineData("bool")]
    public void FormulaCachesErrorsAndMissingInputsCannotEstablishOwnership(string mode)
    {
        var data = new Case(); var cell = data.Source.Rows[0].Cells[0]!;
        switch (mode) { case "formula": cell.FormulaText = "A1"; break; case "error": cell.Error = "#VALUE!"; break;
            case "calculation": cell.CalculationError = "#VALUE!"; break; case "missing": cell.MissingCachedValue = true; break; case "bool": cell.DataType = "Boolean"; break; }
        Assert.Throws<InvalidOperationException>(() => data.Review());
    }

    [Theory]
    [InlineData("shared")][InlineData("other")][InlineData("otherClient")][InlineData("missing")]
    public void ExactUniqueClientBankOwnershipIsRequired(string mode)
    {
        var data = new Case(); data.Banks = mode switch { "shared" => [new(10, 1, "001234567890"), new(11, 1, "001234567890")],
            "other" => [new(11, 1, "001234567890")], "otherClient" => [new(10, 2, "001234567890")], _ => [] };
        Assert.Throws<InvalidOperationException>(() => data.Review());
    }

    [Fact]
    public void ReviewedNameChangeRequiresExactNamesAndIndependentGovernmentOwnership()
    {
        var data = new Case(); data.Target.Name = "Synthetic Corrected";
        data.Request.SourceIdentityReview!.Bindings = [new(data.Row.Id, 10, 0, data.Row.EmployeeName, data.Target.Name, true)];
        Assert.Throws<InvalidOperationException>(() => data.Review());
        data.Target.Uan = "101234567891";
        data.Source.Columns.Add(new() { ColumnIndex = 1, SourceHeader = "UAN", Label = "UAN", Kind = "info" });
        data.Source.Rows[0].Cells.Add(new() { DataType = "Text", Value = data.Target.Uan });
        var reviewed = data.Review(); Assert.True(reviewed.ReviewedNameChange);
        Assert.Empty(ExcelPayrollSetupRepository.HistoricalIdentityIssues(data.Batch, data.Source, data.Row, data.Target, data.Identities, reviewed));
        data.Target.Name = "Different Current Name";
        Assert.Throws<InvalidOperationException>(() => data.Review());
    }

    [Fact]
    public void BankReviewNeverBypassesSharedGovernmentIdentifiers()
    {
        var data = new Case(); data.Target.Uan = "101234567891";
        data.Source.Columns.Add(new() { ColumnIndex = 1, SourceHeader = "UAN", Label = "UAN", Kind = "info" });
        data.Source.Rows[0].Cells.Add(new() { DataType = "Text", Value = data.Target.Uan });
        data.Identities.Add(new() { Id = 11, ClientId = 1, EmployeeCode = "SYN-2", Name = "Synthetic Two", EligibleForMonth = true, Uan = data.Target.Uan });
        Assert.Throws<InvalidOperationException>(() => data.Review());
    }

    [Theory]
    [InlineData("hash")][InlineData("code")][InlineData("name")][InlineData("header")][InlineData("kind")][InlineData("salaryInput")]
    [InlineData("payable")][InlineData("notEmployed")][InlineData("duplicateCode")]
    public void StaleIdentityAndFinancialColumnsCannotBeReinterpretedAsOwnership(string mode)
    {
        var data = new Case();
        switch (mode) { case "hash": data.Request.SourceIdentityReview!.SourceHash = new string('c', 64); break;
            case "code": data.Target.EmployeeCode = "SYN-2"; break; case "name": data.Target.Name = "Other Person"; break;
            case "header": data.Source.Columns[0].SourceHeader = data.Source.Columns[0].Label = "Monthly wage"; break;
            case "kind": data.Source.Columns[0].Kind = "earning"; break;
            case "salaryInput": data.Request.Setup.Components.Add(new() { SourceColumnIndex = 0 }); break;
            case "payable": data.Request.Setup.PayableDaysColumnIndex = 0; break; case "notEmployed": data.Target.EligibleForMonth = false; break;
            case "duplicateCode": data.Identities.Add(new() { Id = 11, ClientId = 1, EmployeeCode = data.Target.EmployeeCode }); break; }
        Assert.Throws<InvalidOperationException>(() => data.Review());
    }

    [Fact]
    public void ConflictingCapturedBankColumnsCannotBeSelectedAround()
    {
        var data = new Case(); data.Source.Columns.Add(new() { ColumnIndex = 1, SourceHeader = "Bank account no", Label = "Bank account no", Kind = "info" });
        data.Source.Rows[0].Cells.Add(new() { DataType = "Text", Value = "009876543210" });
        Assert.Throws<InvalidOperationException>(() => data.Review());
    }

    [Theory]
    [InlineData("unreviewed")][InlineData("version")][InlineData("reason")][InlineData("duplicate")][InlineData("unselected")]
    public void OptInMustBeExplicitBoundedAndSelected(string mode)
    {
        var data = new Case(); var review = data.Request.SourceIdentityReview!;
        switch (mode) { case "unreviewed": review.Reviewed = false; break; case "version": review.Version = 2; break;
            case "reason": review.Reason = ""; break; case "duplicate": review.Bindings.Add(review.Bindings[0]); break;
            case "unselected": data.Request.Assignments.Clear(); break; }
        Assert.Throws<InvalidOperationException>(() => data.Review());
    }
}

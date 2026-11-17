using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecurringContactAllowanceHeaderTests
{
    [Theory]
    [InlineData("Telephone Allowance")]
    [InlineData("Mobile Allowance")]
    [InlineData("Phone Reimbursement")]
    public void ExplicitMonetaryContactAllowanceCanBeAReviewedInfoRuleOrTypedOperand(string header)
    {
        var source = Source(header);
        var row = new ExcelPayslipRow { Id = "synthetic", SourceRow = 2 };
        var request = new ExcelPayrollSetupRequest { ReviewedFixedInputs = true, PayrollDaysColumnIndex = 2, PayableDaysColumnIndex = 3 };
        var proof = ExcelPayrollSetupRepository.TranslateTransferFormulaWithEvidence("A2/C2*D2", request, source, row);
        Assert.Equal("1000/PAYROLL_DAYS*PAYABLE_DAYS", proof.Formula);
        Assert.Equal("1000", Assert.Single(proof.Dependencies).Literal);
        var definition = new NamedFormulaInputDefinition("ALLOWANCE_REFERENCE", "Monthly communication allowance", "Amount", 2, 0, 100000);
        var profile = new NamedFormulaInputProfile(1, 1, "100", "rules", [definition], "declaration");
        var bound = ReviewedNamedFormulaInputs.Bind(profile, [new(definition.Key, 0, 1, header)], source, row, []);
        Assert.Equal(1000, bound.Context.Values[definition.Key]);
        Assert.False(RecurringInputHeaderGuards.HasContactIdentifier(header + " " + header + " (L) " + header));
    }

    [Theory]
    [InlineData("Phone")]
    [InlineData("Telephone Number")]
    [InlineData("Mobile No.")]
    [InlineData("Telephone Allowance / Mobile Number")]
    [InlineData("Telephone Allowance UAN Number")]
    [InlineData("Employee phone allowance")]
    public void BareContactAndMixedIdentifierHeadersStayProtected(string header)
    {
        var source = Source(header);
        var row = new ExcelPayslipRow { Id = "synthetic", SourceRow = 2 };
        var request = new ExcelPayrollSetupRequest { ReviewedFixedInputs = true, PayrollDaysColumnIndex = 2, PayableDaysColumnIndex = 3 };
        Assert.Throws<InvalidOperationException>(() => ExcelPayrollSetupRepository.TranslateTransferFormula("A2/C2*D2", request, source, row));
        var definition = new NamedFormulaInputDefinition("ALLOWANCE_REFERENCE", "Reviewed input", "Amount", 2, 0, 100000);
        var profile = new NamedFormulaInputProfile(1, 1, "100", "rules", [definition], "declaration");
        Assert.Throws<InvalidOperationException>(() => ReviewedNamedFormulaInputs.Bind(profile, [new(definition.Key, 0, 1, header)], source, row, []));
    }

    [Fact]
    public void MonetarySourceHeaderCannotHideAnIdentifierInItsDisplayLabel()
    {
        var source = Source("Telephone Allowance");
        source.Columns[0].Label = "Phone Number";
        var row = new ExcelPayslipRow { Id = "synthetic", SourceRow = 2 };
        var definition = new NamedFormulaInputDefinition("ALLOWANCE_REFERENCE", "Reviewed input", "Amount", 2, 0, 100000);
        Assert.Throws<InvalidOperationException>(() => ReviewedNamedFormulaInputs.Bind(new(1, 1, "100", "rules", [definition], "declaration"),
            [new(definition.Key, 0, 1, "Telephone Allowance")], source, row, []));
    }

    private static ExcelPayslipCalculationSource Source(string header) => new() { HeaderRow = 1, ColumnCount = 4,
        Columns = [new() { ColumnIndex = 0, SourceHeader = header, Label = header, Kind = "info" },
            new() { ColumnIndex = 1, SourceHeader = "Earned allowance", Kind = "earning" }, new() { ColumnIndex = 2, SourceHeader = "Divisor", Kind = "info" },
            new() { ColumnIndex = 3, SourceHeader = "Payable Days", Kind = "info" }],
        Rows = [new() { SourceRow = 1, Cells = [new() { Value = header, DataType = "Text" }, null, null, null] },
            new() { SourceRow = 2, Cells = [new() { Value = "1000", DataType = "Number" }, new() { Value = "900", FormulaText = "A2/C2*D2" }, new() { Value = "30" }, new() { Value = "27" }] }] };
}

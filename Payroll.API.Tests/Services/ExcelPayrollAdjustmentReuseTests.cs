using Payroll.API.Models;
using Payroll.API.Repositories;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollAdjustmentReuseTests
{
    [Fact]
    public void Correction_accepts_only_the_exact_set_of_proven_approved_adjustments()
    {
        Assert.True(ExcelPayrollSetupRepository.AllApprovedAdjustmentsAreReused([17],[17]));
        Assert.True(ExcelPayrollSetupRepository.AllApprovedAdjustmentsAreReused([],[]));
        Assert.False(ExcelPayrollSetupRepository.AllApprovedAdjustmentsAreReused([17,18],[17]));
        Assert.False(ExcelPayrollSetupRepository.AllApprovedAdjustmentsAreReused([17],[]));
        Assert.False(ExcelPayrollSetupRepository.AllApprovedAdjustmentsAreReused([],[17]));
    }
    private const string Batch="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static ExcelPayrollSetupComponent Component()=>new(){Code="TA_DA",Category="Reimbursement",IsPeriodAdjustment=true,Taxable=false};
    private static PayrollAdjustment Existing()=>new(){Id=17,ClientId=7,EmployeeId=11,PayPeriod="2026-09",PayRunType="Regular",
        ComponentCode="TA_DA",AdjustmentType="Reimbursement",Amount=22680,Taxable=false,Status="Approved",ReasonCode="EXCEL_SETUP",
        Notes=$"Reviewed salary Excel {Batch}; import bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb; source row 19"};

    [Fact]
    public void Same_saved_source_approved_literal_is_reused_without_mutating_it()
    {
        var existing=Existing();var before=System.Text.Json.JsonSerializer.Serialize(existing);
        Assert.Same(existing,ExcelPayrollSetupRepository.FindReusableSourceAdjustment(7,"2026-09",Batch,11,19,Component(),22680,[existing]));
        Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize(existing));
    }

    [Theory]
    [InlineData("amount")] [InlineData("category")] [InlineData("tax")] [InlineData("bound")]
    [InlineData("already-applied")] [InlineData("cancelled")] [InlineData("duplicate")] [InlineData("bad-reference")]
    public void Same_source_conflict_never_creates_another_amount(string mutation)
    {
        var a=Existing();var rows=new List<PayrollAdjustment>{a};
        switch(mutation){case "amount":a.Amount++;break;case "category":a.AdjustmentType="Earning";break;
            case "tax":a.Taxable=true;break;case "bound":a.PayRunId=9;break;case "already-applied":a.Status="Applied";break;
            case "cancelled":a.Status="Cancelled";break;case "duplicate":rows.Add(Existing());break;case "bad-reference":a.Notes="Unverified source";break;}
        Assert.ThrowsAny<InvalidOperationException>(()=>ExcelPayrollSetupRepository.FindReusableSourceAdjustment(7,"2026-09",Batch,11,19,Component(),22680,rows));
    }

    [Theory]
    [InlineData("other-month")] [InlineData("other-client")] [InlineData("other-employee")]
    [InlineData("other-row")] [InlineData("other-batch")] [InlineData("other-code")]
    public void Unrelated_records_are_neither_reused_nor_modified(string mutation)
    {
        var a=Existing();switch(mutation){case "other-month":a.PayPeriod="2026-10";break;case "other-client":a.ClientId=8;break;
            case "other-employee":a.EmployeeId=12;break;case "other-row":a.Notes=a.Notes.Replace("row 19","row 20");break;
            case "other-batch":a.Notes=a.Notes.Replace(Batch,new string('c',32));break;case "other-code":a.ComponentCode="OTHER";break;}
        var before=System.Text.Json.JsonSerializer.Serialize(a);
        Assert.Null(ExcelPayrollSetupRepository.FindReusableSourceAdjustment(7,"2026-09",Batch,11,19,Component(),22680,[a]));
        Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize(a));
    }
}

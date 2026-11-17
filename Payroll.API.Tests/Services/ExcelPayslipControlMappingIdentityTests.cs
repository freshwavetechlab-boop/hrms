using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipControlMappingIdentityTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static ExcelPayslipRow Row()=>new(){Id="owned-row",SourceRow=2,EmployeeCode="SYN-01",EmployeeName="Synthetic person",
        Information=[new(){Label="PAN",Value="ABCDE1234F"}],Earnings=[new(){Label="Basic",Amount=1000.123456789m},new(){Label="Allowance",Amount=20}],
        Deductions=[new(){Label="PF",Amount=10}],EmployerContributions=[new(){Label="Employer PF",Amount=50}],DeclaredGross=1000.123456789m,DeclaredDeductions=10,NetPay=1010.123456789m};
    private static ExcelPayslipRow Clone(ExcelPayslipRow row)=>JsonSerializer.Deserialize<ExcelPayslipRow>(JsonSerializer.Serialize(row,Json),Json)!;
    private static ExcelPayslipBatch Batch(ExcelPayslipRow row,int client=42)=>new(){Id=new('a',32),ClientId=client,Month="2026-10",Rows=[row]};
    private static void Review(ExcelPayslipRow row)=>row.WarningReview=new(ExcelPayslipRepository.NegativeAmountHash(row),"stored@example.invalid",DateTime.UtcNow,ExcelPayslipRepository.SourceRowWarnings(row));

    [Fact]
    public void Control_remap_does_not_restore_a_saved_negative_amount_approval()
    {
        var old=Row();old.Deductions[0].Amount=-10;
        old.NegativeAmountReview=new(ExcelPayslipRepository.NegativeAmountHash(old),"stored-negative@example.invalid",DateTime.UtcNow);Review(old);
        var incoming=Clone(old);incoming.DeclaredGross=null;incoming.NegativeAmountReview=null;incoming.WarningReview=null;
        ExcelPayslipRepository.ReusePayslipIdentity(Batch(incoming),Batch(old));
        Assert.Equal(old.Id,incoming.Id);Assert.Null(incoming.NegativeAmountReview);Assert.False(ExcelPayslipRepository.HasCurrentNegativeAmountReview(incoming));Assert.Null(incoming.WarningReview);
        Assert.Contains(ExcelPayslipRepository.NegativeAmountWarning,incoming.Warnings);
    }

    [Theory]
    [InlineData("grossInfo")]
    [InlineData("grossAmount")]
    [InlineData("deductionsInfo")]
    [InlineData("deductionsAmount")]
    public void Same_cash_control_remap_keeps_owned_delivery_id_but_invalidates_saved_reviews_and_pdf(string change)
    {
        var old=Row();Review(old);var incoming=Clone(old);incoming.Id="unowned-browser-id";
        incoming.NegativeAmountReview=null;incoming.WarningReviewToken="foreign-token";
        if(change=="grossInfo"){incoming.Information.Add(new(){Label="Gross subtotal A",Value=old.DeclaredGross!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)});incoming.DeclaredGross=null;}
        if(change=="grossAmount")incoming.DeclaredGross+=1;
        if(change=="deductionsInfo")incoming.DeclaredDeductions=null;
        if(change=="deductionsAmount")incoming.DeclaredDeductions+=1;
        var oldHash=ExcelPayslipRepository.NegativeAmountHash(old);var oldPdf=NotificationRepository.ExcelPayslipBatchHash(Batch(old));
        ExcelPayslipRepository.ReusePayslipIdentity(Batch(incoming),Batch(old));
        Assert.Equal(old.Id,incoming.Id);Assert.Null(incoming.WarningReview);Assert.Null(incoming.WarningReviewToken);Assert.Null(incoming.NegativeAmountReview);
        Assert.NotEqual(oldHash,ExcelPayslipRepository.NegativeAmountHash(incoming));
        Assert.NotEqual(oldPdf,NotificationRepository.ExcelPayslipBatchHash(Batch(incoming)));
        Assert.Equal(old.Earnings.Select(x=>(x.Label,x.Amount)),incoming.Earnings.Select(x=>(x.Label,x.Amount)));
        Assert.Equal(old.Deductions.Select(x=>(x.Label,x.Amount)),incoming.Deductions.Select(x=>(x.Label,x.Amount)));
        Assert.Equal(old.EmployerContributions.Select(x=>(x.Label,x.Amount)),incoming.EmployerContributions.Select(x=>(x.Label,x.Amount)));
    }

    [Theory]
    [InlineData("earningAmount")]
    [InlineData("deductionAmount")]
    [InlineData("employerAmount")]
    [InlineData("net")]
    [InlineData("earningLabel")]
    [InlineData("deductionLabel")]
    [InlineData("employerLabel")]
    [InlineData("order")]
    [InlineData("category")]
    [InlineData("count")]
    public void Any_actual_cash_or_ordered_component_change_retains_existing_new_id_behavior(string change)
    {
        var old=Row();Review(old);var incoming=Clone(old);incoming.DeclaredGross=null;
        switch(change){case "earningAmount":incoming.Earnings[0].Amount+=.01m;break;case "deductionAmount":incoming.Deductions[0].Amount+=.01m;break;
            case "employerAmount":incoming.EmployerContributions[0].Amount+=.01m;break;case "net":incoming.NetPay+=.01m;break;
            case "earningLabel":incoming.Earnings[0].Label="Other";break;case "deductionLabel":incoming.Deductions[0].Label="Other";break;case "employerLabel":incoming.EmployerContributions[0].Label="Other";break;
            case "order":incoming.Earnings.Reverse();break;case "category":var moved=incoming.Earnings[1];incoming.Earnings.RemoveAt(1);incoming.EmployerContributions.Add(moved);break;default:incoming.Earnings.Add(new(){Label="Additional",Amount=0});break;}
        ExcelPayslipRepository.ReusePayslipIdentity(Batch(incoming),Batch(old));
        Assert.NotEqual(old.Id,incoming.Id);Assert.Null(incoming.WarningReview);Assert.Null(incoming.WarningReviewToken);
    }

    [Theory]
    [InlineData("sourceRow")]
    [InlineData("code")]
    [InlineData("name")]
    [InlineData("client")]
    public void A_foreign_control_review_never_owns_saved_delivery_identity(string difference)
    {
        var old=Row();Review(old);var incoming=Clone(old);incoming.Id="incoming-owned-only";incoming.DeclaredGross=null;incoming.WarningReviewToken="foreign-review";
        if(difference=="sourceRow")incoming.SourceRow++;if(difference=="code")incoming.EmployeeCode="OTHER";if(difference=="name")incoming.EmployeeName="Another person";
        ExcelPayslipRepository.ReusePayslipIdentity(Batch(incoming,difference=="client"?43:42),Batch(old));
        Assert.Equal("incoming-owned-only",incoming.Id);Assert.Null(incoming.WarningReview);Assert.Null(incoming.WarningReviewToken);
    }
}

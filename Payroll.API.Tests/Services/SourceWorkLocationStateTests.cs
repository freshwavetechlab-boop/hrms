using Payroll.API.Models;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class SourceWorkLocationStateTests
{
    [Fact]
    public void ReviewedWorkStateCanRemoveDefaultStateFeeWithoutChangingSourceSalaryOrPresence()
    {
        var before=Build(workState:"");
        var after=Build(workState:"Synthetic reviewed distinct region");
        Assert.Equal(200m,before.StatutoryDeductions);
        Assert.Equal(0m,after.StatutoryDeductions);
        Assert.Equal(before.GrossPay,after.GrossPay);
        Assert.Equal(before.PresentDays,after.PresentDays);
        Assert.Equal(before.PayableDays,after.PayableDays);
        Assert.Equal(before.NetPay+200m,after.NetPay);
    }

    [Fact]
    public void ExplicitPersonalStateAndExistingManualFeeAreNotErasedByWorkStateReview()
    {
        var personal=Build("Synthetic reviewed distinct region",personalState:"Synthetic state");
        Assert.Equal(200m,personal.StatutoryDeductions);
        var manual=Build("Synthetic reviewed distinct region",seeded:75);
        Assert.Equal(75m,manual.StatutoryDeductions);
        var configured=ReviewedPeriodProfessionalTaxTests.Build(null,ptValue:"75",ptLine:true,
            workState:"Synthetic reviewed distinct region");
        Assert.Equal(75m,configured.StatutoryDeductions);
    }

    [Fact]
    public void AStateWithAnApplicablePolicyKeepsItsFeeInsteadOfBlanketZero()
    {
        var actual=Build("Synthetic state");
        Assert.Equal(200m,actual.StatutoryDeductions);
        Assert.Equal(29800m,actual.NetPay);
    }

    private static PayRunEmployee Build(string workState,string personalState="",decimal seeded=0) =>
        ReviewedPeriodProfessionalTaxTests.Build(null,ptLine:false,workState:workState,personalState:personalState,seeded:seeded);
}

using System.Text.Json;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class CombinedLevyPrecedenceTests
{
    [Theory]
    [InlineData("PT_LWF_WC",true)]
    [InlineData("PT_LWF_WC",false)]
    [InlineData("WORKMEN_COMP_LWF_PT",true)]
    [InlineData("WORKMEN_COMP_LWF_PT",false)]
    public void ExplicitEmployeeZeroCombinedInputReplacesLegacySeedWithoutAddingAutomaticPt(string code,bool personalDetails)
    {
        var row=ReviewedPeriodProfessionalTaxTests.Build(null,storedPt:0,seeded:208,mixed:true,mixedCode:code,personalDetails:personalDetails);
        Assert.Equal(30000,row.GrossPay);Assert.Equal(0,row.StatutoryDeductions);Assert.Equal(30000,row.NetPay);
        using var json=JsonDocument.Parse(row.DetailsJson);
        var line=Assert.Single(json.RootElement.EnumerateArray(),l=>l.GetProperty("Category").GetString()=="Deduction");
        Assert.Equal(code,line.GetProperty("Id").GetString());Assert.Equal(0,line.GetProperty("amount").GetDecimal());
        var evidence=Assert.Single(json.RootElement.EnumerateArray(),l=>l.GetProperty("Id").GetString()=="COMBINED_LEVY_INPUT");
        Assert.Equal("Summary",evidence.GetProperty("Category").GetString());Assert.Equal(208,evidence.GetProperty("legacySeededAmount").GetDecimal());
    }

    [Theory]
    [InlineData("PT_LWF_WC")]
    [InlineData("WORKMEN_COMP_LWF_PT")]
    public void PositiveCombinedChargeIsNotAddedAgainFromLegacyPersonalField(string code)
    {
        var row=ReviewedPeriodProfessionalTaxTests.Build(null,storedPt:450,seeded:450,mixed:true,mixedCode:code);
        Assert.Equal(450,row.StatutoryDeductions);Assert.Equal(29550,row.NetPay);
        using var json=JsonDocument.Parse(row.DetailsJson);
        Assert.Single(json.RootElement.EnumerateArray(),l=>l.GetProperty("Category").GetString()=="Deduction");
    }

    [Theory]
    [InlineData("PT_LWF_WC",0,200)]
    [InlineData("WORKMEN_COMP_LWF_PT",0,200)]
    [InlineData("PT_LWF_WC",208,208)]
    [InlineData("WORKMEN_COMP_LWF_PT",208,208)]
    public void UnfilledTemplateZeroRetainsExistingAutomaticOrSeededPt(string code,int seed,int expected)
    {
        var row=ReviewedPeriodProfessionalTaxTests.Build(null,seeded:seed,mixed:true,mixedCode:code);
        Assert.Equal(expected,row.StatutoryDeductions);Assert.Equal(30000-expected,row.NetPay);
        Assert.DoesNotContain("COMBINED_LEVY_INPUT",row.DetailsJson);
    }

    [Theory]
    [InlineData("PT_LWF_WC")]
    [InlineData("WORKMEN_COMP_LWF_PT")]
    public void FormulaZeroIsAComputedRuleAndDoesNotFallBackToAnOlderSeed(string code)
    {
        var row=ReviewedPeriodProfessionalTaxTests.Build(null,ptType:"Formula",ptValue:"IF(BASIC_EARNED>=30000,0,125)",seeded:208,mixed:true,mixedCode:code);
        Assert.Equal(0,row.StatutoryDeductions);Assert.Equal(30000,row.NetPay);
    }

    [Fact]
    public void ManualVariableZeroStillUsesExistingSeedUntilAnActualChargeIsSupplied()
    {
        var row=ReviewedPeriodProfessionalTaxTests.Build(null,ptType:"Manual / Variable",storedPt:0,seeded:208,mixed:true);
        Assert.Equal(208,row.StatutoryDeductions);Assert.Equal(29792,row.NetPay);
    }

    [Fact]
    public void UnrelatedZeroDeductionDoesNotSuppressPt()
    {
        var row=ReviewedPeriodProfessionalTaxTests.Build(null,storedPt:0,seeded:208,mixed:true,mixedCode:"INSURANCE");
        Assert.Equal(208,row.StatutoryDeductions);Assert.Equal(29792,row.NetPay);
    }

    [Theory]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public void DuplicateCombinedOrSeparatePtAuthoritiesRequireExplicitCorrection(bool duplicate,bool separate)
    {
        var error=Assert.Throws<InvalidOperationException>(()=>ReviewedPeriodProfessionalTaxTests.Build(null,storedPt:0,seeded:208,mixed:true,duplicateCombined:duplicate,separatePtLine:separate));
        Assert.Contains("one combined",error.Message);
    }

    [Fact]
    public void ExistingReviewedPeriodControlIsNotSilentlyOverriddenByCombinedComponent()
    {
        Assert.Throws<InvalidOperationException>(()=>ReviewedPeriodProfessionalTaxTests.Build(ReviewedPeriodProfessionalTaxTests.Control(),storedPt:0,mixed:true,mixedCode:"WORKMEN_COMP_LWF_PT"));
    }
}

using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed partial class ExcelPayrollTransferTests
{
    private static (ExcelPayrollSetupRepository.Plan Plan,ExcelPayrollSetupRequest Request,JsonObject Old) DistinctFixture()
    {
        var(plan,request)=Fixture();request.UpdateExistingConfiguration=true;request.TemplateScopeReviewVersion=1;
        request.TemplateScope=new(){Reviewed=true,EffectiveTo="2026-09-30",LocationMode="AllClientLocations",CategoryKind="AllClientCategories",
            DivisorMeaning="ContractFixedDays",PayableMeaning="AlreadyPayable",PolicyBasis="SavedSourceRules"};
        request.TemplateDecisions[0].ScopeAction="DifferentScope";
        request.TemplateDecisions[0].ScopeReason="Captured source PF rules differ from the shared legacy template; preserve that template for other employees.";
        plan.Rows[0].ExistingTemplateId="500";plan.Rows[0].Employee.SalaryStructureId="500";plan.Rows[0].Employee.ClientId=42;
        var old=Template();old["lines"]![1]!["formula"]="BASIC_EARNED*10%";return(plan,request,old);
    }

    [Fact]
    public void ExplicitDifferentCapturedRulesCreateOneReusableVariantWithoutUpdatingTheSharedTemplate()
    {
        var(p,r,old)=DistinctFixture();var before=old.ToJsonString();
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[old]);
        Assert.True(p.Preview.CanApply,string.Join("; ",p.Preview.Issues.Where(i=>i.Severity=="Error").Select(i=>i.Message)));
        Assert.Equal("Create reviewed variant",Assert.Single(p.Preview.Transfer!.Templates).Action);
        Assert.Equal("1000",p.Rows[0].TemplateId);Assert.Equal("500",p.Rows[0].Employee.SalaryStructureId);
        Assert.Empty(p.UpdatedTemplateIds);Assert.Empty(p.ReusedTemplateIds);
        Assert.Equal(before,old.ToJsonString());Assert.All(p.Preview.Transfer.Formulas,f=>Assert.Equal("Exact supported translation",f.Status));
        Assert.Equal(88m,p.Preview.Rows.Single().Net);
    }

    [Theory]
    [InlineData("scopeReview")]
    [InlineData("sourceReview")]
    [InlineData("fixedInputs")]
    [InlineData("policyReview")]
    [InlineData("scopeAction")]
    [InlineData("reason")]
    public void ConfiguredEmployeeDoesNotGetANewVariantWithoutCompleteExplicitDifferentRuleReview(string missing)
    {
        var(p,r,old)=DistinctFixture();
        switch(missing){case "scopeReview":r.TemplateScopeReviewVersion=0;break;case "sourceReview":r.TransferReviewVersion=0;break;
            case "fixedInputs":r.ReviewedFixedInputs=false;break;case "policyReview":r.ReviewedPolicyScope=false;break;
            case "scopeAction":r.TemplateDecisions[0].ScopeAction="";break;case "reason":r.TemplateDecisions[0].ScopeReason=" ";break;}
        Assert.False(ExcelPayrollSetupRepository.DistinctReviewedSourceVariant(r,r.TemplateDecisions[0],
            [new("500","Legacy",false,["Different formula"],[]){ConfigurationExact=false}],1));
        if(missing=="sourceReview")return; // Legacy v0 contract has its independent validation path.
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[old]);Assert.False(p.Preview.CanApply);
        Assert.Empty(p.UpdatedTemplateIds);Assert.Equal("500",p.Rows[0].Employee.SalaryStructureId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualRulesCannotBeCopiedEvenWhenApplicabilityOrUnusedInputFallbackDiffers(bool unusedFallback)
    {
        var(p,r,_)=DistinctFixture();var same=Template();if(unusedFallback)same["lines"]![0]!["value"]="250";
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[same]);Assert.False(p.Preview.CanApply);
        Assert.True(Assert.Single(p.Preview.Transfer!.Templates[0].Candidates).ConfigurationExact);
        Assert.Equal("500",p.Rows[0].Employee.SalaryStructureId);Assert.Empty(p.UpdatedTemplateIds);
    }

    [Fact]
    public void DistinctScopeCannotHideAnUnsupportedSourceFormulaDespiteEqualCachedMoney()
    {
        var(p,r,old)=DistinctFixture();p.Source.Rows[0].Cells[4]!.FormulaText="VLOOKUP(C3,Prior!A1:B4,2,0)";
        ExcelPayrollSetupRepository.FinalizeTransferReview(p,r,42,[old]);Assert.False(p.Preview.CanApply);
        Assert.Contains(p.Preview.Transfer!.Formulas,f=>f.Status=="Blocked");Assert.Empty(p.UpdatedTemplateIds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ExplicitDifferentScopeDoesNotAuthorizeMultipleOrMissingRuleFamilies(int count)
    {
        var(_,r,_)=DistinctFixture();Assert.False(ExcelPayrollSetupRepository.DistinctReviewedSourceVariant(r,r.TemplateDecisions[0],
            [new("500","Legacy",false,["Different formula"],[]){ConfigurationExact=false}],count));
    }
}

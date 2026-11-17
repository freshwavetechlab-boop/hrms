using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Xunit;

namespace Payroll.API.Tests.Services;

public sealed class ReviewedTemplateFormulaReuseTests
{
    [Theory]
    [InlineData("BASIC_MONTHLY*40%","(+BASIC_MONTHLY*40%)")]
    [InlineData("ROUND(A)","+(ROUND(+(A),+0))")]
    [InlineData("A+-B","+(A+(+-B))")]
    [InlineData("IF(A>0,B,C)","+IF(+(A)>+0,+B,+C)")]
    [InlineData("TRUE","+(TRUE)")]
    public void ReviewedComparisonDropsOnlyTheEvaluatorsExactIdentityUnaryPlus(string actual,string source)
    {
        Assert.NotEqual(PayrollFormulaEvaluator.Canonicalize(actual),PayrollFormulaEvaluator.Canonicalize(source));
        Assert.NotEqual(PayrollFormulaEvaluator.CanonicalStructure(actual),PayrollFormulaEvaluator.CanonicalStructure(source));
        Assert.Equal(PayrollFormulaEvaluator.ReviewedTemplateStructure(actual),PayrollFormulaEvaluator.ReviewedTemplateStructure(source));
    }
    [Theory]
    [InlineData("A+B","A-B")][InlineData("A","-A")][InlineData("A+B","B+A")]
    [InlineData("A-(B-C)","(A-B)-C")][InlineData("A*40%","A*0.4")]
    [InlineData("A/PAYROLL_DAYS","A/PAYABLE_DAYS")][InlineData("ROUND(A)","ROUNDUP(A)")]
    [InlineData("IF(A>0,B,C)","IF(A>0,C,B)")][InlineData("A>0","0<A")]
    public void OperatorOrderPercentCapsArgumentsAndVariableIdentitiesRemainDistinct(string a,string b)
    {Assert.NotEqual(PayrollFormulaEvaluator.ReviewedTemplateStructure(a),PayrollFormulaEvaluator.ReviewedTemplateStructure(b));}
    [Fact]public void ComparisonParsesWithoutEvaluatingUnknownVariablesOrZeroDivision()
    {Assert.Equal(PayrollFormulaEvaluator.ReviewedTemplateStructure("UNKNOWN/0"),PayrollFormulaEvaluator.ReviewedTemplateStructure("+(UNKNOWN)/+0"));}
    [Theory][InlineData("EXEC(A)")][InlineData("A+")][InlineData("'text'")]
    public void InvalidAndUnsupportedRulesNeverCompare(string value)
    {Assert.Throws<InvalidOperationException>(()=>PayrollFormulaEvaluator.ReviewedTemplateStructure(value));}
    private static (JsonObject Template,ExcelPayrollSetupRepository.ResolvedComponent[] Proposed,ExcelPayrollSetupRepository.ResolvedComponent[] Actual) Fixture()
    {
        var definition=new ExcelPayrollSetupComponent{Code="HRA",ExistingComponentId="102",Name="HRA",Category="Earning",CalculationType="Formula",Formula="(+BASIC_MONTHLY*40%)",Value="0",ProRata=false,Priority=20};
        var actual=JsonSerializer.Deserialize<ExcelPayrollSetupComponent>(JsonSerializer.Serialize(definition))!;actual.Formula="BASIC_MONTHLY*40%";
        return(new(){["id"]="900",["lines"]=new JsonArray(new JsonObject{["componentId"]="102",["value"]="0",["calculationType"]="Formula",["formula"]=actual.Formula,["baseComponent"]="",["proRataOverride"]="false",["roundingMode"]=""})},[new(definition,"102",false)],[new(actual,"102",false)]);
    }
    [Fact]public void OrdinaryExactComparisonRemainsUnchangedUntilExplicitReviewedReuse()
    {
        var f=Fixture();var before=JsonSerializer.Serialize(new{f.Template,f.Proposed});
        Assert.Contains("HRA: formula differs",ExcelPayrollSetupRepository.TransferTemplateDifferences(f.Template,f.Proposed,true));
        Assert.Empty(ExcelPayrollSetupRepository.TransferTemplateDifferences(f.Template,f.Proposed,true,true));
        Assert.Equal(before,JsonSerializer.Serialize(new{f.Template,f.Proposed}));
    }
    [Fact]public void ReuseScopeFingerprintReflectsActualExistingRulesWithoutMutatingSourceSignatures()
    {
        var f=Fixture();var raw=ExcelPayrollSetupRepository.TransferRulesSignature(f.Proposed);
        Assert.Equal(raw,ExcelPayrollSetupRepository.ReusedTemplateRulesSignature(f.Template,f.Proposed));
        Assert.Equal(ExcelPayrollSetupRepository.TransferRulesSignature(f.Actual),ExcelPayrollSetupRepository.ReusedTemplateRulesSignature(f.Template,f.Proposed,true));
        Assert.Equal(raw,ExcelPayrollSetupRepository.TransferRulesSignature(f.Proposed));
    }
    [Theory][InlineData("calculationType","Fixed Amount")][InlineData("componentId","999")][InlineData("proRataOverride","true")][InlineData("roundingMode","Round Down")]
    public void StructuralIdentityCannotHideOtherTemplateDifferences(string key,string value)
    {var f=Fixture();f.Template["lines"]![0]![key]=value;Assert.Contains(ExcelPayrollSetupRepository.TransferTemplateDifferences(f.Template,f.Proposed,true,true),d=>d.EndsWith(key+" differs"));}
}

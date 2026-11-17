using System.Text.Json;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class PayrollFormulaStructureTests
{
    public static IEnumerable<object[]> CapturedLayoutVariants()
    {
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","Payroll","location-formula-layout-variants.json")));
        return document.RootElement.GetProperty("pairs").EnumerateArray().Select(pair=>new object[]{pair.GetProperty("case").GetString()!,pair.GetProperty("configured").GetString()!,pair.GetProperty("captured").GetString()!}).ToArray();
    }

    [Theory]
    [MemberData(nameof(CapturedLayoutVariants))]
    public void Actual_captured_layout_variants_share_tree_without_changing_raw_formula_or_strict_fingerprint(string _,string configured,string captured)
    {
        var first=configured;var second=captured;
        Assert.NotEqual(PayrollFormulaEvaluator.Canonicalize(configured),PayrollFormulaEvaluator.Canonicalize(captured));
        Assert.Equal(PayrollFormulaEvaluator.CanonicalStructure(configured),PayrollFormulaEvaluator.CanonicalStructure(captured));
        Assert.Equal(first,configured);Assert.Equal(second,captured);
    }

    [Theory]
    [InlineData("A+B*C","(A+B)*C")]
    [InlineData("A+B+C","A+(B+C)")]
    [InlineData("A*B*C","A*(B*C)")]
    [InlineData("A/B*C","A/(B*C)")]
    [InlineData("A+B","B+A")]
    [InlineData("+A","A")]
    [InlineData("-A","A")]
    [InlineData("20%","20/100")]
    [InlineData("BASE*25%","BASE*20%")]
    [InlineData("RATE*PAYABLE_DAYS/PAYROLL_DAYS","RATE*PAYABLE_DAYS/30")]
    [InlineData("RATE*PAYABLE_DAYS/PAYROLL_DAYS","RATE*PRESENT_DAYS/PAYROLL_DAYS")]
    [InlineData("IF(BASE<=15000,BASE,15000)","IF(BASE<15000,BASE,15000)")]
    [InlineData("MIN(PF_BASE,15000)*12%","MIN(PF_BASE,25000)*12%")]
    [InlineData("MIN(PF_BASE,15000)*12%","MIN(PF_BASE,15000)*13%")]
    [InlineData("MIN(PF_BASE,15000)*12%","PF_BASE*12%")]
    [InlineData("MIN(PF_BASE,15000)","MIN(15000,PF_BASE)")]
    [InlineData("ROUND(A)","ROUND(A,2)")]
    [InlineData("ROUND(A)","ROUNDUP(A)")]
    [InlineData("ROUNDUP(A)","ROUNDUP(A,0)")]
    [InlineData("ROUND(A)","ROUND(A,0+0)")]
    [InlineData("ROUND(A)","ROUND(A,-0)")]
    [InlineData("IF(A,B)","IF(A,B,FALSE)")]
    public void Financial_rule_or_operator_changes_never_establish_equivalence(string first,string second)=>
        Assert.NotEqual(PayrollFormulaEvaluator.CanonicalStructure(first),PayrollFormulaEvaluator.CanonicalStructure(second));

    [Fact]
    public void Signature_does_not_evaluate_unknown_variables_or_division_and_retains_function_semantics()
    {
        Assert.Equal(PayrollFormulaEvaluator.CanonicalStructure("IF(UNKNOWN_VARIABLE,ROUND(1/0),MAX(A,B))"),
            PayrollFormulaEvaluator.CanonicalStructure("(IF((UNKNOWN_VARIABLE),(ROUND((1)/(0),0)),(MAX((A),(B)))))"));
        Assert.Equal(PayrollFormulaEvaluator.Evaluate("ROUND(-1.5)",_=>0),PayrollFormulaEvaluator.Evaluate("ROUND(-1.5,0)",_=>0));
        Assert.Equal(-2,PayrollFormulaEvaluator.Evaluate("ROUND(-1.5)",_=>0));
        Assert.Equal(12,PayrollFormulaEvaluator.Evaluate("IF(FALSE,1/0,ROUND(12.4))",_=>0));
    }

    [Theory]
    [InlineData("UNKNOWN_FN(A)")]
    [InlineData("ROUND(A,0,0)")]
    [InlineData("A+(")]
    public void Unsupported_or_invalid_formulas_are_rejected(string formula)=>Assert.Throws<InvalidOperationException>(()=>PayrollFormulaEvaluator.CanonicalStructure(formula));
}

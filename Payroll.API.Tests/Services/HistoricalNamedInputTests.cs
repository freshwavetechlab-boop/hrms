using System.Text.Json;
using System.Text.Json.Nodes;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class HistoricalNamedInputTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static HistoricalPeriodSalaryRequest Request()=>new(){Assignments=[new("row",10,"20")],Setup=new(){SourceColumnRoles=new(){[4]="info"}},
        NamedInputReview=new(){TemplateId="20",DeclarationHash=new('a',64),Bindings=[new("PRIOR_SALARY_BASE",4,1,"Last Month Salary")]}};
    [Fact]
    public void Absent_optional_history_properties_keep_exact_legacy_wire_shapes()
    {
        var q=new HistoricalPeriodSalaryRequest();Assert.Equal(JsonSerializer.Serialize(new{q.Setup,q.Assignments,q.SalaryTargetMode},Json),JsonSerializer.Serialize(q,Json));
        var receipt=new HistoricalPeriodSalaryReceipt();Assert.Equal(JsonSerializer.Serialize(new{receipt.ClientId,receipt.BatchId,receipt.PayPeriod,receipt.RequestId,receipt.Fingerprint,receipt.AppliedAtUtc,receipt.Replayed,receipt.Rows},Json),JsonSerializer.Serialize(receipt,Json));
        Assert.DoesNotContain("namedInputs",JsonSerializer.Serialize(new HistoricalPeriodSalaryPreviewRow(),Json));
    }
    [Theory][InlineData("declaration")][InlineData("template")][InlineData("duplicate")][InlineData("header")][InlineData("role")][InlineData("input")][InlineData("output")]
    public void Historical_operand_review_requires_one_exact_template_and_independent_info_cell(string mutation)
    {
        var q=Request();var r=q.NamedInputReview!;
        switch(mutation){case "declaration":r.DeclarationHash=new('A',64);break;case "template":r.TemplateId="21";break;
            case "duplicate":r.Bindings.Add(r.Bindings[0]);break;case "header":r.Bindings[0]=r.Bindings[0] with{HeaderRow=0};break;
            case "role":q.Setup.SourceColumnRoles[4]="earning";break;case "input":q.Setup.Components.Add(new(){SourceColumnIndex=4});break;
            case "output":q.Setup.Components.Add(new(){ExpectedColumnIndex=4});break;}
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateHistoricalNamedInputReview(q));
    }
    [Fact]
    public void Exact_review_is_opt_in_and_operand_never_becomes_global_or_component_input()
    {
        ExcelPayrollSetupRepository.ValidateHistoricalNamedInputReview(Request());
        ExcelPayrollSetupRepository.ValidateHistoricalNamedInputReview(new());
        Assert.DoesNotContain("PRIOR_SALARY_BASE",PayrollValueCatalogue.Names);
        var nullContext=ReviewedNamedFormulaInputs.Signature("legacy",null);Assert.Equal("legacy",nullContext);
        var context=new NamedFormulaInputContext{Definitions=[new("PRIOR_SALARY_BASE","Previous salary","Amount",8,0,1_000_000_000)],Values=new(){["PRIOR_SALARY_BASE"]=38000},DeclarationHash=new('a',64)};
        Assert.NotEqual("legacy",ReviewedNamedFormulaInputs.Signature("legacy",context));
        Assert.False(context.Values.ContainsKey("MONTHLY_CTC"));Assert.All(context.Definitions,d=>Assert.Equal("FormulaOperandOnly",d.Purpose));
    }
    public static IEnumerable<object[]> ActualSourceFive()
    {
        yield return ["2026-08:65",38000m,31m,29m,0m,15000m,false,35548.38709677419m,1800m,0m,33748.38709677419m,1950m,0m];
        yield return ["2026-08:66",85000m,31m,27m,0m,15000m,false,74032.25806451614m,1800m,0m,72232.25806451614m,1950m,0m];
        yield return ["2026-09:65",38000m,30m,30m,0m,15000m,false,38000m,1800m,0m,36200m,1950m,0m];
        yield return ["2026-09:66",85000m,30m,30m,0m,15000m,false,85000m,1800m,0m,83200m,1950m,0m];
        yield return ["2026-10:65",15000m,31m,21m,200m,25000m,true,9961m,1195.354839m,75m,8891m,1295m,324m];
    }
    [Theory][MemberData(nameof(ActualSourceFive))]
    public void Actual_five_sources_use_two_real_reusable_rule_families_and_no_added_operand_earning(string source,decimal prior,decimal days,decimal payable,
        decimal bonus,decimal cap,bool esi,decimal h,decimal j,decimal k,decimal net,decimal m,decimal n)
    {
        var capText=cap.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var rules=new[]{"PRIOR_SALARY_BASE/PAYROLL_DAYS*PAYABLE_DAYS-S_BONUS_EARNED","",
            $"IF(S_BASIC_EARNED>{capText},{capText}*12%,S_BASIC_EARNED*12%)",esi?"S_BASIC_EARNED*0.75%":"0",
            $"IF(S_BASIC_EARNED>{capText},{capText}*13%,S_BASIC_EARNED*13%)",esi?"S_BASIC_EARNED*3.25%":"0"};
        var codes=new[]{"S_BASIC","S_BONUS","S_PF","S_ESI","S_EPF","S_EESI"};
        var components=codes.Select((code,i)=>new PayrollReviewComponent((i+1).ToString(),code,i<2?"Earning":i<4?"Deduction":"Benefit",
            i<2?"Regular Earning":i<4?"Regular Deduction":"Employer Benefit","None",i==1?"Fixed Amount":"Formula",i==1?bonus.ToString(System.Globalization.CultureInfo.InvariantCulture):"0",rules[i],"",false,false,true,i+1,"Fixed Pay")).ToList();
        var context=new NamedFormulaInputContext{Definitions=[new("PRIOR_SALARY_BASE","Last Month Salary","Amount",8,0,1_000_000_000)],Values=new(){["PRIOR_SALARY_BASE"]=prior},DeclarationHash=new('a',64)};
        var actual=PayRunRepository.SimulateTemplate(new(){Mode="Template",ClientId=64,TemplateId="generic-source-family",AnnualCtc=prior*12,PayrollDays=(int)days,PresentDays=payable,PayableDays=payable,NamedInputs=context,
            Snapshot=new("Template",64,"",prior*12,components,components.Select((c,i)=>new PayrollReviewLine(c.Id,c.Value,rules[i],c.CalculationType,"","false")).ToList())});
        Assert.True(actual.Complete,source+": "+string.Join(";",actual.Issues.Select(i=>i.Message)));Assert.Equal(6,actual.Lines.Count);
        var expected=new[]{h,bonus,j,k,m,n};
        for(var i=0;i<codes.Length;i++)Assert.InRange(Math.Abs(actual.Lines.Single(l=>l.Code==codes[i]).Earned-expected[i]),0m,1m);
        foreach(var pair in new[]{(actual.Gross,h+bonus),(actual.Deductions,j+k),(actual.Benefits,m+n),(actual.Net,net)})Assert.InRange(Math.Abs(pair.Item1!.Value-pair.Item2),0m,1m);
        Assert.DoesNotContain(actual.Lines,l=>l.Code=="PRIOR_SALARY_BASE");
    }
}

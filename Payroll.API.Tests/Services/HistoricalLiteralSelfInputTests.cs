using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class HistoricalLiteralSelfInputTests
{
    private static readonly string Hash=new('a',64);
    private static (HistoricalPeriodSalaryRequest Q,ExcelPayslipCalculationSource Source,List<ExcelPayslipRow> Rows) Fixture()
    {
        var rows=new List<ExcelPayslipRow>{new(){Id="one",SourceRow=2},new(){Id="two",SourceRow=3}};
        var q=new HistoricalPeriodSalaryRequest{SalaryTargetMode="ReviewedInitialSourceTarget",Assignments=[new("one",7,"1"),new("two",8,"1")],
            InitialSourceTargetReview=new(){Version=1,Reviewed=true,SourceHash=Hash,MonthlyCtcColumnIndex=0,Meaning="MonthlyPayrollEarningsBudget",ActualJoiningDateNotEstablished=true},
            LiteralSelfInputReview=new(){Version=1,Reviewed=true,SourceHash=Hash,Bindings=[new("literal","11",2,["one","two"])]},
            Setup=new(){RowIds=["one","two"],MonthlyCtcColumnIndex=0,Components=[new(){Key="basic",ExistingComponentId="10",Code="BASE",ExpectedColumnIndex=1,CalculationType="Formula",Formula="MONTHLY_CTC-ALLOW_EARNED",ProRata=false},new(){Key="literal",ExistingComponentId="11",Code="ALLOW",SourceColumnIndex=2,ExpectedColumnIndex=2,CalculationType="Fixed Amount",ProRata=false}]}};
        var source=new ExcelPayslipCalculationSource{Columns=[new(){ColumnIndex=0,SourceHeader="Current CTC",Label="Current CTC",Kind="info"},new(){ColumnIndex=1,SourceHeader="Basic",Label="Basic",Kind="earning"},new(){ColumnIndex=2,SourceHeader="Fixed allowance",Label="Fixed allowance",Kind="earning"}],
            Rows=[new(){SourceRow=2,Cells=[new(){Value="10000"},new(){Value="10000",FormulaText="A2-C2"},new(){Value="0"}]},new(){SourceRow=3,Cells=[new(){Value="10000"},new(){Value="9800",FormulaText="A3-C3"},new(){Value="200"}]}]};
        return(q,source,rows);
    }
    [Fact]
    public void Exact_literal_self_input_is_explicit_complete_and_does_not_reclassify_source_or_default_coverage()
    {
        var(q,s,rows)=Fixture();var before=JsonSerializer.Serialize(new{q,s});
        var proof=ExcelPayrollSetupRepository.ReviewHistoricalLiteralSelfInputs(q,s,rows,Hash);
        Assert.Equal(new[]{0m,200m},proof.Select(p=>p.LiteralValue));Assert.All(proof,p=>{Assert.Equal("literal",p.ComponentKey);Assert.Equal("11",p.InputKey);Assert.Equal(2,p.ColumnIndex);Assert.Equal(Hash,p.SourceHash);});
        var components=q.Setup.Components.Select(c=>new ExcelPayrollSetupRepository.ResolvedComponent(c,c.ExistingComponentId!,false)).ToArray();
        Assert.NotEmpty(ExcelPayrollSetupRepository.HistoricalSourceCoverage(s,q.Setup,components));
        Assert.Empty(ExcelPayrollSetupRepository.HistoricalSourceCoverage(s,q.Setup,components,proof.Select(p=>p.ComponentKey).ToHashSet(StringComparer.Ordinal)));
        Assert.Equal(before,JsonSerializer.Serialize(new{q,s}));Assert.Equal("earning",s.Columns[2].Kind);
    }
    [Theory]
    [InlineData("mode")][InlineData("false")][InlineData("version")][InlineData("hash")][InlineData("duplicate")][InlineData("missingRow")][InlineData("extraRow")]
    [InlineData("inputAlias")][InlineData("codeAlias")][InlineData("columnAlias")][InlineData("role")][InlineData("prorata")][InlineData("formula")][InlineData("base")][InlineData("rowFormula")]
    [InlineData("adjustment")][InlineData("exemption")][InlineData("blankZero")][InlineData("valueFormula")][InlineData("ctc")][InlineData("ctcHeader")][InlineData("attendance")][InlineData("identity")]
    [InlineData("formulaCache")][InlineData("missingCache")][InlineData("calculationError")][InlineData("blank")][InlineData("text")][InlineData("negative")][InlineData("precision")]
    public void Any_unreviewed_alias_derived_or_incomplete_literal_self_binding_is_rejected(string change)
    {
        var(q,s,rows)=Fixture();var review=q.LiteralSelfInputReview!;var binding=review.Bindings[0];var component=q.Setup.Components[1];var cell=s.Rows[1].Cells[2]!;
        switch(change)
        {
            case "mode":q.SalaryTargetMode="PreserveEffectiveHistoricalTarget";break;case "false":review.Reviewed=false;break;case "version":review.Version=0;break;case "hash":review.SourceHash=new('b',64);break;
            case "duplicate":review.Bindings.Add(binding);break;case "missingRow":binding.RowIds.Remove("two");break;case "extraRow":binding.RowIds.Add("not-selected");break;
            case "inputAlias":review.Bindings[0]=binding with{InputKey="011"};break;case "codeAlias":review.Bindings[0]=binding with{ComponentKey="ALLOW"};break;case "columnAlias":review.Bindings[0]=binding with{ColumnIndex=1};break;
            case "role":q.Setup.SourceColumnRoles[2]="info";break;case "prorata":component.ProRata=true;break;case "formula":component.Formula="200";break;case "base":component.BaseComponent="GROSS";break;case "rowFormula":component.RowFormulas["two"]="200";break;
            case "adjustment":component.IsPeriodAdjustment=true;break;case "exemption":component.ExemptWhenSourceZero=true;break;case "blankZero":component.BlankInputAsZero=true;break;case "valueFormula":component.Value="0+1";break;case "ctc":q.Setup.MonthlyCtcColumnIndex=2;break;case "ctcHeader":s.Columns[2].SourceHeader="Monthly CTC";break;case "attendance":q.Setup.PresentDaysColumnIndex=2;break;case "identity":s.Columns[2].SourceHeader="UAN";break;
            case "formulaCache":cell.FormulaText="A3*0.02";break;case "missingCache":cell.MissingCachedValue=true;break;case "calculationError":cell.CalculationError="#VALUE!";break;case "blank":cell.Value="";break;case "text":cell.DataType="Text";break;case "negative":cell.Value="-1";break;case "precision":cell.Value="200.00001";break;
        }
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ReviewHistoricalLiteralSelfInputs(q,s,rows,Hash));
    }
    [Fact]
    public void Omitted_review_keeps_request_preview_receipt_JSON_shape_and_never_approves_an_overlap()
    {
        var(q,s,rows)=Fixture();q.LiteralSelfInputReview=null;
        Assert.Empty(ExcelPayrollSetupRepository.ReviewHistoricalLiteralSelfInputs(q,s,rows,Hash));
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.DoesNotContain("literalSelf",JsonSerializer.Serialize(q,json));Assert.DoesNotContain("literalSelf",JsonSerializer.Serialize(new HistoricalPeriodSalaryReceipt(),json));
        Assert.DoesNotContain("reviewedLiteral",JsonSerializer.Serialize(new HistoricalPeriodSalaryPreviewRow(),json));
    }
}

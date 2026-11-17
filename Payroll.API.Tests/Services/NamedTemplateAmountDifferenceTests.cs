using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class NamedTemplateAmountDifferenceTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private sealed record Case(NamedFormulaTemplateRequest Request,ExcelPayslipBatch Batch,ExcelPayslipCalculationSource Source,
        ExcelPayslipRow Row,ExcelPayrollSetupPreviewRow Calculated,List<ExcelPayrollTransferFormula> Formulas,NamedFormulaInputContext Context);
    private static Case Example()
    {
        string[] codes=["BASIC","BONUS","PF","ESIC","EPF_ER","ESIC_ER"],kinds=["Earning","Earning","Deduction","Deduction","Benefit","Benefit"];
        decimal[] raw=[10000m,200m,1200m,75m,1300m,325m],core=[10000.29m,200m,1200.03m,75m,1300.04m,325.01m],before=[10000.290322m,200m,1200.03483864m,75.002177415m,1300.03774186m,325.009435465m];
        var row=new ExcelPayslipRow{Id="synthetic-named-row",SourceRow=3,NetPay=8925.35m,DeclaredGross=10200m,DeclaredDeductions=1275m};
        var source=new ExcelPayslipCalculationSource{ColumnCount=7,Rows=[new(){SourceRow=2,Cells=Enumerable.Range(0,7).Select(_=>(ExcelPayslipCalculationCell?)new(){Value=""}).ToList()},new(){SourceRow=3}],Columns=[]};
        source.Rows[0].Cells[6]!.Value="Previous period salary";
        var q=new NamedFormulaTemplateRequest{ClientId=91,ExistingTemplateId="123",SourceRowId=row.Id,Definitions=[new("PRIOR_SALARY_BASE","Prior salary base","Amount",8,0,1_000_000_000m)],Bindings=[new("PRIOR_SALARY_BASE",6,2,"Previous period salary")],SourceReview=new(){BatchId=new string('a',32),RowIds=[row.Id],ReviewedFixedInputs=true,EmployeeDecisions=[new(){RowId=row.Id,MatchedEmployeeId=10,AllowCreate=false}]}};
        var calculated=new ExcelPayrollSetupPreviewRow{RowId=row.Id,PayrollDays=31,PresentDays=17,PayableDays=17,Gross=10200.29m,Deductions=1275.03m,Benefits=1625.05m,Net=8925.26m};
        for(var i=0;i<6;i++)
        {
            var amounts=kinds[i]=="Earning"?row.Earnings:kinds[i]=="Deduction"?row.Deductions:row.EmployerContributions;
            amounts.Add(new(){Label=codes[i],Amount=raw[i]});source.Columns.Add(new(){ColumnIndex=i,Label=codes[i],SourceHeader=codes[i],Kind=kinds[i]=="Earning"?"earning":kinds[i]=="Deduction"?"deduction":"employer"});
            source.Rows[1].Cells.Add(new(){Value=raw[i].ToString(CultureInfo.InvariantCulture),DataType="Number"});
            q.SourceReview.Components.Add(new(){Key=codes[i],Code=codes[i],ExistingComponentId=(i+1).ToString(),Category=kinds[i],ExpectedColumnIndex=i,CalculationType=i==1?"Fixed Amount":"Formula",Formula=i==1?"":"SUPPORTED_SOURCE_RULE"});
            calculated.Lines.Add(new(codes[i],raw[i],core[i],core[i]-raw[i]){Category=kinds[i],UnroundedEarned=before[i]});
        }
        source.Columns.Add(new(){ColumnIndex=6,Label="Prior salary base",SourceHeader="Previous period salary",Kind="info"});source.Rows[1].Cells.Add(new(){Value="30000",DataType="Number"});q.SourceReview.SourceColumnRoles[6]="info";
        var batch=new ExcelPayslipBatch{Id=q.SourceReview.BatchId,ClientId=q.ClientId,Month="2026-10",Rows=[row]};
        var hash=ExcelPayslipRepository.EmploymentSourceHash(batch,source);q.SourceHash=hash;q.AmountDifferenceReview=new(){Version=1,Reviewed=true,SourceHash=hash,MaxAbsoluteDifference=1,Reason="Reviewed synthetic cached monetary differences; preserve every amount."};
        var formulas=codes.Select((code,i)=>new ExcelPayrollTransferFormula(row.Id,code,row.SourceRow,i,new string('d',64),i==1?"Reviewed fixed source":"Exact supported translation","Synthetic source proof")).ToList();
        var context=new NamedFormulaInputContext{Definitions=q.Definitions,Values=new(){{"PRIOR_SALARY_BASE",30000m}}};return new(q,batch,source,row,calculated,formulas,context);
    }
    private static NamedFormulaTemplateAmountDifferenceEvidence Review(Case c)=>ExcelPayrollSetupRepository.ReviewNamedTemplateAmounts(c.Request,c.Batch,c.Source,c.Row,c.Calculated,c.Formulas,c.Context);
    private static void Rehash(Case c){var hash=ExcelPayslipRepository.EmploymentSourceHash(c.Batch,c.Source);c.Request.SourceHash=hash;c.Request.AmountDifferenceReview!.SourceHash=hash;}
    [Fact] public void Complete_explicit_configuration_ledger_retains_all_source_and_native_values()
    {
        var c=Example();var original=JsonSerializer.Serialize(c,Json);var result=Review(c);
        Assert.Equal(6,result.Lines.Count);Assert.Equal(4,result.Totals.Count);Assert.Equal(3,result.SourceAggregates.Count);Assert.Equal(64,result.ProofHash.Length);
        Assert.Equal(10000m,result.Lines[0].SourceRaw);Assert.Equal(10000.290322m,result.Lines[0].CoreUnrounded);Assert.Equal(10000.29m,result.Lines[0].Calculated);
        Assert.Equal(.35m,result.SourceAggregates.Single(a=>a.Kind=="Net").RawDelta);Assert.Equal(original,JsonSerializer.Serialize(c,Json));
    }
    [Theory]
    [InlineData("sourceHash")][InlineData("owner")][InlineData("unsupported")][InlineData("missingLine")][InlineData("duplicateFormula")]
    [InlineData("unmappedOutput")][InlineData("category")][InlineData("variance")][InlineData("currencyStage")][InlineData("nativeTotal")]
    [InlineData("adjustment")][InlineData("exemption")][InlineData("balancing")][InlineData("missingNamed")][InlineData("changedNamed")][InlineData("header")]
    public void Review_cannot_waive_mapping_formula_named_input_owner_or_native_accounting_gaps(string change)
    {
        var c=Example();switch(change)
        {
            case "sourceHash":c.Request.AmountDifferenceReview!.SourceHash=new string('b',64);break;
            case "owner":c.Request.SourceReview.EmployeeDecisions[0].AllowCreate=true;break;
            case "unsupported":c.Formulas[0]=c.Formulas[0] with{Status="Unsupported"};break;
            case "missingLine":c.Calculated.Lines.RemoveAt(0);break;
            case "duplicateFormula":c.Formulas[1]=c.Formulas[0];break;
            case "unmappedOutput":c.Request.SourceReview.SourceColumnRoles[6]="earning";break;
            case "category":c.Calculated.Lines[0]=c.Calculated.Lines[0] with{Category="Deduction"};break;
            case "variance":c.Calculated.Lines[0]=c.Calculated.Lines[0] with{Variance=0};break;
            case "currencyStage":c.Calculated.Lines[0]=c.Calculated.Lines[0] with{UnroundedEarned=10000.30m};break;
            case "nativeTotal":c.Calculated.Gross+=.01m;break;
            case "adjustment":c.Request.SourceReview.Components[0].IsPeriodAdjustment=true;break;
            case "exemption":c.Request.SourceReview.Components[0].ExemptWhenSourceZero=true;break;
            case "balancing":c.Request.SourceReview.Components[0].CalculationType="Residual / Balancing";break;
            case "missingNamed":c.Context.Values.Clear();break;
            case "changedNamed":c.Context.Values["PRIOR_SALARY_BASE"]=30001m;break;
            case "header":c.Request.Bindings[0]=c.Request.Bindings[0] with{SourceHeader="Current CTC"};break;
        }
        Assert.Throws<InvalidOperationException>(()=>Review(c));
    }
    [Theory][InlineData(0)][InlineData(-1)][InlineData(1.01)]
    public void Only_explicit_positive_limits_up_to_one_are_supported(decimal limit)
    {var c=Example();c.Request.AmountDifferenceReview!.MaxAbsoluteDifference=limit;Assert.Throws<InvalidOperationException>(()=>Review(c));}
    [Fact] public void A_smaller_declared_limit_is_enforced_even_if_net_is_within_it()
    {var c=Example();c.Request.AmountDifferenceReview!.MaxAbsoluteDifference=.1m;Assert.Throws<InvalidOperationException>(()=>Review(c));}
    [Fact] public void Offsetting_component_errors_cannot_be_hidden_by_equal_gross_or_net()
    {
        var c=Example();c.Row.Earnings[0].Amount=9998m;c.Row.Earnings[1].Amount=202m;c.Source.Rows[1].Cells[0]!.Value="9998";c.Source.Rows[1].Cells[1]!.Value="202";
        c.Calculated.Lines[0]=c.Calculated.Lines[0] with{SourceAmount=9998m,Variance=2.29m};c.Calculated.Lines[1]=c.Calculated.Lines[1] with{SourceAmount=202m,Variance=-2m};Rehash(c);Assert.Throws<InvalidOperationException>(()=>Review(c));
    }
    [Theory][InlineData("Gross")][InlineData("Net")]
    public void Source_self_aggregate_is_checked_independently_of_native_total_difference(string kind)
    {
        var c=Example();if(kind=="Gross")c.Row.DeclaredGross=10201.01m;else c.Row.NetPay=8926.01m;Rehash(c);
        Assert.Throws<InvalidOperationException>(()=>Review(c));
    }
    [Fact] public void Evidence_hash_binds_declaration_reason_and_exact_named_source_context()
    {var c=Example();var proof=Review(c).ProofHash;c.Request.AmountDifferenceReview!.Reason+=" Additional explicit review.";Assert.NotEqual(proof,Review(c).ProofHash);}
    [Fact] public void Missing_optional_fields_keep_exact_legacy_request_preview_and_protected_receipt_json()
    {
        var q=new NamedFormulaTemplateRequest();var legacy=new{q.ClientId,q.ExistingTemplateId,q.Name,q.SourceRowId,q.SourceHash,q.SourceReview,q.Definitions,q.Bindings};
        Assert.Equal(JsonSerializer.Serialize(legacy,Json),JsonSerializer.Serialize(q,Json));
        var preview=new NamedFormulaTemplatePreview("hash",true,[],"source",0,[]);Assert.Equal(JsonSerializer.Serialize(new{preview.PlanHash,preview.CanSave,preview.Blockers,preview.SourceHash,preview.FinancialComponents,preview.Lines},Json),JsonSerializer.Serialize(preview,Json));
        var type=typeof(ExcelPayrollSetupRepository).GetNestedType("NamedTemplateReceipt",BindingFlags.NonPublic)!;var result=new NamedFormulaTemplateResult("123","declaration","request",false);var receipt=Activator.CreateInstance(type,[7,"fingerprint",result])!;
        Assert.Equal(JsonSerializer.Serialize(new{ActorId=7,Fingerprint="fingerprint",Result=result},Json),JsonSerializer.Serialize(receipt,type,Json));
    }
    [Fact] public void Historical_intent_and_bounded_reviews_have_independent_default_off_writer_guards()
    {
        var q=Example().Request;var off=new ExcelPayrollSetupRepository(new ConfigurationBuilder().Build(),null!,null!);
        Assert.Throws<InvalidOperationException>(()=>off.RequireNamedTemplateReviewFlags(q,false));q.AmountDifferenceReview=null;q.HistoricalSalaryUse=true;
        off.RequireNamedTemplateReviewFlags(q,false);Assert.Throws<InvalidOperationException>(()=>off.RequireNamedTemplateReviewFlags(q,true));
        var enabled=new ExcelPayrollSetupRepository(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Payroll:HistoricalAmountDifferenceReviewEnabled","true"},{"Payroll:HistoricalNamedInputsEnabled","true"}}).Build(),null!,null!);
        q=Example().Request;q.HistoricalSalaryUse=true;enabled.RequireNamedTemplateReviewFlags(q,true);
    }
}

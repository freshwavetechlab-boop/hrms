using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class NamedFormulaInputTests
{
    internal static List<NamedFormulaInputDefinition> Definitions => [new("HARDSHIP_BASE_INPUT","Source entry-level CTC hardship base","Amount",4,0,1_000_000_000m),new("HARDSHIP_RATE_PCT","Hardship percent","PercentPoints",4,0,100)];
    internal static NamedFormulaInputContext Context(decimal rate=25)=>new(){Definitions=Definitions,Values=new(){["HARDSHIP_BASE_INPUT"]=54000,["HARDSHIP_RATE_PCT"]=rate},DeclarationHash=new string('a',64)};
    private const string Rule="+ROUND((HARDSHIP_BASE_INPUT*HARDSHIP_RATE_PCT%)/PAYROLL_DAYS*PAYABLE_DAYS,0)";
    internal static PayrollSimulationRequest Scenario(decimal rate=25)
    {
        var components=Enumerable.Range(1,26).Select(i=>new PayrollReviewComponent(i.ToString(),i==1?"ACTUAL_BASIC":i==2?"HARDSHIP":"C"+i,i<=2||i%3==0?"Earning":i%3==1?"Deduction":"Benefit",i%3==2&&i>2?"Employer Benefit":i%3==1&&i>2?"Regular Deduction":"Regular Earning","None",i<=2?"Formula":"Fixed Amount",i<=2?"0":(i*10).ToString(),"","",false,false,true,i,"Fixed Pay")).ToList();
        var lines=components.Select(c=>new PayrollReviewLine(c.Id,c.Value,c.Id=="1"?"ROUND(50000/PAYROLL_DAYS*PAYABLE_DAYS,0)":c.Id=="2"?Rule:"",c.CalculationType,"","false")).ToList();
        return new(){Mode="Template",ClientId=97724,TemplateId="synthetic-generic",AnnualCtc=1_000_000,PayrollDays=31,PresentDays=31,PayableDays=31,NamedInputs=Context(rate),Snapshot=new("Template",97724,"",1_000_000,components,lines)};
    }
    [Theory][InlineData(0)][InlineData(20)][InlineData(25)]
    public void Real_native_twenty_six_line_template_uses_declared_percent_without_extra_money_or_ctc(decimal rate)
    {
        var request=Scenario(rate);var result=PayRunRepository.SimulateTemplate(request);
        Assert.True(result.Complete,string.Join(";",result.Issues.Select(i=>i.Message)));Assert.Equal(26,result.Lines.Count);
        Assert.Equal(50000,result.Lines.Single(l=>l.Code=="ACTUAL_BASIC").Earned);
        Assert.Equal(54000*rate/100,result.Lines.Single(l=>l.Code=="HARDSHIP").Earned);
        Assert.DoesNotContain(result.Lines,l=>l.Code is "HARDSHIP_BASE_INPUT" or "HARDSHIP_RATE_PCT");
        Assert.Equal(1_000_000,request.AnnualCtc);
        Assert.Equal("PercentPoints",result.Lines.Single(l=>l.Code=="HARDSHIP").NamedOperands!.Definitions[1].Unit);
    }
    [Fact]
    public void Twenty_and_twenty_five_keep_same_rules_other_twenty_five_lines_and_employer_cost()
    {
        var old=PayRunRepository.SimulateTemplate(Scenario(20));var next=PayRunRepository.SimulateTemplate(Scenario(25));
        Assert.Equal(old.Lines.Select(l=>l.Formula),next.Lines.Select(l=>l.Formula));
        Assert.Equal(old.Lines.Where(l=>l.Code!="HARDSHIP").Select(l=>(l.Code,l.Category,l.Earned)),next.Lines.Where(l=>l.Code!="HARDSHIP").Select(l=>(l.Code,l.Category,l.Earned)));
        Assert.Equal(2700,next.Gross-old.Gross);Assert.Equal(2700,next.Net-old.Net);Assert.Equal(old.Deductions,next.Deductions);Assert.Equal(old.Benefits,next.Benefits);
    }
    [Fact]
    public void Missing_is_not_zero_and_declared_names_are_not_global_tokens_or_amount_overrides()
    {
        var request=Scenario();request.NamedInputs=null;
        Assert.False(PayRunRepository.SimulateTemplate(request).Complete);
        Assert.DoesNotContain("HARDSHIP_RATE_PCT",PayrollValueCatalogue.Names);
        request.AmountOverrides["HARDSHIP_RATE_PCT"]=25;
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.SimulateTemplate(request));
        var missing=Context();missing.Values.Remove("HARDSHIP_RATE_PCT");
        Assert.Throws<InvalidOperationException>(()=>ReviewedNamedFormulaInputs.ValidateContext(missing,[]));
    }
    [Theory][InlineData("PAYABLE_DAYS")][InlineData("ROUND")][InlineData("TRUE")][InlineData("BASE_MONTHLY")][InlineData("BASE_EARNED")][InlineData("basic")][InlineData("BASIC")]
    public void Reserved_component_function_system_and_alias_names_fail(string key)
    {
        var defs=new List<NamedFormulaInputDefinition>{new(key,"Unsafe input","Amount",2,0,100)};
        Assert.Throws<InvalidOperationException>(()=>ReviewedNamedFormulaInputs.ValidateDefinitions(defs,["BASIC"]));
    }
    [Theory][InlineData(-1)][InlineData(101)][InlineData(25.00001)]
    public void Unit_bounds_and_precision_are_checked_without_rounding(double value)
    {
        var context=Context((decimal)value);
        Assert.Throws<InvalidOperationException>(()=>ReviewedNamedFormulaInputs.ValidateContext(context,[]));
    }
    [Fact]
    public void Duplicate_case_values_undeclared_values_and_wrong_units_fail()
    {
        var context=Context();context.Values["hardship_rate_pct"]=25;
        Assert.Throws<InvalidOperationException>(()=>ReviewedNamedFormulaInputs.ValidateContext(context,[]));
        context=Context();context.Definitions[1]=context.Definitions[1] with{Unit="Days"};
        Assert.Throws<InvalidOperationException>(()=>ReviewedNamedFormulaInputs.ValidateContext(context,[]));
    }
    internal static (ExcelPayslipCalculationSource Source,ExcelPayslipRow Row,List<NamedFormulaInputBinding> Bindings) SourceFixture()
    {
        var source=new ExcelPayslipCalculationSource{ColumnCount=2,HeaderRow=1,SheetName="Synthetic independent operands",Columns=[new(){ColumnIndex=0,Kind="info",SourceHeader="Hardship category %",Label="Hardship category %"},new(){ColumnIndex=1,Kind="info",SourceHeader="Entry-level CTC",Label="Entry-level CTC"}],Rows=[new(){SourceRow=1,Cells=[new(){Value="Hardship category %"},new(){Value="Entry-level CTC"}]},new(){SourceRow=3,Cells=[new(){Value="25"},new(){Value="54000"}]}]};
        return(source,new(){Id="synthetic",SourceRow=3},[new("HARDSHIP_RATE_PCT",0,1,"Hardship category %"),new("HARDSHIP_BASE_INPUT",1,1,"Entry-level CTC")]);
    }
    [Fact]
    public void Explicit_ctc_labelled_literal_is_operand_only_without_salary_target_alias()
    {
        var f=SourceFixture();var profile=new NamedFormulaInputProfile(1,97724,"1","",Definitions,"decl");
        var bound=ReviewedNamedFormulaInputs.Bind(profile,f.Bindings,f.Source,f.Row,[]);
        Assert.Equal(54000,bound.Context.Values["HARDSHIP_BASE_INPUT"]);Assert.Equal("FormulaOperandOnly",bound.Context.Definitions[0].Purpose);
        Assert.False(bound.Context.Values.ContainsKey("MONTHLY_CTC"));Assert.False(bound.Context.Values.ContainsKey("BASIC_MONTHLY"));
    }
    [Theory][InlineData("formula")][InlineData("blank")][InlineData("error")][InlineData("output")][InlineData("identity")][InlineData("wrong-header")][InlineData("boolean")][InlineData("date")][InlineData("numeric-header")][InlineData("date-header")]
    public void Source_leaf_must_be_literal_nonfinancial_and_exactly_reviewed(string mutation)
    {
        var f=SourceFixture();var cell=f.Source.Rows[1].Cells[1]!;
        if(mutation=="formula")cell.FormulaText="=27000*2";
        if(mutation=="blank")cell.Value="";
        if(mutation=="error")cell.Error="#VALUE!";
        if(mutation=="output")f.Source.Columns[1].Kind="earning";
        if(mutation=="identity"){f.Source.Rows[0].Cells[1]!.Value="Bank account";f.Bindings[1]=f.Bindings[1] with{SourceHeader="Bank account"};}
        if(mutation=="wrong-header")f.Bindings[1]=f.Bindings[1] with{SourceHeader="A different source basis"};
        if(mutation=="boolean")cell.DataType="Boolean";
        if(mutation=="date")cell.DataType="Date";
        if(mutation=="numeric-header"){f.Source.Rows[0].Cells[1]!.Value="54000";f.Bindings[1]=f.Bindings[1] with{SourceHeader="54000"};}
        if(mutation=="date-header")f.Source.Rows[0].Cells[1]!.DataType="Date";
        Assert.Throws<InvalidOperationException>(()=>ReviewedNamedFormulaInputs.Bind(new(1,97724,"1","",Definitions,"decl"),f.Bindings,f.Source,f.Row,[]));
    }
    [Fact]
    public void Default_signature_and_request_json_are_unchanged_and_percent_ast_is_preserved()
    {
        Assert.Equal("legacy-signature",ReviewedNamedFormulaInputs.Signature("legacy-signature",null));
        var request=new PayrollSimulationRequest();Assert.DoesNotContain("namedInputs",JsonSerializer.Serialize(request,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.NotEqual(PayrollFormulaEvaluator.CanonicalStructure(Rule),PayrollFormulaEvaluator.CanonicalStructure(Rule.Replace("HARDSHIP_RATE_PCT%","HARDSHIP_RATE_PCT/100")));
        Assert.NotEqual(ReviewedNamedFormulaInputs.Signature("same",Context(20)),ReviewedNamedFormulaInputs.Signature("same",Context(25)));
    }

    [Fact]
    public void Offline_operand_precision_must_be_exactly_representable_without_affecting_online_values()
    {
        PayRunRepository.RequireExcelNamedOperands(Context());
        var context=Context();context.Definitions[0]=context.Definitions[0] with{Precision=8};context.Values["HARDSHIP_BASE_INPUT"]=999999999.12345678m;
        ReviewedNamedFormulaInputs.ValidateContext(context,[]);
        Assert.Contains("15-digit",Assert.Throws<InvalidOperationException>(()=>PayRunRepository.RequireExcelNamedOperands(context)).Message);
        Assert.Equal(999999999.12345678m,context.Values["HARDSHIP_BASE_INPUT"]);
        context.Values["HARDSHIP_BASE_INPUT"]=0.00000001m;PayRunRepository.RequireExcelNamedOperands(context);
    }

    [Fact]
    public void Export_fixture_uses_real_twenty_six_line_native_results_for_fractional_days_and_operand_changes()
    {
        var rows=new List<PayrollFormulaWorkbookEmployee>();
        foreach(var rate in new[]{0m,20m,25m})foreach(var days in new[]{0m,.5m,17m,31m})
        {
            var request=Scenario(rate);request.PresentDays=days;request.PayableDays=days;
            var result=PayRunRepository.SimulateTemplate(request);Assert.True(result.Complete);Assert.Equal(26,result.Lines.Count);
            var context=request.NamedInputs!;context.ManifestHash=new string('b',64);
            rows.Add(new(rows.Count+1,"NAMED-SYN-"+(rows.Count+1),"Synthetic named input example",result){NamedInputs=context});
        }
        if(Environment.GetEnvironmentVariable("FREVO_NAMED_FORMULA_FIXTURE")is{Length:>0} path)
            File.WriteAllText(path,JsonSerializer.Serialize(rows.Select(row=>new{row.EmployeeId,row.EmployeeCode,row.EmployeeName,row.NamedInputs,
                Calculation=new{row.Calculation.ClientId,row.Calculation.Mode,row.Calculation.TemplateId,row.Calculation.Complete,row.Calculation.Inputs,
                    Lines=row.Calculation.Lines.Select(line=>new{line.Code,line.Name,line.Category,line.Formula,line.Raw,line.Monthly,line.Earned,line.ProRata,line.Source,
                        line.ComponentId,line.CalculationType,line.BaseComponent,line.PayType,line.DailyRate,line.AmountOverride,line.MonthlyTarget}),
                    row.Calculation.Gross,row.Calculation.Deductions,row.Calculation.Benefits,row.Calculation.Net}}),new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void Protected_named_records_reject_unknown_fields_and_duplicate_case_aliases()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["IntegrationCredentialEncryption:MasterKey"]="synthetic-named-strict-schema-key"}).Build();
        var profile=new NamedFormulaInputProfile(1,97724,"7","rules",Definitions,"declaration");
        var unknown=JsonSerializer.SerializeToNode(profile,new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();unknown["unreviewedField"]=1;
        Assert.Throws<InvalidOperationException>(()=>NamedFormulaInputStore.Read<NamedFormulaInputProfile>(NamedFormulaInputStore.Protect(unknown,config,"Template"),config,"Template"));
        var duplicate="{\"version\":1,\"Version\":1}";
        var payload=NamedFormulaInputStore.Protector(config).ProtectNamedFormulaInput(duplicate,"Template");
        var envelope=JsonSerializer.Serialize(new{version=1,payload},new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Throws<InvalidOperationException>(()=>NamedFormulaInputStore.Read<NamedFormulaInputProfile>(envelope,config,"Template"));
    }

    [Fact]
    public void Two_complete_location_parts_keep_their_own_named_rates_and_all_twenty_six_outputs()
    {
        var request=Scenario();var snapshot=request.Snapshot!;
        var json=JsonSerializer.Serialize(new{salaryComponents=snapshot.Components.Select(component=>new{component.Id,component.Code,Name=component.Code,
            component.Category,component.ComponentRole,component.StatutoryType,component.CalculationType,component.Value,component.Formula,component.BaseComponent,component.ProRata,component.Active,component.Priority,component.PayType,component.Taxable}),
            salaryStructures=new[]{new{id="7",clientId="97724",annualCtc="1000000",active=true,lines=snapshot.Lines}}},new JsonSerializerOptions(JsonSerializerDefaults.Web));
        PayrollLocationPart Part(string key,decimal rate)=>new(){PartKey=key,AssignmentId=key,WorkLocationId=key=="first"?10:20,SalaryStructureId="7",AnnualCtc=1000000,
            PayrollDays=31,PresentDays=15,PayableDays=15.5m,NamedInputs=Context(rate),ComponentScopes=snapshot.Components.ToDictionary(component=>component.Id,_=>"SourceFormulaPerPart")};
        var period=new PayrollLocationPartPeriod{ClientId=97724,EmployeeId=1,PayPeriod="2026-10",ReferenceAssignmentId="first",Parts=[Part("first",20),Part("second",25)]};
        var actual=PayRunRepository.SimulateLocationParts(period,json,31,30,31);
        Assert.True(actual.Complete,string.Join(";",actual.Issues.Select(issue=>issue.Message)));Assert.Equal(26,actual.Lines.Count);
        Assert.Equal(12150,actual.Lines.Single(line=>line.Code=="HARDSHIP").Earned);
        Assert.Equal(50000,actual.Lines.Single(line=>line.Code=="ACTUAL_BASIC").Earned);
        Assert.Equal(2,actual.Lines.Single(line=>line.Code=="HARDSHIP").NamedOperandParts!.Count);
        Assert.Equal(20,actual.Lines.Single(line=>line.Code=="HARDSHIP").NamedOperandParts!["first"].Values["HARDSHIP_RATE_PCT"]);
        Assert.Equal(25,actual.Lines.Single(line=>line.Code=="HARDSHIP").NamedOperandParts!["second"].Values["HARDSHIP_RATE_PCT"]);
        period.Parts.ForEach(part=>part.ComponentScopes["2"]="Monthly");
        var ambiguous=PayRunRepository.SimulateLocationParts(period,json,31,30,31);
        Assert.False(ambiguous.Complete);Assert.Contains(ambiguous.Issues,issue=>issue.Message.Contains("monthly named operand",StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Immutable_manifests_bind_each_exact_source_row_and_the_complete_part_reference_scope()
    {
        var f=SourceFixture();var second=new ExcelPayslipRow{Id="second",SourceRow=4};
        f.Source.Rows.Add(new(){SourceRow=4,Cells=[new(){Value="20"},new(){Value="54000"}]});
        var batch=new ExcelPayslipBatch{Id=new string('b',32),ClientId=1,Month="2026-10",Rows=[f.Row,second]};
        batch.ProfileLinks=batch.Rows.Select(row=>new ExcelPayslipProfileLink(row.Id,row.SourceRow,5,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,f.Source))).ToList();
        var profile=new NamedFormulaInputProfile(1,1,"7",new string('a',64),Definitions,ReviewedNamedFormulaInputs.DeclarationHash(1,"7",new string('a',64),Definitions));
        var assignmentOne=new string('a',32);var assignmentTwo=new string('c',32);
        PayrollLocationPart Part(ExcelPayslipRow row,string key,string assignment,int location)=>new(){PartKey=key,AssignmentId=assignment,WorkLocationId=location,SalaryStructureId="7",
            NamedInputs=ReviewedNamedFormulaInputs.Bind(profile,f.Bindings,f.Source,row,[]).Context,ComponentScopes=new(){["1"]="SourceFormulaPerPart"}};
        var period=new PayrollLocationPartPeriod{ClientId=1,EmployeeId=5,PayPeriod="2026-10",ReferenceAssignmentId=assignmentOne,
            Parts=[Part(f.Row,"first",assignmentOne,10),Part(second,"second",assignmentTwo,20)]};
        var request=new ExcelPayrollLocationCorrectionRequest{SourceReview=new(){LocationAllocations=new(){Groups=[new(){EmployeeId=5,Parts=[new(){RowId=f.Row.Id,WorkLocationId=10},new(){RowId=second.Id,WorkLocationId=20}]}]}},
            NamedInputReview=new(){TemplateId="7",DeclarationHash=profile.DeclarationHash,Bindings=f.Bindings}};
        var bindings=new Dictionary<string,string>{{assignmentOne,f.Row.Id},{assignmentTwo,second.Id}};
        var manifests=ExcelPayrollSetupRepository.PrepareNamedCorrectionManifests(period,batch,f.Source,request,profile,new string('d',64),new string('e',64),bindings);
        Assert.Equal(2,manifests.Count);Assert.Equal(new[]{25m,20m},manifests.Select(manifest=>manifest.Values["HARDSHIP_RATE_PCT"]));
        Assert.All(manifests,manifest=>{Assert.Equal(new[]{"first","second"},manifest.CompleteSourcePartKeys);Assert.Equal(assignmentOne,manifest.ReferenceAssignmentId);
            Assert.Equal(profile.RulesHash,manifest.NativeRulesHash);Assert.Equal(NamedFormulaInputStore.ManifestHash(manifest),manifest.ManifestHash);});
        period.Parts[1].NamedInputs=period.Parts[0].NamedInputs;
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.PrepareNamedCorrectionManifests(period,batch,f.Source,request,profile,new string('d',64),new string('e',64),bindings));
        period.Parts.RemoveAt(1);
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.PrepareNamedCorrectionManifests(period,batch,f.Source,request,profile,new string('d',64),new string('e',64),bindings));
    }
}

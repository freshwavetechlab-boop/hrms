using System.Text.Json;
using System.Reflection;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class PayrollFormulaWorkbookTests
{
    private static int ApplicablePtRules(string personal,string work,string gender,string month)
    {
        var repo=typeof(PayRunRepository);var flags=BindingFlags.NonPublic|BindingFlags.Static;
        var type=repo.GetNestedType("PayRunSourceEmployee",BindingFlags.NonPublic)!;var employee=Activator.CreateInstance(type)!;
        type.GetProperty("PersonalState")!.SetValue(employee,personal);type.GetProperty("WorkState")!.SetValue(employee,work);type.GetProperty("Gender")!.SetValue(employee,gender);
        var json=JsonSerializer.Serialize(new{statutory=new{pt=true,ptState="Maharashtra",ptStateSlabs=new[]{new{state="Maharashtra",salaryFrom=10000,deductionAmount=200,effectiveFrom="2026-01-01",effectiveTo="2026-12-31",gender="Male",active=true}}}});
        var setup=repo.GetMethod("ReadPayrollSetup",flags)!.Invoke(null,[json]);
        return ((System.Collections.IEnumerable)repo.GetMethod("ApplicableProfessionalTaxSlabs",flags)!.Invoke(null,[employee,setup,month])!).Cast<object>().Count();
    }
    [Fact]public void GlobalPtSettingDoesNotBlockAnEmployeeWithoutApplicableStateGenderOrDateRules()
    {
        Assert.Equal(0,ApplicablePtRules("","Punjab","Male","2026-09"));
        Assert.Equal(0,ApplicablePtRules("Punjab","Maharashtra","Male","2026-09"));
        Assert.Equal(0,ApplicablePtRules("","Maharashtra","Female","2026-09"));
        Assert.Equal(0,ApplicablePtRules("","Maharashtra","Male","2027-01"));
        // Wage bands are deliberately not filtered: changing Excel days could cross a threshold.
        Assert.Equal(1,ApplicablePtRules("","Maharashtra","Male","2026-09"));
        Assert.Equal(1,ApplicablePtRules("","","Male","2026-09"));
    }
    private static PayrollFormulaWorkbookRequest PolicyRequest()=>new(9,"2026-09",[11,12]){AttendancePolicyBatchId="policy-A",AttendanceGroupIds=[101,102]};

    [Fact]
    public void FormulaDownloadRequiresAnExplicitPolicyEvenWithAnEmployeeRoster()
    {
        var missing=new PayrollFormulaWorkbookRequest(9,"2026-09",[11,12]);
        Assert.Contains("attendance policy",Assert.Throws<InvalidOperationException>(()=>PayRunRepository.ValidateFormulaWorkbookRequest(missing)).Message);
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.ValidateFormulaWorkbookRequest(PolicyRequest() with {AttendanceGroupIds=[]}));
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.ValidateFormulaWorkbookRequest(PolicyRequest() with {EmployeeIds=[11,11]}));
    }

    [Fact]
    public void FormulaPolicyMustMatchTheClientActiveGroupsAndEverySelectedEmployee()
    {
        var request=PolicyRequest();
        var groups=new List<PayRunRepository.FormulaWorkbookPolicyGroup>{new(101,9,"policy-A"),new(102,9,"policy-A"),new(103,9,"policy-B")};
        PayRunRepository.RequireFormulaWorkbookPolicy(request,groups,new HashSet<int>{11,12,13});
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.RequireFormulaWorkbookPolicy(request,groups,new HashSet<int>{11}));
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.RequireFormulaWorkbookPolicy(request with {ClientId=10},groups,new HashSet<int>{11,12}));
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.RequireFormulaWorkbookPolicy(request with {AttendanceGroupIds=[101,103]},groups,new HashSet<int>{11,12}));
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.RequireFormulaWorkbookPolicy(request,groups.Where(g=>g.Id!=102).ToList(),new HashSet<int>{11,12}));
        PayRunRepository.RequireFormulaWorkbookPolicy(request with {AttendancePolicyBatchId="group:105",AttendanceGroupIds=[105]},[new(105,9,"")],new HashSet<int>{11,12});
    }
    [Fact]
    public void ExportEvidenceRetainsLiveRulesAndNativeRoundingAcrossAttendanceInputs()
    {
        var components=new List<PayrollReviewComponent>{
            new("1","BASIC","Earning","Regular Earning","None","Fixed Amount","0","","",true,true,true,100,"Fixed Pay"),
            new("2","DA","Earning","Regular Earning","None","Fixed Amount","0","","",true,true,true,110,"Fixed Pay"),
            new("3","HRA","Earning","Regular Earning","None","Formula","0","(BASIC_MONTHLY+DA_MONTHLY)*5%/PAYROLL_DAYS*PAYABLE_DAYS","",false,true,true,120,"Fixed Pay"),
            new("4","PF","Deduction","Statutory Deduction","PF Employee","Formula","0","(BASIC_MONTHLY+DA_MONTHLY)*12%/PAYROLL_DAYS*PAYABLE_DAYS","",false,false,true,130,"Fixed Pay"),
            new("5","PT","Deduction","Statutory Deduction","Professional Tax","Fixed Amount","200","","",false,false,true,140,"Fixed Pay"),
            new("6","EPF_ER","Benefit","Employer Contribution","PF Employer","Formula","0","(BASIC_MONTHLY+DA_MONTHLY)*13%/PAYROLL_DAYS*PAYABLE_DAYS","",false,false,true,150,"Fixed Pay"),
            new("7","ESIC","Deduction","Statutory Deduction","ESI Employee","Formula","0","ROUNDUP((BASIC_EARNED+DA_EARNED+HRA_EARNED)*0.0075,0)","",false,false,true,160,"Fixed Pay"),
            new("8","ESIC_ER","Benefit","Employer Contribution","ESI Employer","Formula","0","ROUNDUP((BASIC_EARNED+DA_EARNED+HRA_EARNED)*0.0325,0)","",false,false,true,170,"Fixed Pay")
        };
        var lines=components.Select(c=>new PayrollReviewLine(c.Id,c.Value,c.Formula,c.CalculationType,c.BaseComponent,c.ProRata?"true":"false")).ToList();
        var scenarios=new List<PayrollFormulaWorkbookEmployee>();
        foreach(var days in new[]{0m,0.5m,17m,20.5m,26m})
        {
            var result=PayRunRepository.SimulateTemplate(new(){ClientId=9,Mode="Template",TemplateId="synthetic-template",AnnualCtc=126000m,
                PayrollDays=26,PresentDays=days,PayableDays=days,AmountOverrides=new(){["1"]=10000m,["2"]=500m},
                Snapshot=new("Template",9,"",126000m,components,lines)});
            Assert.True(result.Complete);
            Assert.Equal("Formula",result.Lines.Single(line=>line.Code=="PF").CalculationType);
            Assert.Contains("PAYABLE_DAYS",result.Lines.Single(line=>line.Code=="PF").Formula);
            Assert.True(result.Lines.Single(line=>line.Code=="BASIC").AmountOverride);
            Assert.False(result.Lines.Single(line=>line.Code=="PF").ProRata);
            Assert.Contains("BASIC_EARNED",result.Lines.Single(line=>line.Code=="ESIC").Formula);
            Assert.Equal(10500m,result.Lines[0].MonthlyTarget);
            Assert.Equal(Math.Max(0,result.Gross!.Value-result.Deductions!.Value),result.Net);
            scenarios.Add(new(scenarios.Count+1,"SYN-"+(scenarios.Count+1),"Synthetic employee "+(scenarios.Count+1),result));
        }
        var dailyComponents=new List<PayrollReviewComponent>{
            new("10","WAGES","Earning","Regular Earning","None","Daily Rate","700","","",false,true,true,100,"Fixed Pay"),
            new("11","PT","Deduction","Statutory Deduction","Professional Tax","Slab Based","0-7500:0;7500-10000:175;10000+:200","","",false,false,true,200,"Fixed Pay")};
        var daily=PayRunRepository.SimulateTemplate(new(){ClientId=9,Mode="Template",TemplateId="synthetic-daily",AnnualCtc=0,PayrollDays=26,PresentDays=17,PayableDays=17,
            Snapshot=new("Template",9,"",0,dailyComponents,dailyComponents.Select(c=>new PayrollReviewLine(c.Id,c.Value,c.Formula,c.CalculationType,c.BaseComponent,"false")).ToList())});
        Assert.True(daily.Complete); Assert.Equal(700m,daily.Lines[0].DailyRate); Assert.Equal(11700m,daily.Net);
        scenarios.Add(new(scenarios.Count+1,"SYN-DAILY","Synthetic daily employee",daily));
        var residualComponents=components.Take(2).Append(new PayrollReviewComponent("12","BALANCE","Earning","Regular Earning","None","Residual / Balancing","0","","GROSS",true,true,true,300,"Fixed Pay")).ToList();
        var residual=PayRunRepository.SimulateTemplate(new(){ClientId=9,Mode="Template",TemplateId="synthetic-balance",AnnualCtc=180000m,PayrollDays=26,PresentDays=17,PayableDays=17,
            AmountOverrides=new(){["1"]=10000m,["2"]=500m},Snapshot=new("Template",9,"",180000m,residualComponents,residualComponents.Select(c=>new PayrollReviewLine(c.Id,c.Value,c.Formula,c.CalculationType,c.BaseComponent,"true")).ToList())});
        Assert.True(residual.Complete); Assert.Equal(4500m,residual.Lines.Single(line=>line.Code=="BALANCE").Monthly);
        scenarios.Add(new(scenarios.Count+1,"SYN-BALANCE","Synthetic balance employee",residual));
        // Optional, privacy-safe native-engine fixture for the actual XLSX exporter tests.
        if(Environment.GetEnvironmentVariable("FREVO_FORMULA_FIXTURE") is {Length:>0} path)
            File.WriteAllText(path,JsonSerializer.Serialize(scenarios,new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
    }
}

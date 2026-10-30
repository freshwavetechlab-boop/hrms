using System.Reflection;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class PayrollLocationPartCalculationTests
{
    private static object Component(string id,string code,string type,string formula="",string category="Earning",bool prorata=false,int priority=100,string statutory="None") =>
        new{id,code,name=code,calculationType=type,value=formula,formula,category,proRata=prorata,priority,statutoryType=statutory,
            active=true,payType="Fixed Pay",componentRole=category=="Deduction"?"Statutory Deduction":"Regular Earning",taxable=true};
    private static object[] Standard(string pfFormula="BASIC_EARNED*12%") =>
        [Component("1","BASIC","Fixed Amount",prorata:true),Component("2","PF","Formula",pfFormula,"Deduction",priority:200,statutory:"PF Employee"),
         Component("3","ER","Formula","BASIC_EARNED*13%","Benefit",priority:300,statutory:"PF Employer")];
    private static string Settings(object[] components) => JsonSerializer.Serialize(new
    {
        salaryComponents=components,salaryStructures=new[]{new{id="A",clientId="11",annualCtc="0",active=true,
            lines=components.Select(component=>new{componentId=component.GetType().GetProperty("id")!.GetValue(component)!.ToString()}).ToArray()}}
    });
    private static PayrollLocationPart Part(string key,decimal paid,decimal monthly=30000,int denominator=30) => new()
    {
        PartKey=key,AssignmentId=key,WorkLocationId=key=="first"?1:2,WorkState="Synthetic state",SalaryStructureId="A",AnnualCtc=monthly*12,
        PayrollDays=denominator,PresentDays=paid,PayableDays=paid,SalaryInputs=new(){["1"]=monthly},
        ComponentScopes=new(){["1"]="SourceFormulaPerPart",["2"]="SourceFormulaPerPart",["3"]="SourceFormulaPerPart"}
    };
    private static PayrollLocationPartPeriod Period(decimal first=17,decimal second=13,decimal monthly=30000,int denominator=30) => new()
    {ClientId=11,EmployeeId=1,PayPeriod="2026-09",Parts=[Part("first",first,monthly,denominator),Part("second",second,monthly,denominator)]};
    private static PayrollSimulationResult Simulate(PayrollLocationPartPeriod period,object[]? components=null,int days=30) =>
        PayRunRepository.SimulateLocationParts(period,Settings(components??Standard()),days,period.Parts.Sum(part=>part.PresentDays),period.Parts.Sum(part=>part.PayableDays));
    private static decimal Earned(PayrollSimulationResult result,string code)=>Assert.Single(result.Lines,line=>line.Code==code).Earned;

    [Fact] public void ActualSettingsFormulasCalculateBothLocationsAndAggregateOneEmployeeWithoutSecondProration()
    {
        var period=Period();var before=JsonSerializer.Serialize(period);var result=Simulate(period);
        Assert.True(result.Complete,string.Join(" ",result.Issues.Select(issue=>issue.Message)));
        Assert.Equal(30000,result.Gross);Assert.Equal(3600,result.Deductions);Assert.Equal(26400,result.Net);Assert.Equal(3900,result.Benefits);
        Assert.Equal(30000,Earned(result,"BASIC"));Assert.Equal(before,JsonSerializer.Serialize(period));
        Assert.Equal("Location parts template",result.Mode);Assert.Equal(11,result.ClientId);Assert.Equal(1,result.EmployeeId);Assert.Equal("2026-09",result.PayPeriod);
        Assert.Same(period,result.LocationAllocations);Assert.Equal("first",result.LocationAllocations!.Parts[0].PartKey);
    }
    [Fact] public void ChangedSettingsFormulaChangesTheAnswerWithoutAnyExpectedSourceAmountInput()
    {
        var first=Simulate(Period());var changed=Simulate(Period(),Standard("BASIC_EARNED*10%"));
        Assert.Equal(3600,first.Deductions);Assert.Equal(3000,changed.Deductions);Assert.Equal(27000,changed.Net);
    }
    [Fact] public void MonthlyCapRunsOnceWhileExplicitPerPartFormulaKeepsItsOwnCap()
    {
        var period=Period();var perPart=Simulate(period,Standard("MIN(BASIC_EARNED,15000)*12%"));
        period.Parts.ForEach(part=>part.ComponentScopes["2"]="Monthly");
        var monthly=Simulate(period,Standard("MIN(BASIC_EARNED,15000)*12%"));
        Assert.True(monthly.Complete,string.Join(" ",monthly.Issues.Select(issue=>issue.Message)));
        Assert.Equal(3360,perPart.Deductions);Assert.Equal(1800,monthly.Deductions);Assert.Equal(28200,monthly.Net);
    }
    [Fact] public void MonthlyFixedDefaultAndAggregateEarnedSuffixRulesRunOnlyOnce()
    {
        var period=Period();period.Parts.ForEach(part=>{part.ComponentScopes["4"]="Monthly";part.ComponentScopes["5"]="Monthly";});
        var fixedDefault=new{id="4",code="MONTH_FEE",name="Monthly fee",calculationType="Fixed Amount",value="25",formula="",category="Deduction",
            proRata=false,priority=400,statutoryType="None",active=true,payType="Fixed Pay",componentRole="Regular Deduction",taxable=false};
        var result=Simulate(period,[..Standard(),fixedDefault,Component("5","MONTH_GROSS_FEE","Formula","GROSS_EARNED*1%","Deduction",priority:500)]);
        Assert.True(result.Complete,string.Join(" ",result.Issues.Select(issue=>issue.Message)));
        Assert.Equal(25,Earned(result,"MONTH_FEE"));Assert.Equal(300,Earned(result,"MONTH_GROSS_FEE"));Assert.Equal(3925,result.Deductions);
    }
    [Fact] public void DifferentRecurringRatesNeedAnExplicitReferenceButEarnedAmountsStillUseEachPartRate()
    {
        var period=Period();period.Parts[1].SalaryInputs["1"]=60000;period.Parts[1].AnnualCtc=720000;
        Assert.False(Simulate(period).Complete);
        period.ReferenceAssignmentId="second";var result=Simulate(period);
        Assert.True(result.Complete,string.Join(" ",result.Issues.Select(issue=>issue.Message)));
        Assert.Equal(43000,result.Gross);Assert.Equal(60000,Assert.Single(result.Lines,line=>line.Code=="BASIC").Monthly);
    }
    [Fact] public void ReferenceForProjectionDoesNotInventAMonthlyStatutoryBaseFromDifferentRates()
    {
        var period=Period();period.ReferenceAssignmentId="second";period.Parts[1].SalaryInputs["1"]=60000;period.Parts[1].AnnualCtc=720000;
        period.Parts.ForEach(part=>part.ComponentScopes["2"]="Monthly");
        var result=Simulate(period,Standard("MIN(BASIC,50000)*12%"));
        Assert.False(result.Complete);Assert.Contains(result.Issues,issue=>issue.Message.Contains("raw monthly base"));
    }
    [Fact] public void AlreadyEarnedFormulaProratesOnceAndRejectsAnAdditionalProRataFlag()
    {
        var period=Period();period.Parts.ForEach(part=>{part.SalaryInputs=new(){["4"]=30000};part.ComponentScopes=new(){["4"]="SourceFormulaPerPart",["1"]="SourceFormulaPerPart"};});
        var rate=Component("4","RATE","Fixed Amount",category:"Information",priority:10);
        var once=Simulate(period,[rate,Component("1","BASIC","Formula","RATE/PAYROLL_DAYS*PAYABLE_DAYS")]);
        Assert.True(once.Complete);Assert.Equal(30000,once.Gross);
        var twice=Simulate(period,[rate,Component("1","BASIC","Formula","RATE/PAYROLL_DAYS*PAYABLE_DAYS",prorata:true)]);
        Assert.False(twice.Complete);Assert.Contains(twice.Issues,issue=>issue.Message.Contains("applied once"));
    }
    [Fact] public void ThirtyOneDayDailyRatesUseCurrentAttendanceWithoutSeptemberConstants()
    {
        var period=Period(17,14,100,31);period.PayPeriod="2026-10";
        period.Parts.ForEach(part=>{part.AnnualCtc=0;part.ComponentScopes=new(){["1"]="SourceFormulaPerPart"};});
        var result=Simulate(period,[Component("1","BASIC","Daily Rate")],31);
        Assert.True(result.Complete,string.Join(" ",result.Issues.Select(issue=>issue.Message)));
        Assert.Equal(3100,result.Gross);Assert.Equal(3100,result.Net);
    }
    [Fact] public void ZeroDayPartKeepsItsRateWithoutBeingPaidAgain()
    {
        var result=Simulate(Period(17,0));Assert.True(result.Complete);Assert.Equal(17000,result.Gross);Assert.Equal(2040,result.Deductions);
    }
    [Fact] public void RepeatedAssignmentCanHaveDistinctPeriodPartKeysButDuplicateKeysAreRejected()
    {
        var period=Period();period.Parts[1].AssignmentId="first";period.Parts[1].WorkLocationId=1;
        Assert.True(Simulate(period).Complete);
        period.Parts[1].PartKey="first";Assert.False(Simulate(period).Complete);
    }
    [Theory][InlineData("scope")][InlineData("cached-output")][InlineData("cross-stage")]
    public void IncompleteReviewOrCopiedOutputNeverBecomesARateOrImplicitRule(string problem)
    {
        var period=Period();
        if(problem=="scope")period.Parts[1].ComponentScopes.Remove("2");
        if(problem=="cached-output")period.Parts[0].SalaryInputs["2"]=1234;
        if(problem=="cross-stage")period.Parts.ForEach(part=>part.ComponentScopes["1"]="Monthly");
        Assert.False(Simulate(period).Complete);
    }
    [Fact] public void NativeMonthlyAttendanceMustEqualAllocationTotalsInsteadOfRedistributingDifference()
    {
        var period=Period();var result=PayRunRepository.SimulateLocationParts(period,Settings(Standard()),30,29,30);
        Assert.False(result.Complete);Assert.Contains(result.Issues,issue=>issue.Message.Contains("native monthly"));
    }
    [Fact] public void PersonalDeductionsAndApprovedAdjustmentsAreAppliedOnlyOnceByTheRealBuildEmployee()
    {
        var row=Build(Period(),seedTds:100,adjustments:[new(){Amount=50,AdjustmentType="Earning"},new(){Amount=25,AdjustmentType="Deduction"}]);
        Assert.Equal(3700,row.StatutoryDeductions);Assert.Equal(50,row.OneTimeEarnings);Assert.Equal(25,row.OneTimeDeductions);Assert.Equal(26325,row.NetPay);
        using var details=JsonDocument.Parse(row.DetailsJson);
        Assert.Single(details.RootElement.EnumerateArray(),line=>line.GetProperty("Id").GetString()=="TDS");
        var basic=details.RootElement.EnumerateArray().Single(line=>line.GetProperty("Id").GetString()=="BASIC");
        Assert.Equal(2,basic.GetProperty("locationParts").GetArrayLength());Assert.False(basic.GetProperty("ProRata").GetBoolean());
    }
    [Fact] public void ExplicitMonthlyPtAndTdsDoNotRepeatSeededPersonalDeductions()
    {
        var period=Period();period.Parts.ForEach(part=>{part.ComponentScopes["4"]="Monthly";part.ComponentScopes["5"]="Monthly";});
        var settings=Settings([..Standard(),Component("4","CUSTOM_PT","Fixed Amount","208","Deduction",priority:400,statutory:"Professional Tax"),
            Component("5","TDS","Fixed Amount","300","Deduction",priority:500,statutory:"TDS")]);
        var row=Build(period,seedTds:100,seedPt:208,settings:settings);
        Assert.Equal(4108,row.StatutoryDeductions);Assert.Equal(25892,row.NetPay);
        using var details=JsonDocument.Parse(row.DetailsJson);
        Assert.Single(details.RootElement.EnumerateArray(),line=>line.GetProperty("Id").GetString()=="TDS");
        Assert.DoesNotContain(details.RootElement.EnumerateArray(),line=>line.GetProperty("Id").GetString()=="PT");
    }
    [Fact] public void OrdinaryLegacyNullPathAndOneFullAllocationHaveTheSameMoney()
    {
        var period=Period();period.Parts.RemoveAt(1);period.Parts[0].PresentDays=period.Parts[0].PayableDays=30;
        var allocated=Build(period);var legacy=Build(null);
        Assert.Equal(legacy.GrossPay,allocated.GrossPay);Assert.Equal(legacy.StatutoryDeductions,allocated.StatutoryDeductions);Assert.Equal(legacy.NetPay,allocated.NetPay);
    }
    [Fact] public void WrongEmployeeOrPeriodEnvelopeCannotCalculateAnotherPersonsAllocations()
    {
        var period=Period();period.EmployeeId=2;Assert.Throws<InvalidOperationException>(()=>Build(period));
        period.EmployeeId=1;period.PayPeriod="2026-10";Assert.Throws<InvalidOperationException>(()=>Build(period));
    }
    private static PayRunEmployee Build(PayrollLocationPartPeriod? period,decimal seedTds=0,PayrollAdjustment[]? adjustments=null,decimal seedPt=0,string? settings=null)
    {
        var type=typeof(PayRunRepository).GetNestedType("PayRunSourceEmployee",BindingFlags.NonPublic)!;var employee=Activator.CreateInstance(type)!;
        void Set(string name,object? value)=>type.GetProperty(name)!.SetValue(employee,value);
        Set("Id",1);Set("ClientId",11);Set("EmployeeCode","SYNTH");Set("FirstName","Synthetic");Set("AnnualCtc",360000m);Set("SalaryStructureId","A");
        Set("SalaryComponents",new Dictionary<string,decimal>{{"1",30000}});Set("LocationParts",period);Set("HasPersonalDetails",true);Set("Tds",seedTds);Set("PtLwfWorkmenComp",seedPt);
        try{return(PayRunEmployee)typeof(PayRunRepository).GetMethod("BuildEmployee",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,
            [1,employee,settings??Settings(Standard()),"2026-09",30,30m,30m,adjustments??[],0m,0m,-1m,false,"TDS"])!;}
        catch(TargetInvocationException error){throw error.InnerException!;}
    }
}

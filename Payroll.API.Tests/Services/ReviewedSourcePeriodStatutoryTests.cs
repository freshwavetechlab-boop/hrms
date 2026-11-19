using System.Reflection;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ReviewedSourcePeriodStatutoryTests
{
    [Fact]
    public void Source_month_zero_cannot_erase_a_positive_or_unreadable_captured_PT_output()
    {
        var source=new ExcelPayslipCalculationSource{ColumnCount=1,Columns=[new(){ColumnIndex=0,SourceHeader="Professional Tax",Kind="deduction"}],
            Rows=[new(){SourceRow=7,Cells=[new(){Value="0",DataType="Number"}]}]};
        var row=new ExcelPayslipRow{SourceRow=7};
        ReviewedSourcePeriodStatutory.RequireNoPositiveSourcePt(source,row);
        source.Rows[0].Cells[0]!.Value="200";
        Assert.Throws<InvalidOperationException>(()=>ReviewedSourcePeriodStatutory.RequireNoPositiveSourcePt(source,row));
        source.Rows[0].Cells[0]!.Value="";
        Assert.Throws<InvalidOperationException>(()=>ReviewedSourcePeriodStatutory.RequireNoPositiveSourcePt(source,row));
        source.Columns[0].SourceHeader="Workmen Comp/LWF/PT";
        source.Rows[0].Cells[0]!.Value="10";
        Assert.Throws<InvalidOperationException>(()=>ReviewedSourcePeriodStatutory.RequireNoPositiveSourcePt(source,row));
    }
    [Fact]
    public void Zero_bindings_are_closed_month_only_and_do_not_turn_into_future_exemptions()
    {
        var q = Request(); ReviewedSourcePeriodStatutory.RequireClosedZeroReview(q, new(2026,11,20));
        q.PayPeriod="2026-11";
        Assert.Throws<InvalidOperationException>(()=>ReviewedSourcePeriodStatutory.RequireClosedZeroReview(q,new(2026,11,20)));
        q=Request();q.Rows[0].Amount=1;
        Assert.Throws<InvalidOperationException>(()=>ReviewedPeriodProfessionalTax.Validate(q));
        q=Request();q.SourcePeriodReviewVersion=0;
        Assert.Throws<InvalidOperationException>(()=>ReviewedPeriodProfessionalTax.Validate(q));
    }
    [Theory]
    [InlineData("positive")][InlineData("blank")][InlineData("error")][InlineData("wrong-role")]
    [InlineData("wrong-kind")][InlineData("unrelated-column")]
    public void A_zero_must_be_the_own_captured_statutory_output(string invalid)
    {
        var source=new ExcelPayslipCalculationSource {ColumnCount=1,Columns=[new(){ColumnIndex=0,Kind="deduction",SourceHeader="Employee PF"}],
            Rows=[new(){SourceRow=7,Cells=[new(){Value="0",DataType="Number"}]}]};
        var row=new ExcelPayslipRow{Id="row-7",SourceRow=7};
        SourcePeriodStatutoryComponent[] components=[new("PF","Deduction","PF Employee")];
        var columns=new Dictionary<string,int>{{"PF",0}};
        ReviewedSourcePeriodStatutory.RequireCapturedZeros(source,row,columns,components);
        if(invalid=="positive")source.Rows[0].Cells[0]!.Value="1";
        if(invalid=="blank")source.Rows[0].Cells[0]!.Value="";
        if(invalid=="error")source.Rows[0].Cells[0]!.Error="#REF!";
        if(invalid=="wrong-role")components=[new("PF","Earning","PF Employee")];
        if(invalid=="wrong-kind")source.Columns[0].Kind="info";
        if(invalid=="unrelated-column")columns["PF"]=1;
        Assert.Throws<InvalidOperationException>(()=>ReviewedSourcePeriodStatutory.RequireCapturedZeros(source,row,columns,components));
    }
    [Fact]
    public void Native_employee_result_uses_reviewed_month_zeros_and_retains_ordinary_formulas_for_other_months()
    {
        var control=ReviewedPeriodProfessionalTaxTests.Control();control.Version=2;
        control.SourceZeroStatutoryColumns=new(){{"PF",1},{"ESIC",2},{"EPF_ER",3},{"ESIC_ER",4}};
        var reviewed=Build(control,"2026-10");
        Assert.Equal(75000,reviewed.GrossPay);Assert.Equal(0,reviewed.StatutoryDeductions);Assert.Equal(73433,reviewed.NetPay);
        using (var details = JsonDocument.Parse(reviewed.DetailsJson))
            Assert.All(details.RootElement.EnumerateArray().Where(line => line.GetProperty("Category").GetString() == "Benefit"),
                line => Assert.Equal(0, line.GetProperty("amount").GetDecimal()));
        var ordinary=Build(null,"2026-11");
        Assert.Equal(3762.50m,ordinary.StatutoryDeductions);Assert.Equal(71237.50m,ordinary.NetPay);
        Assert.Throws<InvalidOperationException>(()=>Build(control,"2026-09"));
        var wrong=ReviewedPeriodProfessionalTaxTests.Control();wrong.Version=2;wrong.ClientId=999;
        Assert.Throws<InvalidOperationException>(()=>Build(wrong,"2026-10"));
    }
    [Fact]
    public void Reviewed_month_calculation_reuses_saved_unknown_presence_evidence()
    {
        var control=ReviewedPeriodProfessionalTaxTests.Control();control.Version=2;
        control.SourceZeroStatutoryColumns=new(){{"PF",1},{"ESIC",2},{"EPF_ER",3},{"ESIC_ER",4}};
        Assert.Throws<InvalidOperationException>(()=>Build(control,"2026-10",unknownPresence:true));
        var result=Build(control,"2026-10",unknownPresence:true,bindRecordedAttendance:true);
        Assert.Null(result.PresentDays);Assert.Equal(31,result.PayableDays);
        Assert.Equal(75000,result.GrossPay);Assert.Equal(73433,result.NetPay);
        Assert.Throws<InvalidOperationException>(()=>Build(control,"2026-10",unknownPresence:true,bindRecordedAttendance:true,wrongEmployee:true));
    }
    private static ReviewedProfessionalTaxRequest Request()=>new(){SourcePeriodReviewVersion=2,ClientId=17,PayPeriod="2026-10",
        SourceBatchId=new('a',32),Reason="Reviewed final source zero amounts",Rows=[new(){EmployeeId=21,SourceRowId="row-7",Mode="ReviewedAmount",Amount=0,SourceZeroStatutoryColumns=new(){{"PF",1}}}]};
    private static PayRunEmployee Build(ReviewedProfessionalTaxControl? control,string period,bool unknownPresence=false,bool bindRecordedAttendance=false,bool wrongEmployee=false)
    {
        var type=typeof(PayRunRepository).GetNestedType("PayRunSourceEmployee",BindingFlags.NonPublic)!;
        var employee=Activator.CreateInstance(type)!;
        void Set(string name,object? value)=>type.GetProperty(name)!.SetValue(employee,value);
        Set("Id",21);Set("ClientId",17);Set("EmployeeCode","SYNTH-21");Set("FirstName","Synthetic");Set("AnnualCtc",0m);
        Set("SalaryStructureId","501");Set("SalaryJson","{}");Set("SalaryComponents",new Dictionary<string,decimal>{{"1",75000m}});
        Set("HasPersonalDetails",true);Set("WorkState","Synthetic State");Set("ReviewedProfessionalTax",control);
        if(bindRecordedAttendance)
        {
            var employees=Array.CreateInstance(type,1);employees.SetValue(employee,0);
            var id=wrongEmployee?22:21;
            var attendance=new Dictionary<int,PayRunRepository.PayRunAttendance>{{id,new(){EmployeeId=id,WorkingDays=31,PresentDays=null,PayableDays=31,
                PayableOnlyEvidence=new(1,17,id,period,new('a',32),"row-7",new('b',32),new('c',64),31,31)}}};
            typeof(PayRunRepository).GetMethod("BindReviewedMonthAttendance",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[employees,attendance]);
        }
        var definitions=new[]{("1","BASIC","Earning","None","Fixed Amount","0",true),
            ("2","PF","Deduction","PF Employee","Formula","MIN(BASIC_EARNED,25000)*12%",false),
            ("3","ESIC","Deduction","ESI Employee","Formula","BASIC_EARNED*0.75%",false),
            ("4","EPF_ER","Benefit","PF Employer","Formula","MIN(BASIC_EARNED,25000)*13%",false),
            ("5","ESIC_ER","Benefit","ESI Employer","Formula","BASIC_EARNED*3.25%",false)};
        var components=definitions.Select((d,i)=>new{id=d.Item1,code=d.Item2,name=d.Item2,category=d.Item3,statutoryType=d.Item4,
            calculationType=d.Item5,value=d.Item6,formula=d.Item6,proRata=d.Item7,active=true,priority=i+1,payType="Fixed Pay"});
        var settings=JsonSerializer.Serialize(new{salaryComponents=components,salaryStructures=new[]{new{id="501",clientId="17",active=true,annualCtc="0",lines=definitions.Select(d=>new{componentId=d.Item1})}},
            statutory=new{pt=true,ptState="Synthetic State",ptStateSlabs=new[]{new{state="Synthetic State",salaryFrom="0",salaryTo="",deductionAmount="200",active=true,gender="All"}}}});
        try{return (PayRunEmployee)typeof(PayRunRepository).GetMethod("BuildEmployee",BindingFlags.NonPublic|BindingFlags.Static)!
            .Invoke(null,[0,employee,settings,period,31,unknownPresence?null:(decimal?)31m,31m,Array.Empty<PayrollAdjustment>(),0m,control is null?0m:1567m,-1m,false,"TDS"])!;}
        catch(TargetInvocationException error){throw error.InnerException!;}
    }
}

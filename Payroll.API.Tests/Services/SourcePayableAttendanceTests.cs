using System.Reflection;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class SourcePayableAttendanceTests
{
    private static SourcePayableAttendanceEvidence Evidence()=>new(1,11,7,"2026-09",new string('a',32),"synthetic-row",new string('b',32),new string('c',64),31,19);

    [Fact]
    public void SourceEvidenceRetainsUnknownRatherThanZeroAndBindsExactScope()
    {
        var encoded=SourcePayableAttendance.Encode(Evidence());Assert.True(encoded.Length<=600);
        var read=SourcePayableAttendance.Read(encoded,11,7,"2026-09",31,19);
        Assert.Null(read.ActualPresentDays);Assert.Equal(19,read.PayableDays);
        Assert.Throws<InvalidOperationException>(()=>SourcePayableAttendance.Read(encoded,12,7,"2026-09",31,19));
        Assert.Throws<InvalidOperationException>(()=>SourcePayableAttendance.Read(encoded,11,8,"2026-09",31,19));
        Assert.Throws<InvalidOperationException>(()=>SourcePayableAttendance.Read(encoded,11,7,"2026-10",31,19));
        Assert.Throws<InvalidOperationException>(()=>SourcePayableAttendance.Read(encoded,11,7,"2026-09",30,19));
        Assert.Throws<InvalidOperationException>(()=>SourcePayableAttendance.Read(encoded,11,7,"2026-09",31,20));
        Assert.Throws<InvalidOperationException>(()=>SourcePayableAttendance.Encode(Evidence() with {ActualPresentDays=0}));
    }

    [Fact]
    public void NativeReaderRequiresReviewedSourceModeForAnUnknownValue()
    {
        var known=new PayRunRepository.PayRunAttendance {EmployeeId=7,WorkingDays=31,PresentDays=0,PayableDays=19,SourceType="Monthly"};
        PayRunRepository.ReadPayableOnlyAttendance(known,11,"2026-09");Assert.Equal(0,known.PresentDays);Assert.Null(known.PayableOnlyEvidence);
        var unknown=new PayRunRepository.PayRunAttendance {EmployeeId=7,WorkingDays=31,PresentDays=null,PayableDays=19,SourceType="Monthly"};
        Assert.Throws<InvalidOperationException>(()=>PayRunRepository.ReadPayableOnlyAttendance(unknown,11,"2026-09"));
        unknown.SourceType=SourcePayableAttendance.SourceType;unknown.Remarks=SourcePayableAttendance.Encode(Evidence());
        PayRunRepository.ReadPayableOnlyAttendance(unknown,11,"2026-09");
        Assert.Null(unknown.PresentDays);Assert.NotNull(unknown.PayableOnlyEvidence);Assert.Equal("Reviewed source payable-only",unknown.InputSource);
    }

    [Theory]
    [InlineData("PRESENT_DAYS*100")][InlineData("IF(present_days>0,1,0)")]
    public void PresenceDependentEffectiveExpressionsAreBlocked(string expression)=>
        Assert.Throws<InvalidOperationException>(()=>SourcePayableAttendance.EnsureIndependent([expression]));

    [Theory]
    [InlineData(true,false,false)][InlineData(false,true,false)][InlineData(false,false,true)]
    public void DailySplitOrLocationPresenceRequirementsCannotBeReplacedByPaidTotals(bool daily,bool split,bool location)=>
        Assert.Throws<InvalidOperationException>(()=>SourcePayableAttendance.EnsureIndependent(["BASIC_EARNED*12%"],daily,split,location));

    [Fact]
    public void ActualCoreCalculatesPaidOnlySalaryAndPersistsUnknownPresenceAndProvenance()
    {
        var unknown=Calculate(null);var known=Calculate(15);
        Assert.Null(unknown.PresentDays);Assert.False(unknown.ActualPresenceKnown);Assert.Equal(19,unknown.PayableDays);
        Assert.Equal(known.GrossPay,unknown.GrossPay);Assert.Equal(known.StatutoryDeductions,unknown.StatutoryDeductions);Assert.Equal(known.NetPay,unknown.NetPay);
        Assert.True(unknown.GrossPay>0);Assert.True(PayRunRepository.HasUnknownPresenceSnapshot(unknown.DetailsJson));
        using var details=JsonDocument.Parse(unknown.DetailsJson);
        var input=Assert.Single(details.RootElement.EnumerateArray(),line=>line.GetProperty("Id").GetString()=="ATTENDANCE_INPUT");
        Assert.Equal(JsonValueKind.Null,input.GetProperty("actualPresentDays").ValueKind);
        Assert.Equal(SourcePayableAttendance.SourceType,input.GetProperty("sourceType").GetString());
        Assert.Contains("synthetic-row",input.GetRawText());
        Assert.False(PayRunRepository.HasUnknownPresenceSnapshot(known.DetailsJson));
    }

    [Fact]
    public void KnownActualZeroRemainsDifferentFromUnknown()
    {
        var zero=Calculate(0);Assert.Equal(0,zero.PresentDays);Assert.True(zero.ActualPresenceKnown);
        Assert.Equal(Calculate(null).NetPay,zero.NetPay);Assert.Null(PayRunRepository.ClampKnownPresence(null,31));
        Assert.Equal(0,PayRunRepository.ClampKnownPresence(0,31));
    }

    [Fact]
    public void CoreDoesNotUseUnknownAsAPresentFormulaOrUnreviewedFallback()
    {
        Assert.Contains("PRESENT_DAYS",Assert.Throws<InvalidOperationException>(()=>Calculate(null,formula:"PRESENT_DAYS*100")).Message);
        Assert.Contains("reviewed",Assert.Throws<InvalidOperationException>(()=>Calculate(null,withProvenance:false)).Message,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportsPayslipAndBillingPreserveUnknownKnowledge()
    {
        var monthly=new EmployeeMonthlyAttendance {PresentDays=null,PayableDays=19};
        var run=Calculate(null);
        using var serialized=JsonDocument.Parse(JsonSerializer.Serialize(run));
        Assert.Equal(JsonValueKind.Null,serialized.RootElement.GetProperty("PresentDays").ValueKind);Assert.False(monthly.ActualPresenceKnown);
        Assert.Equal("Not supplied",EssMssRepository.ActualPresenceText(null));Assert.NotEqual("Not supplied",EssMssRepository.ActualPresenceText(0));
        ReportingRepository.EnsureBillingActualPresenceKnown(null,"Gross salary");
        Assert.Throws<InvalidOperationException>(()=>ReportingRepository.EnsureBillingActualPresenceKnown(null,"Present Days"));
        Assert.Throws<InvalidOperationException>(()=>ReportingRepository.EnsureBillingActualPresenceKnown(null,"WORKED_DAYS"));
        ReportingRepository.EnsureBillingActualPresenceKnown(0,"Present Days");
    }

    private static PayRunEmployee Calculate(decimal? present,string formula="MIN(BASIC_EARNED,15000)*12%",bool withProvenance=true)
    {
        var type=typeof(PayRunRepository).GetNestedType("PayRunSourceEmployee",BindingFlags.NonPublic)!;
        var employee=Activator.CreateInstance(type)!;
        void Set(string property,object? value)=>type.GetProperty(property)!.SetValue(employee,value);
        Set("Id",7);Set("ClientId",11);Set("EmployeeCode","SYNTH-7");Set("FirstName","Synthetic");Set("AnnualCtc",360000m);Set("SalaryStructureId","one");Set("HasPersonalDetails",true);
        Set("SalaryComponents",new Dictionary<string,decimal>{{"1",30000m}});
        if(withProvenance)Set("PayableOnlyAttendance",Evidence());
        var components=new object[]
        {
            new{id="1",code="BASIC",name="Basic",calculationType="Fixed Amount",value="0",formula="",category="Earning",proRata=true,active=true,priority=100,payType="Fixed Pay",taxable=true,statutoryType="None"},
            new{id="2",code="PF",name="PF",calculationType="Formula",value=formula,formula,category="Deduction",proRata=false,active=true,priority=200,payType="Fixed Pay",taxable=false,statutoryType="PF Employee"}
        };
        var settings=JsonSerializer.Serialize(new{salaryComponents=components,salaryStructures=new[]{new{id="one",clientId="11",active=true,lines=new[]{new{componentId="1"},new{componentId="2"}}}}});
        try{return (PayRunEmployee)typeof(PayRunRepository).GetMethod("BuildEmployee",BindingFlags.Static|BindingFlags.NonPublic)!
            .Invoke(null,[1,employee,settings,"2026-09",31,present,19m,Array.Empty<PayrollAdjustment>(),0m,0m,-1m,false,"TDS"])!;}
        catch(TargetInvocationException error){throw error.InnerException!;}
    }
}

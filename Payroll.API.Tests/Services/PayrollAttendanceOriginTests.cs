using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class PayrollAttendanceOriginTests
{
    private static PayrollSimulationRequest Request() => new() { Mode="Employee", ClientId=42, EmployeeId=99123, PayPeriod="2026-09" };

    [Fact] public void MissingAttendanceRemainsUnknownEvenWithHypotheticalDays()
    {
        var request=Request(); request.PayrollDays=30; request.PresentDays=20; request.PayableDays=24;
        var evidence=PayRunRepository.DescribeAttendanceInput(null,request);
        Assert.Equal("Missing native attendance",evidence.SourceKind); Assert.Null(evidence.PresentDays); Assert.Null(evidence.PayableDays);
        Assert.Null(evidence.WeeklyOffPayableUnits); Assert.True(evidence.TemporaryDayOverrides); Assert.Contains("temporary",evidence.Explanation);
    }
    [Fact] public void MonthlyZeroPresentFourPayableDoesNotInventWeeklyOffOrCl()
    {
        var native=new PayRunRepository.PayRunAttendance {EmployeeId=99123,InputSource="Monthly snapshot",WorkingDays=30,PresentDays=0,PayableDays=4};
        var before=JsonSerializer.Serialize(native); var evidence=PayRunRepository.DescribeAttendanceInput(native,Request());
        Assert.Equal(0,evidence.PresentDays); Assert.Equal(4,evidence.PayableDays); Assert.Null(evidence.WeeklyOffPayableUnits); Assert.Null(evidence.HolidayPayableUnits);
        Assert.Null(evidence.CycleStart); Assert.Contains("not loaded",evidence.Explanation); Assert.Contains("does not establish",evidence.Explanation);
        Assert.Equal(before,JsonSerializer.Serialize(native));
    }
    [Fact] public void LegacyDailyExplicitWeeklyOffUnitsStayDistinctFromPresent()
    {
        var native=new PayRunRepository.PayRunAttendance {InputSource="Legacy daily attendance",WorkingDays=30,PresentDays=0,PayableDays=4,WeeklyOffPayableUnits=4,HolidayPayableUnits=0};
        var evidence=PayRunRepository.DescribeAttendanceInput(native,Request());
        Assert.Equal("Legacy daily attendance",evidence.SourceKind); Assert.Equal(0,evidence.PresentDays); Assert.Equal(4,evidence.WeeklyOffPayableUnits); Assert.Equal(0,evidence.HolidayPayableUnits);
        Assert.Contains("one payable day",evidence.Explanation); Assert.Null(evidence.CycleStart);
    }
    [Theory] [InlineData("Dated daily attendance")] [InlineData("Monthly snapshot validated against dated attendance")]
    public void DatedNativeCycleAndValidationErrorArePreserved(string source)
    {
        var native=new PayRunRepository.PayRunAttendance {InputSource=source,WorkingDays=31,PresentDays=20,PayableDays=24,WeeklyOffPayableUnits=4,HolidayPayableUnits=0,
            PolicyError="Stored monthly attendance does not match the effective weekly-off rules.",
            PolicySnapshot=new(1,new DateOnly(2026,8,21),new DateOnly(2026,9,20),"Date-wise",[],[])};
        var evidence=PayRunRepository.DescribeAttendanceInput(native,Request());
        Assert.Equal("2026-09",evidence.PayPeriod); Assert.Equal("2026-08-21",evidence.CycleStart); Assert.Equal("2026-09-20",evidence.CycleEnd);
        Assert.Equal(native.PolicyError,evidence.PolicyValidationError); Assert.Equal(24,evidence.PayableDays);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void DayOverridesAreLabelledWithoutReplacingNativeObservation(bool overridden)
    {
        var request=Request();if(overridden)request.PresentDays=0;
        var native=new PayRunRepository.PayRunAttendance {InputSource="Monthly snapshot",WorkingDays=30,PresentDays=26,PayableDays=30};
        var evidence=PayRunRepository.DescribeAttendanceInput(native,request);
        Assert.Equal(overridden,evidence.TemporaryDayOverrides); Assert.Equal(26,evidence.PresentDays); Assert.Equal(30,evidence.PayableDays);
    }
    [Fact] public void WireProjectionContainsNoEmployeeIdentityRemarksOrPolicyDocuments()
    {
        var native=new PayRunRepository.PayRunAttendance {EmployeeId=99123,InputSource="Monthly snapshot",WorkingDays=30,PresentDays=0,PayableDays=4};
        var json=JsonSerializer.Serialize(PayRunRepository.DescribeAttendanceInput(native,Request()),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("99123",json); Assert.DoesNotContain("employeeId",json); Assert.DoesNotContain("remarks",json); Assert.DoesNotContain("policySnapshot",json);
        Assert.Contains("\"weeklyOffPayableUnits\":null",json);
        Assert.Null(new PayrollSimulationResult {Mode="Template"}.AttendanceEvidence);
    }
}

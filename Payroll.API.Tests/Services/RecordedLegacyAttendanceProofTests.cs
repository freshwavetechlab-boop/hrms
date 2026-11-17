using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class RecordedLegacyAttendanceProofTests
{
    private static readonly DateTime Today=new(2026,11,14);
    private static RecordedLegacyAttendanceGroup Group=>new(51,31,41,1,30,"Legacy");
    private static RecordedLegacyAttendanceMonthly Monthly=>new(30,26,30,"Monthly snapshot","Date-wise","",false);
    private static List<RecordedLegacyAttendanceDay> Days()=>Enumerable.Range(1,30).Select(day=>new RecordedLegacyAttendanceDay(day,31,41,new(2026,9,day),day is 2 or 9 or 16 or 23?"WO":"Present",1,"")).ToList();
    private static string? Check(IReadOnlyList<RecordedLegacyAttendanceDay> days,RecordedLegacyAttendanceMonthly? monthly=null,
        RecordedLegacyAttendanceGroup? group=null,bool reviewed=true,int memberships=1,string period="2026-09")=>
        RecordedLegacyAttendanceProof.Validate(reviewed,31,41,period,memberships,group??Group,monthly??Monthly,days,Today);

    [Fact]
    public void Explicit_recorded_days_match_the_closed_month_without_inventing_weekdays_or_entitlements()
    {
        // The stored WO dates deliberately are not Sundays. The proof checks
        // recorded units, not an inferred calendar or a new weekly-off policy.
        Assert.Null(Check(Days()));
        var half=Days();half[0]=half[0] with{PayableValue=.5m};Assert.Null(Check(half,Monthly with{PresentDays=25.5m,PayableDays=29.5m}));
        var absent=Days();absent[0]=absent[0] with{Status="Absent",PayableValue=0};Assert.Null(Check(absent,Monthly with{PresentDays=25,PayableDays=29}));
        var holiday=Days();holiday[0]=holiday[0] with{Status="H"};Assert.Null(Check(holiday,Monthly with{PresentDays=25}));
    }

    [Fact]
    public void Existing_absence_short_code_is_exact_zero_only_and_keeps_its_raw_status()
    {
        var rows=Days();rows[0]=rows[0] with{Status="A",PayableValue=0};
        Assert.Null(Check(rows,Monthly with{PresentDays=25,PayableDays=29}));
        Assert.Equal("A",rows[0].Status);Assert.Equal(0,rows[0].PayableValue);Assert.Equal(30,rows.Count);
    }

    [Theory]
    [InlineData(-1)][InlineData(.5)][InlineData(1)]
    public void Absence_short_code_never_creates_positive_or_negative_payable_units(decimal value)
    {
        var rows=Days();rows[0]=rows[0] with{Status="A",PayableValue=value};
        Assert.Contains("unsupported status or payable value",Check(rows,Monthly with{PresentDays=25,PayableDays=29})!);
    }

    [Theory]
    [InlineData("UNKNOWN",0)][InlineData("PL",1)][InlineData("LWP1",0)]
    public void Known_absence_alias_does_not_approve_unknown_or_leave_codes(string status,decimal value)
    {
        var rows=Days();rows[0]=rows[0] with{Status=status,PayableValue=value};
        Assert.Contains("unsupported status or payable value",Check(rows,Monthly with{PresentDays=25,PayableDays=29})!);
    }

    [Theory]
    [InlineData("gap")][InlineData("duplicateDate")][InlineData("duplicateId")][InlineData("zeroId")]
    [InlineData("client")][InlineData("employee")][InlineData("outside")][InlineData("time")]
    [InlineData("unknownStatus")][InlineData("negative")][InlineData("excess")][InlineData("woMissingUnit")]
    [InlineData("absentPaid")][InlineData("presentZero")][InlineData("notes")]
    public void Incomplete_ambiguous_or_overridden_persisted_rows_fail_closed(string kind)
    {
        var rows=Days();rows[0]=kind switch {
            "duplicateDate"=>rows[0] with{Date=rows[1].Date},"duplicateId"=>rows[0] with{Id=rows[1].Id},"zeroId"=>rows[0] with{Id=0},
            "client"=>rows[0] with{ClientId=32},"employee"=>rows[0] with{EmployeeId=42},"outside"=>rows[0] with{Date=new(2026,8,31)},
            "time"=>rows[0] with{Date=rows[0].Date.AddHours(1)},"unknownStatus"=>rows[0] with{Status="CL"},
            "negative"=>rows[0] with{PayableValue=-1},"excess"=>rows[0] with{PayableValue=2},
            "woMissingUnit"=>rows[0] with{Status="WO",PayableValue=0},"absentPaid"=>rows[0] with{Status="Absent",PayableValue=1},
            "presentZero"=>rows[0] with{PayableValue=0},"notes"=>rows[0] with{Remarks="Manual override"},_=>rows[0]};
        if(kind=="gap")rows.RemoveAt(0);
        Assert.NotNull(Check(rows));
    }

    [Theory]
    [InlineData("working")][InlineData("present")][InlineData("payable")][InlineData("unknownPresent")]
    [InlineData("inputSource")][InlineData("sourceType")][InlineData("policyError")][InlineData("override")]
    public void Stored_monthly_inputs_must_match_the_explicit_daily_rollup(string kind)
    {
        var monthly=kind switch {
            "working"=>Monthly with{WorkingDays=29},"present"=>Monthly with{PresentDays=25},"payable"=>Monthly with{PayableDays=29},
            "unknownPresent"=>Monthly with{PresentDays=null},"inputSource"=>Monthly with{InputSource="Dated daily attendance"},
            "sourceType"=>Monthly with{SourceType="Monthly"},"policyError"=>Monthly with{PolicyError="Unresolved"},_=>Monthly with{HasOverride=true}};
        Assert.NotNull(Check(Days(),monthly));
    }

    [Theory]
    [InlineData("unreviewed")][InlineData("multiple")][InlineData("none")][InlineData("foreignGroup")]
    [InlineData("wrongEmployee")][InlineData("monthlyGroup")][InlineData("cycle")][InlineData("current")][InlineData("invalidPeriod")]
    public void Explicit_owned_single_group_and_closed_period_are_required(string kind)
    {
        var group=kind switch {"foreignGroup"=>Group with{ClientId=32},"wrongEmployee"=>Group with{EmployeeId=42},
            "monthlyGroup"=>Group with{Mode="Monthly"},"cycle"=>Group with{StartDay=26,EndDay=25},_=>Group};
        Assert.NotNull(Check(Days(),group:group,reviewed:kind!="unreviewed",memberships:kind=="multiple"?2:kind=="none"?0:1,
            period:kind=="current"?"2026-11":kind=="invalidPeriod"?"2026-13":"2026-09"));
    }
}

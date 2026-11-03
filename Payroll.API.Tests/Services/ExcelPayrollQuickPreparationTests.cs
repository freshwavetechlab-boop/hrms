using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollQuickPreparationTests
{
    [Fact]
    public void PreparationReusesCurrentVerifiedMasterLinksWithoutGuessingOrChangingSourceOrRoster()
    {
        var roster=new ExcelPayrollRoster([new("one",2,"Synthetic employee","TEST-A","Applied",null,"Unmatched fresh lookup")],
            [new(7,"TEST-A","Synthetic employee","Test location"),new(8,"TEST-B","Absent employee","Test location")]);
        var batch=new ExcelPayslipBatch {ClientId=42,Rows=[new() {Id="one",EmployeeCode="TEST-A",NetPay=123.456789m,
            Employment=new(7,"TEST-A","Active",null,false,false)}]};
        var source=JsonSerializer.Serialize(batch,ExcelPayslipRepository.JsonOptions);
        var result=ExcelPayrollSetupRepository.ReuseVerifiedMasterLinks(roster,batch);
        Assert.Equal(7,result.Rows[0].EmployeeId);Assert.Equal("Applied",result.Rows[0].Status);
        Assert.Null(roster.Rows[0].EmployeeId);Assert.Equal(8,Assert.Single(result.MissingEmployees).EmployeeId);
        Assert.Equal(source,JsonSerializer.Serialize(batch,ExcelPayslipRepository.JsonOptions));
    }

    [Theory]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public void IdentityReviewAndEmploymentBlocksCannotBecomeAutomaticMasterMatches(bool identityReview,bool blocked)
    {
        var roster=new ExcelPayrollRoster([new("one",2,"Synthetic","TEST-A","Review",null,"Review needed")],[]);
        var batch=new ExcelPayslipBatch {Rows=[new() {Id="one",Employment=new(7,"TEST-A","Review",null,blocked,identityReview)}]};
        var result=ExcelPayrollSetupRepository.ReuseVerifiedMasterLinks(roster,batch);
        Assert.Null(result.Rows[0].EmployeeId);Assert.Equal("Review",result.Rows[0].Status);
    }

    [Fact]
    public void ConflictingPositiveIdentityAndVerifiedMasterLinkRemainAnExplicitReview()
    {
        var roster=new ExcelPayrollRoster([new("one",2,"Synthetic","TEST-A","Existing",8,"Matched by source")],[]);
        var batch=new ExcelPayslipBatch {Rows=[new() {Id="one",Employment=new(7,"TEST-A","Active",null,false,false)}]};
        var result=ExcelPayrollSetupRepository.ReuseVerifiedMasterLinks(roster,batch);
        Assert.Equal(8,result.Rows[0].EmployeeId);Assert.Equal("Review",result.Rows[0].Status);Assert.Contains("disagree",result.Rows[0].Message);
    }

    private static readonly ExcelPayrollSetupRepository.QuickMonthlyAttendance[] Monthly = [new(7,30m,24m,25m,"Monthly")];
    private static string Signature(int client=42,string month="2026-10",string[]? rows=null,
        IReadOnlyList<ExcelPayrollSetupRepository.QuickMonthlyAttendance>? monthly=null,int[]? daily=null) =>
        ExcelPayrollSetupRepository.QuickAttendanceHash(client,"synthetic-batch",month,rows??["one"],"2026-10-01","2026-10-31",monthly??Monthly,daily??[]);
    private static ExcelPayrollSetupRepository.QuickPreparationSnapshot Snapshot() => new(1,"2026-10",new string('a',64),Signature(),"2026-10-01","2026-10-31",Monthly,[]);
    private static ExcelPayrollQuickPreparation Expected(ExcelPayrollSetupRepository.QuickPreparationSnapshot current) => new(1,current.SourceHash,current.AttendanceSnapshotHash);

    [Fact]
    public void SnapshotBindsClientMonthSelectionAndExactAttendanceAndIsOrderIndependent()
    {
        Assert.NotEqual(Signature(),Signature(client:43)); Assert.NotEqual(Signature(),Signature(month:"2026-11"));
        Assert.NotEqual(Signature(),Signature(rows:["other"])); Assert.NotEqual(Signature(),Signature(daily:[7]));
        Assert.NotEqual(Signature(),Signature(monthly:[Monthly[0] with {PresentDays=24.5m}]));
        Assert.NotEqual(Signature(),Signature(monthly:[Monthly[0] with {PresentDays=null}]));
        Assert.NotEqual(Signature(),Signature(monthly:[Monthly[0] with {PayableDays=26m}]));
        Assert.Equal(Signature(rows:["one","two"],daily:[7,9]),Signature(rows:["two","one"],daily:[9,7]));
    }

    [Fact]
    public void MonthlyImportAcceptsOnlyMatchingExistingTotalsAndNeverSubstitutesPayableForUnknownPresent()
    {
        var current=Snapshot(); var expected=Expected(current);
        Assert.Null(ExcelPayrollSetupRepository.QuickPreparationConflict(expected,current,true,[(7,30,24,25)]));
        Assert.NotNull(ExcelPayrollSetupRepository.QuickPreparationConflict(expected,current,true,[(7,30,25,25)]));
        Assert.NotNull(ExcelPayrollSetupRepository.QuickPreparationConflict(expected,current,true,[(7,30,24,26)]));
        var unknown=current with {MonthlyAttendance=[Monthly[0] with {PresentDays=null}]};
        Assert.NotNull(ExcelPayrollSetupRepository.QuickPreparationConflict(Expected(unknown),unknown,true,[(7,30,24,25)]));
        Assert.Null(ExcelPayrollSetupRepository.QuickPreparationConflict(expected,current,false,[(7,30,0,0)]));
        Assert.Null(ExcelPayrollSetupRepository.QuickPreparationConflict(expected,current,true,[(8,30,22,22)]));
    }

    [Fact]
    public void DatedAttendanceAndConcurrentSourceOrAttendanceChangesRequireFreshReview()
    {
        var current=Snapshot();var expected=Expected(current);
        Assert.NotNull(ExcelPayrollSetupRepository.QuickPreparationConflict(expected,current with {DailyEmployeeIds=[7]},true,[(7,30,24,25)]));
        Assert.NotNull(ExcelPayrollSetupRepository.QuickPreparationConflict(expected,current with {SourceHash=new string('b',64)},false,[]));
        Assert.NotNull(ExcelPayrollSetupRepository.QuickPreparationConflict(expected,current with {AttendanceSnapshotHash=new string('c',64)},true,[]));
        Assert.NotNull(ExcelPayrollSetupRepository.QuickPreparationConflict(expected with {Version=2},current,false,[]));
    }

    [Fact]
    public void OptionalQuickGuardDoesNotChangeLegacyRequestSerializationAndMalformedHashesFailEarly()
    {
        Assert.DoesNotContain("quickPreparation",JsonSerializer.Serialize(new ExcelPayrollSetupRequest(),ExcelPayslipRepository.JsonOptions));
        var error=Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.ValidateRequest(new() {QuickPreparation=new(1,"bad","bad")}));
        Assert.Contains("quick preparation",error.Message);
    }
}

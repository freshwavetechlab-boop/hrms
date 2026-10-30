using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;
public sealed class ExcelNativeAttendanceProofTests
{
    private const string SourceHash="cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static (ExcelPayrollSetupRepository.Plan Plan,ExcelPayrollSetupRepository.PlannedRow Row,ExcelPayrollSetupRequest Request) Fixture(bool external=false) {
        var hash=new string('a',64);var other=new string('b',64);var key="wb_"+hash;
        ExcelPayslipCalculationCell N(string value,string? formula=null)=>new(){Value=value,DataType="Number",FormulaText=formula};
        ExcelPayslipCalculationCell T(string value)=>new(){Value=value,DataType="Text"};
        var payable=external?29.5m:28m;var lwp=30-payable;
        var source=new ExcelPayslipCalculationSource {CaptureVersion=1,DependencyReviewStatus="Reviewed",WorkbookKey=key,SheetName="Salary",ColumnCount=7,CompleteSheetCapture=true,HeaderRow=1,
            WorkbookFiles=[new(){WorkbookKey=key,Sha256=hash,OriginalId=new string('d',32),FileName="synthetic.xlsx"}],
            Columns=[new(){ColumnIndex=6,Kind="earning",Label="Wages",SourceHeader="Wages"}],
            Rows=[new(){SourceRow=3,Cells=[T("SYN-1"),N("30"),N(payable.ToString(System.Globalization.CultureInfo.InvariantCulture),external?"B3-E3":"IFERROR(VLOOKUP(A3,Attendance!A2:D3,4,FALSE),0)"),N("20"),N(lwp.ToString(System.Globalization.CultureInfo.InvariantCulture),external?"VLOOKUP(A3,'[1]Tracker'!A2:D3,4,0)":"B3-C3"),N("30000"),N((payable*1000).ToString(System.Globalization.CultureInfo.InvariantCulture),"F3/B3*C3")]}],
            DependencySheets=[new(){WorkbookKey=external?"wb_"+other:key,SourceHash=external?other:hash,SheetName=external?"Tracker":"Attendance",ColumnCount=4,CompleteSheetCapture=true,Rows=[new(){SourceRow=2,Cells=[T("SYN-1"),N("0"),N("0"),N(external?"0.5":"28")]},new(){SourceRow=3,Cells=[T("SYN-2"),N("0"),N("0"),N("0")]}]}]};
        if(external){source.WorkbookFiles.Add(new(){WorkbookKey="wb_"+other,Sha256=other,OriginalId=new string('e',32),FileName="supplied-dependency.xlsx"});source.ExternalMappings=[new(){FromWorkbookKey=key,Reference="[1]",ToWorkbookKey="wb_"+other,Reviewed=true}];}
        var batch=new ExcelPayslipBatch {Id=new string('f',32),ClientId=42,Month="2026-09",Rows=[new(){Id="row-1",SourceRow=3,EmployeeCode="SYN-1",NetPay=payable*1000,Earnings=[new(){Label="Wages",Amount=payable*1000}]}]};
        var plan=new ExcelPayrollSetupRepository.Plan {Batch=batch,Source=source,LinkedOriginals=source.WorkbookFiles.Select(f=>new ExcelPayslipOriginal(f.OriginalId,f.FileName,100,f.Sha256,DateTime.UnixEpoch)).ToList()};
        var row=new ExcelPayrollSetupRepository.PlannedRow {RowId="row-1",SourceRow=3,Employee=new(){Id=10},Preview=new(){RowId="row-1",PayrollDays=30,PresentDays=20,PayableDays=payable}};plan.Rows.Add(row);
        var request=new ExcelPayrollSetupRequest {TransferReviewVersion=1,PayrollDaysColumnIndex=1,PayableDaysColumnIndex=2,PresentDaysColumnIndex=3,AttendanceCycleStartDay=1,AttendanceCycleEndDay=30,
            AttendanceReview=new(){Mode="SourceSeed",SourceHash=SourceHash,SourceMonth="2026-09",CycleStart="2026-09-01",CycleEnd="2026-09-30",PayableMeaning="AlreadyPayable",PresentMeaning="ActualWorked",LwpColumnIndex=4,LwpBasis="PayrollDivisorMinusPayable",Reviewed=true}};
        request.AttendanceReview!.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(request);
        return(plan,row,request);
    }
    private static ExcelPayrollAttendanceProof Proof(ExcelPayrollSetupRepository.Plan p,ExcelPayrollSetupRepository.PlannedRow r,ExcelPayrollSetupRequest q)=>ExcelPayrollSetupRepository.ProveAttendanceInput(p,r,q,42,SourceHash);
    [Fact] public void LegacyNativeBoundaryMapsRecordedPayableAndDerivedLwpWithoutInventingLookupClosure() {
        var(p,r,q)=SavedWorksheetFixture();q.ImportAttendance=false;q.AttendanceReview!.Mode="VerifyExistingNative";
        p.Source.Rows[0].Cells[2]!.FormulaText="IFERROR(VLOOKUP(A3,Attendance!A2:D3,4,FALSE),0)";
        foreach(var index in new[]{1,2,3,4})p.Source.Columns.Add(new(){ColumnIndex=index,Label="Reviewed days",SourceHeader="Reviewed days",Kind="info"});
        q.AttendanceReview.SourceHash=ExcelPayslipRepository.EmploymentSourceHash(p.Batch,p.Source);
        q.AttendanceReview.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(q);
        p.NativeAttendanceInputs[10]=new(10,42,"2026-09",30,20,28,"2026-09-01","2026-09-30",new string('9',64),"");
        var before=System.Text.Json.JsonSerializer.Serialize(p.Source);
        var proof=ExcelPayrollSetupRepository.ProveAttendanceInput(p,r,q,42,q.AttendanceReview.SourceHash);
        Assert.Equal("Native attendance inputs matched",proof.Status);Assert.Equal(2m,proof.SourceLwp);
        Assert.Contains("does not reconstruct original lookup closure",proof.Reason);Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize(p.Source));
        q.AttendanceReview.Mode="SourceSeed";q.AttendanceReview.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(q);
        Assert.Equal("Blocked",ExcelPayrollSetupRepository.ProveAttendanceInput(p,r,q,42,q.AttendanceReview.SourceHash).Status);
    }
    [Theory][InlineData(false)][InlineData(true)] public void CapturedNhdcAndIitChainsSeedNativePeriodInputsWithoutFreezingLookups(bool external) {
        var(p,r,q)=Fixture(external);var before=System.Text.Json.JsonSerializer.Serialize(p.Source);var proof=Proof(p,r,q);
        Assert.Equal("Reviewed source seed — native pending",proof.Status);Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize(p.Source));Assert.Null(proof.NativeHash);
        Assert.Equal(external?0.5m:2,proof.SourceLwp);
    }
    [Fact] public void ExistingNativeSamePeriodAndCycleMatchesWithoutWritingAttendance() {
        var(p,r,q)=Fixture(true);q.ImportAttendance=false;q.AttendanceReview!.Mode="VerifyExistingNative";q.AttendanceReview.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(q);
        p.NativeAttendanceInputs[10]=new(10,42,"2026-09",30,20,29.5m,"2026-09-01","2026-09-30",new string('9',64),"");
        var proof=Proof(p,r,q);Assert.Equal("Native attendance inputs matched",proof.Status);Assert.NotNull(proof.NativeHash);
        q.ImportAttendance=true;q.AttendanceReview!.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(q);Assert.Contains("Turn off",Proof(p,r,q).Reason);
    }
    [Theory][InlineData("month")][InlineData("cycle")][InlineData("hash")][InlineData("client")][InlineData("amount")][InlineData("new employee")]
    public void NativeOrSourceIdentityPeriodCycleHashAndAmountMismatchBlocks(string change) {
        var(p,r,q)=Fixture();q.ImportAttendance=false;q.AttendanceReview!.Mode="VerifyExistingNative";q.AttendanceReview.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(q);
        p.NativeAttendanceInputs[10]=new(10,42,"2026-09",30,20,28,"2026-09-01","2026-09-30",new string('9',64),"");
        if(change=="month")q.AttendanceReview!.SourceMonth="2026-08";
        if(change=="cycle")p.NativeAttendanceInputs[10]=p.NativeAttendanceInputs[10] with {CycleStart="2026-08-26"};
        if(change=="hash")q.AttendanceReview!.SourceHash=new string('0',64);
        if(change=="client")p.NativeAttendanceInputs[10]=p.NativeAttendanceInputs[10] with {ClientId=43};
        if(change=="amount")p.NativeAttendanceInputs[10]=p.NativeAttendanceInputs[10] with {PayableDays=27};
        if(change=="new employee")r.Employee.Id=0;
        Assert.Equal("Blocked",Proof(p,r,q).Status);
    }
    [Theory][InlineData("duplicate")][InlineData("missing")][InlineData("blank")][InlineData("cache")][InlineData("mapping")]
    public void LookupAmbiguityMissingErrorFallbackBlankAndStaleCacheCannotBecomeVerifiedDays(string change) {
        var(p,r,q)=Fixture();var dependency=p.Source.DependencySheets[0];
        if(change=="duplicate")dependency.Rows[1].Cells[0]!.Value="SYN-1";
        if(change=="missing")dependency.Rows[0].Cells[0]!.Value="UNKNOWN";
        if(change=="blank")dependency.Rows[0].Cells[3]=new(){Value="",DataType="Blank"};
        if(change=="cache")p.Source.Rows[0].Cells[2]!.Value="27";
        if(change=="mapping")q.AttendanceReview!.Reviewed=false;
        Assert.Equal("Blocked",Proof(p,r,q).Status);
    }
    [Fact] public void AlreadyPayableClIsNotAddedAgainAndWorkedOnlyCannotBeRelabelled() {
        var(p,r,q)=Fixture();Assert.NotEqual("Blocked",Proof(p,r,q).Status);
        q.AttendanceReview!.PayableMeaning="WorkedOnly";Assert.Contains("CL",Proof(p,r,q).Reason);
        q.AttendanceReview!.PayableMeaning="AlreadyPayable";q.PresentDaysColumnIndex=null;q.AttendanceReview!.PresentMeaning="ActualWorked";Assert.Equal("Blocked",Proof(p,r,q).Status);
    }
    [Fact] public void PartialDivisorsHalfDaysAndLwpBasisRemainDistinct() {
        var(p,r,q)=Fixture(true);q.AttendanceCycleStartDay=26;q.AttendanceCycleEndDay=25;q.AttendanceReview!.CycleStart="2026-08-26";q.AttendanceReview!.CycleEnd="2026-09-25";q.AttendanceReview.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(q);
        Assert.NotEqual("Blocked",Proof(p,r,q).Status);
        p.Source.Rows[0].Cells[1]!.Value="26";r.Preview.PayrollDays=26;Assert.Equal("Blocked",Proof(p,r,q).Status);
        q.AttendanceReview!.LwpBasis="";Assert.Contains("basis",Proof(p,r,q).Reason);
    }
    [Fact] public void LegacyMissingDependencyAndDuplicateEmployeeIdentityHaveActionableBlocks() {
        var(p,r,q)=Fixture(true);p.Source.DependencySheets.Clear();Assert.Contains("Re-capture",Proof(p,r,q).Reason);
        (p,r,q)=Fixture();p.Batch.Rows.Add(new(){Id="duplicate",EmployeeCode="SYN-1",SourceRow=4});Assert.Contains("identity",Proof(p,r,q).Reason);
    }
    [Fact] public void NativeLegacyAggregateReportsSpecificPrerequisite() {
        var(p,r,q)=Fixture();q.ImportAttendance=false;q.AttendanceReview!.Mode="VerifyExistingNative";q.AttendanceReview.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(q);
        p.NativeAttendanceInputs[10]=new(10,42,"2026-09",30,20,28,null,null,new string('9',64),"Native totals have no dated cycle snapshot; review dated attendance.");
        Assert.Contains("dated cycle snapshot",Proof(p,r,q).Reason);
    }
    [Fact] public void LegacyTransitiveDayFormulaCannotBypassReviewAndNativeHashChangesProofFingerprint() {
        var(p,r,q)=Fixture(true);p.Source.CaptureVersion=0;q.AttendanceReview=null;
        p.Preview.Transfer=new(){SourceHash=SourceHash};ExcelPayrollSetupRepository.FinalizeAttendanceProof(p,q,42,SourceHash);
        Assert.Equal("Blocked",p.Preview.Transfer.AttendanceProofs.Single().Status);
        (p,r,q)=Fixture();q.ImportAttendance=false;q.AttendanceReview!.Mode="VerifyExistingNative";q.AttendanceReview.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(q);
        p.NativeAttendanceInputs[10]=new(10,42,"2026-09",30,20,28,"2026-09-01","2026-09-30",new string('9',64),"");
        var before=Proof(p,r,q);p.NativeAttendanceInputs[10]=p.NativeAttendanceInputs[10] with {SnapshotHash=new string('8',64)};
        var after=Proof(p,r,q);Assert.Equal("Native attendance inputs matched",after.Status);Assert.NotEqual(before.InputHash,after.InputHash);
    }
    [Fact] public void ChangingMappedColumnAfterReviewRequiresFreshAcknowledgement() {
        var(p,r,q)=Fixture();q.PayableDaysColumnIndex=3;
        Assert.Contains("selection changed",Proof(p,r,q).Reason);
    }    [Theory][InlineData("legacy")][InlineData("missing")][InlineData("other original")][InlineData("changed hash")][InlineData("dependency hash")][InlineData("reference only")]
    public void OriginalOwnershipFingerprintAndTypedCaptureAreMandatoryForDependencyProof(string change) {
        var(p,r,q)=Fixture(true);
        if(change=="legacy")p.Source.CaptureVersion=0;
        if(change=="missing")p.LinkedOriginals.Clear();
        if(change=="other original")p.LinkedOriginals[0]=p.LinkedOriginals[0] with {Id=new string('0',32)};
        if(change=="changed hash")p.LinkedOriginals[0]=p.LinkedOriginals[0] with {Sha256=new string('0',64)};
        if(change=="dependency hash")p.Source.DependencySheets[0].SourceHash=new string('0',64);
        if(change=="reference only")p.Source.DependencyReviewStatus="ReferenceOnly";
        var proof=Proof(p,r,q);Assert.Equal("Blocked",proof.Status);Assert.Contains(change is "legacy" or "reference only"?"Re-capture":"Re-retain",proof.Reason);
    }
    private static (ExcelPayrollSetupRepository.Plan Plan,ExcelPayrollSetupRepository.PlannedRow Row,ExcelPayrollSetupRequest Request) SavedWorksheetFixture() {
        var(p,r,q)=Fixture();p.Source.CaptureVersion=0;p.Source.DependencyReviewStatus="Legacy";
        p.Source.WorkbookFiles.Clear();p.Source.DependencySheets.Clear();p.Source.ExternalMappings.Clear();p.LinkedOriginals.Clear();
        p.Source.Rows[0].Cells[2]!.FormulaText=null;
        return(p,r,q);
    }
    [Theory][InlineData(false)][InlineData(true)] public void SavedWorksheetUsesCapturedLiteralOrFormulaWithoutClaimingOriginalProvenance(bool formula) {
        var(p,r,q)=SavedWorksheetFixture();if(formula)p.Source.Rows[0].Cells[2]!.FormulaText="B3-2";
        var before=System.Text.Json.JsonSerializer.Serialize(p.Source);var proof=Proof(p,r,q);
        Assert.Equal("Reviewed source seed — native pending",proof.Status);Assert.Contains("original workbook byte provenance is not certified",proof.Reason);
        Assert.Equal(before,System.Text.Json.JsonSerializer.Serialize(p.Source));Assert.Empty(p.LinkedOriginals);Assert.Null(proof.NativeHash);
    }
    [Fact] public void SavedWorksheetCanVerifyActualNativePeriodButDoesNotHideAttendanceDifference() {
        var(p,r,q)=SavedWorksheetFixture();q.ImportAttendance=false;q.AttendanceReview!.Mode="VerifyExistingNative";q.AttendanceReview.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(q);
        p.NativeAttendanceInputs[10]=new(10,42,"2026-09",30,20,28,"2026-09-01","2026-09-30",new string('9',64),"");
        Assert.Equal("Native attendance inputs matched",Proof(p,r,q).Status);
        p.NativeAttendanceInputs[10]=p.NativeAttendanceInputs[10] with {PayableDays=27};
        Assert.Contains("Same-cycle",Proof(p,r,q).Reason);
    }
    [Fact] public void ExplicitSavedZeroIsValidAndIsNotReplacedWithNativePaidDays() {
        var(p,r,q)=SavedWorksheetFixture();p.Source.Rows[0].Cells[2]!.Value="0";r.Preview.PayableDays=0;
        p.Source.Rows[0].Cells[4]!.Value="30";
        Assert.Equal("Reviewed source seed — native pending",Proof(p,r,q).Status);
    }
    [Theory][InlineData("missing cell")][InlineData("missing value")][InlineData("stale cache")][InlineData("cycle")][InlineData("error")][InlineData("external")][InlineData("worksheet")][InlineData("fallback")][InlineData("reference only")][InlineData("unknown capture")]
    public void SavedWorksheetStillBlocksUnknownDependenciesMissingDataAndFalseProvenance(string change) {
        var(p,r,q)=SavedWorksheetFixture();var cells=p.Source.Rows[0].Cells;
        if(change=="missing cell"){cells[2]!.FormulaText="F3/1000-2";cells[5]=null;}
        if(change=="missing value"){cells[2]!.FormulaText="F3/1000-2";cells[5]!.MissingCachedValue=true;}
        if(change=="stale cache")cells[2]!.FormulaText="B3-1";
        if(change=="cycle")cells[2]!.FormulaText="C3";
        if(change=="error")cells[2]!.Error="#VALUE!";
        if(change=="external")cells[2]!.FormulaText="'[1]Tracker'!D3";
        if(change=="worksheet")cells[2]!.FormulaText="Attendance!D3";
        if(change=="fallback")cells[2]!.FormulaText="IFERROR(Attendance!D3,28)";
        if(change=="reference only")p.Source.DependencyReviewStatus="ReferenceOnly";
        if(change=="unknown capture")p.Source.CaptureVersion=2;
        Assert.Equal("Blocked",Proof(p,r,q).Status);
    }
    [Theory][InlineData("identity")][InlineData("hash")][InlineData("meaning")][InlineData("review")]
    public void SavedWorksheetDoesNotRelaxIdentityReviewOrSourceScope(string change) {
        var(p,r,q)=SavedWorksheetFixture();
        if(change=="identity")p.Batch.Rows.Add(new(){Id="other",SourceRow=4,EmployeeCode="SYN-1"});
        if(change=="hash")q.AttendanceReview!.SourceHash=new string('0',64);
        if(change=="meaning")q.AttendanceReview!.PayableMeaning="WorkedOnly";
        if(change=="review")q.AttendanceReview!.Reviewed=false;
        Assert.Equal("Blocked",Proof(p,r,q).Status);
    }
}

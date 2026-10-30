using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollPayableOnlyImportTests
{
    private static (ExcelPayrollSetupRepository.Plan Plan, ExcelPayrollSetupRepository.PlannedRow Row, ExcelPayrollSetupRequest Request) Fixture(decimal payable = 20)
    {
        ExcelPayslipCalculationCell Number(decimal value) => new() { DataType="Number", Value=value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var batchId=new string('a',32);
        var plan=new ExcelPayrollSetupRepository.Plan
        {
            Batch=new() { Id=batchId, ClientId=42, Month="2026-09", Rows=[new() { Id="source-3", SourceRow=3, EmployeeCode="SYN-1" }] },
            Source=new() { CaptureVersion=0, DependencyReviewStatus="Legacy", WorkbookKey="saved-root", SheetName="Salary", CompleteSheetCapture=true, HeaderRow=1, ColumnCount=4,
                Columns=[new(){ColumnIndex=0,Kind="employeeCode"},new(){ColumnIndex=1,Kind="info"},new(){ColumnIndex=2,Kind="info"},new(){ColumnIndex=3,Kind="earning"}],
                Rows=[new() { SourceRow=3, Cells=[new(){DataType="Text",Value="SYN-1"},Number(30),Number(payable),Number(1000)] }] }
        };
        var row=new ExcelPayrollSetupRepository.PlannedRow { RowId="source-3", SourceRow=3, Employee=new() {Id=10,ClientId=42},
            Preview=new() { RowId="source-3", PayrollDays=30, PresentDays=payable, PayableDays=payable } };
        plan.Rows.Add(row); plan.Preview.Rows.Add(row.Preview);
        var request=new ExcelPayrollSetupRequest { BatchId=batchId, TransferReviewVersion=1, ImportAttendance=true, ImportPayableOnlyAttendance=true,
            PayrollDaysColumnIndex=1, PayableDaysColumnIndex=2, PresentDaysColumnIndex=null, AttendanceCycleStartDay=1, AttendanceCycleEndDay=30,
            Components=[new(){Code="WAGES",CalculationType="Formula",Formula="1000/PAYROLL_DAYS*PAYABLE_DAYS",ProRata=false}],
            AttendanceReview=new() { Mode="SourceSeed", Reviewed=true, SourceMonth="2026-09", CycleStart="2026-09-01", CycleEnd="2026-09-30", PayableMeaning="AlreadyPayable", PresentMeaning="NotMapped" } };
        Review(plan,request);
        return (plan,row,request);
    }

    private static string SourceHash(ExcelPayrollSetupRepository.Plan plan) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { plan.Batch, plan.Source },new JsonSerializerOptions(JsonSerializerDefaults.Web))))).ToLowerInvariant();
    private static void Review(ExcelPayrollSetupRepository.Plan plan,ExcelPayrollSetupRequest request)
    {
        request.AttendanceReview!.SourceHash=SourceHash(plan);
        request.AttendanceReview.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(request);
        plan.Preview.Transfer=new() { SourceHash=request.AttendanceReview.SourceHash };
        ExcelPayrollSetupRepository.FinalizeAttendanceProof(plan,request,42,request.AttendanceReview.SourceHash);
    }

    [Fact] public void ExplicitSourcePayableSeedPreservesUnknownPresenceAndCalculationFallback()
    {
        var(plan,row,request)=Fixture();
        ExcelPayrollSetupRepository.FinalizePresentDaysEvidence(plan,request,42);
        var proof=Assert.Single(plan.Preview.Transfer!.AttendanceProofs);
        Assert.True(proof.Status==ExcelPayrollSetupRepository.PayableOnlyProofStatus,proof.Reason);
        Assert.Null(proof.PresentDays); Assert.Equal(20,proof.PayableDays);
        Assert.Null(row.Preview.PresentDaysEvidence!.SourceValue);
        Assert.False(row.Preview.PresentDaysEvidence.ActualWorkedReviewed);
        Assert.Equal("ReviewedPayableOnlyUnknownPresence",row.Preview.PresentDaysEvidence.Status);
        Assert.Equal(20,row.Preview.PresentDays); Assert.True(plan.Preview.CanApply);
        var saved=ExcelPayrollSetupRepository.AttendanceWriteForRow(plan,row,request,42,new string('b',32));
        Assert.Null(saved.PresentDays); Assert.Equal(SourcePayableAttendance.SourceType,saved.SourceType);
        var provenance=SourcePayableAttendance.Read(saved.Remarks,42,10,"2026-09",30,20);
        Assert.Null(provenance.ActualPresentDays); Assert.Equal("NotMapped",provenance.PresentMeaning);
        Assert.Equal(request.BatchId,provenance.BatchId); Assert.Equal(row.RowId,provenance.RowId);
    }

    [Fact] public void DefaultOptOutStillBlocksInventingWorkedDaysAndKeepsLegacyBindingAndJson()
    {
        var(plan,row,request)=Fixture(); request.ImportPayableOnlyAttendance=false; Review(plan,request);
        Assert.Equal("SourceSeed|1|-|2|-|1|30|1",ExcelPayrollSetupRepository.AttendanceBinding(request));
        Assert.DoesNotContain("importPayableOnlyAttendance",JsonSerializer.Serialize(request,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        ExcelPayrollSetupRepository.FinalizePresentDaysEvidence(plan,request,42);
        Assert.False(plan.Preview.CanApply); Assert.Equal("NotMapped",row.Preview.PresentDaysEvidence!.Status);
        Assert.Equal(20,row.Preview.PresentDays);
    }

    [Fact] public void EnablingUnknownPresenceChangesOnlyOptInBindingSuffix()
    {
        var(_,_,request)=Fixture(); Assert.Equal("SourceSeed|1|-|2|-|1|30|1|payableOnly:1",ExcelPayrollSetupRepository.AttendanceBinding(request));
        request.ImportPayableOnlyAttendance=false; var old=ExcelPayrollSetupRepository.AttendanceBinding(request);
        request.ImportPayableOnlyAttendance=true; Assert.Equal(old+"|payableOnly:1",ExcelPayrollSetupRepository.AttendanceBinding(request));
    }

    [Fact] public void SourceZeroPayableIsNotMissingAndKnownZeroPresenceIsNotUnknown()
    {
        var(plan,row,request)=Fixture(0);
        var unknown=ExcelPayrollSetupRepository.AttendanceWriteForRow(plan,row,request,42,new string('b',32));
        Assert.Null(unknown.PresentDays); Assert.Equal(0,SourcePayableAttendance.Read(unknown.Remarks,42,10,"2026-09",30,0).PayableDays);
        request.ImportPayableOnlyAttendance=false; row.Preview.PresentDays=0;
        var known=ExcelPayrollSetupRepository.AttendanceWriteForRow(plan,row,request,42,new string('b',32));
        Assert.Equal(0,known.PresentDays); Assert.Equal("Monthly",known.SourceType);
    }

    [Theory]
    [InlineData("attendance off")][InlineData("present mapped")][InlineData("native mode")][InlineData("unreviewed")][InlineData("worked meaning")][InlineData("payable meaning")][InlineData("legacy transfer")]
    public void OptInRequiresExplicitReviewedSourceSeedAndUnknownPresence(string change)
    {
        var(plan,row,request)=Fixture();
        if(change=="attendance off")request.ImportAttendance=false;
        if(change=="present mapped")request.PresentDaysColumnIndex=2;
        if(change=="native mode")request.AttendanceReview!.Mode="VerifyExistingNative";
        if(change=="unreviewed")request.AttendanceReview!.Reviewed=false;
        if(change=="worked meaning")request.AttendanceReview!.PresentMeaning="ActualWorked";
        if(change=="payable meaning")request.AttendanceReview!.PayableMeaning="WorkedOnly";
        if(change=="legacy transfer")request.TransferReviewVersion=0;
        Assert.NotNull(ExcelPayrollSetupRepository.PayableOnlyRequestError(request));
        Assert.Equal("Blocked",ExcelPayrollSetupRepository.ProveAttendanceInput(plan,row,request,42,SourceHash(plan)).Status);
    }

    [Theory][InlineData("formula")][InlineData("row formula")][InlineData("base")]
    public void EveryPresenceDependentFormulaRemainsBlocked(string field)
    {
        var(plan,row,request)=Fixture();
        if(field=="formula")request.Components[0].Formula="1000*PRESENT_DAYS/PAYROLL_DAYS";
        if(field=="row formula")request.Components[0].RowFormulas[row.RowId]="present_days*100";
        if(field=="base")request.Components[0].BaseComponent="PRESENT_DAYS";
        Assert.Contains("PRESENT_DAYS",ExcelPayrollSetupRepository.PayableOnlyRequestError(request));
        Assert.Equal("Blocked",ExcelPayrollSetupRepository.ProveAttendanceInput(plan,row,request,42,SourceHash(plan)).Status);
    }

    [Theory][InlineData("monthly")][InlineData("daily")][InlineData("dated policy")][InlineData("split PF")][InlineData("schema")]
    public void ExistingAttendanceAndDatedDependenciesArePreservedBeforeAnyWrite(string blocker)
    {
        var error=ExcelPayrollSetupRepository.PayableOnlyWriteStateError(blocker!="schema",blocker=="monthly",blocker=="daily",blocker=="dated policy",blocker=="split PF");
        Assert.NotNull(error);
        Assert.Null(ExcelPayrollSetupRepository.PayableOnlyWriteStateError(true,false,false,false,false));
    }

    [Theory][InlineData("proof missing")][InlineData("proof duplicate")][InlineData("source changed")][InlineData("wrong month")][InlineData("wrong client")][InlineData("wrong batch")][InlineData("review changed")][InlineData("proof error")]
    public void PersistenceRejectsMissingStaleOrForeignSourceProof(string change)
    {
        var(plan,row,request)=Fixture();
        if(change=="proof missing")plan.Preview.Transfer!.AttendanceProofs.Clear();
        if(change=="proof duplicate")plan.Preview.Transfer!.AttendanceProofs.Add(plan.Preview.Transfer.AttendanceProofs[0]);
        if(change=="source changed")plan.Source.Rows[0].Cells[2]!.Value="19";
        if(change=="wrong month")plan.Batch.Month="2026-10";
        if(change=="wrong client")row.Employee.ClientId=43;
        if(change=="wrong batch")request.BatchId=new string('c',32);
        if(change=="review changed")request.AttendanceReview!.CycleStart="2026-08-31";
        if(change=="proof error")plan.Preview.Transfer!.AttendanceProofs[0]=plan.Preview.Transfer.AttendanceProofs[0] with {Status="Blocked"};
        Assert.Throws<InvalidOperationException>(()=>ExcelPayrollSetupRepository.AttendanceWriteForRow(plan,row,request,42,new string('b',32)));
    }

    [Theory][InlineData("missing")][InlineData("error")][InlineData("cache")]
    public void InvalidPayableSourceCellNeverBecomesZeroOrCertifiedInput(string change)
    {
        var(plan,row,request)=Fixture();
        if(change=="missing")plan.Source.Rows[0].Cells[2]=null;
        if(change=="error")plan.Source.Rows[0].Cells[2]!.Error="#VALUE!";
        if(change=="cache")plan.Source.Rows[0].Cells[2]!.MissingCachedValue=true;
        Assert.Equal("Blocked",ExcelPayrollSetupRepository.ProveAttendanceInput(plan,row,request,42,SourceHash(plan)).Status);
    }

    [Theory][InlineData("missing monthly")][InlineData("missing payroll")][InlineData("monthly required")][InlineData("payroll required")][InlineData("wrong column")][InlineData("wrong type")]
    public void CapabilityRequiresBothExistingNullablePresenceColumns(string change)
    {
        var columns=new List<SourcePayableAttendance.NullablePresenceColumn> { new("employee_monthly_attendance","present_days","YES","decimal"),new("payrunemployees","PresentDays","YES","decimal") };
        Assert.True(SourcePayableAttendance.NullablePresenceSchemaReady(columns));
        if(change=="missing monthly")columns.RemoveAt(0);
        if(change=="missing payroll")columns.RemoveAt(1);
        if(change=="monthly required")columns[0]=columns[0] with {IsNullable="NO"};
        if(change=="payroll required")columns[1]=columns[1] with {IsNullable="NO"};
        if(change=="wrong column")columns[1]=columns[1] with {ColumnName="OtherDays"};
        if(change=="wrong type")columns[1]=columns[1] with {DataType="varchar"};
        Assert.False(SourcePayableAttendance.NullablePresenceSchemaReady(columns));
    }
}

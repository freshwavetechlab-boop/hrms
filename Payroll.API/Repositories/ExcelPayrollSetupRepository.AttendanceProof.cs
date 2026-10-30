using System.Globalization;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;
public sealed partial class ExcelPayrollSetupRepository
{
    internal static string AttendanceBinding(ExcelPayrollSetupRequest q)=>string.Join("|",q.AttendanceReview?.Mode??"",q.PayrollDaysColumnIndex.ToString(CultureInfo.InvariantCulture),q.FixedPayrollDays?.ToString(CultureInfo.InvariantCulture)??"-",q.PayableDaysColumnIndex.ToString(CultureInfo.InvariantCulture),q.PresentDaysColumnIndex?.ToString(CultureInfo.InvariantCulture)??"-",q.AttendanceCycleStartDay.ToString(CultureInfo.InvariantCulture),q.AttendanceCycleEndDay.ToString(CultureInfo.InvariantCulture),q.ImportAttendance?"1":"0")+(string.IsNullOrEmpty(q.PayrollDaysCell)?"":"|cell:"+SharedPayrollDaysCell(q))+(q.ImportPayableOnlyAttendance?"|payableOnly:1":"")+(q.OverrideSourcePayableAttendance?"|sourcePayableOverride:1":"");
    internal static void ValidateAttendanceReview(ExcelPayrollAttendanceReview? review) {
        if(review is null)return;
        if(review.Reviewed && (!System.Text.RegularExpressions.Regex.IsMatch(review.SourceHash??"",@"^[a-fA-F0-9]{64}$") || !DateOnly.TryParseExact(review.CycleStart,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var start) || !DateOnly.TryParseExact(review.CycleEnd,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var end) || end.DayNumber-start.DayNumber is <0 or >30))throw new InvalidOperationException("Review a valid saved source fingerprint and explicit 1–31 day cycle before confirmation.");
        if(review.BoundSourceInputs is null||review.BoundSourceInputs.Length>128||review.Mode is not("SourceSeed" or "VerifyExistingNative")||review.SourceHash is null||review.SourceHash.Length>64
            ||review.SourceMonth is null||review.SourceMonth.Length>7||review.CycleStart is null||review.CycleStart.Length>10||review.CycleEnd is null||review.CycleEnd.Length>10
            ||review.PayableMeaning is not("" or "AlreadyPayable" or "WorkedOnly")||review.PresentMeaning is not("" or "ActualWorked" or "NotMapped")
            ||review.LwpColumnIndex is <0 or >1499||review.LwpBasis is not("" or "PayrollDivisorMinusPayable"))throw new InvalidOperationException("Review bounded attendance column meanings, source period, cycle and proof mode.");
    }
    internal static ExcelPayrollAttendanceProof ProveAttendanceInput(Plan plan,PlannedRow row,ExcelPayrollSetupRequest request,int clientId,string sourceHash,bool reviewedLocationAllocation=false) {
        var sourceRow=plan.Batch.Rows.Single(r=>r.Id==row.RowId);var review=request.AttendanceReview;
        var cellRow=plan.Source.Rows.SingleOrDefault(r=>r.SourceRow==sourceRow.SourceRow);
        var p=row.Preview;
        var inputHash=Hash(JsonSerializer.Serialize(new {clientId,BatchId=plan.Batch.Id,plan.Batch.Month,sourceHash,RowId=sourceRow.Id,sourceRow.SourceRow,review,p.PayrollDays,p.PresentDays,p.PayableDays},Json));
        decimal? lwp=null;string? nativeHash=null;NativePayrollAttendanceInput? nativeInput=null;
        ExcelPayrollAttendanceProof Result(string status,string reason)=>new(row.RowId,sourceRow.SourceRow,review?.Mode??"Unreviewed",status,reason,sourceHash,Hash(JsonSerializer.Serialize(new {inputHash,nativeHash},Json)),p.PayrollDays,request.ImportPayableOnlyAttendance?null:p.PresentDays,p.PayableDays,lwp,nativeHash,nativeInput);
        try {
            if(PayableOnlyRequestError(request) is string payableOnlyError)return Result("Blocked",payableOnlyError);
            if(review is null||!review.Reviewed) return Result("Blocked","Review the saved batch's day-column meanings and choose source-seed or existing native attendance proof. No CL allocation is inferred.");
            if(review.BoundSourceInputs!=AttendanceBinding(request))return Result("Blocked","Day-column, divisor, cycle, proof mode or attendance-import selection changed. Review and acknowledge the current mapping again.");
            if(review.SourceHash!=sourceHash||review.SourceMonth!=plan.Batch.Month||plan.Batch.ClientId!=clientId)return Result("Blocked","Saved batch/client/month/hash differs from this attendance review. Refresh and review the current saved version.");
            // A saved, self-contained worksheet is authoritative source evidence without claiming original-byte provenance.
            // Supplied dependency graphs and typed captures still require their owned, fingerprinted originals.
            var savedWorksheet=plan.Source.CaptureVersion==0
                && plan.Source.DependencyReviewStatus is "Legacy" or "" or "Reviewed"
                && plan.Source.WorkbookFiles is {Count:0} && plan.Source.DependencySheets is {Count:0} && plan.Source.ExternalMappings is {Count:0};
            if(!savedWorksheet&&(plan.Source.CaptureVersion!=1||plan.Source.DependencyReviewStatus!="Reviewed"||plan.Source.WorkbookFiles is null||plan.Source.WorkbookFiles.Count==0))
                return Result("Blocked","This legacy/reference-only snapshot has no reviewed typed original capture. Re-capture the exact original and dependencies inside Excel Payslip, retain them through attachment storage, and save a reviewed batch version before attendance certification.");
            if(!savedWorksheet&&ExcelPayslipRepository.ValidateCapturedWorkbookBindings(plan.Source,plan.LinkedOriginals) is string provenanceError)
                return Result("Blocked",provenanceError+" Re-retain the exact originals within this client's Excel Payslip capture and save a reviewed version; legacy JSON or filenames alone cannot certify provenance.");
            if(!DateOnly.TryParseExact(review.CycleStart,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var start)
                ||!DateOnly.TryParseExact(review.CycleEnd,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var end)||end.DayNumber-start.DayNumber is <0 or >30)
                return Result("Blocked","Supply the reviewed 1–31 day source cycle; worksheet names alone do not establish its dates.");
            var expected=PayRunRepository.NativeAttendanceCycle(plan.Batch.Month,request.AttendanceCycleStartDay,request.AttendanceCycleEndDay);
            if(review.CycleStart!=expected.Start||review.CycleEnd!=expected.End)return Result("Blocked","Reviewed source dates differ from the configured native attendance cycle. Correct the cycle or review a separate period; no date split is guessed.");
            if(review.PayableMeaning!="AlreadyPayable")return Result("Blocked","Worked-only/unknown attendance is not payable days. Supply verified payable totals from attendance/leave review; do not add assumed CL or weekly offs.");
            if(request.PresentDaysColumnIndex.HasValue&&review.PresentMeaning!="ActualWorked"||!request.PresentDaysColumnIndex.HasValue&&review.PresentMeaning!="NotMapped")
                return Result("Blocked","Review actual worked/present meaning separately. Missing present-day columns are not proof that worked equals payable.");
            if(request.PresentDaysColumnIndex is null&&request.Components.Any(c=>c.CalculationType=="Formula"&&System.Text.RegularExpressions.Regex.IsMatch(c.Formula,@"\bPRESENT_DAYS\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                return Result("Blocked","A component uses PRESENT_DAYS. Map an actual worked/present column instead of using payable as a fallback.");
            if(string.IsNullOrWhiteSpace(sourceRow.EmployeeCode)||!reviewedLocationAllocation&&plan.Batch.Rows.Count(r=>string.Equals(r.EmployeeCode,sourceRow.EmployeeCode,StringComparison.OrdinalIgnoreCase))!=1)
                return Result("Blocked","Employee code is missing/duplicated in this source batch. Resolve identity or reviewed source-unit aggregation before attendance proof; names are not matching keys.");
            if(!reviewedLocationAllocation&&plan.Rows.Count(r=>r.Employee.Id>0&&r.Employee.Id==row.Employee.Id)>1)return Result("Blocked","Multiple selected rows map to one native employee. Review source-unit aggregation before comparing a whole native month.");
            if(cellRow is null)return Result("Blocked","Saved source row is unavailable; reopen/re-capture the exact original within Excel Payslip and save a reviewed batch version.");
            IReadOnlyDictionary<string,decimal>? nativeBoundary=null;
            var dayColumns=new[]{request.PayableDaysColumnIndex,request.FixedPayrollDays.HasValue?-1:request.PayrollDaysColumnIndex,
                request.PresentDaysColumnIndex??-1,review.LwpColumnIndex??-1}.Where(c=>c>=0).Distinct().ToArray();
            if(review.Mode=="VerifyExistingNative" && savedWorksheet
                && dayColumns.All(index=>plan.Source.Columns.Count(c=>c.ColumnIndex==index&&request.SourceColumnRoles.GetValueOrDefault(index,c.Kind) is "info" or "ignore")==1))
            {
                if(request.ImportAttendance)return Result("Blocked","Existing native proof cannot replace recorded attendance; turn source attendance import off.");
                if(row.Employee.Id<=0||!plan.NativeAttendanceInputs.TryGetValue(row.Employee.Id,out var observed))
                    return Result("Blocked","A recorded same-employee/month native attendance snapshot is required for native day mapping.");
                if(!string.IsNullOrEmpty(observed.Error))return Result("Blocked",observed.Error);
                nativeBoundary=ReviewAppliedNativeDayInputs(clientId,row.Employee.Id,plan.Batch,plan.Source,sourceRow,request,p,observed);
            }
            var columns=new List<(int Column,decimal Expected)> { (request.PayableDaysColumnIndex,p.PayableDays) };
            if(request.FixedPayrollDays is null)columns.Add((request.PayrollDaysColumnIndex,p.PayrollDays));
            if(request.PresentDaysColumnIndex is int present)columns.Add((present,p.PresentDays));
            if(review.LwpColumnIndex is int lwpColumn) {
                if(review.LwpBasis!="PayrollDivisorMinusPayable"||lwpColumn==request.PayableDaysColumnIndex||lwpColumn==request.PayrollDaysColumnIndex||lwpColumn==request.PresentDaysColumnIndex)
                    return Result("Blocked","LWP must be a distinct reviewed column whose basis is divisor minus payable. Calendar-eligibility LWP with a different divisor cannot be relabelled as LOP_DAYS; map verified payable directly or resolve that rule.");
                var lwpCell=cellRow.Cells.ElementAtOrDefault(lwpColumn);lwp=decimal.TryParse(lwpCell?.Value,NumberStyles.Number,CultureInfo.InvariantCulture,out var actualLwp)?actualLwp:null;columns.Add((lwpColumn,Math.Max(0,p.PayrollDays-p.PayableDays)));
            }
            foreach(var (column,expectedValue) in columns.Distinct()) {
                var cell=cellRow.Cells.ElementAtOrDefault(column);if(column<0||cell is null||cell.Error is not null||cell.CalculationError is not null||cell.MissingCachedValue)
                    return Result("Blocked","Attendance source input/cache is missing or invalid. Re-capture the exact original and resolve its dependencies; missing days never mean zero.");
                var calculated=ExcelPayslipCalculationService.EvaluateCapturedCellAtInputBoundary(plan.Source,TransferLetter(column)+sourceRow.SourceRow,nativeBoundary,requireExplicitCells:savedWorksheet);
                if(!decimal.TryParse(cell.Value,NumberStyles.Number,CultureInfo.InvariantCulture,out var cached)||Math.Abs(calculated.Value-cached)>0.000001m||Math.Abs(calculated.Value-expectedValue)>0.000001m)
                    return Result("Blocked","Captured formula, saved day cache and configured/native input differ. Recalculate/review a new saved batch version; do not freeze a stale lookup result.");
                if(calculated.Traces.Any(t=>t.MatchMode.StartsWith("Approximate",StringComparison.Ordinal)||t.MatchMode=="IFERROR"||t.SelectedRow is null||t.MatchingRows!=1||t.Status!="Matched"))
                    return Result("Blocked","Attendance lookup is approximate, duplicate, missing, blank or using an error fallback. Exact employee-input identity needs one nonblank match; standalone Excel behavior remains preserved.");
            }
            var sourceBasis=savedWorksheet?"Authoritative saved batch cells/formulas agree; original workbook byte provenance is not certified. ":"Captured source-month inputs/formulas agree. ";
            if(nativeBoundary is not null)sourceBasis+="Reviewed day columns bind the recorded native period inputs; this does not reconstruct original lookup closure. ";
            if(request.CorrectionOfImportId is not null)sourceBasis+="Salary-only correction retains the recorded monthly/configured cycle inputs; this is not a new dated-attendance or source worked-day certificate. ";
            if(reviewedLocationAllocation)return Result("Reviewed source allocation - aggregate pending",sourceBasis+"This part belongs to the complete reviewed employee group. Native attendance is checked once against the group's allocations; no historical transfer dates are inferred.");
            if(request.ImportPayableOnlyAttendance)return Result(PayableOnlyProofStatus,sourceBasis+"Reviewed already-payable totals may seed the native period with actual presence explicitly unknown. Dated attendance, leave entitlement and full attendance parity are not certified; presence-dependent calculations remain blocked.");
            if(request.OverrideSourcePayableAttendance) {
                if(PayableOverrideRequestError(request) is string overrideError)return Result("Blocked",overrideError);
                return Result("Reviewed source payable override",sourceBasis+"The saved source's already-payable units replace this month's salary payable totals. Native dated presence and attendance memberships are retained separately, with no extra leave/off credits. This does not certify source worked-day parity.");
            }
            if(review.Mode=="SourceSeed")return Result("Reviewed source seed — native pending",sourceBasis+" Configuration may use PAYABLE_DAYS/PRESENT_DAYS/LOP_DAYS as period inputs, never recurring lookup constants. Native ledger/Employee payroll parity must be checked after reviewed setup; no entitlement is granted.");
            if(request.ImportAttendance)return Result("Blocked","Existing native proof keeps native attendance unchanged. Turn off source attendance import before using this mode.");
            if(row.Employee.Id<=0||!plan.NativeAttendanceInputs.TryGetValue(row.Employee.Id,out var native))return Result("Blocked","No existing native employee/attendance snapshot is available. Resolve identity and attendance, or explicitly use source-seed mode with native proof pending.");
            nativeHash=native.SnapshotHash;nativeInput=native;
            if(native.EmployeeId!=row.Employee.Id||native.ClientId!=clientId||native.Month!=plan.Batch.Month)
                return Result("Blocked","Native employee/client/month/cycle differs from the saved source review. No cross-period or name-only proof is accepted.");
            if(!string.IsNullOrEmpty(native.Error))return Result("Blocked",native.Error);
            if(native.CycleStart!=review.CycleStart||native.CycleEnd!=review.CycleEnd)return Result("Blocked","Native dated cycle differs from the reviewed source dates; no cross-cycle proof is accepted.");
            if(native.PayrollDays!=p.PayrollDays||Math.Abs(native.PayableDays-p.PayableDays)>0.000001m||request.PresentDaysColumnIndex.HasValue&&(!native.PresentDays.HasValue||Math.Abs(native.PresentDays.Value-p.PresentDays)>0.000001m))
                return Result("Blocked","Same-cycle native divisor/present/payable inputs differ from the finance source. Review the displayed source/native attendance and policy, then repreview; CL is not automatically credited.");
            return Result("Native attendance inputs matched",sourceBasis+(request.CorrectionOfImportId is null
                ? "The regular attendance engine's same-employee/client/month/dated-cycle inputs match the captured source. "
                : "The regular engine's recorded monthly inputs for this employee/client/month and configured cycle match the captured source. ")
                +"Component/net/employer comparison and Employee statutory simulation remain separate checks.");
        }catch(InvalidOperationException e){return Result("Blocked",e.Message+" Re-capture/map the exact dependency inside Excel Payslip and save a reviewed version before transfer.");}
    }
    internal static void FinalizeAttendanceProof(Plan plan,ExcelPayrollSetupRequest request,int clientId,string sourceHash) {
        if(plan.Preview.Transfer is null)return;
        var requires=request.AttendanceReview is not null||plan.Source.CaptureVersion>0;
        if(!requires) {
            requires=plan.Rows.Any(r=>new[] {request.PayableDaysColumnIndex,request.PresentDaysColumnIndex??-1}.Where(c=>c>=0).Any(c=>{
                var cell=plan.Source.Rows.FirstOrDefault(s=>s.SourceRow==r.SourceRow)?.Cells.ElementAtOrDefault(c);
                return !string.IsNullOrWhiteSpace(cell?.FormulaText);
            }));
        }
        if(!requires)return;
        foreach(var row in plan.Rows.Where(r=>!r.KeepExisting)) {
            var proof=ProveAttendanceInput(plan,row,request,clientId,sourceHash);plan.Preview.Transfer.AttendanceProofs.Add(proof);
            if(proof.Status=="Blocked")plan.Preview.Issues.Add(new("Error",proof.Reason,row.RowId,"Attendance mapping"));
        }
    }
}

using System.Data;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private sealed record AppliedAdjustmentReview(SelectedPayRunAdjustmentReview Review,
        IReadOnlyList<SelectedSourceFinancialLine> Lines,PayrollAttendanceInputEvidence Native,string WorkedMeaning,
        decimal SourceNet,decimal CoreNet);
    private sealed class SourceReviewGroup
    {
        public int Id {get;set;} public int ClientId {get;set;} public string PolicyBatchId {get;set;}="";
        public int CycleStartDay {get;set;} public int CycleEndDay {get;set;} public string Mode {get;set;}="";
    }

    public async Task<SelectedDraftSourceReviewPreview> PreviewSelectedDraftSourceReviewAsync(CreatePayRunRequest request,
        CancellationToken token=default)
    {
        SelectedPayRunDraft.RequireCreation(request,SelectedRunRuntime.Read(configuration));
        var preview=new SelectedDraftSourceReviewPreview();
        if(request.IncludedEmployeeIds.Count!=1||request.AdjustmentIds.Count!=1)
        {preview.Blockers.Add("Current-source review supports one explicitly selected existing employee and one existing approved adjustment.");return preview;}
        await using var connection=CreateConnection();await connection.OpenAsync(token);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,isReadOnly:true,token);
        try
        {
            var state=await ReadSelectedAdjustmentsAsync(connection,transaction,request,false,false);
            RequireSelectedAdjustmentState(state);
            var review=state.Reviews.SingleOrDefault();
            if(review?.SourceWitness is null)throw new SelectedPayRunConflictException("This separate current-source review is only for an existing AppliedV0 receipt; version1 transfer provenance keeps its normal path.");
            preview.Reviews=state.Reviews;preview.Fingerprint=review.SourceWitness.WitnessFingerprint;
            var sourceReview=state.AppliedReviews.Single();
            preview.Lines=sourceReview.Lines.ToList();preview.NativeAttendance=sourceReview.Native;
            preview.SourceActualWorkedMeaning=sourceReview.WorkedMeaning;preview.SourceNet=sourceReview.SourceNet;preview.CoreNet=sourceReview.CoreNet;
        }
        catch(SelectedPayRunConflictException error){preview.Blockers.Add(error.Message);}
        finally{await transaction.RollbackAsync(token);}
        return preview;
    }

    private async Task<AppliedAdjustmentReview> ReviewAppliedAdjustmentAsync(MySqlConnection db,MySqlTransaction? tx,
        CreatePayRunRequest request,PayrollAdjustment adjustment,SelectedPayRunAdjustments.SourceReference reference,
        ExcelPayslipBatch batch,ExcelPayslipCalculationSource source,ExcelPayrollSetupRequest imported,
        ExcelPayrollSetupResult receipt,string receiptFingerprint)
    {
        if(tx is null)throw new SelectedPayRunConflictException("Current-source review requires one caller-owned transaction snapshot.");
        SelectedPayRunAdjustments.RequireUnbound(request,adjustment);
        if(request.IncludedEmployeeIds.Count!=1||request.AdjustmentIds.Count!=1||imported.TransferReviewVersion!=0
            ||receipt.Transfer is not null||receipt.ImportId!=reference.ImportId||batch.Id!=reference.BatchId
            ||batch.ClientId!=request.ClientId||batch.Month!=request.PayPeriod)
            throw new SelectedPayRunConflictException("A single original AppliedV0 source binding is required; no historical receipt is upgraded or replaced.");
        await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db,tx,request.ClientId,batch.Id);
        var active=await db.ExecuteScalarAsync<int?>("SELECT IsActive FROM clients WHERE Id=@ClientId",new{request.ClientId},tx);
        if(active!=1)throw new SelectedPayRunConflictException("The current source client is missing or inactive.");
        var history=await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM payrunemployees e JOIN payruns r ON r.Id=e.PayRunId
WHERE r.ClientId=@ClientId AND r.PayPeriod=@PayPeriod AND r.RunType='Regular'
AND r.Status IN ('Draft','Pending Approval','Approved','Partially Paid','Paid') AND e.IsSkipped=FALSE AND e.EmployeeId=@EmployeeId",
            new{request.ClientId,request.PayPeriod,adjustment.EmployeeId},tx);
        if(history!=0)throw new SelectedPayRunConflictException("This employee already has a regular result in the selected month; no duplicate source review can create another payroll.");
        var groups=(await db.QueryAsync<SourceReviewGroup>(@"SELECT g.id Id,g.client_id ClientId,COALESCE(g.policy_batch_id,'') PolicyBatchId,
g.attendance_cycle_start_day CycleStartDay,g.attendance_cycle_end_day CycleEndDay,COALESCE(g.attendance_mode,'') Mode
FROM attendance_groups g JOIN attendance_group_employees m ON m.attendance_group_id=g.id
WHERE g.client_id=@ClientId AND g.is_active=TRUE AND m.employee_id=@EmployeeId ORDER BY g.id",
            new{request.ClientId,adjustment.EmployeeId},tx)).ToList();
        if(groups.Count!=1||!SelectedPayRunDraft.Groups(request).ToHashSet().SetEquals(groups.Select(g=>g.Id))
            ||groups[0].PolicyBatchId!=request.AttendancePolicyBatchId.Trim()
            ||groups[0].CycleStartDay!=imported.AttendanceCycleStartDay||groups[0].CycleEndDay!=imported.AttendanceCycleEndDay)
            throw new SelectedPayRunConflictException("Current exact attendance group/cycle/policy differs from the Applied source mapping or explicit run selection.");
        var groupState=(await db.QueryAsync("SELECT * FROM attendance_groups WHERE id=@Id AND client_id=@ClientId",new{Id=groups[0].Id,request.ClientId},tx)).ToList();
        EmployeeSimulationEvidence? evidence=null;
        var simulation=await SimulateEmployeeInTransactionAsync(db,tx,new(){Mode="Employee",ClientId=request.ClientId,
            EmployeeId=adjustment.EmployeeId,PayPeriod=request.PayPeriod},default,value=>evidence=value);
        if(!simulation.Complete||evidence is null||simulation.AttendanceEvidence is null)
            throw new SelectedPayRunConflictException("Complete saved Employee/source calculation is required: "+string.Join(" ",simulation.Issues.Where(i=>i.Severity=="Error").Select(i=>i.Message)));
        if(simulation.LocationAllocations is not null)throw new SelectedPayRunConflictException("This initial proof path does not redistribute location allocations; use their separately reviewed source workflow.");
        SelectedPayRunAdjustments.RequireExactIds(request,evidence.Adjustments);
        var identities=await EmployeeRepository.ReadExcelPayrollIdentitySnapshotAsync(db,tx,request.ClientId);
        var employee=identities.SingleOrDefault(e=>e.Id==adjustment.EmployeeId)
            ??throw new SelectedPayRunConflictException("The exact current employee is unavailable in this client.");
        // Use the effective-period assignment actually resolved by the regular
        // engine, not a current-day master assignment from a later salary record.
        employee.SalaryStructureId=simulation.TemplateId;
        var effective=(PayRunSourceEmployee)evidence.Employee;
        employee.AnnualCtc=effective.AnnualCtc;
        employee.SalaryComponents=new(effective.SalaryComponents);
        var native=(PayRunAttendance)evidence.Attendance;
        if(native.WorkingDays is <1 or >31 || native.WorkingDays!=decimal.Truncate(native.WorkingDays))
            throw new SelectedPayRunConflictException("A mapped payroll divisor must be a whole recorded 1-31 day value.");
        var nativeCycle=NativeAttendanceCycle(request.PayPeriod,groups[0].CycleStartDay,groups[0].CycleEndDay);
        var inputSnapshot=SelectedPayRunAppliedSourceReview.Hash(new{request.ClientId,request.PayPeriod,employee.Id,native,groups,groupState,nativeCycle});
        var nativeInputs=new NativePayrollAttendanceInput(employee.Id,request.ClientId,request.PayPeriod,(int)native.WorkingDays,
            native.PresentDays,native.PayableDays,nativeCycle.Start,nativeCycle.End,inputSnapshot,native.PolicyError??"");
        var mapped=ExcelPayrollSetupRepository.ReviewAppliedSourceRow(request.ClientId,employee.Id,batch,source,imported,receipt,
            evidence.SetupJson,employee,identities,evidence.DetailsJson,nativeInputs);
        var normalValidation=ValidatePayRunInputs(0,request,"Regular",[effective],new(){[employee.Id]=native},evidence.SetupJson);
        if(normalValidation.Any(issue=>issue.IsBlocking))
            throw new SelectedPayRunConflictException("Regular payroll input validation failed: "+string.Join(" ",normalValidation.Where(issue=>issue.IsBlocking).Select(issue=>issue.Message)));
        var cycle=RequireAppliedSourceNative(request,mapped.PayrollDays,mapped.PayableDays,mapped.SourcePresentDays,native,
            groups[0].Mode,groups[0].CycleStartDay,groups[0].CycleEndDay);
        var lines=CompareAppliedSourceCore(mapped.Lines,simulation,evidence.DetailsJson,adjustment);
        var sourceRow=batch.Rows.Single(r=>r.SourceRow==reference.SourceRow);
        if(simulation.Gross!=lines.Where(l=>l.Category is "Earning" or "Reimbursement").Sum(l=>l.CoreAmount)
            ||simulation.Deductions!=lines.Where(l=>l.Category=="Deduction").Sum(l=>l.CoreAmount)
            ||simulation.Benefits!=lines.Where(l=>l.Category=="Benefit").Sum(l=>l.CoreAmount)
            ||simulation.Net!=Math.Max(0,simulation.Gross!.Value-simulation.Deductions!.Value))
            throw new SelectedPayRunConflictException("Actual core totals do not reconcile to every reviewed component; expected totals are never substituted.");
        var business=SelectedPayRunAdjustments.BusinessFingerprint(adjustment);
        var nativeHash=SelectedPayRunAppliedSourceReview.Hash(new{request.ClientId,request.PayPeriod,employee.Id,native,groups,groupState,cycle,mapped.SourceActualWorkedMeaning});
        var salaryHash=SelectedPayRunAppliedSourceReview.Hash(new{evidence.Employee,evidence.SetupJson,evidence.TaxInputs,evidence.PolicyInputs});
        var coreHash=SelectedPayRunAppliedSourceReview.Hash(new{evidence.DetailsJson,simulation.Inputs,simulation.Lines,simulation.Gross,simulation.Deductions,simulation.Benefits,simulation.Net});
        var comparisonHash=SelectedPayRunAppliedSourceReview.Hash(new{lines,sourceRow.NetPay,sourceRow.DeclaredGross,sourceRow.DeclaredDeductions,mapped.Arithmetic,simulation.Gross,simulation.Deductions,simulation.Benefits,simulation.Net});
        var witness=SelectedPayRunAppliedSourceReview.BuildWitness(request,receiptFingerprint,mapped.SourceHash,mapped.MappingFingerprint,
            mapped.IdentityFingerprint,mapped.FormulaFingerprint,nativeHash,salaryHash,coreHash,comparisonHash,business);
        var review=new SelectedPayRunAdjustmentReview(adjustment.Id,employee.Id,adjustment.ComponentCode,adjustment.AdjustmentType,
            adjustment.Amount,adjustment.Taxable,batch.Id,reference.ImportId,sourceRow.Id,sourceRow.SourceRow,mapped.SourceHash,receiptFingerprint,business){SourceWitness=witness};
        return new(review,lines,simulation.AttendanceEvidence,mapped.SourceActualWorkedMeaning,sourceRow.NetPay,simulation.Net!.Value);
    }

    internal static System.Text.Json.Nodes.JsonObject StableProjectedTaxInputs(string json)
    {
        var snapshot=System.Text.Json.Nodes.JsonNode.Parse(json)?.AsObject()
            ??throw new InvalidOperationException("A complete no-save tax input snapshot is required for source review.");
        // This one field is generated by ComputeAsync on each calculation. Keep
        // rule/version timestamps, regime, YTD, declaration and all business data.
        snapshot.Remove("calculatedAt");
        return snapshot;
    }

    internal static (string Start,string End) RequireAppliedSourceNative(CreatePayRunRequest request,int sourceDays,
        decimal sourcePayable,decimal? sourcePresent,PayRunAttendance native,string groupMode,int cycleStart,int cycleEnd)
    {
        var cycle=NativeAttendanceCycle(request.PayPeriod,cycleStart,cycleEnd);
        if(!string.IsNullOrEmpty(native.PolicyError)||native.WorkingDays!=sourceDays||native.WorkingDays!=request.TotalWorkingDays
            ||native.PayableDays!=sourcePayable||sourcePresent.HasValue&&native.PresentDays!=sourcePresent)
            throw new SelectedPayRunConflictException("Recorded native attendance/divisor differs from the mapped source, or an existing attendance policy reports an error.");
        if(native.PolicySnapshot is { } dated)
        {
            if(dated.PeriodStart.ToString("yyyy-MM-dd")!=cycle.Start||dated.PeriodEnd.ToString("yyyy-MM-dd")!=cycle.End)
                throw new SelectedPayRunConflictException("The regular engine's dated cycle differs from the source/group cycle.");
        }
        else if(native.InputSource!="Monthly snapshot"||native.SourceType!="Monthly"||groupMode!="Monthly")
            throw new SelectedPayRunConflictException("A legitimate recorded Monthly branch or the normal dated policy snapshot is required; no daily record or attendance meaning is invented.");
        return cycle;
    }

    internal static List<SelectedSourceFinancialLine> CompareAppliedSourceCore(IReadOnlyList<SelectedSourceFinancialLine> source,
        PayrollSimulationResult core,string detailsJson,PayrollAdjustment adjustment)
    {
        if(!core.Complete||source.Count==0||source.Select(l=>l.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=source.Count)
            throw new SelectedPayRunConflictException("Unique complete source and native financial coverage is required.");
        var groups=core.Lines.GroupBy(l=>l.Code,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToList(),StringComparer.OrdinalIgnoreCase);
        if(!groups.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(source.Select(l=>l.Code)))
            throw new SelectedPayRunConflictException("Native payroll has missing or additional financial components, including projected tax; review every component explicitly.");
        using var details=JsonDocument.Parse(detailsJson);
        var adjustmentLines=details.RootElement.EnumerateArray().Where(l=>Text(l,"Id").Equals(adjustment.ComponentCode,StringComparison.OrdinalIgnoreCase)&&!l.TryGetProperty("ComponentRole",out _)).ToArray();
        if(adjustmentLines.Length!=1||NumberFrom(Text(adjustmentLines[0],"amount"))!=adjustment.Amount
            ||Text(adjustmentLines[0],"Category")!=adjustment.AdjustmentType||Bool(adjustmentLines[0],"ProRata",true))
            throw new SelectedPayRunConflictException("The existing approved adjustment must contribute once, with unchanged classification/amount and no attendance proration.");
        var result=new List<SelectedSourceFinancialLine>();
        foreach(var expected in source)
        {
            var actual=groups[expected.Code];
            if(actual.Any(l=>l.Category!=expected.Category))throw new SelectedPayRunConflictException("A financial component category differs even though amounts may match.");
            var classified=details.RootElement.EnumerateArray().Where(l=>Text(l,"Id").Equals(expected.Code,StringComparison.OrdinalIgnoreCase)
                &&l.TryGetProperty("ComponentRole",out _)).ToArray();
            if(classified.Length!=1||Text(classified[0],"ComponentRole")!=expected.ComponentRole
                ||Text(classified[0],"StatutoryType","None")!=expected.StatutoryType)
                throw new SelectedPayRunConflictException("Native component role or statutory classification differs; monetary equality cannot alias components.");
            var period=expected.Code.Equals(adjustment.ComponentCode,StringComparison.OrdinalIgnoreCase);
            if(period)
            {
                if(actual.Count!=2||actual.Count(l=>l.Earned==0)!=1||actual.Count(l=>l.Earned==adjustment.Amount)!=1
                    ||expected.SourceRaw!=adjustment.Amount||expected.Category!=adjustment.AdjustmentType)
                    throw new SelectedPayRunConflictException("The original zero template placeholder and one approved period literal must both remain explicit; no duplicated or recurring amount is permitted.");
                result.Add(expected with{CoreAmount=adjustment.Amount,CoreUnrounded=adjustment.Amount,FormulaStatus="Exact approved period literal"});continue;
            }
            if(actual.Count!=1)throw new SelectedPayRunConflictException("A recurring component has ambiguous native financial lines.");
            var line=actual[0];
            if(line.Earned!=expected.SourceRaw)
            {
                if(line.UnroundedEarned is not decimal before
                    ||ExcelPayrollSetupRepository.SourceDisplayPrecision(before)!=ExcelPayrollSetupRepository.SourceDisplayPrecision(expected.SourceRaw)
                    ||decimal.Round(before,2,MidpointRounding.ToEven)!=line.Earned)
                    throw new SelectedPayRunConflictException("A source/native financial amount differs without deterministic pre-rounding evidence; no tolerance or forced amount is used.");
            }
            result.Add(expected with{CoreAmount=line.Earned,CoreUnrounded=line.UnroundedEarned,
                FormulaStatus=line.Earned==expected.SourceRaw?expected.FormulaStatus:expected.FormulaStatus+"; explained native currency stage"});
        }
        return result;
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    private sealed record HistoricalReportedInputPlan(ReportedPeriodInputProfileV2 Profile,ReportedPeriodBoundRowV2 Bound,
        NamedFormulaInputContext Context,ReportedPeriodCashClosureV2 CashClosure,string OwnershipHash,HistoricalPayableOnlySeedPlan Seed);

    internal static void ValidateReportedHistoricalSalaryRequest(HistoricalPeriodSalaryRequest request)
    {
        if(request?.Setup?.ReviewedSourceMonthlyCtcOperand==true)
            throw new InvalidOperationException("Normal source monthly CTC operands cannot authorize reported-period Employee payroll.");
        if(request?.Setup?.RecurringBlankInputReview is not null)
            throw new InvalidOperationException("Recurring blank operand review cannot authorize reported-period Employee payroll; it belongs to normal configuration only.");
        if(request?.Setup?.ConfigurationDifferenceReview is not null)
            throw new InvalidOperationException("Configuration monetary review cannot authorize reported-period Employee payroll. Use its separate native evidence and historical review.");
        var q=request?.Setup;
        if(q?.ReportedPeriodInputs is not {Version:2,Reviewed:true} || request!.Assignments is null || request.Assignments.Count is <1 or >100
            || request.SalaryTargetMode!="PreserveEffectiveHistoricalTarget" || request.InitialSourceTargetReview is not null
            || request.NamedInputReview is not null || q.NamedInputReview is not null || request.LiteralSelfInputReview is not null
            || request.PeriodProfessionalTaxReview is not null || request.PayableOnlySeedReview is null
            || request.Assignments.Any(a=>a is null||a.EmployeeId<=0||string.IsNullOrWhiteSpace(a.RowId)||!Regex.IsMatch(a.TemplateId??"",@"\A[0-9]{1,20}\z"))
            || request.Assignments.Select(a=>a.EmployeeId).Distinct().Count()!=request.Assignments.Count
            || request.Assignments.Select(a=>a.RowId).Distinct(StringComparer.Ordinal).Count()!=request.Assignments.Count
            || q.RowIds is null || !q.RowIds.Order(StringComparer.Ordinal).SequenceEqual(request.Assignments.Select(a=>a.RowId).Order(StringComparer.Ordinal))
            || request.Assignments.Select(a=>a.TemplateId).Distinct(StringComparer.Ordinal).Count()!=1
            || q.ImportAttendance || q.ImportPayableOnlyAttendance || q.OverrideSourcePayableAttendance || q.LocationAllocations is not null
            || q.CorrectionOfImportId is not null || q.QuickPreparation is not null || q.MonthlyCtcColumnIndex.HasValue || q.ReviewedExtraDutyUnits
            || q.PresentDaysColumnIndex.HasValue || !q.PreserveExistingAttendancePolicies
            || q.Components.Any(c=>c.IsPeriodAdjustment||c.SourceColumnIndex.HasValue||c.ExemptWhenSourceZero||c.ProRata
                ||c.CalculationType is not("Formula" or "Manual / Variable")||string.IsNullOrWhiteSpace(c.ExistingComponentId))
            || q.EmployeeDecisions.Any(d=>d.AllowCreate) || q.EmployeeDecisions.Count!=request.Assignments.Count
            || request.Assignments.Any(a=>q.EmployeeDecisions.Count(d=>d.RowId==a.RowId&&d.MatchedEmployeeId==a.EmployeeId)!=1)
            || !q.ReviewedFixedInputs || !q.ReviewedPolicyScope || q.TransferReviewVersion!=1 || q.TemplateScopeReviewVersion!=1)
            throw new InvalidOperationException("Reported closed salary requires one exact existing managed template, complete existing identities and historical targets, a separately reviewed insert-only payable seed and no Master, policy, actual-presence, amount or initial-target override.");
        ValidateHistoricalPayableOnlySeedReview(request);
        ValidateRequestShape(q);
        ValidateHistoricalSourceIdentityReview(request);ValidateHistoricalAmountDifferenceReview(request);
    }

    private async Task<HistoricalPlan> BuildReportedHistoricalPlanAsync(MySqlConnection db,MySqlTransaction tx,int clientId,
        HistoricalPeriodSalaryRequest request,bool locked,AuthUser actor)
    {
        ValidateReportedHistoricalSalaryRequest(request);ReportedPeriodInputV2Codec.RequireWriterCompatibility(configuration);
        RequireHistoricalAmountDifferenceEnabled(request);
        var q=request.Setup;var ids=request.Assignments.Select(a=>a.EmployeeId).Order().ToArray();
        if(locked)await LockHistoricalInputsAsync(db,tx,clientId,request);
        var state=await EmployeeRepository.ReadClosedSalaryHistoryStateAsync(db,tx,clientId,ids,locked);
        await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db,tx,clientId,q.BatchId);
        var(batch,source)=await ReadSourceAsync(db,tx,clientId,q.BatchId);var(from,to)=ClosedHistoricalPeriod(batch.Month,EmployeeEmploymentRules.Today);
        var hash=ReportedPeriodInputProof.SourceHash(batch,source);
        var preview=new HistoricalPeriodSalaryPreview{ClientId=clientId,BatchId=batch.Id,PayPeriod=batch.Month,EffectiveFrom=from.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),EffectiveTo=to.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),SourceHash=hash};
        var plan=new HistoricalPlan(preview,state,from,to,hash,[]);
        void Block(string message){if(!preview.Blockers.Contains(message,StringComparer.Ordinal))preview.Blockers.Add(message);}
        if(q.EffectiveFrom!=preview.EffectiveFrom)Block("Reported historical salary must start on the exact closed source month.");
        if(ExcelPayslipCalculationService.ValidateSource(source,batch) is {} error)Block(error);
        var selected=batch.Rows.Where(r=>q.RowIds.Contains(r.Id)).ToArray();
        if(selected.Length!=ids.Length)Block("A selected source row is missing.");
        foreach(var row in selected)if(batch.Rows.Count(r=>Normalize(r.EmployeeCode)==Normalize(row.EmployeeCode))!=1)Block("Reported closed salary cannot aggregate duplicated source employees.");
        var original=source.WorkbookFiles.SingleOrDefault(f=>f.WorkbookKey==source.WorkbookKey);
        var originals=await ExcelPayslipRepository.ReadLinkedOriginalsAsync(db,tx,clientId,batch.Id);
        if(original is null||originals.Count(o=>o.Id==original.OriginalId&&o.Sha256.Equals(original.Sha256,StringComparison.OrdinalIgnoreCase))!=1)
            Block("The exact primary original must remain owned by this source/client. Declared period facts do not certify the missing lookup workbook.");
        var raw=await db.QuerySingleAsync<ReportedRawHashes>("SELECT SHA2(CAST(b.batch_json AS CHAR),256) BatchHash,SHA2(CAST(s.source_json AS CHAR),256) SourceHash FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@Batch",new{Client=clientId,Batch=batch.Id},tx);
        var setupText=await PayrollDataTableStore.GetSetupJsonAsync(db,tx);var setup=JsonNode.Parse(setupText)!;
        var masters=setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToList();
        var templates=setup["salaryStructures"]!.AsArray().OfType<JsonObject>().ToArray();
        var templateId=request.Assignments[0].TemplateId;
        var profiles=await ReportedPeriodInputProfileStore.ReadProfilesAsync(db,tx,configuration,clientId,[templateId]);
        if(!profiles.TryGetValue(templateId,out var profile))throw new InvalidOperationException("The exact protected v2 reported template profile is unavailable.");
        var template=templates.SingleOrDefault(t=>Text(t,"id")==templateId&&Text(t,"active")=="true"&&Text(t,"clientId").Split([';',',','|']).Any(v=>v.Split(':')[0].Trim()==clientId.ToString(CultureInfo.InvariantCulture)))
            ??throw new InvalidOperationException("The reported template is inactive or belongs to a different client.");
        if(profile.RulesHash!=LocationSettingsRulesHash(template,masters))Block("The managed reported template rules changed.");
        var scopes=await ReadTemplateScopesAsync(db,tx,clientId);
        foreach(var scopeError in TemplateScopeIssues(q,[]))Block(scopeError);
        if(q.TemplateScope is not {OpenEnded:false} scope||scope.EffectiveTo!=preview.EffectiveTo)Block("Reported template applicability must end at the exact closed month; no future scope is changed.");
        var identities=await ReadNamedSourceSetupIdentitiesAsync(db,tx,clientId,batch.Month);
        var reviewed=await ReviewHistoricalSourceIdentitiesAsync(db,tx,request,batch,source,identities,hash,locked);
        var cycle=PayRunRepository.NativeAttendanceCycle(batch.Month,q.AttendanceCycleStartDay,q.AttendanceCycleEndDay);
        var sourceScope=new ReportedPeriodInputProof.SourceScope(clientId,batch.Id,batch.Month,hash,raw.BatchHash,raw.SourceHash,original?.Sha256.ToLowerInvariant()??"",cycle.Start,cycle.End,batch.Rows.ToDictionary(r=>r.Id,r=>ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,r,source),StringComparer.Ordinal));
        var boundRows=new Dictionary<int,ReportedPeriodBoundRowV2>();var ownership=new Dictionary<int,string>();
        foreach(var assignment in request.Assignments)
        {
            var row=selected.SingleOrDefault(r=>r.Id==assignment.RowId);var identity=identities.SingleOrDefault(i=>i.Id==assignment.EmployeeId&&i.ClientId==clientId);
            if(row is null||identity is null){Block("An exact existing source employee is unavailable.");continue;}
            foreach(var identityError in HistoricalIdentityIssues(batch,source,row,identity,identities,reviewed.GetValueOrDefault(row.Id)))Block("Source row"+row.SourceRow+": "+identityError);
            if(preview.Blockers.Count>0)continue;
            try
            {
                boundRows[assignment.EmployeeId]=ReportedPeriodInputProof.Bind(profile,q.ReportedPeriodInputs!,batch,source,row,sourceScope,masters.SelectMany(m=>new[]{Text(m,"id"),Text(m,"code")}));
                ownership[assignment.EmployeeId]=ReportedIdentityOwnershipHash(batch,source,row,identity,identities,reviewed.GetValueOrDefault(row.Id));
            }
            catch(InvalidOperationException ex){Block("Source row"+row.SourceRow+": "+ex.Message);}
        }
        var seeds=new Dictionary<int,HistoricalPayableOnlySeedPlan>();
        if(preview.Blockers.Count==0)try{seeds=await ReadHistoricalPayableSeedPlansAsync(db,tx,clientId,request,batch,source,boundRows,ownership,locked);}catch(InvalidOperationException ex){Block(ex.Message);}
        var runs=(await db.QueryAsync<HistoricalRunState>("SELECT Id,Status,CAST(RequestJson AS CHAR) RequestJson FROM payruns WHERE ClientId=@Client AND PayPeriod=@Month AND RunType='Regular'",new{Client=clientId,Month=batch.Month},tx)).ToArray();
        if(runs.Any(r=>r.Status is "AwaitingProcessing" or "Queued" or "Processing"))Block("A regular run is already pending or processing for this client/month.");
        var simulations=new List<object>();
        foreach(var assignment in request.Assignments)
        {
            var row=selected.SingleOrDefault(r=>r.Id==assignment.RowId);var result=new HistoricalPeriodSalaryPreviewRow{RowId=assignment.RowId,EmployeeId=assignment.EmployeeId,TemplateId=assignment.TemplateId,SourceRow=row?.SourceRow??0};preview.Rows.Add(result);
            void RowBlock(string message){if(!result.Blockers.Contains(message,StringComparer.Ordinal))result.Blockers.Add(message);}
            if(preview.Blockers.Count>0||row is null||!seeds.TryGetValue(assignment.EmployeeId,out var seed))continue;
            var identity=identities.Single(i=>i.Id==assignment.EmployeeId&&i.ClientId==clientId);result.EmployeeCode=identity.EmployeeCode;result.ReviewedSourceIdentity=reviewed.GetValueOrDefault(row.Id)?.Evidence;
            try
            {
                if(runs.Any(r=>HistoricalRequestIncludes(r.RequestJson,assignment.EmployeeId)))throw new InvalidOperationException("A saved regular request already includes this employee/month; do not stack a closed basis.");
                var basis=EmployeeRepository.RequireClosedSalaryBasis(state,assignment.EmployeeId,from,to);
                if(state.History.Any(h=>h.EmployeeId==assignment.EmployeeId&&h.ActionType=="Closed Excel Salary"&&h.EffectiveFrom.Date==from&&h.EffectiveTo?.Date==to))throw new InvalidOperationException("An existing closed salary receipt already owns this employee/month.");
                result.RetainedAnnualSalaryTarget=basis.AnnualCtc;
                var rowQ=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(JsonSerializer.Serialize(q,Json),Json)!;rowQ.RowIds=[row.Id];rowQ.EmployeeDecisions=[new(){RowId=row.Id,MatchedEmployeeId=assignment.EmployeeId}];
                var templateCopy=template.DeepClone().AsObject();
                var arithmetic=PrepareReportedTemplateArithmetic(new(){ClientId=clientId,ExistingTemplateId=templateId,SourceRowId=row.Id,SourceHash=hash,SourceReview=rowQ,Definitions=profile.Definitions,AmountDifferenceReview=request.AmountDifferenceReview},batch,source,row,templateCopy,masters,profile,boundRows[assignment.EmployeeId],basis.AnnualCtc);
                if(!JsonNode.DeepEquals(template["lines"],templateCopy["lines"]))throw new InvalidOperationException("The actual saved template does not preserve every original reported source AST; preview cannot rewrite a rule.");
                var resolved=ComponentsForRow(q.Components.Select(c=>new ResolvedComponent(c,c.ExistingComponentId!,false)).ToArray(),row.Id);
                var decisions=q.TemplateDecisions.Where(d=>d.ExistingTemplateId==templateId).ToArray();
                if(decisions.Length>1)throw new InvalidOperationException("One exact reviewed applicability decision is required.");
                var applicability=CompareTemplateScope(templateId,ReusedTemplateRulesSignature(template,resolved),q,clientId,scopes,decisions.SingleOrDefault());
                if(!applicability.Compatible)throw new InvalidOperationException("Template applicability: "+applicability.Reason);
                var context=ReportedPeriodCalculationContext.FromBound(profile,arithmetic.Bound,nativeAttendance:seed.PendingNativeInput);
                result.NamedInputs=context;
                var hypothetical=await HistoricalRuns.SimulateHistoricalSalaryInTransactionAsync(db,tx,new(){Mode="Employee",ClientId=clientId,EmployeeId=assignment.EmployeeId,PayPeriod=batch.Month},
                    new HistoricalSalarySimulationAssignment(clientId,assignment.EmployeeId,batch.Month,templateId,basis.AnnualCtc,new Dictionary<string,decimal>(),NamedInputs:context){ReportedAttendanceSeed=seed});
                foreach(var issue in hypothetical.Simulation.Issues.Where(i=>i.Severity=="Error"))RowBlock(issue.Message);
                if(!hypothetical.Simulation.Complete||hypothetical.InputFingerprint.Length!=64)throw new InvalidOperationException("Complete hypothetical Employee proof with the reviewed pending seed is required before insertion.");
                var master=await EmployeeRepository.LoadRecruitmentEmployeeAsync(db,assignment.EmployeeId,tx)??throw new InvalidOperationException("The exact current Master is unavailable.");
                var planned=new PlannedRow{RowId=row.Id,SourceRow=row.SourceRow,Employee=master,TemplateId=templateId,Components=resolved,Preview=arithmetic.Arithmetic.Preview,SalaryInputs=[]};
                foreach(var scopeError in TemplateScopeIssues(q,[planned]))RowBlock(scopeError);
                foreach(var mismatch in CompareHistoricalNative(planned,row,hypothetical.Simulation))RowBlock(mismatch);
                var codes=resolved.Select(c=>c.Definition.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if(hypothetical.Simulation.Lines.Any(l=>l.Earned!=0&&!codes.Contains(l.Code)))RowBlock("Native payroll adds an unmapped nonzero component or adjustment.");
                result.Formulas=resolved.Select(c=>{var cell=arithmetic.Closure.Cells.Single(x=>x.ColumnIndex==c.Definition.ExpectedColumnIndex);return new ExcelPayrollTransferFormula(row.Id,c.Definition.Code,row.SourceRow,c.Definition.ExpectedColumnIndex,ReviewedNamedFormulaInputs.Hash(cell),string.IsNullOrWhiteSpace(cell.OriginalFormula)?"Reviewed fixed source":"Exact supported translation","Complete original cash AST closes only at the declared reported Days and independent operands; lookup verification is not claimed.");}).ToList();
                var amountPlan=new Plan{Batch=batch,Source=source};amountPlan.Components.AddRange(resolved);
                var amounts=ClassifyTransferAmounts(amountPlan,planned,q,hash,result.Formulas,context);
                if(request.AmountDifferenceReview is not null)amounts=ReviewHistoricalAmountDifferences(request,batch,source,row,planned.Preview,result.Formulas,hypothetical.Simulation,hypothetical.InputFingerprint,amounts);
                result.AmountReview=amounts;
                if(amounts.Status is not("Exact at raw precision" or "Explained rounding" or BoundedHistoricalDifferenceStatus))RowBlock(amounts.Reason);
                if(amounts.Status is "Explained rounding" or BoundedHistoricalDifferenceStatus)
                {
                    amounts.Acknowledged=q.RoundingReviews.GetValueOrDefault(row.Id)==amounts.ProofHash;
                    if(!amounts.Acknowledged)RowBlock("Acknowledge the exact source/native currency ledger before saving; no source cash is replaced.");
                }
                result.Gross=planned.Preview.Gross;result.TotalDeductions=planned.Preview.Deductions;result.EmployerCost=planned.Preview.Benefits;result.NetPay=planned.Preview.Net;result.Lines=planned.Preview.Lines;result.SourceMonthlyInputs=[];
                simulations.Add(new{assignment.EmployeeId,hypothetical.InputFingerprint,seed.PlanHash,arithmetic.Bound.ProofHash,arithmetic.Closure,result.AmountReview});
                if(result.Blockers.Count==0)plan.Writes.Add(new HistoricalWrite(assignment,identity.EmployeeCode,basis.AnnualCtc,[],SourceIdentity:result.ReviewedSourceIdentity){ReportedInputs=new(profile,arithmetic.Bound,context,arithmetic.Closure,ownership[assignment.EmployeeId],seed)});
            }
            catch(InvalidOperationException ex){RowBlock(ex.Message);}
        }
        foreach(var row in preview.Rows.Where(r=>r.Blockers.Count>0))Block("Source row"+row.SourceRow+": "+string.Join(" ",row.Blockers));
        if(plan.Writes.Count!=ids.Length&&preview.Blockers.Count==0)Block("Every selected reported closed salary row needs full ownership, source, template, pending attendance and Employee proof.");
        preview.Fingerprint=Hash(JsonSerializer.Serialize(new{Version=2,Purpose="ReportedClosedPeriodSalary",clientId,request,SourceHash=hash,raw,state.CurrentMasterHash,state.History,Setup=setupText,scopes,Seeds=seeds.OrderBy(p=>p.Key),runs,simulations,preview.Rows},Json));
        return plan;
    }

    internal static string ReportedIdentityOwnershipHash(ExcelPayslipBatch batch,ExcelPayslipCalculationSource source,ExcelPayslipRow row,
        Identity target,IReadOnlyList<Identity> identities,HistoricalVerifiedSourceIdentity? reviewed)
    {
        if(HistoricalIdentityIssues(batch,source,row,target,identities,reviewed).Count!=0)throw new InvalidOperationException("Reported source ownership is not verified.");
        var cells=source.Rows.Single(r=>r.SourceRow==row.SourceRow);var anchors=new List<object>();
        foreach(var col in source.Columns)
        {
            var kind=ExcelPayslipMasterSynchronization.ProfileIdentityKey(col.SourceHeader);if(kind.Length==0)kind=ExcelPayslipMasterSynchronization.ProfileIdentityKey(col.Label);
            if(kind is not("pan" or "uan" or "aadhaar"))continue;
            var cell=cells.Cells.ElementAtOrDefault(col.ColumnIndex);if(cell is null||!string.IsNullOrWhiteSpace(cell.FormulaText)||cell.Error is not null||cell.CalculationError is not null||cell.MissingCachedValue)continue;
            string Canonical(string? value)=>Regex.Replace((value??"").ToUpperInvariant(),"[\\s-]","");
            var value=Canonical(cell.Value);if(!Regex.IsMatch(value,kind=="pan"?"^[A-Z]{5}[0-9]{4}[A-Z]$":kind=="aadhaar"?"^[2-9][0-9]{11}$":"^[0-9]{12}$"))continue;
            var owners=identities.Where(i=>Canonical(kind=="pan"?i.Pan:kind=="uan"?i.Uan:i.Aadhaar)==value).Select(i=>i.Id).Distinct().Order().ToArray();
            if(owners.Length!=1||owners[0]!=target.Id)throw new InvalidOperationException("A declared government anchor is not globally unique to the exact source employee.");
            anchors.Add(new{kind,col.ColumnIndex,CellHash=ReportedPeriodInputProof.CellHash(cell),ValueHash=ReviewedNamedFormulaInputs.Hash(value),owners});
        }
        if(anchors.Count==0&&reviewed is null)throw new InvalidOperationException("A reported source seed requires government ownership or the exact independently reviewed bank evidence.");
        return Hash(JsonSerializer.Serialize(new{Version=2,batch.ClientId,EmployeeId=target.Id,SourceIdentity=ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source),Anchors=anchors,ReviewedBank=reviewed?.Evidence},Json));
    }

    internal static Task<IReadOnlyDictionary<string,HistoricalVerifiedSourceIdentity>> ReadReportedReviewedIdentitiesAsync(MySqlConnection db,MySqlTransaction tx,HistoricalPeriodSalaryRequest request,
        ExcelPayslipBatch batch,ExcelPayslipCalculationSource source,IReadOnlyList<Identity> identities,string hash)
        =>ReviewHistoricalSourceIdentitiesAsync(db,tx,request,batch,source,identities,hash,false);

    private static bool TrustedReportedHistoricalNative(HistoricalPeriodSalaryRequest request,ExcelPayslipBatch batch,ExcelPayslipRow row,PayrollSimulationResult native)
    {
        if(request.Setup.ReportedPeriodInputs is null||request.PayableOnlySeedReview is not{Reviewed:true,QuantityBasis:"ReportedPeriodInputsV2"})return false;
        var contexts=native.Lines.Where(l=>l.NamedOperands is not null).Select(l=>l.NamedOperands!).ToArray();
        if(contexts.Length==0)return false;
        foreach(var context in contexts)
        {
            var proof=context.ReportedPeriodProof;var attendance=proof?.NativeAttendance;
            if(proof is null||attendance is null||proof.Bound.ClientId!=batch.ClientId||proof.Bound.BatchId!=batch.Id||proof.Bound.PayPeriod!=batch.Month
                ||proof.Bound.RowId!=row.Id||proof.Bound.SourceRow!=row.SourceRow||proof.Bound.SourceHash!=request.Setup.ReportedPeriodInputs.SourceHash
                ||attendance.EmployeeId!=native.EmployeeId||attendance.Month!=batch.Month||attendance.PresentDays.HasValue
                ||attendance.ProofKind is not(HistoricalPayableSeedPendingKind or HistoricalPayableSeedSavedKind))return false;
            ReportedPeriodCalculationContext.Validate(context,[],proof.Bound.PayrollDivisor,proof.Bound.PayableDays);
        }
        return true;
    }

    private static bool ReportedZeroPeriodOutput(bool reported,ExcelPayslipCalculationSource source,ExcelPayslipRow row,ExcelPayrollSetupComponent c)
    {
        if(!reported||c.CalculationType!="Manual / Variable"||c.ExpectedColumnIndex is not int index||c.SourceColumnIndex.HasValue||c.IsPeriodAdjustment)return false;
        var cell=source.Rows.Single(r=>r.SourceRow==row.SourceRow).Cells.ElementAtOrDefault(index);
        return string.IsNullOrWhiteSpace(cell?.FormulaText)&&ReadNumber(source.Rows.Single(r=>r.SourceRow==row.SourceRow),index,source.Columns.Single(col=>col.ColumnIndex==index).BlankAsZero)==0;
    }
}

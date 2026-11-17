using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class PayslipDesignRepository
{
    private async Task<ReferencePlan> BuildReferencePlanAsync(MySqlConnection db,MySqlTransaction tx,
        PayrollReferenceInformationReviewRequest request,bool locked,bool requireAbsent,bool captureValues=true,ReferenceManifest? sourceWitness=null)
    {
        ValidateReferenceRequest(request);
        if(tx.Connection!=db)throw new InvalidOperationException("Reference review needs the caller's exact database transaction.");
        var suffix=locked?" FOR UPDATE":"";
        if(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@ClientId AND IsActive=TRUE",new{request.ClientId},tx)!=1)
            throw new InvalidOperationException("The reference Information client is unavailable.");
        if(requireAbsent&&await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code",new{request.ClientId,Code=ReferenceManifestCode(request.RunId)},tx)!=0)
            throw new InvalidOperationException("This saved run already has a reference Information manifest. Replay its original request; no duplicate or replacement is created.");
        var run=await db.QuerySingleOrDefaultAsync<ReferenceNativeRun>("SELECT Id,ClientId,PayPeriod,RunType,Status,TotalWorkingDays,CAST(RequestJson AS CHAR) RequestJson FROM payruns WHERE Id=@RunId AND ClientId=@ClientId"+suffix,new{request.RunId,request.ClientId},tx)
            ??throw new InvalidOperationException("The reference Information run is unavailable for this client.");
        if(run.PayPeriod!=request.PayPeriod||run.RunType!="Regular"||run.Status is not("Draft" or "Approved" or "Partially Paid" or "Paid")||run.TotalWorkingDays is <1 or >31)
            throw new InvalidOperationException("Review one complete calculated regular payroll in its exact saved month.");
        var native=(await db.QueryAsync<ReferenceNativeRow>("SELECT Id,PayRunId,EmployeeId,ClientId,PresentDays,PayableDays,MonthlyGross,GrossPay,StatutoryDeductions,OneTimeEarnings,OneTimeDeductions,ManualTds,NetPay,CAST(DetailsJson AS CHAR) DetailsJson,IsSkipped,PaymentStatus,PaymentDate FROM payrunemployees WHERE PayRunId=@RunId ORDER BY Id"+suffix,new{request.RunId},tx)).ToArray();
        if(native.Length is <1 or >ReferenceRosterLimit||native.Any(row=>row.ClientId!=request.ClientId||row.IsSkipped||row.Id<=0||row.EmployeeId<=0)
            ||native.Select(row=>row.EmployeeId).Distinct().Count()!=native.Length||native.Select(row=>row.Id).Distinct().Count()!=native.Length)
            throw new InvalidOperationException("The reference manifest requires the complete bounded saved native roster; no skipped, foreign or duplicate employees are omitted.");
        var ids=native.Select(row=>row.EmployeeId).Order().ToArray();
        var original=JsonSerializer.Deserialize<CreatePayRunRequest>(run.RequestJson,Json)??throw new InvalidOperationException("The original saved payroll request is unavailable.");
        if(original.ClientId!=request.ClientId||original.PayPeriod!=request.PayPeriod||original.IncludedEmployeeIds is null||original.ExcludedEmployeeIds is null
            ||original.IncludedEmployeeIds.Count>0&&!original.IncludedEmployeeIds.ToHashSet().SetEquals(ids)||original.ExcludedEmployeeIds.Intersect(ids).Any())
            throw new InvalidOperationException("The original request does not cover this exact complete saved native roster.");
        var design=await ReadNativeSourceDesignSelectionAsync(db,tx,request.ClientId,locked,request.PayPeriod)
            ??throw new InvalidOperationException("An authenticated active client Excel source design is required.");
        if(design.Template.Id!=request.TemplateId||!design.Applies)throw new InvalidOperationException("The requested exact active client design or run-period applicability changed.");
        var fields=design.Profile.Fields.Where(field=>field.SourceColumn==request.SourceInfoColumn&&field.Kind=="info").ToArray();
        if(fields.Length!=1)throw new InvalidOperationException("Bind one existing source Information column; financial fields cannot be converted or created.");
        var field=fields[0];RequireMonthlyReferenceTarget(field);
        var profileHash=ReferenceProfileCoreHash(design.Profile,design.Template,request.SourceInfoColumn,request.PayPeriod);var templateHash=ReferenceTemplateCoreHash(design.Template);
        var sourceStored=await SourceAsync(db,request.ClientId,request.SourceBatchId,tx,locked);
        var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(sourceStored.BatchJson,Json)??throw new InvalidOperationException("Reference source batch is invalid.");
        var source=sourceStored.SourceJson is null?null:JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(sourceStored.SourceJson,Json);
        if(source is null||batch.ClientId!=request.ClientId||batch.Month!=request.PayPeriod||batch.Id!=request.SourceBatchId
            ||source.Columns.Count(column=>column.ColumnIndex==request.SourceInfoColumn&&column.Kind=="info")!=1)
            throw new InvalidOperationException("The exact current source and its existing Information column are required.");
        var currentCanonicalSourceHash=ReferenceHash(new{Batch=batch,Source=source});
        var sourceDataHash=PayrollSourceDataProvenance.SnapshotHash(sourceStored.BatchJson,sourceStored.SourceJson!);
        var receiptCode=$"closed_salary_receipt:{request.ClientId}:{request.HistoricalReceiptRequestId}";
        var historicalEnvelope=await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code AND IsEnabled=TRUE"+suffix,new{request.ClientId,Code=receiptCode},tx)
            ??throw new InvalidOperationException("The exact protected historical initial-target receipt is missing.");
        if(historicalEnvelope.Length>4*1024*1024)throw new InvalidOperationException("The historical receipt exceeds its metadata bound.");
        var envelope=JsonNode.Parse(historicalEnvelope)!;
        var plain=envelope["purpose"]?.ToString()=="ClosedHistoricalSalaryReceipt"?protector.UnprotectHistoricalSalary(envelope["payload"]?.ToString()??""):null;
        if(plain is null)throw new InvalidOperationException("The existing closed historical receipt cannot be authenticated.");
        var storedReceipt=JsonNode.Parse(plain)!;
        var sourceHash=storedReceipt["sourceHash"]?.ToString()??"";var historicalReceiptHash=ReferenceHash(historicalEnvelope);
        if(!ReferenceDigest(sourceHash))throw new InvalidOperationException("The original signed historical canonical source hash is invalid.");
        var sameHistoricalAuthority=sourceWitness is not null&&sourceWitness.Request.ClientId==request.ClientId&&sourceWitness.Request.RunId==request.RunId
            &&sourceWitness.Request.SourceBatchId==request.SourceBatchId&&sourceWitness.Request.HistoricalReceiptRequestId==request.HistoricalReceiptRequestId;
        var sameRawSourceProof=sameHistoricalAuthority&&sourceWitness!.SourceHash==sourceHash&&sourceWitness.SourceDataHash==sourceDataHash
            &&sourceWitness.HistoricalReceiptHash==historicalReceiptHash;
        if(sameHistoricalAuthority&&!sameRawSourceProof||currentCanonicalSourceHash!=sourceHash&&!sameRawSourceProof)
            throw new InvalidOperationException("The exact historical source/receipt provenance changed. Only canonical server-owned profileLinks maintenance proved by an existing authenticated manifest is exempt.");
        var receipt=storedReceipt["receipt"]!.Deserialize<HistoricalPeriodSalaryReceipt>(Json)??throw new InvalidOperationException("The authenticated historical receipt is invalid.");
        var reviewed=storedReceipt["reviewedRequest"]!.Deserialize<HistoricalPeriodSalaryRequest>(Json)??throw new InvalidOperationException("The authenticated reviewed request is missing.");
        if(receipt.ClientId!=request.ClientId||receipt.PayPeriod!=request.PayPeriod||receipt.BatchId!=request.SourceBatchId||receipt.RequestId!=request.HistoricalReceiptRequestId
            ||reviewed.SalaryTargetMode!="ReviewedInitialSourceTarget"
            ||reviewed.InitialSourceTargetReview is not{Version:1,Reviewed:true} review||review.SourceHash!=sourceHash||review.MonthlyCtcColumnIndex!=request.SourceInfoColumn
            ||review.Meaning!="MonthlyPayrollEarningsBudget"||!review.ActualJoiningDateNotEstablished
            ||receipt.Rows.Count!=native.Length||receipt.Rows.Select(row=>row.EmployeeId).Distinct().Count()!=native.Length
            ||receipt.Rows.Select(row=>row.RowId).Distinct(StringComparer.Ordinal).Count()!=native.Length||receipt.Rows.Select(row=>row.HistoryId).Distinct().Count()!=native.Length
            ||!receipt.Rows.Select(row=>row.EmployeeId).Order().SequenceEqual(ids)||receipt.InitialSourceTargets is null||receipt.InitialSourceTargets.Count!=native.Length
            ||!receipt.InitialSourceTargets.Select(row=>row.EmployeeId).Order().SequenceEqual(ids)||receipt.InitialSourceTargets.Any(target=>target.MonthlyCtcColumnIndex!=request.SourceInfoColumn)
            ||reviewed.Assignments.Count!=native.Length||!reviewed.Assignments.OrderBy(row=>row.EmployeeId).Select(row=>(row.EmployeeId,row.RowId,row.TemplateId))
                .SequenceEqual(receipt.Rows.OrderBy(row=>row.EmployeeId).Select(row=>(row.EmployeeId,row.RowId,row.TemplateId)))
            ||reviewed.Setup.RowIds.Count!=native.Length||!reviewed.Setup.RowIds.Order(StringComparer.Ordinal).SequenceEqual(receipt.Rows.Select(row=>row.RowId).Order(StringComparer.Ordinal))
            ||reviewed.Setup.EmployeeDecisions.Count>0&&(reviewed.Setup.EmployeeDecisions.Count!=native.Length||reviewed.Setup.EmployeeDecisions.Any(decision=>decision.AllowCreate
                ||!receipt.Rows.Any(row=>row.RowId==decision.RowId&&row.EmployeeId==decision.MatchedEmployeeId))))
            throw new InvalidOperationException("The complete native roster/source column must exactly match the authenticated initial-target receipt, assignments and reviewed matching decisions.");
        if(receipt.Rows.Any(row=>batch.Rows.Count(sourceRow=>sourceRow.Id==row.RowId)!=1))throw new InvalidOperationException("An authenticated receipt source row is missing or ambiguous in the current source.");
        var start=DateTime.ParseExact(request.PayPeriod+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture);var end=start.AddMonths(1).AddDays(-1);
        var histories=(await db.QueryAsync<ReferenceStoredHistory>("SELECT Id,EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,SHA2(CAST(SalaryJson AS CHAR),256) SalaryJsonHash FROM employee_it0008_basic_pay WHERE ClientId=@ClientId AND EmployeeId IN @Ids AND EffectiveFrom<=@End AND (EffectiveTo IS NULL OR EffectiveTo>=@Start) ORDER BY EmployeeId,Id"+suffix,new{request.ClientId,Ids=ids,Start=start,End=end},tx)).ToArray();
        var historyHash=ReferenceHash(histories);var nativeHashes=native.OrderBy(row=>row.EmployeeId).Select(row=>new{row.EmployeeId,row.Id,FinancialHash=ReferenceNativeFinancialHash(run,row)}).ToArray();
        var nativeRosterHash=ReferenceHash(nativeHashes);
        var runOwnershipHash=ReferenceHash(new{run.Id,run.ClientId,run.PayPeriod,run.RunType,run.TotalWorkingDays,OriginalRequestHash=ReferenceHash(run.RequestJson),EmployeeIds=ids});
        var stateWitnessHash=ReferenceHash(new{Version=1,Request=request,ProfileHash=profileHash,TemplateHash=templateHash,
            OriginalSignedCanonicalSourceHash=sourceHash,SourceDataHash=sourceDataHash,
            HistoricalReceiptHash=historicalReceiptHash,HistoryHash=historyHash,NativeRosterHash=nativeRosterHash,RunOwnershipHash=runOwnershipHash});
        var reviewStateHash=ReferenceHash(new{StateWitnessHash=stateWitnessHash,run.Status,Payments=native.OrderBy(row=>row.EmployeeId).Select(row=>new{row.Id,row.EmployeeId,row.PaymentStatus,row.PaymentDate}).ToArray()});
        var declaration=ReferenceBudgetDeclaration();var declarationHash=PayrollReferenceInformation.DeclarationHash(declaration);
        var frozen=new List<ReferenceFrozenEmployee>();var rows=new List<PayrollReferenceInformationReviewRow>();
        IReadOnlyDictionary<int,string> verifiedExistingIdentities=new Dictionary<int,string>();
        if(captureValues&&reviewed.Setup.EmployeeDecisions.Count==0)
        {
            // The closed historical writer allows no caller matching decisions
            // when its normal government/durable-source ownership proof passes.
            // Recheck that same source and whole current ownership catalogue in
            // this transaction. Explicit signed bank/name reviews keep their
            // existing independent path; no current salary is read as a budget.
            var identities=await ExcelPayrollSetupRepository.ReadIdentitiesAsync(db,tx,request.ClientId,request.PayPeriod);
            var proofs=new Dictionary<int,string>();
            foreach(var saved in native)
            {
                var receiptRow=receipt.Rows.Single(row=>row.EmployeeId==saved.EmployeeId);
                if(receipt.SourceIdentities?.Any(identity=>identity.EmployeeId==saved.EmployeeId&&identity.RowId==receiptRow.RowId)==true)continue;
                var sourceRow=batch.Rows.Single(row=>row.Id==receiptRow.RowId);
                var target=identities.SingleOrDefault(identity=>identity.Id==saved.EmployeeId&&identity.ClientId==request.ClientId)
                    ??throw new InvalidOperationException("The empty-decision receipt's existing Master identity is missing.");
                if(ExcelPayrollSetupRepository.HistoricalIdentityIssues(batch,source,sourceRow,target,identities).Count>0)
                    throw new InvalidOperationException("Reference Information from an empty-decision receipt requires the same unique government/durable-source ownership, exact code/name and source-month employment proof as historical payroll. Review identity before capturing metadata.");
                proofs.Add(saved.EmployeeId,ReferenceHash(new{Version=1,Kind="VerifiedExistingClosedReferenceIdentity",request.ClientId,request.PayPeriod,SourceHash=sourceHash,
                    sourceRow.Id,sourceRow.SourceRow,Target=target,Ownership=identities.OrderBy(identity=>identity.Id).ToArray()}));
            }
            verifiedExistingIdentities=proofs;
        }
        if(captureValues)
        foreach(var saved in native.OrderBy(row=>row.EmployeeId))
        {
            var receiptRow=receipt.Rows.Single(row=>row.EmployeeId==saved.EmployeeId);
            var signed=storedReceipt["receipt"]?["sourceIdentities"]?.AsArray().SingleOrDefault(identity=>identity?["employeeId"]?.GetValue<int>()==saved.EmployeeId&&identity?["rowId"]?.ToString()==receiptRow.RowId);
            var scope=new PayrollReferenceScope(request.ClientId,saved.EmployeeId,request.RunId,request.PayPeriod,saved.Id,ReferenceNativeFinancialHash(run,saved),
                request.SourceBatchId,receiptRow.RowId,sourceHash,signed is null?new string('0',64):ReferenceHash(signed));
            var existingIdentityProof=verifiedExistingIdentities.GetValueOrDefault(saved.EmployeeId);
            if(signed is null)scope=scope with{SourceIdentityHash=PayrollReferenceInformation.AuthenticateClosedReceiptRowIdentity(scope,historicalEnvelope,protector,existingIdentityProof)};
            var evidence=histories.Where(history=>history.EmployeeId==saved.EmployeeId).Select(history=>history.Evidence()).ToArray();
            var nativeEvidence=new PayrollReferenceNativeRow(saved.Id,request.ClientId,saved.EmployeeId,request.RunId,request.PayPeriod,scope.NativeFinancialHash);
            var input=signed is null?PayrollReferenceInformation.FromClosedInitialReceiptRow("ANNUAL_TARGET",scope,nativeEvidence,evidence,historicalEnvelope,protector,existingIdentityProof)
                :PayrollReferenceInformation.FromClosedInitialReceipt("ANNUAL_TARGET",scope,nativeEvidence,evidence,historicalEnvelope,protector);
            var info=new PayrollReferenceField(request.ClientId,saved.EmployeeId,request.RunId,request.PayPeriod,request.TemplateId,profileHash,request.SourceInfoColumn,field.Label,"info",scope.NativeFinancialHash);
            var profile=new PayrollReferenceProfile(request.ClientId,request.TemplateId,profileHash,design.Profile.Fields.Select(value=>new PayrollReferenceProfileColumn(value.SourceColumn,value.Label,value.Kind)).ToArray());
            var binding=new PayrollReferenceBinding(1,scope,info,"MONTHLY_BUDGET",declarationHash,new Dictionary<string,string>{{input.Key,input.ProofHash}});
            var snapshot=PayrollReferenceInformation.Freeze(declaration,binding,profile,info,[input]);
            frozen.Add(new(binding,snapshot));rows.Add(new(saved.EmployeeId,saved.Id,receiptRow.RowId,snapshot.Value));
        }
        var planHash=ReferenceHash(new{Version=1,Request=request,StateWitnessHash=stateWitnessHash,Label=field.Label,Declaration=declaration,Employees=frozen});
        var manifest=new ReferenceManifest(1,"ReferenceInformationManifest",0,request,planHash,stateWitnessHash,profileHash,templateHash,sourceHash,sourceDataHash,historicalReceiptHash,
            historyHash,nativeRosterHash,runOwnershipHash,field.Label,declaration,frozen,"",default);
        return new(manifest,reviewStateHash,rows,ids);
    }
    private static string ReferenceNativeFinancialHash(ReferenceNativeRun run,ReferenceNativeRow row)=>ReferenceHash(new
    {row.Id,row.ClientId,row.EmployeeId,row.PayRunId,run.PayPeriod,run.TotalWorkingDays,row.PresentDays,row.PayableDays,row.MonthlyGross,row.GrossPay,
        row.StatutoryDeductions,row.OneTimeEarnings,row.OneTimeDeductions,row.ManualTds,row.NetPay,row.DetailsJson,row.IsSkipped});

    // Same caller-owned read transaction. Absence is checked by run key before
    // source/history/provider work. Writer gate is intentionally not consulted:
    // disabling new configuration never bypasses an existing authenticated run.
    internal async Task<PayrollReferenceInformationOverride> ReadReferenceInformationOverrideAsync(MySqlConnection db,MySqlTransaction tx,
        PayslipDesignProfile profile,PayslipDesignTemplate template,int runId,string payPeriod,int employeeId)
    {
        if(tx.Connection!=db||profile is null||template is null||profile.ClientId<=0||runId<=0||employeeId<=0
            ||template.Id!=profile.TemplateId||template.ClientId!=profile.ClientId)
            throw new InvalidOperationException("Reference Information rendering requires the exact caller transaction, profile and saved employee.");
        var manifest=await ReadReferenceProtectedAsync<ReferenceManifest>(db,tx,profile.ClientId,ReferenceManifestCode(runId));
        if(manifest is null)return new(PayrollReferenceStatus.NotApplicable,null,null,null,null,"");
        if(manifest.Version!=1||manifest.Kind!="ReferenceInformationManifest"||manifest.ActorId<=0||manifest.Request is null||manifest.Declaration is null
            ||manifest.Request.RunId!=runId||manifest.Request.ClientId!=profile.ClientId
            ||manifest.Request.PayPeriod!=payPeriod||manifest.Request.TemplateId!=profile.TemplateId||profile.TemplateId!=template.Id||template.ClientId!=profile.ClientId
            ||manifest.ProfileHash!=ReferenceProfileCoreHash(profile,template,manifest.Request.SourceInfoColumn,manifest.Request.PayPeriod)||manifest.TemplateHash!=ReferenceTemplateCoreHash(template)||manifest.Employees is null
            ||manifest.Employees.Count is <1 or >ReferenceRosterLimit||manifest.Employees.Any(row=>row is null||row.Binding is null||row.Snapshot is null)
            ||ReferenceHash(manifest.Declaration)!=ReferenceHash(ReferenceBudgetDeclaration()))
            throw new InvalidOperationException("This saved run's reference Information profile/template/period changed. Review it again; no legacy amount is substituted.");
        ValidateReferenceStoredManifest(manifest,profile.ClientId,runId);
        var current=await BuildReferencePlanAsync(db,tx,manifest.Request,false,false,false,manifest);
        if(current.Manifest.StateWitnessHash!=manifest.StateWitnessHash||manifest.Employees.Select(row=>row.Binding.Scope.EmployeeId).Distinct().Count()!=manifest.Employees.Count
            ||!manifest.Employees.Select(row=>row.Binding.Scope.EmployeeId).Order().SequenceEqual(current.EmployeeIds))
            throw new InvalidOperationException("The manifested source/history/native whole-roster evidence is stale; no legacy amount is substituted.");
        foreach(var row in manifest.Employees)
        {
            if(row.Binding.Scope.ClientId!=profile.ClientId||row.Binding.Scope.PayRunId!=runId||row.Binding.Scope.PayPeriod!=payPeriod||row.Binding.Field.TemplateId!=template.Id
                ||row.Binding.Field.ProfileHash!=manifest.ProfileHash||row.Binding.Field.SourceColumn!=manifest.Request.SourceInfoColumn||row.Binding.Field.Label!=manifest.Label
                ||row.Binding.DeclarationHash!=PayrollReferenceInformation.DeclarationHash(manifest.Declaration)||row.Snapshot.Unit!=PayrollReferenceUnit.Amount)
                throw new InvalidOperationException("The protected complete reference snapshot contains a foreign or invalid row.");
            PayrollReferenceInformation.ValidateSnapshot(row.Snapshot,row.Binding,row.Binding.Field);
        }
        var selected=manifest.Employees.SingleOrDefault(row=>row.Binding.Scope.EmployeeId==employeeId)
            ??throw new InvalidOperationException("The requested employee is outside this complete manifested payroll roster.");
        return new(PayrollReferenceStatus.Resolved,manifest.Request.SourceInfoColumn,manifest.Label,PayrollReferenceUnit.Amount,selected.Snapshot.Value,ReferenceHash(manifest));
    }
}

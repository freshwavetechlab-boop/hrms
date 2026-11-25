using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    private sealed record ReportedTemplatePlan(NamedFormulaTemplatePreview Preview, JsonObject Template,
        List<JsonObject> Masters, ReportedPeriodInputProfileV2 Profile, string FamilyHash);

    private async Task<ReportedTemplatePlan> BuildReportedTemplateAsync(MySqlConnection db, MySqlTransaction tx,
        NamedFormulaTemplateRequest request, bool locked)
    {
        var q = request.SourceReview;
        if (request.ClientId <= 0 || q?.ReportedPeriodInputs is not { } review || q.RowIds.Count != 1
            || q.RowIds[0] != request.SourceRowId || q.EmployeeDecisions.Count != 1 || q.EmployeeDecisions[0].RowId != request.SourceRowId
            || q.EmployeeDecisions[0].AllowCreate || q.EmployeeDecisions[0].MatchedEmployeeId is not > 0
            || q.NamedInputReview is not null || q.LocationAllocations is not null || q.MonthlyCtcColumnIndex.HasValue
            || q.ImportAttendance || q.PresentDaysColumnIndex.HasValue || q.ReviewedExtraDutyUnits
            || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 || request.Bindings.Count != 0)
            throw new InvalidOperationException("Reported template review needs one exact existing employee, no attendance write or invented presence, and only the dedicated typed source bindings.");
        RequireNamedTemplateReviewFlags(request, locked);
        if (locked) ReportedPeriodInputV2Codec.RequireWriterCompatibility(configuration);
        var suffix = locked ? " FOR UPDATE" : "";
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@Id AND IsActive=TRUE" + suffix, new { Id=request.ClientId }, tx) is null)
            throw new InvalidOperationException("Select an active client.");
        if (locked)
        {
            await db.QueryAsync<long>("SELECT Id FROM salarystructures ORDER BY Id FOR UPDATE", transaction:tx);
            await db.QueryAsync<long>("SELECT Id FROM salarystructurelines ORDER BY Id FOR UPDATE", transaction:tx);
            await db.QueryAsync<long>("SELECT Id FROM salarycomponents ORDER BY Id FOR UPDATE", transaction:tx);
            await db.QueryAsync<string>("SELECT id FROM excel_payslip_batches WHERE client_id=@Client AND id=@Batch FOR UPDATE", new { Client=request.ClientId, Batch=q.BatchId },tx);
            await db.QueryAsync<string>("SELECT ModuleCode FROM modulesettings WHERE client_id=@Client ORDER BY ModuleCode FOR UPDATE",new { Client=request.ClientId },tx);
        }
        var (batch, source) = await ReadSourceAsync(db,tx,request.ClientId,q.BatchId);
        if (ExcelPayslipCalculationService.ValidateSource(source,batch) is { } sourceError) throw new InvalidOperationException(sourceError);
        await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db,tx,request.ClientId,batch.Id);
        // Reported operands and their native reader bind the complete typed snapshot,
        // including retained server profile links, rather than the employment-only hash.
        var sourceHash = ReportedPeriodInputProof.SourceHash(batch,source);
        if (request.SourceHash != sourceHash || review.SourceHash != sourceHash) throw new InvalidOperationException("The reported template source changed.");
        var row = batch.Rows.Single(r=>r.Id==request.SourceRowId);
        var employeeId = q.EmployeeDecisions[0].MatchedEmployeeId!.Value;
        if (locked) await db.QueryAsync<int>("SELECT Id FROM employees WHERE Id=@Id AND ClientId=@Client FOR UPDATE",new { Id=employeeId,Client=request.ClientId },tx);
        var employee = await EmployeeRepository.LoadRecruitmentEmployeeAsync(db,employeeId,tx) ?? throw new InvalidOperationException("The exact source employee is unavailable.");
        var identities = await ReadNamedSourceSetupIdentitiesAsync(db,tx,request.ClientId,batch.Month);
        var target = identities.SingleOrDefault(i=>i.Id==employeeId && i.ClientId==request.ClientId);
        if (target is null || !HistoricalGovernmentAnchorExists(source,row,target,identities)
            || HistoricalIdentityIssues(batch,source,row,target,identities).Count != 0)
            throw new InvalidOperationException("Reported template source requires unique unchanged government identity ownership; code/name alone never authenticates reported facts.");
        var primary = source.WorkbookFiles.SingleOrDefault(f=>f.WorkbookKey==source.WorkbookKey)
            ?? throw new InvalidOperationException("Retain the primary original workbook.");
        var originals = await ExcelPayslipRepository.ReadLinkedOriginalsAsync(db,tx,request.ClientId,batch.Id);
        if (originals.Count(o=>o.Id==primary.OriginalId && o.Sha256.Equals(primary.Sha256,StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidOperationException("The exact primary captured workbook must remain owned by this source/client. Unavailable day lookups are not certified.");
        var raw = await db.QuerySingleAsync<ReportedRawHashes>("SELECT SHA2(CAST(b.batch_json AS CHAR),256) BatchHash,SHA2(CAST(s.source_json AS CHAR),256) SourceHash FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@Batch",new { Client=request.ClientId,Batch=batch.Id },tx);
        var cycle = PayRunRepository.NativeAttendanceCycle(batch.Month,q.AttendanceCycleStartDay,q.AttendanceCycleEndDay);
        var scope = new ReportedPeriodInputProof.SourceScope(request.ClientId,batch.Id,batch.Month,sourceHash,raw.BatchHash,raw.SourceHash,
            primary.Sha256.ToLowerInvariant(),cycle.Start,cycle.End,batch.Rows.ToDictionary(r=>r.Id,r=>ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,r,source),StringComparer.Ordinal));
        var setup = JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db,tx))!;
        var masters = setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToList();
        var template = setup["salaryStructures"]!.AsArray().OfType<JsonObject>().SingleOrDefault(t=>Text(t,"id")==request.ExistingTemplateId && Text(t,"active")=="true"
            && Text(t,"clientId").Split([';',',','|']).Any(v=>v.Split(':')[0].Trim()==request.ClientId.ToString(CultureInfo.InvariantCulture)))?.DeepClone().AsObject()
            ?? throw new InvalidOperationException("Select the exact active client source template.");
        var profile = new ReportedPeriodInputProfileV2(2,request.ClientId,request.ExistingTemplateId,LocationSettingsRulesHash(template,masters),request.Definitions,review.TotalPayableKeys,"");
        profile = profile with { DeclarationHash=ReportedPeriodInputProof.DeclarationHash(profile) };
        var bound = ReportedPeriodInputProof.Bind(profile,review,batch,source,row,scope,masters.SelectMany(m=>new[]{Text(m,"id"),Text(m,"code")}));
        // The common structural validator is reached ONLY after typed binding.
        // Every legacy entrypoint still rejects a nonnull reported review.
        ValidateRequestShape(q);
        var calculated = PrepareReportedTemplateArithmetic(request,batch,source,row,template,masters,profile,bound,employee.AnnualCtc);
        template["name"]=request.Name.Trim(); template["clientId"]=request.ClientId.ToString(CultureInfo.InvariantCulture);
        var family=ReportedTemplateFamily(template,masters,profile);
        var blockers=new List<string>();
        if (!configuration.GetValue<bool>(HistoricalNamedInputsFlag) || !configuration.GetValue<bool>(ReportedPeriodInputProof.WriterFlag))
            blockers.Add("Reported period template writers are disabled until compatible API and payroll-worker readers are deployed and verified.");
        var hash=Hash(JsonSerializer.Serialize(new { request,batch,source,raw,employee,setup,template,family,calculated.Bound,calculated.Closure,calculated.Amounts },Json));
        return new(new(hash,blockers.Count==0,blockers,sourceHash,calculated.Arithmetic.Preview.Lines.Count,calculated.Arithmetic.Preview.Lines){AmountDifferenceEvidence=calculated.Amounts},template,masters,profile,family);
    }

    internal sealed record ReportedTemplateArithmetic(RowArithmetic Arithmetic,ReportedPeriodBoundRowV2 Bound,
        ReportedPeriodCashClosureV2 Closure,NamedFormulaTemplateAmountDifferenceEvidence? Amounts);

    internal static ReportedTemplateArithmetic PrepareReportedTemplateArithmetic(NamedFormulaTemplateRequest request,
        ExcelPayslipBatch batch,ExcelPayslipCalculationSource source,ExcelPayslipRow row,JsonObject template,
        List<JsonObject> masters,ReportedPeriodInputProfileV2 profile,ReportedPeriodBoundRowV2 bound,decimal retainedAnnual)
    {
        if (request.SourceReview.ReportedPeriodInputs is not { } review || retainedAnnual<=0
            || request.SourceHash!=ReportedPeriodInputProof.SourceHash(batch,source))
            throw new InvalidOperationException("Reported template arithmetic requires its exact source review and a retained positive salary target; it does not infer an initial annual salary.");
        var closure=ReportedPeriodInputProof.CloseFinancialGraph(review,batch,source,row,bound);
        if (request.SourceReview.FixedPayrollDays != bound.PayrollDivisor || request.SourceReview.PayrollDaysColumnIndex != -1
            || request.SourceReview.PresentDaysColumnIndex.HasValue || request.SourceReview.ImportAttendance
            || source.Columns.Where(c=>c.Kind is "earning" or "deduction" or "employer" or "grossTotal" or "deductionTotal" or "netPay")
                .Any(c=>request.SourceReview.SourceColumnRoles.GetValueOrDefault(c.ColumnIndex,c.Kind)!=c.Kind)
            || bound.Cells.Any(c=>request.SourceReview.SourceColumnRoles.GetValueOrDefault(c.ColumnIndex,source.Columns.Single(s=>s.ColumnIndex==c.ColumnIndex).Kind) is not("info" or "ignore")))
            throw new InvalidOperationException("Reported template configuration must retain the exact original divisor and financial roles, separate unknown presence, and typed Information operands.");
        if (closure.Cells.Any(c=>c.SavedAmount<0)) throw new InvalidOperationException("Signed negative financial outputs require their separate native review; a zero clamp is not permitted.");
        var q=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(JsonSerializer.Serialize(request.SourceReview,Json),Json)!;
        var definitions=q.Components; var lines=template["lines"]!.AsArray().OfType<JsonObject>().ToArray();
        var cash=closure.Cells.Where(c=>c.Kind is "earning" or "deduction" or "employer").ToArray();
        if (lines.Length!=definitions.Count || definitions.Count!=cash.Length || definitions.Any(d=>d.ExpectedColumnIndex is null || d.SourceColumnIndex.HasValue || d.IsPeriodAdjustment || d.ExemptWhenSourceZero || d.ProRata)
            || definitions.Select(d=>d.ExpectedColumnIndex).Distinct().Count()!=definitions.Count || !definitions.Select(d=>d.ExpectedColumnIndex!.Value).Order().SequenceEqual(cash.Select(c=>c.ColumnIndex).Order()))
            throw new InvalidOperationException("Map every complete cash output exactly once. Reported day rules cannot use component amount overrides, duplicate proration or unreviewed period adjustments.");
        var resolved=new List<ResolvedComponent>();
        for (var i=0;i<definitions.Count;i++)
        {
            var d=definitions[i]; var line=lines[i]; var master=masters.Single(m=>Text(m,"id")==d.ExistingComponentId); var cell=cash.Single(c=>c.ColumnIndex==d.ExpectedColumnIndex);
            var role=d.Category switch { "Earning" or "Reimbursement"=>"earning","Deduction"=>"deduction","Benefit"=>"employer",_=>"" };
            if (role!=cell.Kind || Text(line,"componentId")!=d.ExistingComponentId || ReusedComponentDifferences(master,d).Count!=0
                || Text(line,"calculationType")!=d.CalculationType || Text(line,"value")!=d.Value || Text(line,"baseComponent")!=d.BaseComponent
                || Text(line,"proRataOverride")!="false" || Text(line,"roundingMode")!="")
                throw new InvalidOperationException("Existing component order, classification, defaults and rounding must remain exact; named facts are not new salary components.");
            if (string.IsNullOrWhiteSpace(cell.OriginalFormula))
            {
                if (cell.SavedAmount!=0 || d.CalculationType!="Manual / Variable")
                    throw new InvalidOperationException("A nonzero source period cash amount needs an approved transaction. It cannot become a permanent recurring input or fixed template output.");
                d.Formula="";
            }
            else
            {
                if (d.CalculationType!="Formula") throw new InvalidOperationException("Every original financial formula must remain a formula.");
                d.Formula=cell.NativeExpression;
            }
            line["formula"]=d.Formula; resolved.Add(new(d,d.ExistingComponentId!,false));
        }
        var context=ReportedPeriodCalculationContext.FromBound(profile,bound);
        var captured=source.Rows.Single(r=>r.SourceRow==row.SourceRow);
        var arithmetic=CalculateRow(batch.ClientId,q,resolved,captured,row,source.Columns.Where(c=>c.BlankAsZero).Select(c=>c.ColumnIndex).ToHashSet(),source,retainedAnnual,context);
        if (arithmetic.Issues.Any(i=>i.Severity=="Error") || arithmetic.Preview.Lines.Count!=definitions.Count)
            throw new InvalidOperationException("The native reported-period template cannot calculate every original financial output.");
        var difference=ReviewReportedTemplateAmounts(request,batch,source,row,arithmetic.Preview,bound,closure);
        return new(arithmetic,bound,closure,difference);
    }

    private static NamedFormulaTemplateAmountDifferenceEvidence? ReviewReportedTemplateAmounts(NamedFormulaTemplateRequest request,
        ExcelPayslipBatch batch,ExcelPayslipCalculationSource source,ExcelPayslipRow row,ExcelPayrollSetupPreviewRow calculated,
        ReportedPeriodBoundRowV2 bound,ReportedPeriodCashClosureV2 closure)
    {
        var review=request.AmountDifferenceReview;
        if (review is not null)
        {
            ValidateHistoricalAmountDifferenceReview(new(){AmountDifferenceReview=review});
            if (review.SourceHash!=bound.SourceHash) throw new InvalidOperationException("The explicit monetary review belongs to a different source.");
        }
        var limit=review?.MaxAbsoluteDifference??0.000001m;
        void Exact(decimal difference,string meaning) { if (Math.Abs(difference)>limit) throw new InvalidOperationException(meaning+" exceeds its existing explicit monetary review; no source cash is replaced."); }
        var ledger=new List<ExcelPayrollTransferAmountLine>();
        foreach(var d in request.SourceReview.Components)
        {
            var line=calculated.Lines.Single(l=>l.Code.Equals(d.Code,StringComparison.OrdinalIgnoreCase)); var original=closure.Cells.Single(c=>c.ColumnIndex==d.ExpectedColumnIndex);
            if(line.SourceAmount is not decimal amount || line.UnroundedEarned is not decimal unrounded || amount!=original.SavedAmount
                || line.CalculatedAmount<0 || decimal.Round(unrounded,2,MidpointRounding.ToEven)!=line.CalculatedAmount)
                throw new InvalidOperationException("Every reported calculation must retain its exact original amount and native currency stage.");
            Exact(unrounded-amount,d.Code+" unrounded amount"); Exact(line.CalculatedAmount-amount,d.Code+" amount");
            var display=SourceCurrencyDisplay(amount); Exact(line.CalculatedAmount-display,d.Code+" displayed amount");
            ledger.Add(new(d.Code,d.Category,amount,display,unrounded,line.CalculatedAmount,line.CalculatedAmount-amount,line.CalculatedAmount-display));
        }
        var totals=new List<HistoricalAmountDifferenceTotal>();
        void Total(string kind,decimal original,decimal actual) { Exact(actual-original,kind); var display=SourceCurrencyDisplay(original); Exact(actual-display,kind+" displayed"); totals.Add(new(kind,original,display,actual,actual-original,actual-display)); }
        Total("Gross",row.DeclaredGross??row.Earnings.Sum(a=>a.Amount),calculated.Gross!.Value); Total("Deductions",row.DeclaredDeductions??row.Deductions.Sum(a=>a.Amount),calculated.Deductions!.Value);
        Total("Employer",row.EmployerContributions.Sum(a=>a.Amount),calculated.Benefits!.Value); Total("Net",row.NetPay,calculated.Net!.Value);
        if(review is null)return null;
        var owner=request.SourceReview.EmployeeDecisions.Single().MatchedEmployeeId!.Value;
        var evidence=new NamedFormulaTemplateAmountDifferenceEvidence(2,batch.ClientId,owner,request.ExistingTemplateId,batch.Id,row.Id,row.SourceRow,review,ledger,totals,[],closure.ProofHash,bound.ProofHash,Hash(JsonSerializer.Serialize(calculated,Json)),"");
        return evidence with { ProofHash=Hash(JsonSerializer.Serialize(evidence,Json)) };
    }

    private static string ReportedTemplateFamily(JsonObject template,List<JsonObject> masters,ReportedPeriodInputProfileV2 profile)
        =>ReviewedNamedFormulaInputs.Hash(new { Version=2,lines=template["lines"],components=template["lines"]!.AsArray().Select(line=>masters.Single(m=>Text(m,"id")==line!["componentId"]!.ToString())),profile.Definitions,profile.TotalPayableKeys });

    private async Task<NamedFormulaTemplateResult> SaveReportedTemplateAsync(NamedFormulaTemplateSaveRequest command,AuthUser actor)
    {
        var request=command.Request;
        if (!SelectedRunRuntime.Read(configuration).Safe || !command.Acknowledged || !Guid.TryParseExact(command.RequestId,"N",out _))
            throw new InvalidOperationException("Reported template saving needs explicit acknowledgment, one stable request reference and the isolated reviewed runtime.");
        ReportedPeriodInputV2Codec.RequireWriterCompatibility(configuration);
        var fingerprint=Hash(JsonSerializer.Serialize(command,Json)); var receiptKey="reported-input-save:"+command.RequestId;
        await using var db=Db(); await db.OpenAsync(); await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await db.QueryAsync<int>("SELECT Id FROM clients WHERE Id=@Id FOR UPDATE",new { Id=request.ClientId },tx);
        var previous=await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@Client AND ModuleCode=@Key FOR UPDATE",new { Client=request.ClientId,Key=receiptKey },tx);
        if(previous is not null)
        {
            var receipt=NamedFormulaInputStore.Read<ReportedTemplateReceipt>(previous,configuration,"ReportedSalaryPeriodTemplateReceiptV2");
            if(receipt.ActorId!=actor.Id || receipt.Fingerprint!=fingerprint) throw new InvalidOperationException("This request reference belongs to a different reported template review.");
            await tx.RollbackAsync(); return receipt.Result with { Replayed=true };
        }
        var plan=await BuildReportedTemplateAsync(db,tx,request,true); RequireMatchingSetupPlanHash(plan.Preview.PlanHash,command.PlanHash);
        var profiles=await ReportedPeriodInputProfileStore.ReadAllAsync(db,tx,configuration,request.ClientId);
        var setup=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db,tx))!;
        ReportedPeriodInputProfileV2? profile=null;
        foreach(var candidate in profiles.Values)
        {
            var template=setup["salaryStructures"]!.AsArray().OfType<JsonObject>().SingleOrDefault(t=>Text(t,"id")==candidate.TemplateId && Text(t,"active")=="true");
            if(template is not null && candidate.RulesHash!=LocationSettingsRulesHash(template,plan.Masters))
                throw new InvalidOperationException("A managed reported template's monetary rules changed; no stale family or duplicate replacement is accepted.");
            if(template is not null && ReportedTemplateFamily(template,plan.Masters,candidate)==plan.FamilyHash) { profile=candidate; break; }
        }
        if(profile is null)
        {
            var next=await PayrollDataTableStore.NextSalaryTemplateIdAsync(db,tx,await db.ExecuteScalarAsync<long?>("SELECT MAX(Id) FROM salarystructures",transaction:tx)??0);
            var id=next.ToString(CultureInfo.InvariantCulture); plan.Template["id"]=next;
            await db.ExecuteAsync("INSERT INTO salarystructures(Id,ClientId,ClientRef,Name,AnnualCtc,Active) VALUES(@Id,@Client,@Ref,@Name,@Annual,TRUE)",
                new { Id=next,Client=request.ClientId,Ref=request.ClientId.ToString(CultureInfo.InvariantCulture),Name=request.Name.Trim(),Annual=decimal.TryParse(Text(plan.Template,"annualCtc"),NumberStyles.Number,CultureInfo.InvariantCulture,out var annual)?annual:0m },tx);
            var index=0;
            foreach(var line in plan.Template["lines"]!.AsArray().OfType<JsonObject>())
                await db.ExecuteAsync("INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,Formula,BaseComponent,ProRataOverride,RoundingMode,SortOrder) VALUES(@Id,@Component,@Value,@Type,@Formula,@Base,@ProRata,@Rounding,@Order)",
                    new { Id=next,Component=Text(line,"componentId"),Value=Text(line,"value"),Type=Text(line,"calculationType"),Formula=Text(line,"formula"),Base=Text(line,"baseComponent"),ProRata=Text(line,"proRataOverride"),Rounding=Text(line,"roundingMode"),Order=index++ },tx);
            var current=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db,tx))!;
            var saved=current["salaryStructures"]!.AsArray().OfType<JsonObject>().Single(t=>Text(t,"id")==id);
            profile=plan.Profile with { TemplateId=id,RulesHash=LocationSettingsRulesHash(saved,plan.Masters),DeclarationHash="" };
            profile=profile with { DeclarationHash=ReportedPeriodInputProof.DeclarationHash(profile) };
            await ReportedPeriodInputProfileStore.InsertAsync(db,tx,configuration,profile);
        }
        var result=new NamedFormulaTemplateResult(profile.TemplateId,profile.DeclarationHash,command.RequestId,false);
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@Client,@Code,TRUE,CAST(@Json AS JSON))",new { Client=request.ClientId,Code=receiptKey,Json=NamedFormulaInputStore.Protect(new ReportedTemplateReceipt(actor.Id,fingerprint,result,plan.Preview.AmountDifferenceEvidence),configuration,"ReportedSalaryPeriodTemplateReceiptV2") },tx);
        await tx.CommitAsync(); return result;
    }
    private sealed record ReportedTemplateReceipt(int ActorId,string Fingerprint,NamedFormulaTemplateResult Result,NamedFormulaTemplateAmountDifferenceEvidence? AmountDifferenceEvidence);
    private sealed class ReportedRawHashes { public string BatchHash {get;set;}=""; public string SourceHash {get;set;}=""; }
}

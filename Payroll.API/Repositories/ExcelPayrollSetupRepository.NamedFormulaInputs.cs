using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    private sealed record NamedTemplatePlan(NamedFormulaTemplatePreview Preview,JsonObject Template,
        List<JsonObject> Masters,List<NamedFormulaInputDefinition> Definitions,string FamilyHash)
    {
        internal NamedFormulaTemplateAmountDifferenceEvidence? AmountDifferenceEvidence { get; init; }
    }
    internal const string HistoricalNamedTemplateCompatibilityMessage="Historical named-input template saving is disabled until compatible payroll API and worker readers are deployed. Preview evidence does not authorize a template or salary assignment write.";
    internal void RequireNamedTemplateReviewFlags(NamedFormulaTemplateRequest request,bool forWrite)
    {
        var review=new HistoricalPeriodSalaryRequest {AmountDifferenceReview=request.AmountDifferenceReview};
        ValidateHistoricalAmountDifferenceReview(review);
        RequireHistoricalAmountDifferenceEnabled(review);
        if(forWrite&&request.HistoricalSalaryUse&&!configuration.GetValue<bool>("Payroll:HistoricalNamedInputsEnabled"))
            throw new InvalidOperationException(HistoricalNamedTemplateCompatibilityMessage);
    }
    internal static string NamedTemplateFamily(JsonObject template,List<JsonObject> masters,List<NamedFormulaInputDefinition> definitions)
        =>ReviewedNamedFormulaInputs.Hash(new{lines=template["lines"],components=(template["lines"]!.AsArray()).Select(line=>masters.Single(m=>Text(m,"id")==line!["componentId"]!.ToString())),definitions});
    private async Task<NamedTemplatePlan> BuildNamedTemplateAsync(MySqlConnection db,MySqlTransaction tx,NamedFormulaTemplateRequest request,bool locked)
    {
        if(request is null||request.ClientId<=0||request.SourceReview is null||request.SourceReview.RowIds is null||request.SourceReview.RowIds.Count!=1||request.SourceReview.RowIds[0]!=request.SourceRowId
            ||request.SourceReview.MonthlyCtcColumnIndex.HasValue||request.SourceReview.EmployeeDecisions is null||request.SourceReview.EmployeeDecisions.Count!=1||request.SourceReview.EmployeeDecisions[0] is null||request.SourceReview.EmployeeDecisions[0].AllowCreate
            ||string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>200||request.Definitions is null||request.Bindings is null)
            throw new InvalidOperationException("Review one exact existing-employee source row, an existing client template and explicit formula operand declarations; no salary target is inferred.");
        ValidateRequest(request.SourceReview);
        RequireNamedTemplateReviewFlags(request,locked);
        var suffix=locked?" FOR UPDATE":"";
        if(await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@Id AND IsActive=TRUE"+suffix,new{Id=request.ClientId},tx) is null)throw new InvalidOperationException("Select an active client.");
        if(locked)
        {
            await db.QueryAsync<long>("SELECT Id FROM salarystructures ORDER BY Id FOR UPDATE",transaction:tx);
            await db.QueryAsync<long>("SELECT Id FROM salarystructurelines ORDER BY Id FOR UPDATE",transaction:tx);
            await db.QueryAsync<long>("SELECT Id FROM salarycomponents ORDER BY Id FOR UPDATE",transaction:tx);
            await db.QueryAsync<string>("SELECT id FROM excel_payslip_batches WHERE client_id=@Client AND id=@Batch FOR UPDATE",new{Client=request.ClientId,Batch=request.SourceReview.BatchId},tx);
            await db.QueryAsync<string>("SELECT ModuleCode FROM modulesettings WHERE client_id=@Client ORDER BY ModuleCode FOR UPDATE",new{Client=request.ClientId},tx);
        }
        var (batch,source)=await ReadSourceAsync(db,tx,request.ClientId,request.SourceReview.BatchId);
        if(ExcelPayslipCalculationService.ValidateSource(source,batch) is{} sourceError)throw new InvalidOperationException(sourceError);
        await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db,tx,request.ClientId,batch.Id);
        var sourceHash=ExcelPayslipRepository.EmploymentSourceHash(batch,source);
        if(request.SourceHash!=sourceHash)throw new InvalidOperationException("The reviewed named-input source changed.");
        var row=batch.Rows.Single(r=>r.Id==request.SourceRowId);
        var employeeId=request.SourceReview.EmployeeDecisions.Single().MatchedEmployeeId??throw new InvalidOperationException("Select the exact existing employee.");
        var employee=await EmployeeRepository.LoadRecruitmentEmployeeAsync(db,employeeId,tx)??throw new InvalidOperationException("The source employee is unavailable.");
        var identities=await ReadIdentitiesAsync(db,tx,request.ClientId,batch.Month);var issues=new List<ExcelPayrollSetupIssue>();
        if(employee.ClientId!=request.ClientId||MatchIdentity(row,SourceInformation(row,source,request.SourceReview.SourceWorkLocationColumnIndex),identities,request.SourceReview.EmployeeDecisions.Single(),issues)?.Id!=employeeId||issues.Any(i=>i.Severity=="Error"))
            throw new InvalidOperationException("Unique current strong same-client source identity is required for the named template review.");
        var setup=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db,tx))!.AsObject();
        var masters=setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToList();
        var template=setup["salaryStructures"]!.AsArray().OfType<JsonObject>().SingleOrDefault(t=>Text(t,"id")==request.ExistingTemplateId&&Text(t,"active")=="true"&&Text(t,"clientId").Split([';',',','|']).Any(v=>v.Split(':')[0].Trim()==request.ClientId.ToString(CultureInfo.InvariantCulture)))?.DeepClone().AsObject()
            ??throw new InvalidOperationException("The exact active client source template is unavailable.");
        ReviewedNamedFormulaInputs.ValidateDefinitions(request.Definitions,masters.SelectMany(m=>new[]{Text(m,"id"),Text(m,"code")}));
        var temporary=new NamedFormulaInputProfile(1,request.ClientId,request.ExistingTemplateId,"",request.Definitions,"");
        var bound=ReviewedNamedFormulaInputs.Bind(temporary,request.Bindings,source,row,masters.SelectMany(m=>new[]{Text(m,"id"),Text(m,"code")}));
        RequireReviewedNamedSourceRoles(request.SourceReview,source,request.Bindings);
        var leaves=request.Bindings.ToDictionary(b=>b.ColumnIndex,b=>new NamedRecurringSourceBoundary(b.Key,bound.Context.Values[b.Key]));
        if(request.SourceReview.PayrollDaysColumnIndex is int days&&leaves.ContainsKey(days)||request.SourceReview.PayableDaysColumnIndex is int payable&&leaves.ContainsKey(payable)||request.SourceReview.PresentDaysColumnIndex is int present&&leaves.ContainsKey(present))
            throw new InvalidOperationException("Named operands cannot replace attendance/day inputs.");
        var definitions=JsonSerializer.Deserialize<List<ExcelPayrollSetupComponent>>(JsonSerializer.Serialize(request.SourceReview.Components,Json),Json)!;
        var lines=template["lines"]!.AsArray().OfType<JsonObject>().ToArray();
        if(lines.Length!=definitions.Count||definitions.Any(d=>d.IsPeriodAdjustment||d.ExemptWhenSourceZero||d.ExpectedColumnIndex is null))throw new InvalidOperationException("Every existing financial line needs one source output; named inputs are not extra salary components.");
        var captured=source.Rows.Single(r=>r.SourceRow==row.SourceRow);var resolved=new List<ResolvedComponent>();
        var formulaEvidence=new List<ExcelPayrollTransferFormula>();
        for(var i=0;i<definitions.Count;i++)
        {
            var d=definitions[i];var line=lines[i];var master=masters.Single(m=>Text(m,"id")==d.ExistingComponentId);
            if(Text(line,"componentId")!=d.ExistingComponentId||ReusedComponentDifferences(master,d).Any(change=>change is not("Priority" or "PayType"))
                ||Text(line,"calculationType")!=d.CalculationType||Text(line,"value")!=d.Value||Text(line,"baseComponent")!=d.BaseComponent||Text(line,"proRataOverride")!=d.ProRata.ToString().ToLowerInvariant()||Text(line,"roundingMode")!="")
                throw new InvalidOperationException("Component order, monetary configuration, classification or rounding differs from the selected existing template.");
            var cell=captured.Cells[d.ExpectedColumnIndex!.Value]??throw new InvalidOperationException("A captured financial output is missing.");
            if(d.CalculationType=="Formula"&&!string.IsNullOrWhiteSpace(cell.FormulaText))d.Formula=TranslateNamedFormulaWithEvidence(cell.FormulaText,request.SourceReview,source,row,leaves).Formula;
            var proof=InspectNamedTransferFormula(request.SourceReview,source,row,d,leaves);
            if(proof.Status=="Blocked")throw new InvalidOperationException(d.Code+": "+proof.Reason);
            formulaEvidence.Add(proof);
            line["formula"]=d.Formula;resolved.Add(new(d,d.ExistingComponentId!,false));
        }
        ReviewCorrectionAggregates(source,row,request.SourceReview,resolved);
        var arithmetic=CalculateRow(request.ClientId,request.SourceReview,resolved,captured,row,source.Columns.Where(c=>c.BlankAsZero).Select(c=>c.ColumnIndex).ToHashSet(),source,employee.AnnualCtc,bound.Context);
        if(arithmetic.Issues.Any(i=>i.Severity=="Error")||arithmetic.Preview.Lines.Count!=definitions.Count
            ||request.AmountDifferenceReview is null&&arithmetic.Preview.Lines.Any(l=>l.Variance is null||Math.Abs(l.Variance.Value)>0.000001m))throw new InvalidOperationException("The complete native named-input financial calculation does not match every captured source output.");
        var difference=request.AmountDifferenceReview is null?null:ReviewNamedTemplateAmounts(request,batch,source,row,arithmetic.Preview,formulaEvidence,bound.Context);
        template["name"]=request.Name.Trim();template["clientId"]=request.ClientId.ToString(CultureInfo.InvariantCulture);
        var family=NamedTemplateFamily(template,masters,request.Definitions);
        var raw=await db.QuerySingleAsync("SELECT SHA2(CAST(b.batch_json AS CHAR),256) BatchHash,SHA2(CAST(s.source_json AS CHAR),256) SourceHash FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@Batch",new{Client=request.ClientId,Batch=batch.Id},tx);
        // Retain the exact legacy hash shape when the optional review is absent.
        var planHash=difference is null?Hash(JsonSerializer.Serialize(new{request,batch,source,raw,employee,setup,template,family},Json))
            :Hash(JsonSerializer.Serialize(new{request,batch,source,raw,employee,setup,template,family,amountDifferenceEvidence=difference},Json));
        var blockers=request.HistoricalSalaryUse&&!configuration.GetValue<bool>("Payroll:HistoricalNamedInputsEnabled")
            ?new List<string>{HistoricalNamedTemplateCompatibilityMessage}:[];
        return new(new(planHash,blockers.Count==0,blockers,sourceHash,definitions.Count,arithmetic.Preview.Lines){AmountDifferenceEvidence=difference},template,masters,request.Definitions,family){AmountDifferenceEvidence=difference};
    }
    public async Task<NamedFormulaTemplatePreview> PreviewNamedFormulaTemplateAsync(NamedFormulaTemplateRequest request,AuthUser actor)
    {
        if(request is null)throw new InvalidOperationException("Submit an explicit named template review.");
        if(!CanManageLocationCorrection(actor,request.ClientId))throw new UnauthorizedAccessException();
        await using var db=Db();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        if (request.SourceReview?.ReportedPeriodInputs is not null)
        {
            var reported=await BuildReportedTemplateAsync(db,tx,request,false); await tx.RollbackAsync(); return reported.Preview;
        }
        var plan=await BuildNamedTemplateAsync(db,tx,request,false);await tx.RollbackAsync();return plan.Preview;
    }
    public async Task<NamedFormulaTemplateResult> SaveNamedFormulaTemplateAsync(NamedFormulaTemplateSaveRequest command,AuthUser actor)
    {
        var request=command?.Request??throw new InvalidOperationException("Submit the exact reviewed named template request.");
        if(!CanManageLocationCorrection(actor,request.ClientId))throw new UnauthorizedAccessException();
        if (request.SourceReview?.ReportedPeriodInputs is not null)
            return await SaveReportedTemplateAsync(command!,actor);
        if(!SelectedRunRuntime.Read(configuration).Safe||!command.Acknowledged||!Guid.TryParseExact(command.RequestId,"N",out _))throw new InvalidOperationException("Explicit current review, stable request reference and isolated runtime are required.");
        var fingerprint=Hash(JsonSerializer.Serialize(command,Json));var receiptKey="named-input-save:"+command.RequestId;
        await using var db=Db();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await db.QueryAsync<int>("SELECT Id FROM clients WHERE Id=@Id FOR UPDATE",new{Id=request.ClientId},tx);
        var previous=await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@Client AND ModuleCode=@Key FOR UPDATE",new{Client=request.ClientId,Key=receiptKey},tx);
        if(previous is not null)
        {
            var receipt=NamedFormulaInputStore.Read<NamedTemplateReceipt>(previous,configuration,"TemplateReceipt");
            if(receipt.Fingerprint!=fingerprint||receipt.ActorId!=actor.Id)throw new InvalidOperationException("This request reference belongs to a different named template review.");
            await tx.RollbackAsync();return receipt.Result with{Replayed=true};
        }
        var plan=await BuildNamedTemplateAsync(db,tx,request,true);RequireMatchingSetupPlanHash(plan.Preview.PlanHash,command.PlanHash);
        // A genuinely identical generic family is reused independently of this
        // source employee's values. No extra template is made for20 versus25.
        var existing=(await db.QueryAsync<string>("SELECT ModuleCode FROM modulesettings WHERE client_id=@Client AND ModuleCode LIKE 'named-input-t:%'",new{Client=request.ClientId},tx)).Select(code=>code["named-input-t:".Length..]).ToArray();
        var profiles=await NamedFormulaInputStore.ReadProfilesAsync(db,tx,configuration,request.ClientId,existing);
        NamedFormulaInputProfile? profile=null;
        var setup=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db,tx))!;
        foreach(var candidate in profiles.Values)
        {
            var t=setup["salaryStructures"]!.AsArray().OfType<JsonObject>().SingleOrDefault(t=>Text(t,"id")==candidate.TemplateId&&Text(t,"active")=="true");
            if(t is not null&&candidate.RulesHash!=LocationSettingsRulesHash(t,plan.Masters))throw new InvalidOperationException("A managed named template's monetary rules changed; review its exact declaration instead of reusing or duplicating it.");
            if(candidate.ClientId==request.ClientId&&t is not null&&NamedTemplateFamily(t,plan.Masters,candidate.Definitions)==plan.FamilyHash){profile=candidate;break;}
        }
        if(profile is null)
        {
            var next=Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),1+(await db.ExecuteScalarAsync<long?>("SELECT MAX(Id) FROM salarystructures",transaction:tx)??0));
            var id=next.ToString(CultureInfo.InvariantCulture);plan.Template["id"]=next;
            await db.ExecuteAsync("INSERT INTO salarystructures(Id,ClientId,ClientRef,Name,AnnualCtc,Active) VALUES(@Id,@Client,@Ref,@Name,@Annual,TRUE)",new{Id=next,Client=request.ClientId,Ref=request.ClientId.ToString(CultureInfo.InvariantCulture),Name=request.Name.Trim(),Annual=decimal.TryParse(Text(plan.Template,"annualCtc"),NumberStyles.Number,CultureInfo.InvariantCulture,out var annual)?annual:0m},tx);
            var index=0;
            foreach(var line in plan.Template["lines"]!.AsArray().OfType<JsonObject>())
                await db.ExecuteAsync(@"INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,Formula,BaseComponent,ProRataOverride,RoundingMode,SortOrder)
VALUES(@Id,@Component,@Value,@Type,@Formula,@Base,@ProRata,@Rounding,@Order)",new{Id=next,Component=Text(line,"componentId"),Value=Text(line,"value"),Type=Text(line,"calculationType"),Formula=Text(line,"formula"),Base=Text(line,"baseComponent"),ProRata=Text(line,"proRataOverride"),Rounding=Text(line,"roundingMode"),Order=index++},tx);
            // Hash the database projection, preserving its canonical DTO types.
            var current=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db,tx))!;
            var saved=current["salaryStructures"]!.AsArray().OfType<JsonObject>().Single(t=>Text(t,"id")==id);
            var rules=LocationSettingsRulesHash(saved,plan.Masters);
            profile=new(1,request.ClientId,id,rules,plan.Definitions,ReviewedNamedFormulaInputs.DeclarationHash(request.ClientId,id,rules,plan.Definitions));
            await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@Client,@Code,TRUE,CAST(@Json AS JSON))",new{Client=request.ClientId,Code=ReviewedNamedFormulaInputs.ProfileKey(id),Json=NamedFormulaInputStore.Protect(profile,configuration,"Template")},tx);
        }
        var result=new NamedFormulaTemplateResult(profile.TemplateId,profile.DeclarationHash,command.RequestId,false);
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@Client,@Code,TRUE,CAST(@Json AS JSON))",new{Client=request.ClientId,Code=receiptKey,Json=NamedFormulaInputStore.Protect(new NamedTemplateReceipt(actor.Id,fingerprint,result){AmountDifferenceEvidence=plan.AmountDifferenceEvidence,HistoricalSalaryUse=request.HistoricalSalaryUse},configuration,"TemplateReceipt")},tx);
        await tx.CommitAsync();return result;
    }
    private sealed record NamedTemplateReceipt(int ActorId,string Fingerprint,NamedFormulaTemplateResult Result)
    {
        [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        public NamedFormulaTemplateAmountDifferenceEvidence? AmountDifferenceEvidence { get; init; }
        [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)]
        public bool HistoricalSalaryUse { get; init; }
    }

    internal static NamedFormulaTemplateAmountDifferenceEvidence ReviewNamedTemplateAmounts(NamedFormulaTemplateRequest request,
        ExcelPayslipBatch batch,ExcelPayslipCalculationSource source,ExcelPayslipRow row,ExcelPayrollSetupPreviewRow calculated,
        IReadOnlyList<ExcelPayrollTransferFormula> formulas,NamedFormulaInputContext namedContext)
    {
        var declaration=request.AmountDifferenceReview??throw new InvalidOperationException("An explicit configuration monetary review is required.");
        ValidateHistoricalAmountDifferenceReview(new(){AmountDifferenceReview=declaration});
        var sourceHash=ExcelPayslipRepository.EmploymentSourceHash(batch,source);
        var owner=request.SourceReview.EmployeeDecisions.SingleOrDefault();
        if(request.SourceHash!=sourceHash||declaration.SourceHash!=sourceHash||request.ClientId!=batch.ClientId
            ||request.SourceReview.BatchId!=batch.Id||request.SourceRowId!=row.Id||calculated.RowId!=row.Id
            ||batch.Rows.Count(r=>r.Id==row.Id&&r.SourceRow==row.SourceRow)!=1||owner is null||owner.RowId!=row.Id||owner.AllowCreate||owner.MatchedEmployeeId is not >0)
            throw new InvalidOperationException("The bounded named-template review must retain its exact current source and existing employee owner.");
        var definitions=request.SourceReview.Components;
        var columns=source.Columns.Where(c=>request.SourceReview.SourceColumnRoles.GetValueOrDefault(c.ColumnIndex,c.Kind) is "earning" or "deduction" or "employer").ToArray();
        if(definitions.Count==0||definitions.Count!=calculated.Lines.Count||definitions.Count!=formulas.Count
            ||definitions.Any(d=>string.IsNullOrWhiteSpace(d.ExistingComponentId)||d.ExpectedColumnIndex is null||d.IsPeriodAdjustment||d.ExemptWhenSourceZero||d.CalculationType is "Manual / Variable" or "Residual / Balancing")
            ||definitions.Select(d=>d.Code.ToUpperInvariant()).Distinct().Count()!=definitions.Count||definitions.Select(d=>d.ExpectedColumnIndex).Distinct().Count()!=definitions.Count
            ||!definitions.Select(d=>d.ExpectedColumnIndex!.Value).Order().SequenceEqual(columns.Select(c=>c.ColumnIndex).Order())
            ||formulas.Any(f=>f.RowId!=row.Id||f.SourceRow!=row.SourceRow||!Regex.IsMatch(f.SourceHash??"",@"\A[a-f0-9]{64}\z")
                ||!definitions.Any(d=>d.Code.Equals(f.Code,StringComparison.OrdinalIgnoreCase)&&d.ExpectedColumnIndex==f.SourceColumn)
                ||f.Status!="Exact supported translation"&&!(request.SourceReview.ReviewedFixedInputs&&f.Status=="Reviewed fixed source"))
            ||formulas.Select(f=>f.Code.ToUpperInvariant()).Distinct().Count()!=definitions.Count
            ||request.Definitions.Count==0||request.Bindings.Count!=request.Definitions.Count
            ||!request.Definitions.Select(d=>d.Key).Order(StringComparer.Ordinal).SequenceEqual(namedContext.Values.Keys.Order(StringComparer.Ordinal))
            ||ReviewedNamedFormulaInputs.Hash(request.Definitions)!=ReviewedNamedFormulaInputs.Hash(namedContext.Definitions))
            throw new InvalidOperationException("Complete recurring financial outputs, supported source rules and declared named operands are required; a monetary review cannot waive a missing input or mapping.");
        RequireReviewedNamedSourceRoles(request.SourceReview,source,request.Bindings);
        var rebound=ReviewedNamedFormulaInputs.Bind(new(1,request.ClientId,request.ExistingTemplateId,"",request.Definitions,namedContext.DeclarationHash),
            request.Bindings,source,row,definitions.SelectMany(d=>new[]{d.Code,d.ExistingComponentId!}));
        if(!rebound.Context.Values.OrderBy(v=>v.Key,StringComparer.Ordinal).SequenceEqual(namedContext.Values.OrderBy(v=>v.Key,StringComparer.Ordinal)))
            throw new InvalidOperationException("The calculated named operands differ from their exact independent captured source cells.");
        var cells=source.Rows.Single(r=>r.SourceRow==row.SourceRow);var ledger=new List<ExcelPayrollTransferAmountLine>();
        void Bound(decimal delta,string meaning){if(Math.Abs(delta)>declaration.MaxAbsoluteDifference)throw new InvalidOperationException(meaning+" exceeds the explicit named-template monetary limit. No financial amount is replaced.");}
        foreach(var d in definitions)
        {
            var line=calculated.Lines.SingleOrDefault(l=>l.Code.Equals(d.Code,StringComparison.OrdinalIgnoreCase));var column=columns.Single(c=>c.ColumnIndex==d.ExpectedColumnIndex);
            var role=d.Category switch{"Earning" or "Reimbursement"=>"earning","Deduction"=>"deduction","Benefit"=>"employer",_=>""};
            var saved=d.Category switch{"Earning" or "Reimbursement"=>row.Earnings,"Deduction"=>row.Deductions,_=>row.EmployerContributions};var matches=saved.Where(a=>string.Equals(a.Label,column.Label,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(line?.SourceAmount is not decimal amount||line.UnroundedEarned is not decimal before||line.Variance is null||line.Category!=d.Category
                ||request.SourceReview.SourceColumnRoles.GetValueOrDefault(column.ColumnIndex,column.Kind)!=role||matches.Length!=1
                ||amount<0||before<0||line.CalculatedAmount<0||decimal.Round(before,2,MidpointRounding.ToEven)!=line.CalculatedAmount
                ||line.Variance!=line.CalculatedAmount-amount||SourceDisplayPrecision(matches[0].Amount)!=SourceDisplayPrecision(amount)
                ||SourceDisplayPrecision(ReadNumber(cells,column.ColumnIndex,d.BlankInputAsZero||column.BlankAsZero))!=SourceDisplayPrecision(amount))
                throw new InvalidOperationException("Every native configuration currency stage and captured mapped source amount must remain complete and unchanged.");
            Bound(before-amount,d.Code+" pre-currency difference");Bound(line.CalculatedAmount-amount,d.Code+" raw difference");var display=SourceCurrencyDisplay(amount);Bound(line.CalculatedAmount-display,d.Code+" displayed difference");
            ledger.Add(new(d.Code,d.Category,amount,display,before,line.CalculatedAmount,line.CalculatedAmount-amount,line.CalculatedAmount-display));
        }
        decimal Sum(params string[] kinds)=>ledger.Where(l=>kinds.Contains(l.Category)).Sum(l=>l.Calculated);
        var gross=Sum("Earning","Reimbursement");var deductions=Sum("Deduction");var benefits=Sum("Benefit");var net=Math.Max(0,gross-deductions);
        if(calculated.Gross!=gross||calculated.Deductions!=deductions||calculated.Benefits!=benefits||calculated.Net!=net
            ||row.Earnings.Count!=ledger.Count(l=>l.Category is "Earning" or "Reimbursement")||row.Deductions.Count!=ledger.Count(l=>l.Category=="Deduction")||row.EmployerContributions.Count!=ledger.Count(l=>l.Category=="Benefit"))
            throw new InvalidOperationException("The complete native configuration totals and source financial coverage must reconcile without an amount override.");
        var sourceGross=row.Earnings.Sum(a=>a.Amount);var sourceDeductions=row.Deductions.Sum(a=>a.Amount);var sourceBenefits=row.EmployerContributions.Sum(a=>a.Amount);
        if(sourceGross<0||sourceDeductions<0||sourceBenefits<0||row.NetPay<0||row.DeclaredGross is <0||row.DeclaredDeductions is <0)throw new InvalidOperationException("Negative source financial values require their separate existing review.");
        var totals=new List<HistoricalAmountDifferenceTotal>();
        void Total(string kind,decimal raw,decimal actual){var display=SourceCurrencyDisplay(raw);Bound(actual-raw,kind+" raw total");Bound(actual-display,kind+" displayed total");totals.Add(new(kind,raw,display,actual,actual-raw,actual-display));}
        Total("Gross",row.DeclaredGross??sourceGross,gross);Total("Deductions",row.DeclaredDeductions??sourceDeductions,deductions);Total("Employer",sourceBenefits,benefits);Total("Net",row.NetPay,net);
        var aggregates=new List<HistoricalSourceAggregateDifference>();
        void Aggregate(string kind,decimal stored,decimal sum){Bound(stored-sum,kind+" cached source aggregate");var delta=SourceCurrencyDisplay(stored)-SourceCurrencyDisplay(sum);Bound(delta,kind+" displayed source aggregate");aggregates.Add(new(kind,stored,sum,stored-sum,delta));}
        Aggregate("Gross",row.DeclaredGross??sourceGross,sourceGross);Aggregate("Deductions",row.DeclaredDeductions??sourceDeductions,sourceDeductions);Aggregate("Net",row.NetPay,Math.Max(0,sourceGross-sourceDeductions));
        var review=new HistoricalAmountDifferenceReview{Version=declaration.Version,Reviewed=declaration.Reviewed,SourceHash=declaration.SourceHash,MaxAbsoluteDifference=declaration.MaxAbsoluteDifference,Reason=declaration.Reason.Trim()};
        var evidence=new NamedFormulaTemplateAmountDifferenceEvidence(1,batch.ClientId,owner.MatchedEmployeeId!.Value,request.ExistingTemplateId,batch.Id,row.Id,row.SourceRow,review,ledger,totals,aggregates,
            Hash(JsonSerializer.Serialize(formulas,Json)),Hash(JsonSerializer.Serialize(new{Context=namedContext,Cells=rebound.Cells},Json)),Hash(JsonSerializer.Serialize(calculated,Json)),"");
        return evidence with{ProofHash=Hash(JsonSerializer.Serialize(new{Version="named-template-bounded-monetary-difference-v1",request,batch,source,row,calculated,evidence},Json))};
    }

    private static void RequireReviewedNamedSourceRoles(ExcelPayrollSetupRequest request,ExcelPayslipCalculationSource source,IReadOnlyList<NamedFormulaInputBinding> bindings)
    {
        foreach(var binding in bindings)
        {
            var column=source.Columns.SingleOrDefault(c=>c.ColumnIndex==binding.ColumnIndex);
            if(column is null||request.SourceColumnRoles.GetValueOrDefault(binding.ColumnIndex,column.Kind)!="info"
                ||request.Components.Any(c=>c.ExpectedColumnIndex==binding.ColumnIndex||c.SourceColumnIndex==binding.ColumnIndex))
                throw new InvalidOperationException("Named operands need explicitly reviewed independent Information columns; a financial output or component amount input cannot be reused as a separate operand.");
        }
    }
}

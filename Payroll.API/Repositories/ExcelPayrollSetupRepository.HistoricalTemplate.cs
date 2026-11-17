using System.Globalization;
using System.Text;
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
    private sealed record HistoricalTemplateReview(int Version,string Purpose,int ActorId,int ClientId,DateTime ExpiresAtUtc,
        string Fingerprint,HistoricalTemplateConfigurationRequest Request);
    private sealed record HistoricalTemplateStored(int Version,string Purpose,int ActorId,HistoricalTemplateConfigurationReceipt Receipt,
        HistoricalTemplateConfigurationRequest Request,string SourceHash,string SetupHash);
    private sealed record HistoricalTemplatePlan(HistoricalTemplateConfigurationPreview Preview,JsonObject Before,JsonObject After);

    internal static bool CanCreateHistoricalTemplate(AuthUser actor,int clientId)=>CanManageHistoricalSalary(actor,clientId)
        &&actor.Permissions.Contains("settings.manage",StringComparer.OrdinalIgnoreCase);
    private static void ValidateHistoricalTemplateRequest(HistoricalTemplateConfigurationRequest request)
    {
        if(request is null||!Regex.IsMatch(request.TemplateId??"",@"\A[1-9][0-9]{0,18}\z")||!long.TryParse(request.TemplateId,out _)
            ||!Regex.IsMatch(request.ExpectedSourceHash??"",@"\A[a-f0-9]{64}\z")
            ||request.ExpectedSetupHash is null||request.ExpectedSetupHash.Length>0&&!Regex.IsMatch(request.ExpectedSetupHash,@"\A[a-f0-9]{64}\z"))
            throw new InvalidOperationException("Review one new positive template ID and exact source/setup witnesses.");
        ValidateHistoricalSalaryRequest(request.SourceReview);
        if(request.SourceReview.Assignments.Any(a=>a.TemplateId!=request.TemplateId)
            ||request.SourceReview.Setup.Components.Any(c=>c.RowFormulas.Values.Any(formula=>!TransferFieldEqual("formula",formula,c.Formula))))
            throw new InvalidOperationException("One reusable template must use the same reviewed component rules for every selected source row.");
    }

    private async Task<HistoricalTemplatePlan> BuildHistoricalTemplatePlanAsync(MySqlConnection db,MySqlTransaction tx,int clientId,HistoricalTemplateConfigurationRequest request)
    {
        ValidateHistoricalTemplateRequest(request);var q=request.SourceReview;var setupRequest=q.Setup;
        await PayrollFormulaStorage.RequireAsync(db,tx,PayrollFormulaStorage.SetupRequestFormulas(setupRequest));
        await ExcelPayslipRepository.RequireCurrentConsolidationSourceAsync(db,tx,clientId,setupRequest.BatchId);
        var(batch,source)=await ReadSourceAsync(db,tx,clientId,setupRequest.BatchId);var period=ClosedHistoricalPeriod(batch.Month,EmployeeEmploymentRules.Today);
        var before=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db,tx))!.AsObject();var after=before.DeepClone().AsObject();
        var preview=new HistoricalTemplateConfigurationPreview{ClientId=clientId,SourceHash=Hash(JsonSerializer.Serialize(new{Batch=batch,Source=source},Json)),SetupHash=Hash(before.ToJsonString())};
        void Block(string error){if(!preview.Blockers.Contains(error,StringComparer.Ordinal))preview.Blockers.Add(error);}
        if(preview.SourceHash!=request.ExpectedSourceHash)Block("The exact saved source changed. Review its fresh source hash.");
        if(preview.SetupHash!=request.ExpectedSetupHash)Block("Review the exact current setup hash before creating one template.");
        if(ExcelPayslipCalculationService.ValidateSource(source,batch) is string sourceError)Block(sourceError);
        if(source.CaptureVersion>0&&(source.DependencyReviewStatus!="Reviewed"||ExcelPayslipRepository.ValidateCapturedWorkbookBindings(source,await ExcelPayslipRepository.ReadLinkedOriginalsAsync(db,tx,clientId,batch.Id)) is not null))Block("Review the exact captured original and dependency ownership before copying reusable rules.");
        if(setupRequest.EffectiveFrom!=period.From.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)||setupRequest.TemplateScope is not{Reviewed:true,OpenEnded:false} scope||scope.EffectiveTo!=period.To.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture))Block("Review applicability for this one complete closed source month.");
        var selected=batch.Rows.Where(row=>setupRequest.RowIds.Contains(row.Id)).ToArray();
        if(selected.Length!=q.Assignments.Count||selected.Select(r=>r.Id).Distinct(StringComparer.Ordinal).Count()!=selected.Length)Block("Every selected source row must still belong uniquely to this batch.");
        var masters=before["salaryComponents"]!.AsArray().OfType<JsonObject>().ToArray();var components=new List<ResolvedComponent>();
        foreach(var component in setupRequest.Components)
        {
            var master=masters.SingleOrDefault(m=>Text(m,"id")==component.ExistingComponentId);
            if(master is null||ReusedComponentDifferences(master,component).Count>0)Block("The reviewed reusable component Master changed or its classification differs.");
            else components.Add(new(component,Text(master,"id"),false));
        }
        var literals=ReviewHistoricalLiteralSelfInputs(q,source,selected,preview.SourceHash);
        foreach(var issue in HistoricalSourceCoverage(source,setupRequest,components,literals.Select(e=>e.ComponentKey).ToHashSet(StringComparer.Ordinal)))Block(issue);
        ValidateSharedPayrollDaysBinding(setupRequest,source,selected,batch.Rows);
        var identities=await ReadIdentitiesAsync(db,tx,clientId,batch.Month);
        var reviewedIdentities=await ReviewHistoricalSourceIdentitiesAsync(db,tx,q,batch,source,identities,preview.SourceHash,false);
        var state=await EmployeeRepository.ReadClosedSalaryHistoryStateAsync(db,tx,clientId,q.Assignments.Select(a=>a.EmployeeId).ToArray());
        foreach(var row in selected)
        {
            var assignment=q.Assignments.Single(a=>a.RowId==row.Id);var identity=identities.SingleOrDefault(i=>i.Id==assignment.EmployeeId);
            if(identity is null){Block("The selected source needs an existing employee in this client.");continue;}
            foreach(var error in HistoricalIdentityIssues(batch,source,row,identity,identities,reviewedIdentities.GetValueOrDefault(row.Id)))Block(error);
            HistoricalInitialSourceTargetEvidence? initial=null;decimal annual;
            if(q.SalaryTargetMode==InitialSourceTargetMode)
            {
                var current=await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=@Id AND ClientId=@ClientId",new{Id=assignment.EmployeeId,ClientId=clientId},tx);
                initial=ReadInitialSourceTarget(q,source,row,assignment.EmployeeId,current,preview.SourceHash);annual=initial.AnnualSalaryTarget;
                EmployeeRepository.RequireClosedInitialSalaryWindow(state,assignment.EmployeeId,period.From,period.To,annual,current);
            }
            else annual=EmployeeRepository.RequireClosedSalaryBasis(state,assignment.EmployeeId,period.From,period.To).AnnualCtc;
            foreach(var component in components)
            {
                var formula=InspectTransferFormula(setupRequest,source,row,component.Definition,HistoricalInitialSourceBoundary(q,initial));
                if(formula.Status is not("Exact supported translation" or "Reviewed fixed source"))Block(formula.Code+": "+formula.Reason);
            }
            var arithmetic=CalculateRow(clientId,setupRequest,components,source.Rows.Single(r=>r.SourceRow==row.SourceRow),row,
                source.Columns.Where(c=>c.BlankAsZero).Select(c=>c.ColumnIndex).ToHashSet(),source,annual);
            foreach(var issue in arithmetic.Issues.Where(i=>i.Severity=="Error"))Block(issue.Message);
            if(q.PeriodProfessionalTaxReview is not null)RequireHistoricalPtSourceClosure(q,source,row,preview.SourceHash);
            var employee=await EmployeeRepository.LoadRecruitmentEmployeeAsync(db,assignment.EmployeeId,tx)??throw new InvalidOperationException("The selected client employee is unavailable.");
            foreach(var issue in TemplateScopeIssues(setupRequest,[new(){Employee=employee,RowId=row.Id,SourceRow=row.SourceRow,Components=components,TemplateId=request.TemplateId}]))Block(issue);
        }
        var templates=before["salaryStructures"]!.AsArray();
        var idExists=templates.OfType<JsonObject>().Any(t=>Text(t,"id")==request.TemplateId);
        if(idExists)Block("This template ID already exists. Do not overwrite or choose a new ID without another reviewed command.");
        if(templates.OfType<JsonObject>().Any(t=>SettingsRepository.IsPureClientRow(t,clientId)&&Text(t,"name").Equals(setupRequest.TemplateName.Trim(),StringComparison.OrdinalIgnoreCase)))Block("This client already has a template with the reviewed name.");
        preview.Template=new JsonObject{["id"]=long.Parse(request.TemplateId,CultureInfo.InvariantCulture),["clientId"]=clientId.ToString(CultureInfo.InvariantCulture),["name"]=setupRequest.TemplateName.Trim(),["annualCtc"]="0",["active"]=true,
            ["lines"]=new JsonArray(components.Select(c=>(JsonNode)new JsonObject{["componentId"]=c.Id,["value"]=c.Definition.Value,["calculationType"]=c.Definition.CalculationType,["formula"]=c.Definition.Formula,["baseComponent"]=c.Definition.BaseComponent,["proRataOverride"]=c.Definition.ProRata.ToString().ToLowerInvariant(),["roundingMode"]=""}).ToArray())};
        if(!idExists){after["salaryStructures"]!.AsArray().Add(preview.Template.DeepClone());PayrollDataTableStore.RequireSingleSalaryStructureAppend(before,after,clientId);}
        var rawSource=await db.ExecuteScalarAsync<string>("SELECT CONCAT(SHA2(CAST(b.batch_json AS CHAR),256),':',SHA2(CAST(s.source_json AS CHAR),256)) FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@ClientId AND b.id=@BatchId",new{ClientId=clientId,BatchId=batch.Id},tx);
        preview.Fingerprint=Hash(JsonSerializer.Serialize(new{Version=1,ClientId=clientId,Request=request,preview.SourceHash,preview.SetupHash,RawSource=rawSource,Template=preview.Template},Json));
        return new(preview,before,after);
    }

    public async Task<HistoricalTemplateConfigurationPreview> PreviewHistoricalTemplateConfigurationAsync(int clientId,HistoricalTemplateConfigurationRequest request,AuthUser actor)
    {
        if(!CanCreateHistoricalTemplate(actor,clientId))throw new UnauthorizedAccessException();
        await using var db=Db();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var plan=await BuildHistoricalTemplatePlanAsync(db,tx,clientId,request);await tx.RollbackAsync();
        if(plan.Preview.CanCreate){var expiry=DateTime.UtcNow.AddMinutes(30);var value=JsonSerializer.Serialize(new HistoricalTemplateReview(1,"ClosedTemplateConfiguration",actor.Id,clientId,expiry,plan.Preview.Fingerprint,request),Json);if(Encoding.UTF8.GetByteCount(value)>2_000_000)throw new InvalidOperationException("Review fewer source rows.");plan.Preview.ReviewToken=HistoricalProtector.ProtectHistoricalSalary(value);plan.Preview.ExpiresAtUtc=expiry;}
        return plan.Preview;
    }

    public async Task<HistoricalTemplateConfigurationReceipt> ApplyHistoricalTemplateConfigurationAsync(int clientId,HistoricalTemplateConfigurationApplyRequest command,AuthUser actor)
    {
        if(!CanCreateHistoricalTemplate(actor,clientId))throw new UnauthorizedAccessException();
        if(!SelectedRunRuntime.Read(configuration).Safe||command is null||!command.Acknowledged||!Guid.TryParse(command.RequestId,out var parsed)||command.ReviewToken is null||command.ReviewToken.Length is <1 or >3_000_000)throw new InvalidOperationException("Acknowledge the fresh one-template review in the isolated local payroll runtime.");
        var review=JsonSerializer.Deserialize<HistoricalTemplateReview>(HistoricalProtector.UnprotectHistoricalSalary(command.ReviewToken)??throw new InvalidOperationException("Invalid template review."),Json);
        if(review is not{Version:1,Purpose:"ClosedTemplateConfiguration"}||review.ActorId!=actor.Id||review.ClientId!=clientId)throw new UnauthorizedAccessException();
        ValidateHistoricalTemplateRequest(review.Request);var requestId=parsed.ToString("N");var key="closed_template_receipt:"+clientId+":"+requestId;
        await using var db=Db();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        if(await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@Id AND IsActive=TRUE FOR UPDATE",new{Id=clientId},tx)is null)throw new InvalidOperationException("The selected client is unavailable.");
        var prior=await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code FOR UPDATE",new{ClientId=clientId,Code=key},tx);
        if(prior is not null){var old=JsonSerializer.Deserialize<HistoricalTemplateStored>(HistoricalProtector.UnprotectHistoricalSalary(JsonSerializer.Deserialize<string>(prior)??"")??throw new InvalidOperationException("The template receipt is invalid."),Json);if(old is not{Version:1,Purpose:"ClosedTemplateConfigurationReceipt"}||old.ActorId!=actor.Id||old.Receipt.ClientId!=clientId||old.Receipt.Fingerprint!=review.Fingerprint||old.Receipt.TemplateId!=review.Request.TemplateId)throw new InvalidOperationException("This request ID belongs to another template action.");await tx.RollbackAsync();return old.Receipt with{Replayed=true};}
        if(review.ExpiresAtUtc<=DateTime.UtcNow)throw new InvalidOperationException("This template review expired.");
        await db.QueryAsync<long>("SELECT Id FROM payrollsetups ORDER BY Id FOR UPDATE",transaction:tx);await db.QueryAsync<long>("SELECT Id FROM salarystructures ORDER BY Id FOR UPDATE",transaction:tx);await db.QueryAsync<long>("SELECT Id FROM salarystructurelines ORDER BY Id FOR UPDATE",transaction:tx);await db.QueryAsync<long>("SELECT Id FROM paysliptemplates ORDER BY Id FOR UPDATE",transaction:tx);
        await LockHistoricalInputsAsync(db,tx,clientId,review.Request.SourceReview);
        var plan=await BuildHistoricalTemplatePlanAsync(db,tx,clientId,review.Request);
        if(!plan.Preview.CanCreate||plan.Preview.Fingerprint!=review.Fingerprint)throw new SelectedPayRunConflictException("The exact source, ownership, component or complete setup witness changed. No template was created.");
        await PayrollDataTableStore.SaveSelectedClientSetupJsonAsync(db,tx,clientId,plan.Before,plan.After,singleStructureAppendOnly:true);
        var fresh=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db,tx))!.AsObject();var saved=fresh["salaryStructures"]!.AsArray().OfType<JsonObject>().SingleOrDefault(t=>Text(t,"id")==review.Request.TemplateId);
        if(saved is null||TransferTemplateDifferences(saved,review.Request.SourceReview.Setup.Components.Select(c=>new ResolvedComponent(c,c.ExistingComponentId!,false)).ToArray()).Count>0)throw new InvalidOperationException("The stored template differs from the reviewed reusable rules; the complete transaction must roll back.");
        var freshOthers=fresh.DeepClone().AsObject();freshOthers["salaryStructures"]!.AsArray().Remove(freshOthers["salaryStructures"]!.AsArray().Single(n=>n is JsonObject t&&Text(t,"id")==review.Request.TemplateId));
        if(!JsonNode.DeepEquals(freshOthers,plan.Before))throw new InvalidOperationException("Another setup section changed during the one-template append; the complete transaction must roll back.");
        var receipt=new HistoricalTemplateConfigurationReceipt(clientId,review.Request.TemplateId,requestId,review.Fingerprint,false);
        var payload=HistoricalProtector.ProtectHistoricalSalary(JsonSerializer.Serialize(new HistoricalTemplateStored(1,"ClosedTemplateConfigurationReceipt",actor.Id,receipt,review.Request,plan.Preview.SourceHash,plan.Preview.SetupHash),Json));
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,CAST(@Json AS JSON))",new{ClientId=clientId,Code=key,Json=JsonSerializer.Serialize(payload)},tx);
        await tx.CommitAsync();return receipt;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    internal sealed record HistoricalPtPrevious(int EmployeeId,string? Json,bool? Enabled);
    internal sealed record HistoricalPtSaved(HistoricalProfessionalTaxEvidence Evidence,HistoricalPtPrevious Previous);

    private async Task<(string Hash,HistoricalPtPrevious Previous)> ReadHistoricalPtPreviousAsync(MySqlConnection db,MySqlTransaction tx,
        int clientId,int employeeId,string month,string batchId,string rowId,string sourceHash,bool locked)
    {
        var saved=await db.QuerySingleOrDefaultAsync<PtSetting>("SELECT CAST(SettingsJson AS CHAR) Json,IsEnabled Enabled FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code"+(locked?" FOR UPDATE":""),
            new{ClientId=clientId,Code=ReviewedPeriodProfessionalTax.Key(employeeId,month)},tx);
        if(saved is null)return("",new(employeeId,null,null));
        var old=ReadPt<ReviewedProfessionalTaxControl>(saved.Json,"PeriodControl");ReviewedPeriodProfessionalTax.RequireControl(old,clientId,employeeId,month);
        if(!saved.Enabled || old.Mode==ReviewedPeriodProfessionalTax.Reviewed&&old.Amount!=0 || old.SourceBatchId!=batchId || old.SourceRowId!=rowId || old.SourceHash!=sourceHash)
            throw new InvalidOperationException("An existing positive, differently sourced or stale period PT control cannot be replaced by closed salary review.");
        return(ExcelPayrollSetupRepository.Hash(JsonSerializer.Serialize(new{saved.Json,saved.Enabled})),new(employeeId,saved.Json,saved.Enabled));
    }

    private async Task<HistoricalProfessionalTaxEvidence> PrepareHistoricalConditionalPtAsync(MySqlConnection db,MySqlTransaction tx,
        PayRunSourceEmployee employee,string setupJson,string period,HistoricalConditionalProfessionalTax review)
    {
        await ExcelPayrollSetupRepository.RequireHistoricalPtIdentityAsync(db,tx,employee.ClientId,review.ReviewedRequest,review.RowId,employee.Id,review.SourceHash);
        if(employee.LocationParts is not null || ReadSeededPayrollDeductions(employee).Any(row=>row.Amount>0&&(ReviewedPeriodProfessionalTax.IsPt(row.Code,row.StatutoryType)||row.Code=="PT_LWF_WC")))
            throw new InvalidOperationException("Location allocations or positive/mixed seeded PT cannot be hidden by a composed closed-period zero review.");
        var setup=ReadPayrollSetup(setupJson);var structure=setup.Structures.SingleOrDefault(s=>s.Id==employee.SalaryStructureId);
        if(structure is null||structure.Lines.Any(line=>setup.Components.Any(c=>c.Id==line.ComponentId&&(ReviewedPeriodProfessionalTax.IsPt(c.Code,c.StatutoryType)||c.Code=="PT_LWF_WC"))))
            throw new InvalidOperationException("The proposed historical profile must preserve the source outputs without an explicit PT/mixed-PT template line.");
        var source=await ReadPtSourceAsync(db,tx,employee.ClientId,review.ReviewedRequest.Setup.BatchId,false);
        var prior=await ReadHistoricalPtPreviousAsync(db,tx,employee.ClientId,employee.Id,period,source.Batch.Id,review.RowId,source.Hash,false);
        var basis=PtBasis(employee,setupJson,period);
        var stableId=ExcelPayrollSetupRepository.Hash(employee.ClientId+"|"+employee.Id+"|"+period+"|"+review.SourceHash+"|"+basis)[..32];
        var control=new ReviewedProfessionalTaxControl{ClientId=employee.ClientId,EmployeeId=employee.Id,PayPeriod=period,
            Mode=ReviewedPeriodProfessionalTax.Reviewed,Amount=0,SourceBatchId=source.Batch.Id,SourceRowId=review.RowId,SourceHash=source.Hash,NativeBasisHash=basis,
            Reason=review.Reason,ReviewedById=review.ActorId,ReviewedBy=review.ActorEmail,ReviewedAtUtc=DateTime.UnixEpoch,RequestId=stableId};
        ReviewedPeriodProfessionalTax.Resolve(control,employee.ClientId,employee.Id,period,source.Hash,basis,false,0,false,false,0);
        employee.ReviewedProfessionalTax=control;
        return new(employee.Id,review.RowId,0,review.SourceHash,source.Hash,basis,prior.Hash,true);
    }

    internal async Task<HistoricalPtSaved> SaveHistoricalProfessionalTaxAsync(MySqlConnection db,MySqlTransaction tx,int clientId,
        HistoricalPeriodSalaryRequest request,HistoricalProfessionalTaxEvidence reviewed,string requestId,AuthUser actor,
        HistoricalSourceIdentityEvidence? identity,HistoricalSimulationContext? context=null)
    {
        if(request.PeriodProfessionalTaxReview is not {Reviewed:true} pt || reviewed.Amount!=0 || !reviewed.ProposedBasisWhatIf
            || reviewed.SourceHash!=pt.SourceHash || !request.Assignments.Any(a=>a.EmployeeId==reviewed.EmployeeId&&a.RowId==reviewed.RowId))
            throw new InvalidOperationException("A complete proposed-basis source PT review is required before the atomic historical save.");
        var period=request.Setup.EffectiveFrom[..7];
        var shared=context is null?null:UseHistoricalSimulationContext(db,tx,new(){Mode="Employee",ClientId=clientId,EmployeeId=reviewed.EmployeeId,PayPeriod=period},null,context,"ActualControlBasis");
        var employee=shared is null?await db.QuerySingleAsync<PayRunSourceEmployee>("SELECT e.*,COALESCE(w.State,'') WorkState FROM employees e LEFT JOIN worklocations w ON w.Id=e.WorkLocationId WHERE e.Id=@Id AND e.ClientId=@ClientId",
            new{Id=reviewed.EmployeeId,ClientId=clientId},tx):shared.Employees[reviewed.EmployeeId];
        if(shared is null)await LoadEmployeeTablesAsync(db,tx,[employee],period,loadReviewedProfessionalTax:false);
        var setup=shared?.SetupJson??await PayrollDataTableStore.GetSetupJsonAsync(db,tx);
        var conditional=new HistoricalConditionalProfessionalTax(reviewed.RowId,reviewed.SourceHash,pt.Reason.Trim(),actor.Id,actor.Email,request);
        var actual=await PrepareHistoricalConditionalPtAsync(db,tx,employee,setup,period,conditional);
        if(actual.NativeBasisHash!=reviewed.NativeBasisHash||actual.PtSourceHash!=reviewed.PtSourceHash||actual.PreviousControlHash!=reviewed.PreviousControlHash)
            throw new InvalidOperationException("The actual saved historical PT basis differs from its proposed review; the complete transaction must roll back.");
        await ExcelPayrollSetupRepository.RequireHistoricalPtIdentityAsync(db,tx,clientId,request,reviewed.RowId,reviewed.EmployeeId,reviewed.SourceHash,identity);
        var prior=await ReadHistoricalPtPreviousAsync(db,tx,clientId,employee.Id,employee.ReviewedProfessionalTax!.PayPeriod,request.Setup.BatchId,reviewed.RowId,actual.PtSourceHash,true);
        var control=employee.ReviewedProfessionalTax;control.ReviewedAtUtc=DateTime.UtcNow;control.RequestId=requestId;control.HistoricalReceiptId=requestId;
        ReviewedPeriodProfessionalTax.RequireControl(control,clientId,employee.Id,control.PayPeriod);
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,CAST(@Json AS JSON)) ON DUPLICATE KEY UPDATE IsEnabled=TRUE,SettingsJson=VALUES(SettingsJson)",
            new{ClientId=clientId,Code=ReviewedPeriodProfessionalTax.Key(employee.Id,control.PayPeriod),Json=ProtectPt(control,"PeriodControl")},tx);
        return new(actual with{ProposedBasisWhatIf=false},prior.Previous);
    }

    private async Task RequireHistoricalPtReceiptAsync(MySqlConnection db,MySqlTransaction tx,ReviewedProfessionalTaxControl control,
        PayRunSourceEmployee employee,string setupJson,string period)
    {
        if(!Guid.TryParseExact(control.HistoricalReceiptId,"N",out _))throw new InvalidOperationException("Historical period PT receipt reference is invalid.");
        var text=await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code AND IsEnabled=TRUE",
            new{control.ClientId,Code="closed_salary_receipt:"+control.ClientId+":"+control.HistoricalReceiptId},tx);
        var envelope=text is null?null:JsonNode.Parse(text);
        var plaintext=envelope?["purpose"]?.ToString()=="ClosedHistoricalSalaryReceipt"?PtProtector.UnprotectHistoricalSalary(envelope["payload"]!.ToString()):null;
        var saved=plaintext is null?null:JsonNode.Parse(plaintext);
        var receipt=saved?["receipt"];var request=saved?["reviewedRequest"]?.Deserialize<HistoricalPeriodSalaryRequest>(ReviewedPeriodProfessionalTax.Json);
        var rows=receipt?["rows"]?.AsArray();var tax=receipt?["professionalTax"]?.AsArray();
        var entry=tax?.SingleOrDefault(e=>e?["employeeId"]?.GetValue<int>()==employee.Id&&e?["rowId"]?.ToString()==control.SourceRowId);
        if(saved?["actorId"]?.GetValue<int>()!=control.ReviewedById||receipt?["clientId"]?.GetValue<int>()!=control.ClientId
            ||receipt?["batchId"]?.ToString()!=control.SourceBatchId||receipt?["payPeriod"]?.ToString()!=period
            ||receipt?["requestId"]?.ToString()!=control.HistoricalReceiptId||request is null||entry is null
            ||entry["amount"]?.GetValue<decimal>()!=0||entry["proposedBasisWhatIf"]?.GetValue<bool>()!=false
            ||entry["nativeBasisHash"]?.ToString()!=control.NativeBasisHash||entry["ptSourceHash"]?.ToString()!=control.SourceHash
            ||saved["sourceHash"]?.ToString()!=entry["sourceHash"]?.ToString()||PtBasis(employee,setupJson,period)!=control.NativeBasisHash
            ||rows is null||rows.Count(row=>row?["employeeId"]?.GetValue<int>()==employee.Id&&row?["rowId"]?.ToString()==control.SourceRowId&&row?["templateId"]?.ToString()==employee.SalaryStructureId)!=1)
            throw new InvalidOperationException("The historical PT control lacks its exact authenticated salary/source/actor/basis receipt; no code-only identity fallback is accepted.");
        var applied=rows.Single(row=>row?["employeeId"]?.GetValue<int>()==employee.Id&&row?["rowId"]?.ToString()==control.SourceRowId)!;
        var start=DateTime.ParseExact(period+"-01","yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);
        if(!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM employee_it0008_basic_pay WHERE Id=@Id AND ClientId=@ClientId AND EmployeeId=@EmployeeId AND ActionType='Closed Excel Salary' AND Status='Historical' AND EffectiveFrom=@Start AND EffectiveTo=@End AND SalaryStructureId=@Template)",
            new{Id=applied["historyId"]!.GetValue<long>(),control.ClientId,EmployeeId=employee.Id,Start=start,End=start.AddMonths(1).AddDays(-1),Template=employee.SalaryStructureId},tx))
            throw new InvalidOperationException("The historical PT receipt no longer resolves its exact closed salary history.");
        var identity=receipt?["sourceIdentities"]?.AsArray().SingleOrDefault(e=>e?["employeeId"]?.GetValue<int>()==employee.Id&&e?["rowId"]?.ToString()==control.SourceRowId)?.Deserialize<HistoricalSourceIdentityEvidence>(ReviewedPeriodProfessionalTax.Json);
        await ExcelPayrollSetupRepository.RequireHistoricalPtIdentityAsync(db,tx,control.ClientId,request,control.SourceRowId,employee.Id,entry["sourceHash"]!.ToString(),identity);
    }
}

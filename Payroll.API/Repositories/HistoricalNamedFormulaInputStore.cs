using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

// A separate scope of the existing typed operand store. No location assignment,
// salary-map key, current Master edit or formula-amount override is manufactured.
internal static class HistoricalNamedFormulaInputStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static string Key(long historyId)=>"named-input-h:"+historyId.ToString(CultureInfo.InvariantCulture);
    internal static string ManifestHash(HistoricalNamedFormulaInputManifest manifest)=>
        ReviewedNamedFormulaInputs.Hash(manifest with{ManifestHash=""});

    internal static async Task<HistoricalNamedFormulaInputManifest> InsertAsync(MySqlConnection db,MySqlTransaction tx,
        IConfiguration configuration,NamedFormulaInputProfile profile,NamedFormulaInputContext context,List<NamedFormulaInputCell> cells,
        long historyId,int employeeId,ExcelPayslipBatch batch,ExcelPayslipCalculationSource source,ExcelPayslipRow row,
        string sourceHash,string requestId,int actorId,DateTime reviewedAtUtc)
    {
        if(tx.Connection!=db||historyId<=0||actorId<=0||!Guid.TryParseExact(requestId,"N",out _)||profile.ClientId!=batch.ClientId
            ||profile.TemplateId.Length==0||profile.DeclarationHash!=context.DeclarationHash)
            throw new InvalidOperationException("A historical operand manifest needs its exact caller transaction, new salary history, reviewer and protected declaration.");
        ReviewedNamedFormulaInputs.ValidateContext(context,[]);
        var history=await ReadHistoryAsync(db,tx,historyId);
        RequireHistory(history,batch.ClientId,employeeId,batch.Month,profile.TemplateId);
        var raw=await ReadSourceAsync(db,tx,batch.ClientId,batch.Id);
        if(SourceHash(raw)!=sourceHash||sourceHash!=ReviewedNamedFormulaInputs.Hash(new{Batch=batch,Source=source}))
            throw new InvalidOperationException("The exact historical operand source changed before its atomic save.");
        var manifest=new HistoricalNamedFormulaInputManifest(1,batch.ClientId,employeeId,batch.Month,historyId,profile.TemplateId,
            Date(history.EffectiveFrom),Date(history.EffectiveTo!.Value),history.AnnualCtc,history.SalaryJsonHash,batch.Id,row.Id,row.SourceRow,
            sourceHash,raw.BatchHash,raw.SourceHash,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source),profile.DeclarationHash,
            profile.Definitions,new(context.Values,StringComparer.Ordinal),cells,requestId,actorId,reviewedAtUtc,"");
        manifest=manifest with{ManifestHash=ManifestHash(manifest)};
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,CAST(@Json AS JSON))",
            new{ClientId=batch.ClientId,Code=Key(historyId),Json=NamedFormulaInputStore.Protect(manifest,configuration,"HistoricalAssignment")},tx);
        return manifest;
    }

    internal static async Task<NamedFormulaInputContext> LoadAsync(MySqlConnection db,MySqlTransaction tx,IConfiguration configuration,
        int clientId,int employeeId,string month,long historyId,string templateId,NamedFormulaInputProfile profile,string envelope)
    {
        if(tx.Connection!=db||profile.ClientId!=clientId||profile.TemplateId!=templateId)
            throw new InvalidOperationException("Historical named operands need the exact existing client/template/transaction.");
        var m=NamedFormulaInputStore.Read<HistoricalNamedFormulaInputManifest>(envelope,configuration,"HistoricalAssignment");
        if(m.Version!=1||m.ClientId!=clientId||m.EmployeeId!=employeeId||m.PayPeriod!=month||m.HistoryId!=historyId||m.TemplateId!=templateId
            ||m.ReviewerId<=0||!Guid.TryParseExact(m.RequestId,"N",out _)||m.ManifestHash!=ManifestHash(m)
            ||m.DeclarationHash!=profile.DeclarationHash||!Same(m.Definitions,profile.Definitions))
            throw new InvalidOperationException("Historical named operands are disabled, stale, tampered, foreign or missing their exact declaration/history scope.");
        var history=await ReadHistoryAsync(db,tx,historyId);RequireHistory(history,clientId,employeeId,month,templateId);
        if(Date(history.EffectiveFrom)!=m.EffectiveFrom||Date(history.EffectiveTo!.Value)!=m.EffectiveTo
            ||history.AnnualCtc!=m.AnnualSalaryTarget||history.SalaryJsonHash!=m.SalaryJsonHash)
            throw new InvalidOperationException("The authenticated historical salary window, target or component inputs changed.");
        var setup=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db,tx))!;
        var masters=setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToArray();
        var template=setup["salaryStructures"]!.AsArray().OfType<JsonObject>().SingleOrDefault(t=>t["id"]?.ToString()==templateId&&t["active"]?.ToString()=="true")
            ??throw new InvalidOperationException("The historical named-operand template is unavailable.");
        if(!(template["clientId"]?.ToString()??"").Split([';',',','|']).Any(value=>value.Split(':')[0].Trim()==clientId.ToString(CultureInfo.InvariantCulture))
            ||ExcelPayrollSetupRepository.LocationSettingsRulesHash(template,masters)!=profile.RulesHash)
            throw new InvalidOperationException("The managed historical template rules changed; no stale operand or salary fallback is permitted.");
        var raw=await ReadSourceAsync(db,tx,clientId,m.BatchId);
        var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(raw.Batch,Json)!;var source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(raw.Source,Json)!;
        if(batch.ClientId!=clientId||batch.Month!=month||batch.Id!=m.BatchId||SourceHash(raw)!=m.SourceHash||raw.BatchHash!=m.RawBatchHash||raw.SourceHash!=m.RawSourceHash)
            throw new InvalidOperationException("The protected historical operand source version changed; no contact snapshot, cached output or zero is substituted.");
        var row=batch.Rows.SingleOrDefault(r=>r.Id==m.RowId&&r.SourceRow==m.SourceRow)
            ??throw new InvalidOperationException("The exact historical operand source row is missing.");
        if(ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source)!=m.IdentityHash)
            throw new InvalidOperationException("The protected historical source identity changed.");
        var receiptText=await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code AND IsEnabled=TRUE",
            new{ClientId=clientId,Code="closed_salary_receipt:"+clientId+":"+m.RequestId},tx);
        var receiptEnvelope=receiptText is null?null:JsonNode.Parse(receiptText);
        var plain=receiptEnvelope?["purpose"]?.ToString()=="ClosedHistoricalSalaryReceipt"
            ?NamedFormulaInputStore.Protector(configuration).UnprotectHistoricalSalary(receiptEnvelope["payload"]!.ToString()):null;
        var stored=plain is null?null:JsonNode.Parse(plain);var receipt=stored?["receipt"];
        var request=stored?["reviewedRequest"]?.Deserialize<HistoricalPeriodSalaryRequest>(Json);
        var retained=receipt?["namedInputs"]?.AsArray().Where(n=>n?["historyId"]?.GetValue<long>()==historyId&&n?["employeeId"]?.GetValue<int>()==employeeId).ToArray();
        if(stored?["actorId"]?.GetValue<int>()!=m.ReviewerId||stored?["sourceHash"]?.ToString()!=m.SourceHash||receipt?["requestId"]?.ToString()!=m.RequestId
            ||receipt?["clientId"]?.GetValue<int>()!=clientId||receipt?["payPeriod"]?.ToString()!=month||receipt?["batchId"]?.ToString()!=m.BatchId
            ||request?.NamedInputReview is not{} review||review.TemplateId!=templateId||review.DeclarationHash!=profile.DeclarationHash
            ||!request.Assignments.Any(a=>a.EmployeeId==employeeId&&a.RowId==row.Id&&a.TemplateId==templateId)
            ||receipt?["rows"]?.AsArray().Count(n=>n?["historyId"]?.GetValue<long>()==historyId&&n?["employeeId"]?.GetValue<int>()==employeeId&&n?["rowId"]?.ToString()==row.Id&&n?["templateId"]?.ToString()==templateId)!=1
            ||retained?.Length!=1||!JsonNode.DeepEquals(retained[0],JsonSerializer.SerializeToNode(m,Json)))
            throw new InvalidOperationException("The historical typed operands lack their exact acknowledged source/history/reviewer receipt.");
        ExcelPayrollSetupRepository.ValidateHistoricalNamedInputReview(request);
        var identity=receipt?["sourceIdentities"]?.AsArray().SingleOrDefault(n=>n?["employeeId"]?.GetValue<int>()==employeeId&&n?["rowId"]?.ToString()==row.Id)?.Deserialize<HistoricalSourceIdentityEvidence>(Json);
        // Existing helper authenticates source/employee ownership only; it does
        // not infer a PT amount or depend on a PT control being present.
        await ExcelPayrollSetupRepository.RequireHistoricalPtIdentityAsync(db,tx,clientId,request,row.Id,employeeId,m.SourceHash,identity);
        var rebound=ReviewedNamedFormulaInputs.Bind(profile,review.Bindings,source,row,masters.SelectMany(master=>new[]{master["id"]!.ToString(),master["code"]!.ToString()}));
        if(!Same(rebound.Cells,m.Cells)||!Same(rebound.Context.Values,m.Values))
            throw new InvalidOperationException("A historical operand literal, header, precision or provenance changed.");
        rebound.Context.ManifestHash=m.ManifestHash;return rebound.Context;
    }

    private static bool Same<T>(T a,T b)=>JsonNode.DeepEquals(JsonSerializer.SerializeToNode(a,Json),JsonSerializer.SerializeToNode(b,Json));
    private static string Date(DateTime value)=>value.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private static async Task<History> ReadHistoryAsync(MySqlConnection db,MySqlTransaction tx,long id)=>
        await db.QuerySingleOrDefaultAsync<History>("SELECT Id,EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,SHA2(CAST(SalaryJson AS CHAR),256) SalaryJsonHash FROM employee_it0008_basic_pay WHERE Id=@Id",new{Id=id},tx)
        ??throw new InvalidOperationException("The exact historical operand salary history is missing.");
    private static void RequireHistory(History h,int client,int employee,string month,string template)
    {
        if(!DateTime.TryParseExact(month+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var start)
            ||h.ClientId!=client||h.EmployeeId!=employee||h.SalaryStructureId!=template||h.ActionType!="Closed Excel Salary"||h.Status!="Historical"
            ||h.EffectiveFrom!=start||h.EffectiveTo!=start.AddMonths(1).AddDays(-1)||h.AnnualCtc<=0)
            throw new InvalidOperationException("Named operands require one exact closed calendar-month salary history; current/future salary and other assignments are outside this scope.");
    }
    private static async Task<Raw> ReadSourceAsync(MySqlConnection db,MySqlTransaction tx,int client,string batch)=>
        await db.QuerySingleAsync<Raw>("SELECT CAST(b.batch_json AS CHAR) Batch,CAST(s.source_json AS CHAR) Source,SHA2(CAST(b.batch_json AS CHAR),256) BatchHash,SHA2(CAST(s.source_json AS CHAR),256) SourceHash FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@Batch",new{Client=client,Batch=batch},tx);
    private static string SourceHash(Raw raw)=>ReviewedNamedFormulaInputs.Hash(new{Batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(raw.Batch,Json),Source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(raw.Source,Json)});
    private sealed class Raw{public string Batch{get;set;}="";public string Source{get;set;}="";public string BatchHash{get;set;}="";public string SourceHash{get;set;}="";}
    private sealed class History{public long Id{get;set;}public int ClientId{get;set;}public int EmployeeId{get;set;}public string ActionType{get;set;}="";public DateTime EffectiveFrom{get;set;}public DateTime? EffectiveTo{get;set;}public string Status{get;set;}="";public string SalaryStructureId{get;set;}="";public decimal AnnualCtc{get;set;}public string SalaryJsonHash{get;set;}="";}
}

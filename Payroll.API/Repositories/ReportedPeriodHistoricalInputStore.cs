using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

// Origin-distinct protected closed-history operands. A formatter, source cache,
// normal Applied import or current Master salary cannot substitute for this.
internal static class ReportedPeriodHistoricalInputStore
{
    private const string Purpose="ClosedReportedSalaryAssignmentV2";
    private const string IndexPurpose="ClosedReportedSalaryMonthIndexV2";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static string Key(long historyId)=>"reported-input-h:"+historyId.ToString(CultureInfo.InvariantCulture);
    internal static string MonthKey(int employeeId,string month)=>"reported-input-e:"+employeeId.ToString(CultureInfo.InvariantCulture)+":"+month;
    internal sealed record MonthIndex(int Version,int ClientId,int EmployeeId,string PayPeriod,long HistoryId,string TemplateId,string AssignmentHash);
    internal static string AssignmentHash(ReportedPeriodHistoricalAssignmentV2 value)=>ReviewedNamedFormulaInputs.Hash(value with{AssignmentHash=""});
    private static string HashText(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal static async Task<ReportedPeriodHistoricalAssignmentV2> InsertAsync(MySqlConnection db,MySqlTransaction tx,IConfiguration configuration,
        ReportedPeriodInputProfileV2 profile,ReportedPeriodBoundRowV2 bound,ExcelPayrollSetupRepository.HistoricalPayableSeedSaved seed,
        long historyId,int employeeId,string requestId,int actorId,DateTime reviewedAtUtc)
    {
        ReportedPeriodInputV2Codec.RequireWriterCompatibility(configuration);
        if(tx.Connection!=db||tx.IsolationLevel!=System.Data.IsolationLevel.Serializable||historyId<=0||employeeId!=seed.Plan.EmployeeId
            ||profile.ClientId!=seed.Plan.ClientId||profile.TemplateId!=seed.Plan.TemplateId||bound.ProofHash!=seed.Plan.BoundInputs.ProofHash
            ||actorId<=0||reviewedAtUtc.Kind!=DateTimeKind.Utc||!Guid.TryParseExact(requestId,"N",out _))
            throw new InvalidOperationException("Closed reported assignment requires the real atomic history, saved monthly seed, actor and acknowledged request.");
        var h=await db.QuerySingleAsync<History>(@"SELECT Id,EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,
CAST(SalaryJson AS CHAR) SalaryJson,SHA2(CAST(SalaryJson AS CHAR),256) SalaryHash FROM employee_it0008_basic_pay WHERE Id=@Id",new{Id=historyId},tx);
        RequireHistory(h,profile.ClientId,employeeId,bound.PayPeriod,profile.TemplateId);
        var raw=await db.QuerySingleAsync<Raw>(@"SELECT CAST(b.batch_json AS CHAR) Batch,CAST(s.source_json AS CHAR) Source,
SHA2(CAST(b.batch_json AS CHAR),256) BatchHash,SHA2(CAST(s.source_json AS CHAR),256) SourceHash
FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@Batch",new{Client=bound.ClientId,Batch=bound.BatchId},tx);
        if(raw.BatchHash!=bound.RawBatchHash||raw.SourceHash!=bound.RawSourceHash||Canonical(raw)!=bound.SourceHash)
            throw new InvalidOperationException("Source changed before the atomic v2 assignment insert.");
        var manifest=new ReportedPeriodInputManifestV2(2,bound.ClientId,employeeId,bound.PayPeriod,historyId,profile.TemplateId,
            seed.Plan.VerifiedIdentityOwnershipHash,seed.NativeInput.SnapshotHash,requestId,actorId,reviewedAtUtc,bound,"");
        manifest=manifest with{ManifestHash=ReportedPeriodInputV2Codec.ManifestHash(manifest)};
        // Validate the declaration and manifest together using its existing
        // independent v2 protector, before protecting the actual history tuple.
        ReportedPeriodInputV2Codec.ValidateManifestForProfile(manifest,profile);
        var assignment=new ReportedPeriodHistoricalAssignmentV2(2,manifest,Date(h.EffectiveFrom),Date(h.EffectiveTo!.Value),h.AnnualCtc,h.SalaryHash,
            seed.MonthlyWitness.Id,seed.Plan.Group.Id,ReviewedNamedFormulaInputs.Hash(seed.Plan.Group),seed.ProvenanceHash,"");
        assignment=assignment with{AssignmentHash=AssignmentHash(assignment)};
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@Client,@Code,TRUE,CAST(@Json AS JSON))",
            new{Client=bound.ClientId,Code=Key(historyId),Json=NamedFormulaInputStore.Protect(assignment,configuration,Purpose)},tx);
        var index=new MonthIndex(2,bound.ClientId,employeeId,bound.PayPeriod,historyId,profile.TemplateId,assignment.AssignmentHash);
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@Client,@Code,TRUE,CAST(@Json AS JSON))",
            new{Client=bound.ClientId,Code=MonthKey(employeeId,bound.PayPeriod),Json=NamedFormulaInputStore.Protect(index,configuration,IndexPurpose)},tx);
        return assignment;
    }

    internal static async Task<Dictionary<int,MonthIndex>> ReadMonthIndicesAsync(MySqlConnection db,MySqlTransaction tx,IConfiguration configuration,int client,string month,int[] employeeIds)
    {
        var keys=employeeIds.Distinct().Select(id=>MonthKey(id,month)).ToArray();var result=new Dictionary<int,MonthIndex>();
        if(keys.Length==0)return result;
        var values=await db.QueryAsync<ReportedPeriodInputProfileStore.Setting>("SELECT ModuleCode Code,IsEnabled Enabled,CAST(SettingsJson AS CHAR) Json FROM modulesettings WHERE client_id=@Client AND ModuleCode IN @Keys",new{Client=client,Keys=keys},tx);
        foreach(var setting in values)
        {
            if(!setting.Enabled)throw new InvalidOperationException("The reported employee/month assignment index is disabled; legacy fallback is not accepted.");
            var index=NamedFormulaInputStore.Read<MonthIndex>(setting.Json,configuration,IndexPurpose);
            if(index.Version!=2||index.ClientId!=client||index.PayPeriod!=month||!employeeIds.Contains(index.EmployeeId)||index.HistoryId<=0
                ||string.IsNullOrWhiteSpace(index.TemplateId)||!System.Text.RegularExpressions.Regex.IsMatch(index.AssignmentHash??"",@"\A[a-f0-9]{64}\z")||setting.Code!=MonthKey(index.EmployeeId,month))
                throw new InvalidOperationException("The protected reported employee/month index is foreign or stale.");
            result.Add(index.EmployeeId,index);
        }
        return result;
    }

    internal sealed class ReadScope
    {
        internal required MySqlConnection Db {get;init;}
        internal required MySqlTransaction Tx {get;init;}
        internal required int ClientId {get;init;}
        internal required string Month {get;init;}
        internal Dictionary<long,History> Histories {get;init;}=[];
        internal Dictionary<int,ExcelPayrollSetupRepository.HistoricalPayableSeedMonthly[]> Monthly {get;init;}=[];
        internal Dictionary<int,ExcelPayrollSetupRepository.HistoricalPayableSeedGroup[]> Groups {get;init;}=[];
        internal Dictionary<string,Raw> Sources {get;}=new(StringComparer.Ordinal);
        internal Dictionary<string,JsonNode> Receipts {get;}=new(StringComparer.Ordinal);
        internal HashSet<string> OriginalOwnershipVerified {get;}=new(StringComparer.Ordinal);
        internal Dictionary<string,IReadOnlyDictionary<string,ExcelPayrollSetupRepository.HistoricalVerifiedSourceIdentity>> ReviewedOwners {get;}=new(StringComparer.Ordinal);
        internal IReadOnlyList<ExcelPayrollSetupRepository.Identity>? Identities {get;set;}
        internal required JsonObject Setup {get;init;}
    }

    // Batch the cheap native rows and share source/receipt/global-ownership
    // reads only inside this one caller-owned transaction. No cross-run cache.
    internal static async Task<ReadScope> CreateReadScopeAsync(MySqlConnection db,MySqlTransaction tx,int clientId,string month,int[] employeeIds,long[] historyIds)
    {
        if(tx.Connection!=db||employeeIds.Length is <1 or >1000||historyIds.Any(id=>id<=0))throw new InvalidOperationException("Bounded exact v2 Employee reader transaction required.");
        await ExcelPayrollSetupRepository.RequireHistoricalSeedMembershipSchemaAsync(db,tx);
        var histories=await db.QueryAsync<History>(@"SELECT Id,EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,
CAST(SalaryJson AS CHAR) SalaryJson,SHA2(CAST(SalaryJson AS CHAR),256) SalaryHash FROM employee_it0008_basic_pay WHERE ClientId=@Client AND Id IN @Ids",new{Client=clientId,Ids=historyIds},tx);
        var monthly=(await db.QueryAsync<ExcelPayrollSetupRepository.HistoricalPayableSeedMonthly>(@"SELECT id Id,client_id ClientId,employee_id EmployeeId,attendance_month Month,
working_days WorkingDays,present_days PresentDays,payable_days PayableDays,lop_days LopDays,source_type SourceType,COALESCE(remarks,'') Remarks
FROM employee_monthly_attendance WHERE client_id=@Client AND attendance_month=@Month AND employee_id IN @Ids",new{Client=clientId,Month=month,Ids=employeeIds},tx)).ToArray();
        var groups=(await db.QueryAsync<ExcelPayrollSetupRepository.HistoricalPayableSeedGroup>(@"SELECT g.id Id,g.client_id ClientId,m.employee_id EmployeeId,g.attendance_cycle_start_day StartDay,
g.attendance_cycle_end_day EndDay,g.attendance_mode Mode,COALESCE(g.policy_batch_id,'') PolicyBatchId,COALESCE(g.work_week,'') WorkWeek
FROM attendance_groups g JOIN attendance_group_employees m ON m.attendance_group_id=g.id WHERE g.client_id=@Client AND g.is_active=TRUE AND m.employee_id IN @Ids",new{Client=clientId,Ids=employeeIds},tx)).ToArray();
        return new(){Db=db,Tx=tx,ClientId=clientId,Month=month,Histories=histories.ToDictionary(h=>h.Id),Monthly=monthly.GroupBy(m=>m.EmployeeId).ToDictionary(g=>g.Key,g=>g.ToArray()),Groups=groups.GroupBy(g=>g.EmployeeId).ToDictionary(g=>g.Key,g=>g.ToArray()),Setup=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db,tx))!.AsObject()};
    }

    internal static async Task<NamedFormulaInputContext> LoadAsync(ReadScope cache,IConfiguration configuration,int employeeId,long historyId,
        string templateId,ReportedPeriodInputProfileV2 profile,string envelope,MonthIndex index)
    {
        if(cache.Tx.Connection!=cache.Db||profile.ClientId!=cache.ClientId||profile.TemplateId!=templateId)
            throw new InvalidOperationException("Foreign v2 native reader scope.");
        var a=NamedFormulaInputStore.Read<ReportedPeriodHistoricalAssignmentV2>(envelope,configuration,Purpose);var m=a.Inputs;
        ReportedPeriodInputV2Codec.ValidateManifestForProfile(m,profile);
        if(a.Version!=2||m is null||a.AssignmentHash!=AssignmentHash(a)||m.ClientId!=cache.ClientId||m.EmployeeId!=employeeId
            ||m.PayPeriod!=cache.Month||m.HistoryId!=historyId||m.TemplateId!=templateId||m.ManifestHash!=ReportedPeriodInputV2Codec.ManifestHash(m))
            throw new InvalidOperationException("Reported history assignment is stale, foreign, missing or tampered; no salary fallback is permitted.");
        if(index.EmployeeId!=employeeId||index.ClientId!=cache.ClientId||index.PayPeriod!=cache.Month||index.HistoryId!=historyId||index.TemplateId!=templateId||index.AssignmentHash!=a.AssignmentHash)
            throw new InvalidOperationException("The real effective history no longer matches its protected employee/month assignment index.");
        if(!cache.Histories.TryGetValue(historyId,out var h))throw new InvalidOperationException("The exact reported closed history is missing.");
        RequireHistory(h,cache.ClientId,employeeId,cache.Month,templateId);
        if(a.EffectiveFrom!=Date(h.EffectiveFrom)||a.EffectiveTo!=Date(h.EffectiveTo!.Value)||a.AnnualSalaryTarget!=h.AnnualCtc||a.SalaryInputsHash!=h.SalaryHash)
            throw new InvalidOperationException("The protected reported history dates, retained annual target or empty salary map changed.");
        var masters=cache.Setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToArray();
        var template=cache.Setup["salaryStructures"]!.AsArray().OfType<JsonObject>().SingleOrDefault(t=>t["id"]?.ToString()==templateId&&t["active"]?.ToString()=="true")
            ??throw new InvalidOperationException("The managed reported template is unavailable.");
        if(!(template["clientId"]?.ToString()??"").Split([';',',','|']).Any(v=>v.Split(':')[0].Trim()==cache.ClientId.ToString(CultureInfo.InvariantCulture))
            ||ExcelPayrollSetupRepository.LocationSettingsRulesHash(template,masters)!=profile.RulesHash)
            throw new InvalidOperationException("The managed reported template client or monetary rules changed.");
        if(!cache.Sources.TryGetValue(m.BoundInputs.BatchId,out var raw))
        {
            raw=await cache.Db.QuerySingleAsync<Raw>(@"SELECT CAST(b.batch_json AS CHAR) Batch,CAST(s.source_json AS CHAR) Source,SHA2(CAST(b.batch_json AS CHAR),256) BatchHash,SHA2(CAST(s.source_json AS CHAR),256) SourceHash
FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@Batch",new{Client=cache.ClientId,Batch=m.BoundInputs.BatchId},cache.Tx);cache.Sources.Add(m.BoundInputs.BatchId,raw);
        }
        var batch=raw.ParsedBatch??=JsonSerializer.Deserialize<ExcelPayslipBatch>(raw.Batch,Json)!;var source=raw.ParsedSource??=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(raw.Source,Json)!;
        if(batch.ClientId!=cache.ClientId||batch.Month!=cache.Month||batch.Id!=m.BoundInputs.BatchId||Canonical(raw)!=m.BoundInputs.SourceHash
            ||raw.BatchHash!=m.BoundInputs.RawBatchHash||raw.SourceHash!=m.BoundInputs.RawSourceHash)
            throw new InvalidOperationException("The exact protected reported source version changed.");
        var row=batch.Rows.Single(r=>r.Id==m.BoundInputs.RowId&&r.SourceRow==m.BoundInputs.SourceRow);
        if(ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source)!=m.BoundInputs.SourceIdentityHash)throw new InvalidOperationException("Reported source identity changed.");
        if(!cache.Receipts.TryGetValue(m.RequestId,out var stored))
        {
            var text=await cache.Db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@Client AND ModuleCode=@Code AND IsEnabled=TRUE",new{Client=cache.ClientId,Code="closed_salary_receipt:"+cache.ClientId+":"+m.RequestId},cache.Tx);
            var outer=text is null?null:JsonNode.Parse(text);
            if(outer?["purpose"]?.ToString()!="ClosedHistoricalSalaryReceipt")throw new InvalidOperationException("The real protected closed salary receipt is missing.");
            var plain=NamedFormulaInputStore.Protector(configuration).UnprotectHistoricalSalary(outer!["payload"]!.ToString())??throw new InvalidOperationException("The closed reported receipt cannot be authenticated.");
            stored=JsonNode.Parse(plain)!;cache.Receipts.Add(m.RequestId,stored);
        }
        var receipt=stored["receipt"];var request=stored["reviewedRequest"]?.Deserialize<HistoricalPeriodSalaryRequest>(Json)
            ??throw new InvalidOperationException("The closed reported receipt lacks its exact reviewed request.");
        ExcelPayrollSetupRepository.ValidateReportedHistoricalSalaryRequest(request);
        var retained=receipt?["reportedInputs"]?.AsArray().Where(n=>n?["inputs"]?["historyId"]?.GetValue<long>()==historyId&&n?["inputs"]?["employeeId"]?.GetValue<int>()==employeeId).ToArray();
        if(stored["actorId"]?.GetValue<int>()!=m.ReviewerId||stored["sourceHash"]?.ToString()!=m.BoundInputs.SourceHash
            ||receipt?["requestId"]?.ToString()!=m.RequestId||receipt?["clientId"]?.GetValue<int>()!=cache.ClientId||receipt?["payPeriod"]?.ToString()!=cache.Month||receipt?["batchId"]?.ToString()!=batch.Id
            ||request.Setup.BatchId!=batch.Id||!request.Assignments.Any(s=>s.EmployeeId==employeeId&&s.RowId==row.Id&&s.TemplateId==templateId)
            ||receipt?["rows"]?.AsArray().Count(n=>n?["historyId"]?.GetValue<long>()==historyId&&n?["employeeId"]?.GetValue<int>()==employeeId&&n?["rowId"]?.ToString()==row.Id&&n?["templateId"]?.ToString()==templateId)!=1
            ||retained?.Length!=1||!JsonNode.DeepEquals(retained[0],JsonSerializer.SerializeToNode(a,Json)))
            throw new InvalidOperationException("Reported operands do not belong to this exact acknowledged actor/source/history/closed receipt.");
        cache.Identities??=await ExcelPayrollSetupRepository.ReadNamedSourceSetupIdentitiesAsync(cache.Db,cache.Tx,cache.ClientId,cache.Month);
        if(!cache.ReviewedOwners.TryGetValue(m.RequestId,out var owners))
        {
            owners=await ExcelPayrollSetupRepository.ReadReportedReviewedIdentitiesAsync(cache.Db,cache.Tx,request,batch,source,cache.Identities,m.BoundInputs.SourceHash);cache.ReviewedOwners.Add(m.RequestId,owners);
        }
        var identity=cache.Identities.Single(i=>i.Id==employeeId&&i.ClientId==cache.ClientId);
        var ownership=ExcelPayrollSetupRepository.ReportedIdentityOwnershipHash(batch,source,row,identity,cache.Identities,owners.GetValueOrDefault(row.Id));
        if(ownership!=m.NativeIdentityOwnershipHash)throw new InvalidOperationException("The protected reported government/bank ownership changed.");
        var reviewedIdentity=receipt?["sourceIdentities"]?.AsArray().SingleOrDefault(n=>n?["employeeId"]?.GetValue<int>()==employeeId&&n?["rowId"]?.ToString()==row.Id);
        if(!JsonNode.DeepEquals(reviewedIdentity,owners.GetValueOrDefault(row.Id) is{} verified?JsonSerializer.SerializeToNode(verified.Evidence,Json):null))
            throw new InvalidOperationException("The protected reviewed bank identity evidence changed.");
        var original=source.WorkbookFiles.Single(f=>f.WorkbookKey==source.WorkbookKey);
        if(!cache.OriginalOwnershipVerified.Contains(batch.Id))
        {
            var originals=await ExcelPayslipRepository.ReadLinkedOriginalsAsync(cache.Db,cache.Tx,cache.ClientId,batch.Id);
            if(originals.Count(o=>o.Id==original.OriginalId&&o.Sha256.Equals(original.Sha256,StringComparison.OrdinalIgnoreCase))!=1)throw new InvalidOperationException("The primary original workbook is no longer owned by the exact source.");
            cache.OriginalOwnershipVerified.Add(batch.Id);
        }
        var q=request.Setup;var cycle=PayRunRepository.NativeAttendanceCycle(cache.Month,q.AttendanceCycleStartDay,q.AttendanceCycleEndDay);
        var bound=ReportedPeriodInputProof.Bind(profile,q.ReportedPeriodInputs!,batch,source,row,new(cache.ClientId,batch.Id,cache.Month,m.BoundInputs.SourceHash,raw.BatchHash,raw.SourceHash,original.Sha256.ToLowerInvariant(),cycle.Start,cycle.End,batch.Rows.ToDictionary(r=>r.Id,r=>ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,r,source))),masters.SelectMany(c=>new[]{c["id"]!.ToString(),c["code"]!.ToString()}));
        if(bound.ProofHash!=m.BoundInputs.ProofHash||ReviewedNamedFormulaInputs.Hash(bound)!=ReviewedNamedFormulaInputs.Hash(m.BoundInputs))throw new InvalidOperationException("A reported value, exact original formula, precision, header or source boundary changed.");
        var closure=ReportedPeriodInputProof.CloseFinancialGraph(q.ReportedPeriodInputs!,batch,source,row,bound);
        if(closure.Cells.Any(c=>c.SavedAmount<0||string.IsNullOrWhiteSpace(c.OriginalFormula)&&c.SavedAmount!=0))throw new InvalidOperationException("Signed or nonzero period cash is outside this bounded reported assignment.");
        var monthly=cache.Monthly.GetValueOrDefault(employeeId)??[];var groups=cache.Groups.GetValueOrDefault(employeeId)??[];
        if(monthly.Length!=1||groups.Length!=1)throw new InvalidOperationException("One real monthly row and one existing active group must own the reported snapshot.");
        var native=ExcelPayrollSetupRepository.AuthenticClosedReportedSeedNative(monthly[0],groups[0],bound,ownership,m.RequestId);
        if(monthly[0].Id!=a.MonthlyAttendanceId||groups[0].Id!=a.ExistingAttendanceGroupId||ReviewedNamedFormulaInputs.Hash(groups[0])!=a.GroupStateHash
            ||HashText(monthly[0].Remarks)!=a.ProvenanceHash||native.SnapshotHash!=m.NativeAttendanceSnapshotHash)
            throw new InvalidOperationException("The actual saved monthly row, existing group or protected native snapshot changed.");
        // Validate the exact manifest/profile pair using readers that stay usable
        // after writer flags are turned off. Never re-encrypt as a read shortcut.
        ReportedPeriodInputProof.ValidateProfile(profile);ReportedPeriodInputProof.ValidateBound(m.BoundInputs);
        if(profile.DeclarationHash!=bound.DeclarationHash||profile.TemplateId!=m.TemplateId)throw new InvalidOperationException("The exact reported declaration changed.");
        return ReportedPeriodCalculationContext.FromBound(profile,bound,m.ManifestHash,native);
    }

    private static string Canonical(Raw raw)=>ReportedPeriodInputProof.SourceHash(JsonSerializer.Deserialize<ExcelPayslipBatch>(raw.Batch,Json)!,JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(raw.Source,Json)!);
    private static string Date(DateTime value)=>value.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private static void RequireHistory(History h,int client,int employee,string month,string template)
    {
        var from=DateTime.ParseExact(month+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture);
        if(h.ClientId!=client||h.EmployeeId!=employee||h.ActionType!="Closed Excel Salary"||h.Status!="Historical"||h.SalaryStructureId!=template
            ||h.EffectiveFrom!=from||h.EffectiveTo!=from.AddMonths(1).AddDays(-1)||h.AnnualCtc<=0||JsonNode.Parse(h.SalaryJson) is not JsonObject map||map.Count!=0)
            throw new InvalidOperationException("Reported operands require their exact closed-month history, retained annual target and empty physical-input map; current/future salary is not a fallback.");
    }
    internal sealed class History{public long Id{get;set;}public int EmployeeId{get;set;}public int ClientId{get;set;}public string ActionType{get;set;}="";public DateTime EffectiveFrom{get;set;}public DateTime? EffectiveTo{get;set;}public string Status{get;set;}="";public string SalaryStructureId{get;set;}="";public decimal AnnualCtc{get;set;}public string SalaryJson{get;set;}="";public string SalaryHash{get;set;}="";}
    internal sealed class Raw{public string Batch{get;set;}="";public string Source{get;set;}="";public string BatchHash{get;set;}="";public string SourceHash{get;set;}="";internal ExcelPayslipBatch? ParsedBatch{get;set;}internal ExcelPayslipCalculationSource? ParsedSource{get;set;}}
}

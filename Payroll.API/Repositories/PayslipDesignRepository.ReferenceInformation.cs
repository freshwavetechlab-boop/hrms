using System.Data;
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
    private const int ReferenceRosterLimit=5000;
    private const int ReferenceMetadataLimit=32*1024*1024;
    private const string ReferencePurpose="PayrollReferenceInformation";
    private static string ReferenceManifestCode(int runId)=>$"prfi:run:{runId}";
    private static string ReferenceReceiptCode(string requestId)=>$"prfi:req:{requestId}";
    private static string ReferenceArchiveCode(int runId,string manifestHash)=>$"prfi:old:{runId}:{manifestHash}";
    private sealed record ReferenceProtectedEnvelope(int Version,string Purpose,string Value);
    private sealed record ReferenceReviewToken(int Version,string Kind,int ActorId,PayrollReferenceInformationReviewRequest Request,
        string PlanHash,string StateWitnessHash,string ReviewStateHash,DateTime ExpiresAtUtc,string? PreviousManifestHash);
    private sealed record ReferenceFrozenEmployee(PayrollReferenceBinding Binding,PayrollReferenceSnapshot Snapshot);
    private sealed record ReferenceManifest(int Version,string Kind,int ActorId,PayrollReferenceInformationReviewRequest Request,
        string PlanHash,string StateWitnessHash,string ProfileHash,string TemplateHash,string SourceHash,string SourceDataHash,string HistoricalReceiptHash,
        string HistoryHash,string NativeRosterHash,string RunOwnershipHash,string Label,PayrollReferenceDeclaration Declaration,
        IReadOnlyList<ReferenceFrozenEmployee> Employees,string RequestId,DateTime AppliedAtUtc);
    private sealed record ReferenceStoredReceipt(int Version,string Kind,int ActorId,string CommandHash,
        string ManifestCode,string ManifestHash,PayrollReferenceInformationReceipt Receipt);
    private sealed record ReferenceManifestArchive(int Version,string Kind,int ActorId,int ClientId,int RunId,string ManifestHash,
        string ReplacementRequestId,DateTime ArchivedAtUtc,ReferenceManifest Manifest);
    private sealed record ReferencePlan(ReferenceManifest Manifest,string ReviewStateHash,
        IReadOnlyList<PayrollReferenceInformationReviewRow> Rows,IReadOnlyList<int> EmployeeIds);
    private sealed record ReferenceNativeRun(int Id,int ClientId,string PayPeriod,string RunType,string Status,int TotalWorkingDays,string RequestJson);
    private sealed class ReferenceNativeRow
    {
        public long Id{get;set;} public int PayRunId{get;set;} public int EmployeeId{get;set;} public int ClientId{get;set;}
        public decimal? PresentDays{get;set;} public decimal PayableDays{get;set;} public decimal MonthlyGross{get;set;}
        public decimal GrossPay{get;set;} public decimal StatutoryDeductions{get;set;} public decimal OneTimeEarnings{get;set;}
        public decimal OneTimeDeductions{get;set;} public decimal ManualTds{get;set;} public decimal NetPay{get;set;}
        public string DetailsJson{get;set;}=""; public bool IsSkipped{get;set;} public string PaymentStatus{get;set;}="";
        public DateTime? PaymentDate{get;set;}
    }
    private sealed class ReferenceStoredHistory
    {
        public long Id{get;set;} public int EmployeeId{get;set;} public int ClientId{get;set;}
        public string ActionType{get;set;}=""; public DateTime EffectiveFrom{get;set;} public DateTime? EffectiveTo{get;set;}
        public string Status{get;set;}=""; public string SalaryStructureId{get;set;}=""; public decimal AnnualCtc{get;set;}
        public string SalaryJsonHash{get;set;}="";
        internal PayrollReferenceHistory Evidence()=>new(Id,ClientId,EmployeeId,SalaryStructureId,ActionType,Status,
            DateOnly.FromDateTime(EffectiveFrom),EffectiveTo.HasValue?DateOnly.FromDateTime(EffectiveTo.Value):DateOnly.MaxValue,
            AnnualCtc,PayrollReferenceInformation.Hash(this));
    }

    private void RequireReferenceWriter(AuthUser actor,int clientId)
    {
        if(!configuration.GetValue<bool>("PayrollReferenceInformation:Enabled"))throw new InvalidOperationException("Payroll reference Information configuration is not enabled.");
        if(!CanManageReferenceInformation(actor,clientId))throw new UnauthorizedAccessException("Manage settings and read payslips for this exact client to review payroll reference Information.");
    }
    private static string ReferenceHash(object value)=>PayrollReferenceInformation.Hash(value);
    private static bool ReferenceDigest(string? value)=>value is {Length:64}&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateReferenceRequest(PayrollReferenceInformationReviewRequest request)
    {
        if(request is null||request.ClientId<=0||request.RunId<=0||request.TemplateId<=0||request.SourceInfoColumn is <0 or >=1500
            ||request.PayPeriod is not {Length:7}||!DateOnly.TryParseExact(request.PayPeriod+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _)
            ||!Guid.TryParseExact(request.SourceBatchId,"N",out _)||!Guid.TryParseExact(request.HistoricalReceiptRequestId,"N",out _))
            throw new InvalidOperationException("Select one exact saved run/month, client design, source and authenticated historical receipt.");
    }
    private static PayrollReferenceDeclaration ReferenceBudgetDeclaration()=>new(1,PayrollReferenceInformation.Purpose,
        [new("ANNUAL_TARGET",PayrollReferenceUnit.Amount,PayrollReferenceInputMeaning.AnnualSalaryTarget)],
        [new("MONTHLY_BUDGET",PayrollReferenceUnit.Amount,PayrollReferenceOutputMeaning.MonthlySalaryBudget,
            new PayrollReferenceOperation(PayrollReferenceOperator.Divide,[new PayrollReferenceInputExpression("ANNUAL_TARGET"),new PayrollReferenceLiteral(12)]))]);

    public async Task<PayrollReferenceInformationReview> PreviewReferenceInformationAsync(PayrollReferenceInformationReviewRequest request,AuthUser actor)
    {
        ValidateReferenceRequest(request);RequireReferenceWriter(actor,request.ClientId);
        await using var db=Db();await db.OpenAsync();
        await using var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,isReadOnly:true);
        var previous=await ReadReferenceProtectedAsync<ReferenceManifest>(db,tx,request.ClientId,ReferenceManifestCode(request.RunId));
        if(request.RefreshExisting&&previous is null)throw new InvalidOperationException("No existing reference manifest can be refreshed for this run.");
        if(previous is not null)ValidateReferenceStoredManifest(previous,request.ClientId,request.RunId);
        var plan=await BuildReferencePlanAsync(db,tx,request,false,!request.RefreshExisting,sourceWitness:previous);
        var expires=DateTime.UtcNow.AddMinutes(30);
        var review=new ReferenceReviewToken(1,"ReferenceInformationReview",actor.Id,request,plan.Manifest.PlanHash,
            plan.Manifest.StateWitnessHash,plan.ReviewStateHash,expires,previous is null?null:ReferenceHash(previous));
        var token=protector.ProtectPayrollReferenceInformation(JsonSerializer.Serialize(review,Json));
        await tx.RollbackAsync();
        return new(request.ClientId,request.RunId,request.PayPeriod,request.TemplateId,request.SourceInfoColumn,
            plan.Manifest.Label,plan.Manifest.PlanHash,plan.Manifest.StateWitnessHash,token,expires,plan.Rows,request.RefreshExisting,review.PreviousManifestHash);
    }

    public async Task<PayrollReferenceInformationReceipt> ApplyReferenceInformationAsync(PayrollReferenceInformationApplyRequest command,AuthUser actor)
    {
        if(command is null||!command.Confirmed||!Guid.TryParse(command.RequestId,out var guid)||string.IsNullOrWhiteSpace(command.ReviewToken)||command.ReviewToken.Length>16000)
            throw new InvalidOperationException("Confirm the reviewed complete payroll reference scope with a stable request GUID.");
        var review=ReadReferenceReview(command.ReviewToken);RequireReferenceWriter(actor,review.Request.ClientId);
        if(review.ActorId!=actor.Id)throw new UnauthorizedAccessException("This payroll reference review belongs to another user.");
        var requestId=guid.ToString("N");var commandHash=ReferenceHash(new{command.ReviewToken,command.Confirmed,RequestId=requestId});
        await using var db=Db();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync(IsolationLevel.Serializable);
        await LockReferenceClientAsync(db,tx,review.Request.ClientId);
        await RequireReferenceWorkflowIsolationAsync(db,tx);
        var prior=await ReadReferenceProtectedAsync<ReferenceStoredReceipt>(db,tx,review.Request.ClientId,ReferenceReceiptCode(requestId),true);
        if(prior is not null)
        {
            if(prior.Version!=1||prior.Kind!="ReferenceInformationReceipt"||prior.ActorId!=actor.Id||prior.CommandHash!=commandHash
                ||prior.Receipt.RequestId!=requestId||prior.Receipt.ClientId!=review.Request.ClientId||prior.Receipt.RunId!=review.Request.RunId)
                throw new InvalidOperationException("This payroll reference request GUID was used for another command or actor.");
            var retained=await ReadReferenceProtectedAsync<ReferenceManifest>(db,tx,review.Request.ClientId,prior.ManifestCode,true)
                ??throw new InvalidOperationException("The current payroll reference manifest is missing.");
            ValidateReferenceStoredManifest(retained,review.Request.ClientId,review.Request.RunId);
            if(ReferenceHash(retained)!=prior.ManifestHash)
            {
                var archive=await ReadReferenceProtectedAsync<ReferenceManifestArchive>(db,tx,review.Request.ClientId,ReferenceArchiveCode(review.Request.RunId,prior.ManifestHash),true);
                if(archive is null||archive.Version!=1||archive.Kind!="ReferenceInformationArchive"||archive.ClientId!=review.Request.ClientId||archive.RunId!=review.Request.RunId
                    ||archive.ManifestHash!=prior.ManifestHash||ReferenceHash(archive.Manifest)!=prior.ManifestHash||archive.Manifest.RequestId!=requestId)
                    throw new InvalidOperationException("The historical reference receipt cannot be verified against an immutable previous manifest.");
                ValidateReferenceStoredManifest(archive.Manifest,review.Request.ClientId,review.Request.RunId);
            }
            await tx.RollbackAsync();return prior.Receipt;
        }
        if(review.ExpiresAtUtc<=DateTime.UtcNow||review.ExpiresAtUtc>DateTime.UtcNow.AddMinutes(31))
            throw new InvalidOperationException("The payroll reference review expired. Preview the exact current state again.");
        var previous=await ReadReferenceProtectedAsync<ReferenceManifest>(db,tx,review.Request.ClientId,ReferenceManifestCode(review.Request.RunId),true);
        if(review.Request.RefreshExisting)
        {
            if(previous is null||ReferenceHash(previous)!=review.PreviousManifestHash)throw new InvalidOperationException("The current reference manifest changed after refresh review; no metadata was saved.");
            ValidateReferenceStoredManifest(previous,review.Request.ClientId,review.Request.RunId);
        }
        else if(previous is not null||review.PreviousManifestHash is not null)throw new InvalidOperationException("An existing reference manifest needs an explicit reviewed refresh.");
        var plan=await BuildReferencePlanAsync(db,tx,review.Request,true,!review.Request.RefreshExisting,sourceWitness:previous);
        if(plan.Manifest.PlanHash!=review.PlanHash||plan.Manifest.StateWitnessHash!=review.StateWitnessHash||plan.ReviewStateHash!=review.ReviewStateHash)
            throw new InvalidOperationException("Payroll reference source, profile, history or native state changed after review. No metadata was saved.");
        var appliedAt=DateTime.UtcNow;
        var manifest=plan.Manifest with{ActorId=actor.Id,RequestId=requestId,AppliedAtUtc=appliedAt};
        var manifestHash=ReferenceHash(manifest);var code=ReferenceManifestCode(review.Request.RunId);
        var archiveCode=previous is null?null:ReferenceArchiveCode(review.Request.RunId,review.PreviousManifestHash!);
        var receipt=new PayrollReferenceInformationReceipt(1,review.Request.ClientId,review.Request.RunId,review.Request.PayPeriod,
            review.Request.TemplateId,review.Request.SourceInfoColumn,manifest.Employees.Count,requestId,review.PlanHash,manifestHash,appliedAt,
            review.Request.RefreshExisting,review.PreviousManifestHash,archiveCode);
        if(previous is null)await InsertReferenceProtectedAsync(db,tx,review.Request.ClientId,code,manifest);
        else
        {
            await InsertReferenceProtectedAsync(db,tx,review.Request.ClientId,archiveCode!,new ReferenceManifestArchive(1,"ReferenceInformationArchive",actor.Id,
                review.Request.ClientId,review.Request.RunId,review.PreviousManifestHash!,requestId,appliedAt,previous));
            await ReplaceReferenceProtectedAsync(db,tx,review.Request.ClientId,code,manifest);
        }
        await InsertReferenceProtectedAsync(db,tx,review.Request.ClientId,ReferenceReceiptCode(requestId),
            new ReferenceStoredReceipt(1,"ReferenceInformationReceipt",actor.Id,commandHash,code,manifestHash,receipt));
        await tx.CommitAsync();return receipt;
    }

    private ReferenceReviewToken ReadReferenceReview(string token)
    {
        try
        {
            var plain=protector.UnprotectPayrollReferenceInformation(token);
            var value=plain is null?null:JsonSerializer.Deserialize<ReferenceReviewToken>(plain,Json);
            if(value is null||value.Version!=1||value.Kind!="ReferenceInformationReview"||value.ActorId<=0
                ||!ReferenceDigest(value.PlanHash)||!ReferenceDigest(value.StateWitnessHash)||!ReferenceDigest(value.ReviewStateHash)
                ||value.Request is null)throw new InvalidOperationException();
            if(value.Request.RefreshExisting?!ReferenceDigest(value.PreviousManifestHash):value.PreviousManifestHash is not null)throw new InvalidOperationException();
            ValidateReferenceRequest(value.Request);return value;
        }
        catch(Exception error)when(error is JsonException or InvalidOperationException or ArgumentException)
        {throw new InvalidOperationException("The payroll reference review token cannot be authenticated.",error);}
    }
    private static async Task LockReferenceClientAsync(MySqlConnection db,MySqlTransaction tx,int clientId)
    {
        if(await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE",new{ClientId=clientId},tx) is null)
            throw new InvalidOperationException("The payroll reference client is unavailable.");
    }
    private async Task<T?> ReadReferenceProtectedAsync<T>(MySqlConnection db,MySqlTransaction tx,int clientId,string code,bool locked=false)where T:class
    {
        var rows=(await db.QueryAsync<(bool IsEnabled,string Json)>("SELECT IsEnabled,CAST(SettingsJson AS CHAR) Json FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code"+(locked?" FOR UPDATE":""),new{ClientId=clientId,Code=code},tx)).ToArray();
        if(rows.Length==0)return null;
        if(rows.Length!=1||!rows[0].IsEnabled||rows[0].Json.Length>ReferenceMetadataLimit*2)throw new InvalidOperationException("Payroll reference metadata is disabled, ambiguous or unbounded.");
        try
        {
            if(protector is null)throw new InvalidOperationException("The registered reference Information presentation protector is unavailable.");
            var envelope=JsonSerializer.Deserialize<ReferenceProtectedEnvelope>(rows[0].Json,Json);
            var plain=envelope is {Version:1,Purpose:ReferencePurpose}?protector.UnprotectPayrollReferenceInformation(envelope.Value):null;
            if(plain is null||plain.Length>ReferenceMetadataLimit)throw new InvalidOperationException();
            return JsonSerializer.Deserialize<T>(plain,Json)??throw new InvalidOperationException();
        }
        catch(Exception error)when(error is JsonException or InvalidOperationException or ArgumentException)
        {throw new InvalidOperationException("Payroll reference metadata authentication/schema failed; no legacy amount is substituted.",error);}
    }
    private async Task InsertReferenceProtectedAsync(MySqlConnection db,MySqlTransaction tx,int clientId,string code,object value)
    {
        var plain=JsonSerializer.Serialize(value,Json);if(plain.Length>ReferenceMetadataLimit)throw new InvalidOperationException("Payroll reference metadata exceeds its bounded size.");
        var envelope=new ReferenceProtectedEnvelope(1,ReferencePurpose,protector.ProtectPayrollReferenceInformation(plain));
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,CAST(@Json AS JSON))",
            new{ClientId=clientId,Code=code,Json=JsonSerializer.Serialize(envelope,Json)},tx);
    }
    private async Task ReplaceReferenceProtectedAsync(MySqlConnection db,MySqlTransaction tx,int clientId,string code,object value)
    {
        var plain=JsonSerializer.Serialize(value,Json);if(plain.Length>ReferenceMetadataLimit)throw new InvalidOperationException("Payroll reference metadata exceeds its bounded size.");
        var envelope=new ReferenceProtectedEnvelope(1,ReferencePurpose,protector.ProtectPayrollReferenceInformation(plain));
        if(await db.ExecuteAsync("UPDATE modulesettings SET IsEnabled=TRUE,SettingsJson=CAST(@Json AS JSON) WHERE client_id=@ClientId AND ModuleCode=@Code",new{ClientId=clientId,Code=code,Json=JsonSerializer.Serialize(envelope,Json)},tx)!=1)
            throw new InvalidOperationException("The locked current reference manifest could not be replaced atomically.");
    }
}

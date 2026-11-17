using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

// Opt-in protected sidecars only. No DDL, migrations, salary-map reinterpretation,
// or current/future Master writes. Absence retains the old salary-map shape.
internal static partial class NamedFormulaInputStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions StrictJson=new(JsonSerializerDefaults.Web){UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
    private sealed record Envelope(int Version,string Payload);
    private sealed class Setting{public string Value{get;set;}="";public bool Enabled{get;set;}}
    internal static PortableIntegrationCredentialProtector Protector(IConfiguration configuration)=>new(configuration,new EphemeralDataProtectionProvider(),NullLogger<PortableIntegrationCredentialProtector>.Instance);
    internal static string Protect<T>(T value,IConfiguration config,string kind)=>JsonSerializer.Serialize(new Envelope(1,Protector(config).ProtectNamedFormulaInput(JsonSerializer.Serialize(value,Json),kind)),Json);
    internal static T Read<T>(string raw,IConfiguration config,string kind)
    {
        try
        {
            RequireUniqueJson(raw);
            var envelope=JsonSerializer.Deserialize<Envelope>(raw,StrictJson);
            var text=envelope is{Version:1}?Protector(config).UnprotectNamedFormulaInput(envelope.Payload,kind):null;
            if(text is not null)RequireUniqueJson(text);
            return text is null?throw new InvalidOperationException("The named operand record cannot be authenticated."):JsonSerializer.Deserialize<T>(text,StrictJson)??throw new InvalidOperationException("Named operand evidence is incomplete.");
        }
        catch(JsonException){throw new InvalidOperationException("Named operand evidence is malformed; no default values were used.");}
    }
    private static void RequireUniqueJson(string raw)
    {
        if(raw.Length>1_000_000)throw new InvalidOperationException("Named operand evidence exceeds its bounded limit.");
        using var document=JsonDocument.Parse(raw,new JsonDocumentOptions{MaxDepth=32});
        static void Check(JsonElement node)
        {
            if(node.ValueKind==JsonValueKind.Object)
            {
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var property in node.EnumerateObject()){if(!names.Add(property.Name))throw new InvalidOperationException("Duplicate named operand JSON fields are not accepted.");Check(property.Value);}
            }
            else if(node.ValueKind==JsonValueKind.Array)foreach(var item in node.EnumerateArray())Check(item);
        }
        Check(document.RootElement);
    }
    internal static async Task<NamedFormulaInputProfile?> ReadProfileAsync(MySqlConnection db,IDbTransaction? tx,IConfiguration config,int client,string template,bool locked=false)
    {
        var row=await db.QuerySingleOrDefaultAsync<Setting>("SELECT CAST(SettingsJson AS CHAR) Value,IsEnabled Enabled FROM modulesettings WHERE client_id=@Client AND ModuleCode=@Code"+(locked?" FOR UPDATE":""),new{Client=client,Code=ReviewedNamedFormulaInputs.ProfileKey(template)},tx);
        if(row is null)return null;
        if(!row.Enabled)throw new InvalidOperationException("The named-operand template declaration is disabled.");
        var profile=Read<NamedFormulaInputProfile>(row.Value,config,"Template");
        ReviewedNamedFormulaInputs.ValidateDefinitions(profile.Definitions,[]);
        if(profile.Version!=1||profile.ClientId!=client||profile.TemplateId!=template||profile.DeclarationHash!=ReviewedNamedFormulaInputs.DeclarationHash(client,template,profile.RulesHash,profile.Definitions))
            throw new InvalidOperationException("The named-operand declaration owner or signature changed.");
        return profile;
    }
    internal static async Task<Dictionary<string,NamedFormulaInputProfile>> ReadProfilesAsync(MySqlConnection db,IDbTransaction tx,
        IConfiguration config,int client,IReadOnlyCollection<string> templates)
    {
        if(templates.Count==0)return [];
        if(templates.Count>1000||templates.Any(template=>string.IsNullOrWhiteSpace(template)||template.Length>40))
            throw new InvalidOperationException("Review a bounded named-operand template roster.");
        var rows=await db.QueryAsync<(string Code,string Value,bool Enabled)>("SELECT ModuleCode Code,CAST(SettingsJson AS CHAR) Value,IsEnabled Enabled FROM modulesettings WHERE client_id=@Client AND ModuleCode IN @Keys",
            new{Client=client,Keys=templates.Distinct(StringComparer.Ordinal).Select(ReviewedNamedFormulaInputs.ProfileKey).ToArray()},tx);
        var result=new Dictionary<string,NamedFormulaInputProfile>(StringComparer.Ordinal);
        foreach(var row in rows)
        {
            if(!row.Enabled)throw new InvalidOperationException("The named-operand template declaration is disabled.");
            var template=row.Code["named-input-t:".Length..];var profile=Read<NamedFormulaInputProfile>(row.Value,config,"Template");
            ReviewedNamedFormulaInputs.ValidateDefinitions(profile.Definitions,[]);
            if(profile.Version!=1||profile.ClientId!=client||profile.TemplateId!=template||profile.DeclarationHash!=ReviewedNamedFormulaInputs.DeclarationHash(client,template,profile.RulesHash,profile.Definitions)||!result.TryAdd(template,profile))
                throw new InvalidOperationException("The named-operand declaration owner or signature changed.");
        }
        return result;
    }
    internal static string ManifestHash(NamedFormulaInputManifest m)=>ReviewedNamedFormulaInputs.Hash(new{m.Version,m.ClientId,m.EmployeeId,m.Month,m.AssignmentId,m.TemplateId,m.WorkLocationId,m.SourcePartKey,m.BatchId,m.RowId,m.SourceRow,m.SourceHash,m.RawBatchHash,m.RawSourceHash,m.IdentityHash,m.DeclarationHash,m.Definitions,values=m.Values.OrderBy(p=>p.Key,StringComparer.Ordinal),m.Cells,m.CompleteSourcePartKeys,m.ReferenceAssignmentId,m.NativeRulesHash,m.ComponentScopesHash});
    internal static async Task InsertManifestAsync(MySqlConnection db,IDbTransaction tx,IConfiguration config,NamedFormulaInputManifest manifest)
    {
        if(manifest.ManifestHash!=ManifestHash(manifest))throw new InvalidOperationException("The named operand assignment signature is invalid.");
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@Client,@Code,TRUE,CAST(@Json AS JSON))",new{Client=manifest.ClientId,Code=ReviewedNamedFormulaInputs.AssignmentKey(manifest.AssignmentId),Json=Protect(manifest,config,"Assignment")},tx);
    }
    internal static async Task<NamedFormulaInputManifest?> ReadManifestAsync(MySqlConnection db,IDbTransaction? tx,IConfiguration config,int client,string assignment)
    {
        var row=await db.QuerySingleOrDefaultAsync<Setting>("SELECT CAST(SettingsJson AS CHAR) Value,IsEnabled Enabled FROM modulesettings WHERE client_id=@Client AND ModuleCode=@Code",new{Client=client,Code=ReviewedNamedFormulaInputs.AssignmentKey(assignment)},tx);
        if(row is null)return null;
        if(!row.Enabled)throw new InvalidOperationException("The period named operand manifest is disabled.");
        var m=Read<NamedFormulaInputManifest>(row.Value,config,"Assignment");
        if(m.Version!=1||m.ClientId!=client||m.AssignmentId!=assignment||m.ManifestHash!=ManifestHash(m))throw new InvalidOperationException("The named operand assignment ownership or signature is invalid.");
        return m;
    }
    internal static async Task<NamedFormulaInputContext?> LoadApprovedAsync(MySqlConnection db,IDbTransaction tx,IConfiguration config,
        int client,int employee,string month,string template,string assignment,int location,string part,string batchId,string rowId,string sourceHash,
        IReadOnlyCollection<string> completeParts,string? reference,IReadOnlyDictionary<string,string> componentScopes)
    {
        var profile=await ReadProfileAsync(db,tx,config,client,template);
        var m=await ReadManifestAsync(db,tx,config,client,assignment);
        if(profile is null&&m is null)return null;
        if(profile is null||m is null||m.EmployeeId!=employee||m.Month!=month||m.TemplateId!=template||m.WorkLocationId!=location||m.SourcePartKey!=part||m.BatchId!=batchId||m.RowId!=rowId||m.SourceHash!=sourceHash
            ||m.DeclarationHash!=profile.DeclarationHash||m.NativeRulesHash!=profile.RulesHash||m.ReferenceAssignmentId!=reference
            ||m.CompleteSourcePartKeys.Count!=completeParts.Count||!m.CompleteSourcePartKeys.SequenceEqual(completeParts.Order(StringComparer.Ordinal))
            ||m.ComponentScopesHash!=ReviewedNamedFormulaInputs.Hash(componentScopes.OrderBy(pair=>pair.Key,StringComparer.Ordinal))
            ||!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(m.Definitions,Json),JsonSerializer.SerializeToNode(profile.Definitions,Json)))
            throw new InvalidOperationException("The exact period named operands/declaration are missing or out of scope; no legacy or zero fallback is allowed.");
        var firstSourceRequest = await RequireAssignmentApprovalAsync(db, tx, config, m);
        var raw=await db.QuerySingleAsync(@"SELECT CAST(b.batch_json AS CHAR) Batch,CAST(s.source_json AS CHAR) Source,SHA2(CAST(b.batch_json AS CHAR),256) BatchHash,SHA2(CAST(s.source_json AS CHAR),256) SourceHash
FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@Batch",new{Client=client,Batch=batchId},tx);
        var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>((string)raw.Batch,Json)!;
        var source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>((string)raw.Source,Json)!;
        var row=batch.Rows.SingleOrDefault(r=>r.Id==rowId&&r.SourceRow==m.SourceRow)??throw new InvalidOperationException("The exact named-operand source row is unavailable.");
        if(batch.Month!=month||m.RawBatchHash!=(string)raw.BatchHash||m.RawSourceHash!=(string)raw.SourceHash||ExcelPayslipRepository.EmploymentSourceHash(batch,source)!=m.SourceHash
            ||ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source)!=m.IdentityHash
            ||firstSourceRequest is null&&(batch.ProfileLinks??[]).Count(link=>link.EmployeeId==employee&&link.RowId==row.Id&&link.SourceRow==row.SourceRow&&link.SourceIdentityHash==m.IdentityHash)!=1)
            throw new InvalidOperationException("The approved named-operand source or durable identity changed. Current contact/name snapshots are not used as immutable salary authority.");
        if (firstSourceRequest is not null)
        {
            var nativeTx = tx as MySqlTransaction ?? throw new InvalidOperationException("A same-database transaction is required for first-source identity evidence.");
            var identities = await ExcelPayrollSetupRepository.ReadNamedSourceSetupIdentitiesAsync(db, nativeTx, client, month);
            var bankOwners = firstSourceRequest.SourceIdentityReview is null ? null : await ExcelPayrollSetupRepository.ReadSourceNamedBankOwnersAsync(db, nativeTx);
            var bankIdentity = ExcelPayrollSetupRepository.ReviewSourceNamedBankIdentity(firstSourceRequest, batch, source, row, employee, identities, bankOwners ?? []);
            ExcelPayrollSetupRepository.RequireNamedSourceSetupOwner(batch, source, row, employee, identities, firstSourceRequest.SourceWorkLocationColumnIndex, bankIdentity);
        }
        var rebound=ReviewedNamedFormulaInputs.Bind(profile,m.Cells.Select(c=>new NamedFormulaInputBinding(c.Key,c.ColumnIndex,c.HeaderRow,c.HumanHeader)).ToArray(),source,row,[]);
        if(!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(rebound.Context.Values,Json),JsonSerializer.SerializeToNode(m.Values,Json))||!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(rebound.Cells,Json),JsonSerializer.SerializeToNode(m.Cells,Json)))
            throw new InvalidOperationException("A protected source operand no longer matches its exact captured literal.");
        rebound.Context.ManifestHash=m.ManifestHash;return rebound.Context;
    }
}

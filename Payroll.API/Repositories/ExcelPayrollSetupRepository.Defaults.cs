using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static JsonObject InactiveDefaultsDraft(JsonObject? defaults)
    {
        if (defaults is null || JsonSerializer.SerializeToUtf8Bytes(defaults, Json).Length > 64000)
            throw new InvalidOperationException("Use a defaults draft of at most 64 KiB.");
        var copy = defaults.DeepClone().AsObject();
        foreach (var key in new[] { "leave", "holiday", "shift" })
        {
            if (copy[key] is not null && copy[key] is not JsonObject) throw new InvalidOperationException("Leave, holiday and shift proposals must be objects.");
            var proposal = copy[key] as JsonObject ?? new JsonObject();
            proposal["status"] = "Draft"; proposal["active"] = false;
            if (key == "leave") proposal["quotaApproved"] = false;
            copy[key] = proposal;
        }
        return copy;
    }

    public async Task<object> SaveDefaultsDraftAsync(int clientId, ExcelPayrollDefaultsDraftRequest request, string actor)
    {
        if (!Guid.TryParseExact(request.BatchId,"N",out _) || !Guid.TryParseExact(request.ImportId,"N",out _) || request.ExpectedRevision < 0)
            throw new InvalidOperationException("Select a saved applied setup and its current draft revision.");
        var defaults = InactiveDefaultsDraft(request.EditableDefaults);
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE",new { ClientId=clientId },tx) is null)
            throw new InvalidOperationException("Select an active client.");
        await ReadSourceAsync(db,tx,clientId,request.BatchId);
        var saved = await db.QuerySingleOrDefaultAsync<SavedSetup>(@"SELECT id ImportId,request_id RequestId,status Status,CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND batch_id=@BatchId AND id=@ImportId FOR UPDATE",new { ClientId=clientId,request.BatchId,request.ImportId },tx);
        if (saved is null || saved.Status != "Applied") throw new InvalidOperationException("Open the existing applied setup before editing its inactive proposals.");
        var metadata = JsonNode.Parse(saved.ResultJson)!.AsObject();
        var revision = metadata["editableDefaultsRevision"]?.GetValue<int>() ?? 0;
        if (revision != request.ExpectedRevision) throw new InvalidOperationException("These proposals changed in another session. Reopen the setup before saving.");
        var original = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(saved.RequestJson,Json)!;
        var previous = metadata["editableDefaultsDraft"]?.DeepClone() ?? original.EditableDefaults.DeepClone();
        var history = metadata["editableDefaultsHistory"] as JsonArray ?? new JsonArray();
        history.Add(new JsonObject { ["revision"]=revision,["previousDefaults"]=previous,["changedBy"]=actor.Length>190?actor[..190]:actor,["changedAtUtc"]=DateTime.UtcNow.ToString("o") });
        metadata["editableDefaultsHistory"]=history;
        metadata["editableDefaultsDraft"]=defaults.DeepClone();
        metadata["editableDefaultsRevision"]=revision+1;
        // Financial request/hash/result IDs remain unchanged. Only inactive proposals
        // and their edit history are updated; payroll tables are never touched here.
        await db.ExecuteAsync("UPDATE excel_payroll_setup_imports SET result_json=CAST(@Result AS JSON) WHERE id=@Id AND client_id=@ClientId",new { Id=saved.ImportId,ClientId=clientId,Result=metadata.ToJsonString(Json) },tx);
        await tx.CommitAsync();
        return new { importId=saved.ImportId,editableDefaultsDraft=defaults,defaultsRevision=revision+1 };
    }
}

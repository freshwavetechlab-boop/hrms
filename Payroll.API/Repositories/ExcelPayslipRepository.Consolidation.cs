using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    private const string ConsolidationCreate = "batch-group-create", ConsolidationUndo = "batch-group-undo";
    private sealed class ConsolidationSnapshot
    {
        public string Id { get; set; } = "";
        public string BatchJson { get; set; } = "";
        public string? SourceJson { get; set; }
        public byte[]? BatchPayload { get; set; }
        public byte[]? SourcePayload { get; set; }
        public string StateJson { get; set; } = "{}";
        public ExcelPayslipBatch Batch => JsonSerializer.Deserialize<ExcelPayslipBatch>(BatchJson, JsonOptions)!;
        public ExcelPayslipCalculationSource? Source => SourceJson is null ? null : JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(SourceJson, JsonOptions);
        public string Hash => ExcelPayslipLegacyReview.SnapshotHash(BatchJson,SourceJson);
        public string StateHash => ExcelPayslipSavedConsolidation.Hash(StateJson);
    }
    private sealed class ConsolidationAudit
    {
        public int ClientId { get; set; }
        public string BatchId { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string Action { get; set; } = "";
        public string ObjectId { get; set; } = "";
        public string ProofJson { get; set; } = "{}";
        public DateTime AtUtc { get; set; }
    }
    private sealed class ConsolidationProof
    {
        public int Version { get; set; } = 1;
        public int ClientId { get; set; }
        public string GroupId { get; set; } = "";
        public string CanonicalBatchId { get; set; } = "";
        public string CanonicalHash { get; set; } = "";
        public int CanonicalRows { get; set; }
        public List<ConsolidationSourceProof> Sources { get; set; } = [];
    }
    private sealed class ConsolidationSourceProof
    {
        public string BatchId { get; set; } = "";
        public string SnapshotHash { get; set; } = "";
        public List<ConsolidationRowPair> Rows { get; set; } = [];
    }
    private sealed record ConsolidationRowPair(int SourceRow,string CanonicalRowId,string SourceRowId);

    private static async Task<List<ConsolidationAudit>> ReadConsolidationAuditsAsync(MySqlConnection db,int clientId,MySqlTransaction? tx=null)
    {
        if(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='excel_payslip_cleanup_actions'",transaction:tx)==0)return [];
        // Do not sort JSON-bearing rows in MySQL. The audit payload is metadata, never a copied salary snapshot.
        var rows=(await db.QueryAsync<ConsolidationAudit>(@"SELECT batch_id BatchId,request_id RequestId,request_hash RequestHash,
action_code Action,object_id ObjectId,CAST(proof_json AS CHAR) ProofJson,created_at_utc AtUtc FROM excel_payslip_cleanup_actions
WHERE client_id=@ClientId AND action_code IN ('batch-group-create','batch-group-undo')",new {ClientId=clientId},tx)).ToList();
        if(rows.Count>10000)throw new InvalidOperationException("The consolidation history exceeds its bounded review size.");
        return rows.OrderBy(row=>row.AtUtc).ThenBy(row=>row.RequestId,StringComparer.Ordinal).ToList();
    }
    private static async Task<List<ConsolidationAudit>> ReadCatalogConsolidationAuditsAsync(MySqlConnection db,
        IReadOnlyCollection<int> clientIds, MySqlTransaction tx)
    {
        if (clientIds.Count == 0 || await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='excel_payslip_cleanup_actions'",transaction:tx)==0) return [];
        var rows = (await db.QueryAsync<ConsolidationAudit>(@"SELECT client_id ClientId,batch_id BatchId,request_id RequestId,request_hash RequestHash,
action_code Action,object_id ObjectId,CAST(proof_json AS CHAR) ProofJson,created_at_utc AtUtc FROM excel_payslip_cleanup_actions
WHERE client_id IN @ClientIds AND action_code IN ('batch-group-create','batch-group-undo')", new { ClientIds = clientIds }, tx)).ToList();
        if (rows.GroupBy(row => row.ClientId).Any(group => group.Count() > 10000))
            throw new InvalidOperationException("The consolidation history exceeds its bounded review size.");
        return rows.OrderBy(row => row.ClientId).ThenBy(row => row.AtUtc).ThenBy(row => row.RequestId,StringComparer.Ordinal).ToList();
    }
    private sealed class CatalogSnapshot
    {
        public int ClientId { get; set; }
        public string BatchId { get; set; } = "";
        public byte[]? BatchPayload { get; set; }
        public byte[]? SourcePayload { get; set; }
    }
    private static async Task<IReadOnlyList<ExcelPayslipBatchSummary>> FilterCatalogConsolidatedSummariesAsync(MySqlConnection db,
        MySqlTransaction tx, IReadOnlyList<ExcelPayslipBatchSummary> summaries)
    {
        var clientIds = summaries.Select(summary => summary.ClientId).Distinct().ToArray();
        var audits = await ReadCatalogConsolidationAuditsAsync(db, clientIds, tx);
        var groups = audits.GroupBy(audit => audit.ClientId)
            .SelectMany(client => ActiveConsolidations(client.ToList(), client.Key).Select(group =>
                new ExcelPayslipConsolidationCatalogGroup(client.Key, group.Proof.GroupId, group.Proof.CanonicalBatchId,
                    group.Proof.CanonicalHash, group.Proof.Sources.ToDictionary(source => source.BatchId, source => source.SnapshotHash, StringComparer.Ordinal))))
            .ToArray();
        var hashes = new Dictionary<(int ClientId, string BatchId), string>();
        foreach (var keys in ExcelPayslipConsolidationCatalog.SnapshotQueries(groups))
        {
            var args = new DynamicParameters();
            var terms = keys.Select((key,index) => { args.Add("Client"+index,key.ClientId); args.Add("Batch"+index,key.BatchId); return $"(b.client_id=@Client{index} AND b.id=@Batch{index})"; });
            // A bounded exact-pair query avoids one network round trip per client/version.
            // Large JSON is not sorted or rewritten; use the same hash function as every existing proof.
            var snapshots = await db.QueryAsync<CatalogSnapshot>($@"SELECT b.client_id ClientId,b.id BatchId,
COMPRESS(CONVERT(CAST(b.batch_json AS CHAR) USING utf8mb4)) BatchPayload,COMPRESS(CONVERT(CAST(s.source_json AS CHAR) USING utf8mb4)) SourcePayload FROM excel_payslip_batches b
LEFT JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id
WHERE {string.Join(" OR ",terms)}", args, tx);
            foreach (var snapshot in snapshots)
                hashes.Add((snapshot.ClientId,snapshot.BatchId),ExcelPayslipLegacyReview.SnapshotHash(
                    ExcelPayslipConsolidationCatalog.DecodeSnapshot(snapshot.BatchPayload,false)!,
                    ExcelPayslipConsolidationCatalog.DecodeSnapshot(snapshot.SourcePayload,true)));
        }
        return ExcelPayslipConsolidationCatalog.Visible(summaries, groups, hashes);
    }
    private static ConsolidationProof ConsolidationDocument(ConsolidationAudit audit,int clientId)
    {
        ConsolidationProof? proof;
        try {proof=JsonSerializer.Deserialize<ConsolidationProof>(audit.ProofJson,JsonOptions);}
        catch(JsonException){throw new InvalidOperationException("Consolidation metadata is invalid; no source relationship was inferred.");}
        if(proof is null || proof.Version is not (1 or 2) || proof.ClientId!=clientId || proof.CanonicalBatchId!=audit.BatchId || audit.Action==ConsolidationCreate && proof.GroupId!=audit.RequestId
            || !Guid.TryParseExact(proof.GroupId,"N",out _) || !Guid.TryParseExact(proof.CanonicalBatchId,"N",out _)
            || proof.CanonicalHash?.Length!=64 || proof.Sources is null || proof.Sources.Count is <1 or >8 || proof.Sources.Any(s=>s is null || !Guid.TryParseExact(s.BatchId,"N",out _) || s.BatchId==proof.CanonicalBatchId || s.SnapshotHash?.Length!=64 || s.Rows is null || s.Rows.Any(row=>row is null)
                || !ExcelPayslipSavedConsolidation.SourceRowBindingsValid(proof.Version,proof.CanonicalRows,s.Rows.Select(row=>(row.SourceRow,row.CanonicalRowId,row.SourceRowId)).ToArray()))
            || proof.Sources.Select(s=>s.BatchId).Distinct(StringComparer.Ordinal).Count()!=proof.Sources.Count)
            throw new InvalidOperationException("Consolidation metadata needs review; no source relationship was inferred.");
        return proof;
    }
    private static Task<ConsolidationAudit?> ReadConsolidationRequestAsync(MySqlConnection db,MySqlTransaction tx,int clientId,string requestId) =>
        db.QuerySingleOrDefaultAsync<ConsolidationAudit>(@"SELECT request_id RequestId,request_hash RequestHash,action_code Action
FROM excel_payslip_cleanup_actions WHERE client_id=@ClientId AND request_id=@RequestId",new {ClientId=clientId,RequestId=requestId},tx);
    private static IEnumerable<(ConsolidationAudit Audit,ConsolidationProof Proof)> ActiveConsolidations(List<ConsolidationAudit> audits,int clientId) =>
        audits.Where(a=>a.Action==ConsolidationCreate && ExcelPayslipSavedConsolidation.GroupIsActive(a.RequestId,audits.Select(a=>(a.Action,a.ObjectId))))
            .Select(a=>(a,ConsolidationDocument(a,clientId)));
    private static async Task<ConsolidationSnapshot?> ReadConsolidationSnapshotAsync(MySqlConnection db,MySqlTransaction? tx,int clientId,string id)
    {
        var snapshot = await db.QuerySingleOrDefaultAsync<ConsolidationSnapshot>(@"SELECT b.id Id,
COMPRESS(CONVERT(CAST(b.batch_json AS CHAR) USING utf8mb4)) BatchPayload,COMPRESS(CONVERT(CAST(s.source_json AS CHAR) USING utf8mb4)) SourcePayload,
CAST(b.send_state_json AS CHAR) StateJson FROM excel_payslip_batches b LEFT JOIN excel_payslip_calculation_sources s
ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@ClientId AND b.id=@Id",new {ClientId=clientId,Id=id},tx);
        if (snapshot is not null)
        {
            snapshot.BatchJson = ExcelPayslipConsolidationCatalog.DecodeSnapshot(snapshot.BatchPayload,false)!;
            snapshot.SourceJson = ExcelPayslipConsolidationCatalog.DecodeSnapshot(snapshot.SourcePayload,true);
            snapshot.BatchPayload = null; snapshot.SourcePayload = null;
        }
        return snapshot;
    }
    private static void ValidateConsolidationSelection(int clientId,string canonical,IReadOnlyCollection<string>? sources)
    {
        if(clientId<=0 || !Guid.TryParseExact(canonical,"N",out var canonicalId) || sources is null || sources.Count is <1 or >8
            || sources.Any(id=>!Guid.TryParseExact(id,"N",out var sourceId) || sourceId==canonicalId) || sources.Select(id=>Guid.ParseExact(id,"N")).Distinct().Count()!=sources.Count)
            throw new InvalidOperationException("Select one canonical saved batch and 1–8 different source versions in the same client.");
    }
    private static string ConsolidationRequestHash(string canonical,IEnumerable<string> sources,string token) =>
        ExcelPayslipSavedConsolidation.Hash(JsonSerializer.Serialize(new {canonical,sources=sources.Order(StringComparer.Ordinal),token},JsonOptions));
    private static async Task<(ExcelPayslipConsolidationPreview Preview,ConsolidationSnapshot Canonical,List<ConsolidationSnapshot> Sources)> BuildConsolidationPreviewAsync(
        MySqlConnection db,MySqlTransaction tx,int clientId,string canonicalId,List<string> sourceIds,List<ConsolidationAudit> audits)
    {
        var canonical=await ReadConsolidationSnapshotAsync(db,tx,clientId,canonicalId) ?? throw new InvalidOperationException("Canonical batch was not found for this client.");
        var sources=new List<ConsolidationSnapshot>();
        foreach(var id in sourceIds.Order(StringComparer.Ordinal))sources.Add(await ReadConsolidationSnapshotAsync(db,tx,clientId,id) ?? throw new InvalidOperationException("A selected source batch was not found for this client."));
        if(canonical.Batch.ClientId!=clientId || canonical.Batch.Id!=canonicalId || sources.Any(s=>s.Batch.ClientId!=clientId || s.Batch.Id!=s.Id))throw new InvalidOperationException("Saved batch ownership does not match the selected client/IDs.");
        var preview=ExcelPayslipSavedConsolidation.Compare(canonical.Batch,canonical.Source,sources.Select(s=>(s.Batch,s.Source)).ToList());
        preview.CanonicalSnapshotHash=canonical.Hash;
        foreach(var source in preview.Sources)
        {
            var stored=sources.Single(s=>s.Id==source.BatchId);source.BatchHash=ExcelPayslipSavedConsolidation.Hash(stored.BatchJson);
            source.SourceHash=stored.SourceJson is null?"":ExcelPayslipSavedConsolidation.Hash(stored.SourceJson);
        }
        var members=sourceIds.Append(canonicalId).ToHashSet(StringComparer.Ordinal);
        if(ExcelPayslipSavedConsolidation.HasOverlap(members,ActiveConsolidations(audits,clientId).Select(g=>g.Proof.Sources.Select(s=>s.BatchId).Append(g.Proof.CanonicalBatchId))))
            preview.Blockers.Add("A selected batch already belongs to an active consolidation. Open its retained history or undo that exact grouping first.");
        preview.Token=ExcelPayslipSavedConsolidation.Hash(JsonSerializer.Serialize(new {version=1,clientId,
            previewToken=ExcelPayslipSavedConsolidation.PreviewToken(preview),canonicalState=canonical.StateHash,
            sourceStates=sources.Select(s=>new{s.Id,s.StateHash}),audits=audits.Select(a=>new{a.RequestId,a.RequestHash,a.Action,a.ObjectId})},JsonOptions));
        return (preview,canonical,sources);
    }
    public async Task<ExcelPayslipConsolidationPreview> PreviewConsolidationAsync(int clientId,ExcelPayslipConsolidationRequest request)
    {
        ValidateConsolidationSelection(clientId,request.CanonicalBatchId,request.SourceBatchIds);
        request=request with {CanonicalBatchId=Guid.Parse(request.CanonicalBatchId).ToString("N"),SourceBatchIds=request.SourceBatchIds.Select(id=>Guid.Parse(id).ToString("N")).ToList()};
        await using var db=Db();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var result=await BuildConsolidationPreviewAsync(db,tx,clientId,request.CanonicalBatchId,request.SourceBatchIds,await ReadConsolidationAuditsAsync(db,clientId,tx));
        await tx.RollbackAsync();return result.Preview;
    }
    public async Task<ExcelPayslipConsolidationStatus> ApplyConsolidationAsync(int clientId,ExcelPayslipConsolidationApplyRequest request,string actor)
    {
        ValidateConsolidationSelection(clientId,request.CanonicalBatchId,request.SourceBatchIds);
        request=request with {CanonicalBatchId=Guid.Parse(request.CanonicalBatchId).ToString("N"),SourceBatchIds=request.SourceBatchIds.Select(id=>Guid.Parse(id).ToString("N")).ToList()};
        if(!request.Acknowledged || !Guid.TryParse(request.RequestId,out var parsed) || !System.Text.RegularExpressions.Regex.IsMatch(request.Token??"",@"^[a-fA-F0-9]{64}$"))
            throw new InvalidOperationException("Review the fresh consolidation preview and explicitly acknowledge retaining all original snapshots.");
        var requestId=parsed.ToString("N");var requestHash=ConsolidationRequestHash(request.CanonicalBatchId,request.SourceBatchIds,request.Token!);
        await using var db=Db();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        if(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='excel_payslip_cleanup_actions'",transaction:tx)==0)throw new InvalidOperationException("The audited cleanup metadata schema is required before consolidation. No snapshots were changed.");
        if(await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE",new {ClientId=clientId},tx) is null)throw new InvalidOperationException("Client unavailable.");
        var audits=await ReadConsolidationAuditsAsync(db,clientId,tx);
        var replay=await ReadConsolidationRequestAsync(db,tx,clientId,requestId);
        if(replay is not null)
        {
            ExcelPayslipSavedConsolidation.ValidateReplay(replay.Action,replay.RequestHash,ConsolidationCreate,requestHash);
            await tx.CommitAsync();return (await GetConsolidationAsync(clientId,request.CanonicalBatchId,requestId))!;
        }
        var built=await BuildConsolidationPreviewAsync(db,tx,clientId,request.CanonicalBatchId,request.SourceBatchIds,audits);
        if(!built.Preview.CanConsolidate)throw new InvalidOperationException(string.Join(" ",built.Preview.Blockers));
        if(built.Preview.Token!=request.Token)throw new InvalidOperationException("Saved batch, source, delivery or grouping history changed. Review a fresh consolidation preview.");
        var canonicalRows=built.Canonical.Batch.Rows.ToDictionary(r=>r.SourceRow);
        var proof=new ConsolidationProof {Version=built.Sources.Any(source=>source.Batch.Rows.Count<built.Preview.CanonicalRows)?2:1,ClientId=clientId,GroupId=requestId,CanonicalBatchId=request.CanonicalBatchId,CanonicalHash=built.Canonical.Hash,CanonicalRows=built.Preview.CanonicalRows,
            Sources=built.Sources.Select(s=>new ConsolidationSourceProof {BatchId=s.Id,SnapshotHash=s.Hash,Rows=s.Batch.Rows.Select(r=>new ConsolidationRowPair(r.SourceRow,canonicalRows[r.SourceRow].Id,r.Id)).ToList()}).ToList()};
        await WriteConsolidationAuditAsync(db,tx,clientId,requestId,requestHash,ConsolidationCreate,"",proof,actor);
        await tx.CommitAsync();return (await GetConsolidationAsync(clientId,request.CanonicalBatchId,requestId))!;
    }
    private static Task<int> WriteConsolidationAuditAsync(MySqlConnection db,MySqlTransaction tx,int clientId,string requestId,string requestHash,string action,string objectId,ConsolidationProof proof,string actor) =>
        db.ExecuteAsync(@"INSERT INTO excel_payslip_cleanup_actions(client_id,batch_id,request_id,request_hash,action_code,object_id,batch_hash,keeper_id,keeper_hash,proof_json,actor)
VALUES(@ClientId,@Id,@RequestId,@RequestHash,@Action,@ObjectId,@BatchHash,@Id,@BatchHash,CAST(@Proof AS JSON),@Actor)",
            new {ClientId=clientId,Id=proof.CanonicalBatchId,RequestId=requestId,RequestHash=requestHash,Action=action,ObjectId=objectId,BatchHash=proof.CanonicalHash,Proof=JsonSerializer.Serialize(proof,JsonOptions),Actor=Limit(actor,190)},tx);
    public async Task<ExcelPayslipConsolidationStatus?> GetConsolidationAsync(int clientId,string batchId,string? exactGroupId=null)
    {
        if(clientId<=0 || !Guid.TryParseExact(batchId,"N",out _))return null;
        batchId=Guid.Parse(batchId).ToString("N");
        if(exactGroupId is not null)exactGroupId=Guid.ParseExact(exactGroupId,"N").ToString("N");
        await using var db=Db();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var audits=await ReadConsolidationAuditsAsync(db,clientId,tx);
        var choices=audits.Where(a=>a.Action==ConsolidationCreate).Select(a=>(Audit:a,Proof:ConsolidationDocument(a,clientId)))
            .Where(g=>exactGroupId is not null?g.Proof.GroupId==exactGroupId:g.Proof.CanonicalBatchId==batchId || g.Proof.Sources.Any(s=>s.BatchId==batchId)).ToList();
        var group=choices.OrderByDescending(g=>ExcelPayslipSavedConsolidation.GroupIsActive(g.Proof.GroupId,audits.Select(a=>(a.Action,a.ObjectId)))).ThenByDescending(g=>g.Audit.AtUtc).FirstOrDefault();
        if(group.Audit is null){await tx.RollbackAsync();return null;}
        var result=new ExcelPayslipConsolidationStatus {GroupId=group.Proof.GroupId,CanonicalBatchId=group.Proof.CanonicalBatchId,SourceBatchIds=group.Proof.Sources.Select(s=>s.BatchId).ToList(),
            Active=ExcelPayslipSavedConsolidation.GroupIsActive(group.Proof.GroupId,audits.Select(a=>(a.Action,a.ObjectId))),CanonicalRows=group.Proof.CanonicalRows,CreatedAtUtc=DateTime.SpecifyKind(group.Audit.AtUtc,DateTimeKind.Utc),
            History=audits.Where(a=>a.RequestId==group.Proof.GroupId || a.Action==ConsolidationUndo && a.ObjectId==group.Proof.GroupId).Select(a=>new ExcelPayslipConsolidationHistory(a.Action,a.RequestId,DateTime.SpecifyKind(a.AtUtc,DateTimeKind.Utc))).ToList()};
        result.CanUndo=result.Active;
        var canonical=await ReadConsolidationSnapshotAsync(db,tx,clientId,result.CanonicalBatchId);
        var liveHashes=new List<object>();
        foreach(var source in group.Proof.Sources)
        {
            var snapshot=await ReadConsolidationSnapshotAsync(db,tx,clientId,source.BatchId);liveHashes.Add(new {source.BatchId,Hash=snapshot?.Hash,StateHash=snapshot?.StateHash});
            if(canonical is null || snapshot is null || canonical.Hash!=group.Proof.CanonicalHash || snapshot.Hash!=source.SnapshotHash)
            {result.Warnings.Add("A retained snapshot changed or is unavailable; prior-version delivery is not asserted for "+source.BatchId+".");continue;}
            var state=JsonSerializer.Deserialize<ExcelPayslipSendState>(snapshot.StateJson,JsonOptions) ?? new();
            var statuses=BuildDeliveryStatuses(snapshot.Batch,state,await ReadDeliveryQueueAsync(db,clientId,state,tx));
            result.HistoricalDelivery.AddRange(ExcelPayslipSavedConsolidation.BindHistoricalDeliveries(canonical.Batch,snapshot.Batch,statuses.Items));
        }
        result.Token=ExcelPayslipSavedConsolidation.Hash(JsonSerializer.Serialize(new {clientId,result.GroupId,result.Active,canonicalHash=canonical?.Hash,canonicalState=canonical?.StateHash,liveHashes,
            audits=audits.Select(a=>new{a.RequestId,a.RequestHash,a.Action,a.ObjectId})},JsonOptions));
        await tx.RollbackAsync();return result;
    }
    public async Task<ExcelPayslipConsolidationStatus> UndoConsolidationAsync(int clientId,string groupId,ExcelPayslipConsolidationUndoRequest request,string actor)
    {
        if(clientId<=0 || !Guid.TryParseExact(groupId,"N",out _) || !Guid.TryParse(request.RequestId,out var parsed) || !System.Text.RegularExpressions.Regex.IsMatch(request.Token??"",@"^[a-fA-F0-9]{64}$"))throw new InvalidOperationException("Review the exact active grouping and its fresh undo token.");
        groupId=Guid.Parse(groupId).ToString("N");
        var requestId=parsed.ToString("N");var requestHash=ExcelPayslipSavedConsolidation.Hash(JsonSerializer.Serialize(new {groupId,request.Token},JsonOptions));
        // Read status first; the client lock below serializes a second fresh token verification.
        await using var db=Db();await db.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        if(await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE",new {ClientId=clientId},tx) is null)throw new InvalidOperationException("Client unavailable.");
        var audits=await ReadConsolidationAuditsAsync(db,clientId,tx);var group=audits.SingleOrDefault(a=>a.RequestId==groupId && a.Action==ConsolidationCreate) ?? throw new InvalidOperationException("Grouping not found for this client.");
        var proof=ConsolidationDocument(group,clientId);var replay=await ReadConsolidationRequestAsync(db,tx,clientId,requestId);
        if(replay is not null){ExcelPayslipSavedConsolidation.ValidateReplay(replay.Action,replay.RequestHash,ConsolidationUndo,requestHash);await tx.CommitAsync();return (await GetConsolidationAsync(clientId,proof.CanonicalBatchId,groupId))!;}
        if(!ExcelPayslipSavedConsolidation.GroupIsActive(groupId,audits.Select(a=>(a.Action,a.ObjectId))))throw new InvalidOperationException("This grouping is already undone. Refresh retained history.");
        var fresh=await GetConsolidationAsync(clientId,proof.CanonicalBatchId,groupId) ?? throw new InvalidOperationException("Grouping unavailable.");
        if(fresh.Token!=request.Token)throw new InvalidOperationException("Grouping or source history changed. Review a fresh undo preview.");
        await WriteConsolidationAuditAsync(db,tx,clientId,requestId,requestHash,ConsolidationUndo,groupId,proof,actor);
        await tx.CommitAsync();return (await GetConsolidationAsync(clientId,proof.CanonicalBatchId,groupId))!;
    }
    private static async Task<IReadOnlyList<ExcelPayslipBatchSummary>> FilterConsolidatedSummariesAsync(MySqlConnection db,int clientId,IReadOnlyList<ExcelPayslipBatchSummary> summaries,bool includeConsolidated=false)
    {
        var groups=ActiveConsolidations(await ReadConsolidationAuditsAsync(db,clientId),clientId).ToList();
        var fresh=new Dictionary<string,bool>(StringComparer.Ordinal);
        foreach(var group in groups)
        {
            var canonical=await ReadConsolidationSnapshotAsync(db,null,clientId,group.Proof.CanonicalBatchId);
            var sourceHashes=new Dictionary<string,string?>(StringComparer.Ordinal);
            foreach(var source in group.Proof.Sources)sourceHashes[source.BatchId]=(await ReadConsolidationSnapshotAsync(db,null,clientId,source.BatchId))?.Hash;
            fresh[group.Proof.GroupId]=ExcelPayslipSavedConsolidation.SnapshotBindingsMatch(canonical?.Hash,group.Proof.CanonicalHash,sourceHashes,group.Proof.Sources.ToDictionary(s=>s.BatchId,s=>s.SnapshotHash,StringComparer.Ordinal));
        }
        foreach(var summary in summaries)
        {
            var source=groups.SingleOrDefault(g=>g.Proof.Sources.Any(s=>s.BatchId==summary.Id));
            var canonical=groups.SingleOrDefault(g=>g.Proof.CanonicalBatchId==summary.Id);
            if(source.Proof is not null){summary.IsConsolidatedSource=true;summary.ConsolidationStale=!fresh[source.Proof.GroupId];summary.CanonicalBatchId=source.Proof.CanonicalBatchId;summary.ConsolidationGroupId=source.Proof.GroupId;}
            if(canonical.Proof is not null){summary.ConsolidationStale=!fresh[canonical.Proof.GroupId];summary.ConsolidationGroupId=canonical.Proof.GroupId;summary.ConsolidatedSourceBatchIds=canonical.Proof.Sources.Select(s=>s.BatchId).ToList();}
        }
        return summaries.Where(s=>!ExcelPayslipSavedConsolidation.ShouldHideSource(s.IsConsolidatedSource,!s.ConsolidationStale,includeConsolidated)).ToArray();
    }
    internal static async Task RequireMutableConsolidationSnapshotAsync(MySqlConnection db,MySqlTransaction? tx,int clientId,string batchId)
    {
        if(Guid.TryParseExact(batchId,"N",out var parsedBatch))batchId=parsedBatch.ToString("N");
        var groups=ActiveConsolidations(await ReadConsolidationAuditsAsync(db,clientId,tx),clientId);
        if(groups.Any(g=>g.Proof.CanonicalBatchId==batchId || g.Proof.Sources.Any(s=>s.BatchId==batchId)))
            throw new InvalidOperationException("Consolidated source snapshots are retained unchanged. Undo the grouping before changing source contacts; no historical delivery or financial evidence is rewritten.");
    }
    internal static async Task RequireCurrentConsolidationSourceAsync(MySqlConnection db,MySqlTransaction? tx,int clientId,string batchId)
    {
        if(Guid.TryParseExact(batchId,"N",out var parsedBatch))batchId=parsedBatch.ToString("N");
        var group=ActiveConsolidations(await ReadConsolidationAuditsAsync(db,clientId,tx),clientId).SingleOrDefault(g=>g.Proof.Sources.Any(s=>s.BatchId==batchId));
        if(group.Proof is not null)throw new InvalidOperationException("This retained version is consolidated into "+group.Proof.CanonicalBatchId+". Use the canonical saved batch for new configuration; historical receipts remain available.");
    }
    private static async Task AddConsolidationDeleteReferencesAsync(MySqlConnection db,MySqlTransaction tx,int clientId,string batchId,ExcelPayslipDeletePreview preview)
    {
        foreach(var group in ActiveConsolidations(await ReadConsolidationAuditsAsync(db,clientId,tx),clientId).Where(g=>g.Proof.CanonicalBatchId==batchId || g.Proof.Sources.Any(s=>s.BatchId==batchId)))
        {preview.ConsolidationReferences++;preview.Objects.Add(new("Consolidation",group.Proof.GroupId,"Retained","Canonical and retained source versions stay linked. Undo this reversible grouping before considering any independent cleanup; delivery/payroll guards still apply."));}
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    internal static bool HasCurrentWarningReview(ExcelPayslipRow row) => row.WarningReview is { } review
        && !string.IsNullOrWhiteSpace(review.ReviewedBy) && review.ReviewedAtUtc != default
        && review.Warnings is { Count: > 0 } && review.AmountsHash == NegativeAmountHash(row);

    internal static string WarningReviewToken(ExcelPayslipBatch batch, ExcelPayslipRow row) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { batch.ClientId, batch.Id, batch.Month,
            amounts = NegativeAmountHash(row), warnings = SourceRowWarnings(row) }, JsonOptions))));

    internal static void AnnotateWarningReviews(ExcelPayslipBatch batch)
    {
        foreach (var row in batch.Rows)
        {
            row.Warnings = RowWarnings(row);
            row.WarningReviewToken = WarningReviewToken(batch, row);
            if (!HasCurrentWarningReview(row)) row.WarningReview = null;
        }
    }

    public async Task<(ExcelPayslipBatch? Item, string? Error)> ReviewWarningsAsync(int clientId, string id,
        ExcelPayslipWarningReviewRequest request, string actor)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return (null, "Choose a saved payslip batch.");
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId=clientId }, tx);
        await RequireMutableConsolidationSnapshotAsync(db, tx, clientId, id);
        var json = await db.ExecuteScalarAsync<string?>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id FOR UPDATE",
            new { ClientId=clientId, Id=id }, tx);
        if (json is null) return (null, "The saved batch was not found for this client.");
        var patched = PatchWarningReviews(json, clientId, id, request, actor, DateTime.UtcNow);
        if (patched != json)
            await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Json AS JSON) WHERE client_id=@ClientId AND id=@Id",
                new { ClientId=clientId, Id=id, Json=patched }, tx);
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(patched, JsonOptions)!;
        await AnnotateEmploymentAsync(db, batch, tx, configuration);
        await tx.CommitAsync();
        return (batch, null);
    }

    // One atomic, metadata-only patch. Stale/foreign rows reject the whole request;
    // review never changes salary, formulas, source documents or delivery state.
    internal static string PatchWarningReviews(string json, int clientId, string batchId,
        ExcelPayslipWarningReviewRequest request, string actor, DateTime utc)
    {
        if (request is null || request.Action is not ("review" or "restore") || request.Rows is not {Count: > 0 and <= 1000}
            || request.Rows.Any(r => r is null || !TextValid(r.RowId,120,true) || !ValidSignature(r.Token))
            || request.Rows.Select(r=>r.RowId).Distinct(StringComparer.Ordinal).Count()!=request.Rows.Count)
            throw new InvalidOperationException("Choose distinct saved warning rows and refresh their review details.");
        if (!TextValid(actor,190,true) || utc==default) throw new InvalidOperationException("A signed-in reviewer is required.");
        var batch=JsonSerializer.Deserialize<ExcelPayslipBatch>(json,JsonOptions)
            ?? throw new InvalidOperationException("The saved batch is unavailable.");
        if(batch.ClientId!=clientId || batch.Id!=batchId) throw new InvalidOperationException("Choose a saved batch belonging to this client.");
        var selected=new List<ExcelPayslipRow>();
        foreach(var selection in request.Rows)
        {
            var row=batch.Rows.SingleOrDefault(r=>r.Id==selection.RowId)
                ?? throw new InvalidOperationException("A selected row does not belong to this saved batch.");
            if(!string.Equals(WarningReviewToken(batch,row),selection.Token,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The source amounts or warnings changed. Refresh and review them before clearing warnings.");
            selected.Add(row);
        }
        var document=JsonNode.Parse(json)!.AsObject();var changed=false;
        foreach(var row in selected)
        {
            var node=document["rows"]!.AsArray().Single(n=>n!["id"]!.GetValue<string>()==row.Id)!.AsObject();
            if(request.Action=="restore")
            {
                if(row.WarningReview is null)continue;
                row.WarningReview=null;node.Remove("warningReview");
            }
            else
            {
                if(RowWarnings(row).Count==0)continue;
                row.WarningReview=new(NegativeAmountHash(row),actor,utc,SourceRowWarnings(row));
                node["warningReview"]=JsonSerializer.SerializeToNode(row.WarningReview,JsonOptions);
            }
            node["warnings"]=JsonSerializer.SerializeToNode(RowWarnings(row),JsonOptions);
            // A response token must never become a persisted/imported approval.
            node.Remove("warningReviewToken");changed=true;
        }
        if(!changed)return json;
        var result=document.ToJsonString();
        if(Encoding.UTF8.GetByteCount(result)>MaximumBatchBytes)throw new InvalidOperationException("The reviewed batch exceeds its storage limit.");
        return result;
    }
}

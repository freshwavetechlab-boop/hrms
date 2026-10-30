using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    internal const string NegativeAmountWarning = "This source row contains a negative amount; review it before export or sending.";
    internal static bool HasNegativeAmounts(ExcelPayslipRow row) => row.NetPay < 0
        || row.Earnings.Concat(row.Deductions).Concat(row.EmployerContributions).Any(item => item.Amount < 0);

    internal static string NegativeAmountHash(ExcelPayslipRow row) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { row.Id, row.SourceRow, row.EmployeeCode, row.EmployeeName,
            row.Earnings, row.Deductions, row.EmployerContributions, row.NetPay, row.DeclaredGross, row.DeclaredDeductions }, JsonOptions))));

    internal static bool HasCurrentNegativeAmountReview(ExcelPayslipRow row) => row.NegativeAmountReview is { } review
        && !string.IsNullOrWhiteSpace(review.ReviewedBy) && review.ReviewedAtUtc != default
        && review.AmountsHash == NegativeAmountHash(row);

    public async Task<(ExcelPayslipBatch? Item, string? Error)> ReviewNegativeAmountAsync(int clientId, string id,
        ExcelPayslipNegativeAmountReviewRequest request, string actor)
    {
        if (!Guid.TryParseExact(id, "N", out _) || !TextValid(request.RowId, 120, true)) return (null, "Choose a saved payslip row.");
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        // Use the existing client/batch lock order and preserve mail state and formula documents.
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx);
        await RequireMutableConsolidationSnapshotAsync(db, tx, clientId, id);
        var json = await db.ExecuteScalarAsync<string?>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id FOR UPDATE",
            new { ClientId = clientId, Id = id }, tx);
        if (json is null) return (null, "The saved batch was not found for this client.");
        var patched = PatchNegativeAmountReview(json, clientId, id, request.RowId, actor, DateTime.UtcNow);
        if (patched != json)
            await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Json AS JSON) WHERE client_id=@ClientId AND id=@Id",
                new { ClientId = clientId, Id = id, Json = patched }, tx);
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(patched, JsonOptions)!;
        await AnnotateEmploymentAsync(db, batch, tx);
        await tx.CommitAsync();
        return (batch, null);
    }

    internal static string PatchNegativeAmountReview(string json, int clientId, string batchId, string rowId, string actor, DateTime utc)
    {
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(json, JsonOptions)!;
        if (batch.ClientId != clientId || batch.Id != batchId) throw new InvalidOperationException("Choose a saved batch belonging to this client.");
        var row = batch.Rows.SingleOrDefault(item => item.Id == rowId)
            ?? throw new InvalidOperationException("The selected row does not belong to this saved batch.");
        if (!HasNegativeAmounts(row)) throw new InvalidOperationException("This row has no negative source amount to review.");
        if (!TextValid(actor, 190, true) || utc == default) throw new InvalidOperationException("A signed-in reviewer is required.");
        if (HasCurrentNegativeAmountReview(row)) return json;
        row.NegativeAmountReview = new(NegativeAmountHash(row), actor, utc);
        // Patch metadata only, keeping exact monetary literals, unknown fields and every other row intact.
        var document = JsonNode.Parse(json)!.AsObject();
        var savedRow = document["rows"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == rowId)!;
        savedRow["negativeAmountReview"] = JsonSerializer.SerializeToNode(row.NegativeAmountReview, JsonOptions);
        savedRow["warnings"] = JsonSerializer.SerializeToNode(RowWarnings(row), JsonOptions);
        var result = document.ToJsonString();
        if (Encoding.UTF8.GetByteCount(result) > MaximumBatchBytes) throw new InvalidOperationException("The reviewed batch exceeds its storage limit.");
        return result;
    }
}

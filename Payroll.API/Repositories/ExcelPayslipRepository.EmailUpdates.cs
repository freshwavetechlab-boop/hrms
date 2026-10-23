using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    public async Task<(ExcelPayslipBatch? Item, string? Error)> UpdateEmailsAsync(int clientId, string id,
        ExcelPayslipEmailUpdates request, string actor)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return (null, "Invalid payslip batch.");
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        // Same client lock and order as mail submission. Never reset delivery state or queued recipients.
        await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { ClientId = clientId }, tx);
        var json = await db.ExecuteScalarAsync<string?>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id FOR UPDATE", new { ClientId = clientId, Id = id }, tx);
        if (json is null) return (null, "The saved batch was not found for this client.");
        var source = await db.ExecuteScalarAsync<string?>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=@ClientId AND batch_id=@Id FOR UPDATE", new { ClientId = clientId, Id = id }, tx);
        var patched = PatchEmailDocuments(json, source, clientId, id, request, actor, DateTime.UtcNow);
        if (patched.Changed > 0)
        {
            await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Json AS JSON) WHERE client_id=@ClientId AND id=@Id", new { ClientId = clientId, Id = id, Json = patched.BatchJson }, tx);
            if (patched.SourceJson is not null && patched.SourceJson != source)
                await db.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Json AS JSON) WHERE client_id=@ClientId AND batch_id=@Id", new { ClientId = clientId, Id = id, Json = patched.SourceJson }, tx);
        }
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(patched.BatchJson, JsonOptions)!;
        await AnnotateEmploymentAsync(db, batch, tx);
        await tx.CommitAsync();
        return (batch, null);
    }

    // Patch raw documents, not a typed salary round-trip: preserve unknown fields and exact monetary literals.
    internal static (string BatchJson, string? SourceJson, int Changed) PatchEmailDocuments(string batchJson,
        string? sourceJson, int clientId, string batchId, ExcelPayslipEmailUpdates request, string actor, DateTime utc)
    {
        var document = JsonNode.Parse(batchJson)!.AsObject();
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(batchJson, JsonOptions)!;
        if (batch.ClientId != clientId || batch.Id != batchId || batch.Month != request.Month)
            throw new InvalidOperationException("The email list does not belong to this client, batch and salary month.");
        if (request.Updates is null || request.Updates.Count is < 1 or > 1000 || !TextValid(request.SourceFileName, 240))
            throw new InvalidOperationException("Import between 1 and 1000 email updates from one exported list.");
        var lookup = batch.Rows.ToDictionary(r => r.Id);
        var seen = new HashSet<string>();
        var changed = new Dictionary<string, string>();
        var auditRows = new JsonArray();
        foreach (var update in request.Updates)
        {
            if (update is null || update.RowId is null || !seen.Add(update.RowId) || !lookup.TryGetValue(update.RowId, out var row)
                || row.EmployeeCode.Trim() != update.EmployeeCode?.Trim() || row.EmployeeName.Trim() != update.EmployeeName?.Trim()
                || EmailUpdateLocation(row).Trim() != update.Location?.Trim())
                throw new InvalidOperationException("An email row is unknown, duplicated or has changed employee details. Export the current batch again.");
            if (!TextValid(update.Email, 254, true) || !TryEmail(update.Email, out var email))
                throw new InvalidOperationException($"{row.EmployeeCode}: enter one valid email address. Blank cells must be skipped, not cleared.");
            // An interrupted save can be retried without another audit entry or a new batch.
            if (row.Email == email) continue;
            if (row.Email != update.PreviousEmail)
                throw new InvalidOperationException($"{row.EmployeeCode}: the saved email changed after your preview. Reload the batch and import again.");
            changed[row.Id] = email;
            auditRows.Add(new JsonObject { ["rowId"] = row.Id, ["employeeCode"] = row.EmployeeCode,
                ["previousEmail"] = row.Email, ["email"] = email });
        }
        if (changed.Count == 0) return (batchJson, sourceJson, 0);
        foreach (var row in document["rows"]!.AsArray())
            if (changed.TryGetValue(row!["id"]!.GetValue<string>(), out var email)) row["email"] = email;
        var history = document["emailUpdateHistory"] as JsonArray;
        if (history is null) { history = new JsonArray(); document["emailUpdateHistory"] = history; }
        history.Add(new JsonObject { ["atUtc"] = utc.ToString("o"), ["actor"] = Limit(actor, 190),
            ["sourceFileName"] = request.SourceFileName, ["changes"] = auditRows });
        string? updatedSource = sourceJson;
        if (sourceJson is not null)
        {
            var source = JsonNode.Parse(sourceJson)!;
            var emailColumns = source["columns"]!.AsArray().Where(c => c!["kind"]!.GetValue<string>() == "email")
                .Select(c => c!["columnIndex"]!.GetValue<int>()).ToArray();
            var emails = batch.Rows.Where(r => changed.ContainsKey(r.Id)).ToDictionary(r => r.SourceRow, r => changed[r.Id]);
            foreach (var row in source["rows"]!.AsArray())
                if (emails.TryGetValue(row!["sourceRow"]!.GetValue<int>(), out var email))
                    foreach (var column in emailColumns)
                    {
                        var cells = row["cells"]!.AsArray();
                        while (cells.Count <= column) cells.Add(null);
                        cells[column] = new JsonObject { ["value"] = email };
                    }
            updatedSource = source.ToJsonString();
        }
        var updated = document.ToJsonString();
        if (Encoding.UTF8.GetByteCount(updated) > MaximumBatchBytes || updatedSource is not null && Encoding.UTF8.GetByteCount(updatedSource) > 16 * 1024 * 1024)
            throw new InvalidOperationException("Email updates exceed this batch's storage limit. No changes were saved.");
        return (updated, updatedSource, changed.Count);
    }

    internal static string EmailUpdateLocation(ExcelPayslipRow row) => row.Information.FirstOrDefault(i =>
        Regex.IsMatch(i.Label, @"work\s*location|location\s*name|^location$|tehsil", RegexOptions.IgnoreCase))?.Value ?? "";
}

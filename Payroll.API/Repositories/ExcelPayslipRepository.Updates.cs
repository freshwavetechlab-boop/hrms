using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    private sealed class StoredUpdateReview
    {
        public int Version { get; set; } = 1;
        public int ActorId { get; set; }
        public int ClientId { get; set; }
        public string BatchId { get; set; } = "";
        public DateTime ExpiresAtUtc { get; set; }
        public string BatchHash { get; set; } = "";
        public string SourceHash { get; set; } = "";
        public ExcelPayslipUpdatePreviewRequest Request { get; set; } = new();
        public List<ExcelPayslipUpdateChange> Changes { get; set; } = [];
        public List<UpdateInput> Inputs { get; set; } = [];
        public List<string> Blockers { get; set; } = [];
        public Dictionary<int, string> MasterHashes { get; set; } = [];
    }
    private sealed class StoredUpdateReceipt
    {
        public int ActorId { get; set; }
        public int ClientId { get; set; }
        public string BatchId { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public int AppliedChanges { get; set; }
        public int UpdatedEmployees { get; set; }
        public string SourceFileName { get; set; } = "";
        public DateTime AppliedAtUtc { get; set; }
        public List<ExcelPayslipUpdateChange> Changes { get; set; } = [];
    }
    private PortableIntegrationCredentialProtector UpdateProtector => updateReviewProtector
        ?? new PortableIntegrationCredentialProtector(configuration, new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);
    private static string UpdateStorageCode(string kind, int clientId, string id) => "xp_update_" + kind + ":" + clientId + ":" + id;
    private static string UpdateTextHash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string UpdateMasterHash(UpdateContext context, int employeeId) => CorrectionHash(new
        { Fields = context.Masters[employeeId].OrderBy(field => field.Key, StringComparer.Ordinal), Basis = context.MasterBasis.GetValueOrDefault(employeeId, "") });

    internal static bool CanUseBulkUpdate(AuthUser actor, int clientId) => actor.Id > 0 && actor.IsActive && !actor.MustChangePassword
        && actor.Permissions.Contains("employees.manage", StringComparer.OrdinalIgnoreCase) && SelectedClientAccess.Allows(actor, clientId);
    private static void RequireBulkUpdateActor(AuthUser actor, int clientId)
    {
        if (!CanUseBulkUpdate(actor, clientId)) throw new UnauthorizedAccessException("Employee Master management permission is required for this client.");
    }
    private static void RequireUpdateBatchId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("Select an existing saved payslip batch.");
    }

    public async Task<ExcelPayslipUpdateCatalog> GetBulkUpdateCatalogAsync(int clientId, string id, AuthUser actor)
    {
        RequireBulkUpdateActor(actor, clientId); RequireUpdateBatchId(id);
        await using var db = Db(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        var context = await LoadUpdateContextAsync(db, tx, clientId, id);
        var result = new ExcelPayslipUpdateCatalog { ClientId = clientId, BatchId = id, Month = context.Raw.Month,
            Fields = context.Fields.Values.Select(binding => binding.Field).ToList(), Blockers = context.Blockers };
        result.Rows = context.Raw.Rows.Select(row => new ExcelPayslipUpdateCatalogRow { EmployeeCode = UpdateCode(context, row), EmployeeName = UpdateName(context, row),
            Values = context.Fields.ToDictionary(pair => pair.Key, pair => UpdateFieldValue(context, row, pair.Value), StringComparer.Ordinal) }).ToList();
        await tx.RollbackAsync(); return result;
    }

    public async Task<ExcelPayslipUpdatePreview> PreviewBulkUpdateAsync(int clientId, string id, ExcelPayslipUpdatePreviewRequest request, AuthUser actor)
    {
        RequireBulkUpdateActor(actor, clientId); RequireUpdateBatchId(id);
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, JsonOptions)) > 4 * 1024 * 1024)
            throw new InvalidOperationException("Select fewer update rows; this review exceeds 4 MiB.");
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE", new { ClientId = clientId }, tx) is null)
            throw new InvalidOperationException("Select an active client.");
        var context = await LoadUpdateContextAsync(db, tx, clientId, id);
        var plan = await PrepareUpdatePlanAsync(db, tx, context, request);
        var token = Guid.NewGuid().ToString("N");
        var review = new StoredUpdateReview { ActorId = actor.Id, ClientId = clientId, BatchId = id, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30),
            BatchHash = UpdateTextHash(context.BatchJson), SourceHash = UpdateTextHash(context.SourceJson), Request = request,
            Changes = plan.Changes, Inputs = plan.Inputs, Blockers = plan.Blockers,
            MasterHashes = plan.Inputs.Where(input => input.MasterKey is not null).Select(input => input.EmployeeId).Distinct()
                .ToDictionary(employeeId => employeeId, employeeId => UpdateMasterHash(context, employeeId)) };
        // Expired, unusable reviews are temporary encrypted workspace data. Keep
        // applied receipts permanently; their separate prefix is never pruned.
        await db.ExecuteAsync(@"DELETE FROM modulesettings WHERE client_id=@ClientId AND ModuleCode LIKE @Prefix
AND JSON_UNQUOTE(JSON_EXTRACT(SettingsJson,'$.expiresAtUtc')) < @Cutoff LIMIT 100",
            new { ClientId = clientId, Prefix = "xp_update_review:" + clientId + ":%", Cutoff = DateTime.UtcNow.ToString("o") }, tx);
        await StoreUpdateDocumentAsync(db, tx, clientId, UpdateStorageCode("review", clientId, token), review, review.ExpiresAtUtc);
        await tx.CommitAsync();
        return new() { Token = token, Changes = plan.Changes, Blockers = plan.Blockers };
    }

    public async Task<ExcelPayslipUpdateApplyResult> ApplyBulkUpdateAsync(int clientId, string id, ExcelPayslipUpdateApplyRequest request, AuthUser actor)
    {
        RequireBulkUpdateActor(actor, clientId); RequireUpdateBatchId(id);
        if (request is null || !Guid.TryParseExact(request.Token, "N", out _) || !Guid.TryParse(request.RequestId, out var requestGuid)
            || !request.Acknowledged || request.SelectedChangeIds is null || request.SelectedChangeIds.Count is < 1 or > 5000
            || request.SelectedChangeIds.Any(string.IsNullOrWhiteSpace) || request.SelectedChangeIds.Distinct(StringComparer.Ordinal).Count() != request.SelectedChangeIds.Count)
            throw new InvalidOperationException("Select reviewed changes and acknowledge the update before applying it.");
        var requestId = requestGuid.ToString("N");
        var fingerprint = CorrectionHash(new { clientId, BatchId = id, actor.Id, request.Token, Ids = request.SelectedChangeIds.OrderBy(value => value, StringComparer.Ordinal), request.Acknowledged, request.AcknowledgeZeroChanges });
        await using var db = Db(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE", new { ClientId = clientId }, tx) is null)
            throw new InvalidOperationException("Select an active client.");
        var receiptCode = UpdateStorageCode("receipt", clientId, requestId);
        var receipt = await ReadUpdateDocumentAsync<StoredUpdateReceipt>(db, tx, clientId, receiptCode, true);
        if (receipt is not null)
        {
            if (receipt.ActorId != actor.Id || receipt.ClientId != clientId || receipt.BatchId != id || receipt.Fingerprint != fingerprint)
                throw new InvalidOperationException("This update request ID was already used for a different review or selection.");
            var existingBatch = await ReadCorrectionBatchAsync(db, clientId, id, tx) ?? throw new InvalidOperationException("The saved batch no longer exists.");
            await tx.RollbackAsync();
            return new() { Batch = existingBatch, AppliedChanges = receipt.AppliedChanges, UpdatedEmployees = receipt.UpdatedEmployees, Replayed = true };
        }
        var reviewCode = UpdateStorageCode("review", clientId, request.Token);
        var review = await ReadUpdateDocumentAsync<StoredUpdateReview>(db, tx, clientId, reviewCode, true)
            ?? throw new InvalidOperationException("This Excel review is unavailable. Preview the file again; no changes were applied.");
        if (review.Version != 1 || review.ActorId != actor.Id || review.ClientId != clientId || review.BatchId != id || review.ExpiresAtUtc < DateTime.UtcNow)
            throw new InvalidOperationException("This Excel review expired or belongs to another user/client/batch. Preview again.");
        var selected = request.SelectedChangeIds.ToHashSet(StringComparer.Ordinal);
        var reviewed = review.Changes.Where(change => selected.Contains(change.Id)).ToArray();
        if (reviewed.Length != selected.Count || reviewed.Any(change => !change.CanApply || change.Computed) || review.Blockers.Count > 0)
            throw new InvalidOperationException("Only valid, individually reviewed input changes can be selected. Resolve the review blockers first.");
        if (!request.AcknowledgeZeroChanges && reviewed.Any(change => change.Type == "number" && decimal.TryParse(change.NewValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) && value == 0))
            throw new InvalidOperationException("Explicitly acknowledge the selected zero-value updates before applying them.");
        await db.ExecuteScalarAsync<string?>("SELECT id FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id FOR UPDATE", new { ClientId = clientId, Id = id }, tx);
        await db.ExecuteScalarAsync<string?>("SELECT batch_id FROM excel_payslip_calculation_sources WHERE client_id=@ClientId AND batch_id=@Id FOR UPDATE", new { ClientId = clientId, Id = id }, tx);
        var selectedInputs = review.Inputs.Where(input => reviewed.Any(change => change.FieldId == input.FieldId
            && change.Id == UpdateChangeId(input.RowId, change))).ToArray();
        var masterIds = selectedInputs.Where(input => input.MasterKey is not null).Select(input => input.EmployeeId).Distinct().ToArray();
        if (masterIds.Length > 0)
        {
            await db.QueryAsync<int>("SELECT Id FROM employees WHERE ClientId=@ClientId AND Id IN @Ids ORDER BY Id FOR UPDATE", new { ClientId = clientId, Ids = masterIds }, tx);
            foreach (var table in new[] { "employeepersonaldetails", "employeepaymentdetails", "employeesalarycomponents" })
                await db.QueryAsync<int>($"SELECT EmployeeId FROM {table} WHERE EmployeeId IN @Ids FOR UPDATE", new { Ids = masterIds }, tx);
        }
        var context = await LoadUpdateContextAsync(db, tx, clientId, id);
        if (review.BatchHash != UpdateTextHash(context.BatchJson) || review.SourceHash != UpdateTextHash(context.SourceJson))
            throw new InvalidOperationException("The saved batch or formulas changed after preview. Preview the file again.");
        if (context.Blockers.Count > 0) throw new InvalidOperationException(string.Join(" ", context.Blockers));
        foreach (var employeeId in masterIds)
            if (!context.Masters.ContainsKey(employeeId) || review.MasterHashes.GetValueOrDefault(employeeId) != UpdateMasterHash(context, employeeId))
                throw new InvalidOperationException("Employee Master changed after preview. Preview the file again before applying any change.");
        var selectedRequest = new ExcelPayslipUpdatePreviewRequest { Month = review.Request.Month, SourceFileName = review.Request.SourceFileName,
            Rows = selectedInputs.GroupBy(input => input.RowId).Select(group => new ExcelPayslipUpdateRowInput
            { EmployeeCode = UpdateCode(context, context.Raw.Rows.Single(row => row.Id == group.Key)), Values = group.ToDictionary(input => input.FieldId, input => input.Value, StringComparer.Ordinal) }).ToList() };
        var plan = await PrepareUpdatePlanAsync(db, tx, context, selectedRequest);
        if (plan.Blockers.Count > 0 || plan.Changes.Any(change => !change.Computed && !change.CanApply)
            || plan.Changes.Where(change => change.CanApply).Any(change => !selected.Contains(change.Id))
            || plan.Changes.Count(change => change.CanApply) != selected.Count
            || plan.Changes.Where(change => change.Computed).Any(change => review.Changes.All(prior => !prior.Computed || prior.Id != change.Id)))
            throw new InvalidOperationException("The selected changes have a different calculated impact. Preview this selected subset again before applying it.");
        // Only the two reviewed JSON documents, explicitly selected Master fields,
        // audits and the idempotency receipt share this single commit. Mail state is never written.
        foreach (var (employeeId, changes) in plan.MasterChanges.OrderBy(pair => pair.Key))
            await EmployeeRepository.ApplyReviewedExcelMasterFieldsAsync(db, tx, employeeId, clientId, changes, actor.Email);
        if (!JsonNode.DeepEquals(JsonNode.Parse(context.BatchJson), JsonNode.Parse(plan.BatchJson)))
            await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Json AS JSON) WHERE client_id=@ClientId AND id=@Id", new { ClientId = clientId, Id = id, Json = plan.BatchJson }, tx);
        if (context.Source is not null && !JsonNode.DeepEquals(JsonNode.Parse(context.SourceJson), JsonNode.Parse(plan.SourceJson)))
            await db.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Json AS JSON) WHERE client_id=@ClientId AND batch_id=@Id", new { ClientId = clientId, Id = id, Json = plan.SourceJson }, tx);
        var count = plan.Changes.Count;
        await db.ExecuteAsync(@"INSERT INTO auditlogs(UserId,UserEmail,Action,Resource,Method,Path,StatusCode,DetailsJson)
VALUES(@ActorId,@Actor,'excel-payslips.reviewed-update',@BatchId,'POST','/api/excel-payslips/batches/update-apply',200,CAST(@Details AS JSON))",
            new { ActorId = actor.Id, Actor = actor.Email, BatchId = id, Details = JsonSerializer.Serialize(new { clientId, batchId = id, requestId,
                changes = plan.Changes.Select(change => new { change.FieldId, change.Destination, change.Computed }), updatedEmployees = plan.MasterChanges.Count, noEmployeeCreation = true, mailHistoryRetained = true }, JsonOptions) }, tx);
        await StoreUpdateDocumentAsync(db, tx, clientId, receiptCode, new StoredUpdateReceipt { ActorId = actor.Id, ClientId = clientId, BatchId = id,
            Fingerprint = fingerprint, AppliedChanges = count, UpdatedEmployees = plan.MasterChanges.Count,
            SourceFileName = review.Request.SourceFileName, AppliedAtUtc = DateTime.UtcNow, Changes = plan.Changes }, null);
        await db.ExecuteAsync("DELETE FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code", new { ClientId = clientId, Code = reviewCode }, tx);
        var resultBatch = await ReadCorrectionBatchAsync(db, clientId, id, tx) ?? throw new InvalidOperationException("The updated batch could not be verified.");
        await tx.CommitAsync();
        return new() { Batch = resultBatch, AppliedChanges = count, UpdatedEmployees = plan.MasterChanges.Count };
    }

    private async Task StoreUpdateDocumentAsync<T>(MySqlConnection db, MySqlTransaction tx, int clientId, string code, T value, DateTime? expires)
    {
        var plaintext = JsonSerializer.Serialize(value, JsonOptions);
        if (Encoding.UTF8.GetByteCount(plaintext) > 4 * 1024 * 1024) throw new InvalidOperationException("This Excel review is too large. Review fewer rows at a time.");
        var json = JsonSerializer.Serialize(new { payload = UpdateProtector.ProtectExcelPayslipUpdate(plaintext), expiresAtUtc = expires }, JsonOptions);
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@ClientId,@Code,TRUE,CAST(@Json AS JSON))",
            new { ClientId = clientId, Code = code, Json = json }, tx);
    }
    private async Task<T?> ReadUpdateDocumentAsync<T>(MySqlConnection db, MySqlTransaction tx, int clientId, string code, bool forUpdate) where T : class
    {
        var json = await db.ExecuteScalarAsync<string?>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code" + (forUpdate ? " FOR UPDATE" : ""), new { ClientId = clientId, Code = code }, tx);
        if (json is null) return null;
        var payload = JsonNode.Parse(json)?["payload"]?.GetValue<string>();
        var plaintext = payload is null ? null : UpdateProtector.UnprotectExcelPayslipUpdate(payload);
        return plaintext is null ? null : JsonSerializer.Deserialize<T>(plaintext, JsonOptions);
    }
}

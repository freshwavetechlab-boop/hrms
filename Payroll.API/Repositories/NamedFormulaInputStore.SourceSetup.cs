using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

internal static partial class NamedFormulaInputStore
{
    internal const string SourceSetupApprovalPrefix = "named-input-setup:";
    private sealed record SourceSetupApproval(int Version, int ClientId, string ImportId, string RequestId, string NormalRequestId,
        string RequestHash, string PlanHash, string RequestJsonHash, string ResultJsonHash, int ReviewerId,
        List<NamedFormulaInputManifest> Manifests);
    private sealed class SourceSetupReceipt
    {
        public string Id { get; set; } = "";
        public string Status { get; set; } = "";
        public string BatchId { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string PlanHash { get; set; } = "";
        public string RequestJson { get; set; } = "";
        public string ResultJson { get; set; } = "";
        public string RequestJsonHash { get; set; } = "";
        public string ResultJsonHash { get; set; } = "";
    }

    private static Task<SourceSetupReceipt?> ReadSourceSetupReceiptAsync(MySqlConnection db, IDbTransaction tx, int client, string import) =>
        db.QuerySingleOrDefaultAsync<SourceSetupReceipt>(@"SELECT id Id,status Status,batch_id BatchId,request_id RequestId,request_hash RequestHash,plan_hash PlanHash,
CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson,SHA2(CAST(request_json AS CHAR),256) RequestJsonHash,SHA2(CAST(result_json AS CHAR),256) ResultJsonHash
FROM excel_payroll_setup_imports WHERE client_id=@Client AND id=@Import", new { Client = client, Import = import }, tx);

    internal static async Task InsertSourceSetupApprovalAsync(MySqlConnection db, IDbTransaction tx, IConfiguration configuration,
        int client, string import, string normalRequestId, string requestHash, string planHash, int reviewerId,
        List<NamedFormulaInputManifest> manifests)
    {
        if (reviewerId <= 0 || !Guid.TryParse(normalRequestId, out var request) || manifests.Count is < 1 or > 100
            || manifests.Any(m => m.ClientId != client || m.RequestId != request.ToString("N") || m.ReviewerId != reviewerId
                || m.ManifestHash != ManifestHash(m)))
            throw new InvalidOperationException("A complete authenticated first-source named review is required.");
        var saved = await ReadSourceSetupReceiptAsync(db, tx, client, import)
            ?? throw new InvalidOperationException("The same-transaction normal source setup receipt is missing.");
        var approval = new SourceSetupApproval(1, client, import, request.ToString("N"), normalRequestId, requestHash, planHash,
            saved.RequestJsonHash, saved.ResultJsonHash, reviewerId, manifests);
        ValidateSourceSetupApproval(approval, saved);
        var current = await db.QuerySingleAsync<(string BatchHash, string SourceHash)>(@"SELECT SHA2(CAST(b.batch_json AS CHAR),256) BatchHash,SHA2(CAST(s.source_json AS CHAR),256) SourceHash
FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@Batch",
            new { Client = client, Batch = saved.BatchId }, tx);
        if (manifests.Any(m => m.RawBatchHash != current.BatchHash || m.RawSourceHash != current.SourceHash))
            throw new InvalidOperationException("The original source changed during first-source setup; no named assignments were approved.");
        var correctionExists = await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM modulesettings WHERE client_id=@Client AND ModuleCode=@Code)",
            new { Client = client, Code = ExcelPayrollSetupRepository.LocationCorrectionReceiptPrefix + approval.RequestId }, tx);
        if (correctionExists) throw new InvalidOperationException("The named request already has another approval origin.");
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@Client,@Code,TRUE,CAST(@Json AS JSON))",
            new { Client = client, Code = SourceSetupApprovalPrefix + approval.RequestId, Json = Protect(approval, configuration, "SourceSetupApproval") }, tx);
    }

    internal static async Task RequireSourceSetupReplayAsync(MySqlConnection db, IDbTransaction tx, IConfiguration configuration,
        int client, string import, string normalRequestId)
    {
        if (!Guid.TryParse(normalRequestId, out var id)) throw new InvalidOperationException("The original source setup request reference is invalid.");
        var approval = await ReadSourceSetupApprovalAsync(db, tx, configuration, client, id.ToString("N"));
        if (approval.ImportId != import || approval.NormalRequestId != normalRequestId)
            throw new InvalidOperationException("The named first-source receipt belongs to another reviewed operation.");
    }

    private static async Task<SourceSetupApproval> ReadSourceSetupApprovalAsync(MySqlConnection db, IDbTransaction tx,
        IConfiguration config, int client, string requestId)
    {
        var setting = await db.QuerySingleOrDefaultAsync<Setting>("SELECT CAST(SettingsJson AS CHAR) Value,IsEnabled Enabled FROM modulesettings WHERE client_id=@Client AND ModuleCode=@Code",
            new { Client = client, Code = SourceSetupApprovalPrefix + requestId }, tx);
        if (setting is null || !setting.Enabled) throw new InvalidOperationException("The authenticated first-source named approval is missing or disabled.");
        var approval = Read<SourceSetupApproval>(setting.Value, config, "SourceSetupApproval");
        if (approval.ClientId != client || approval.RequestId != requestId) throw new InvalidOperationException("The first-source named approval has a different owner.");
        var saved = await ReadSourceSetupReceiptAsync(db, tx, client, approval.ImportId)
            ?? throw new InvalidOperationException("The approved normal source setup receipt is unavailable.");
        ValidateSourceSetupApproval(approval, saved);
        return approval;
    }

    private static void ValidateSourceSetupApproval(SourceSetupApproval approval, SourceSetupReceipt saved)
    {
        if (approval.Version != 1 || approval.ClientId <= 0 || approval.ReviewerId <= 0
            || !Guid.TryParseExact(approval.ImportId, "N", out _) || !Guid.TryParseExact(approval.RequestId, "N", out _)
            || !Guid.TryParse(approval.NormalRequestId, out var requestId) || requestId.ToString("N") != approval.RequestId
            || saved.Id != approval.ImportId || saved.Status != "Applied" || saved.RequestId != approval.NormalRequestId
            || saved.RequestHash != approval.RequestHash || saved.PlanHash != approval.PlanHash
            || saved.RequestJsonHash != approval.RequestJsonHash || saved.ResultJsonHash != approval.ResultJsonHash
            || approval.Manifests is null || approval.Manifests.Count is < 1 or > 100
            || approval.Manifests.Select(m => m.AssignmentId).Distinct(StringComparer.Ordinal).Count() != approval.Manifests.Count)
            throw new InvalidOperationException("The protected first-source named approval no longer matches the exact Applied receipt.");
        var request = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(saved.RequestJson, Json)
            ?? throw new InvalidOperationException("The approved source setup request is invalid.");
        var result = JsonSerializer.Deserialize<ExcelPayrollSetupResult>(saved.ResultJson, Json)
            ?? throw new InvalidOperationException("The approved source setup result is invalid.");
        var review = request.NamedInputReview;
        if (review is null || request.LocationAllocations is null || request.BatchId != saved.BatchId || result.ImportId != saved.Id
            || !request.RowIds.Order(StringComparer.Ordinal).SequenceEqual(approval.Manifests.Select(m => m.RowId).Order(StringComparer.Ordinal))
            || result.LocationAssignmentReceipts.Count != approval.Manifests.Count)
            throw new InvalidOperationException("The first-source named approval does not retain the complete reviewed source selection.");
        foreach (var m in approval.Manifests)
        {
            var group = request.LocationAllocations.Groups.SingleOrDefault(g => g.EmployeeId == m.EmployeeId);
            var parts = group?.Parts.Where(p => p.RowId == m.RowId && p.WorkLocationId == m.WorkLocationId && p.TemplateId == m.TemplateId).ToArray();
            var receipts = result.LocationAssignmentReceipts.Where(r => r.AssignmentId == m.AssignmentId && r.RowId == m.RowId && r.EmployeeId == m.EmployeeId
                && r.PartKey == m.SourcePartKey && r.TemplateId == m.TemplateId && r.WorkLocationId == m.WorkLocationId && r.SourceHash == m.SourceHash
                && r.PayPeriod == m.Month && r.RulesHash == ReviewedNamedFormulaInputs.RulesHash(m.NativeRulesHash, m.DeclarationHash)).ToArray();
            var decisions = request.EmployeeDecisions.Where(d => d.RowId == m.RowId).ToArray();
            if (m.Version != 1 || m.ClientId != approval.ClientId || m.RequestId != approval.RequestId || m.ReviewerId != approval.ReviewerId
                || m.ReviewedAtUtc == DateTime.MinValue || m.ManifestHash != ManifestHash(m) || m.BatchId != saved.BatchId
                || m.TemplateId != review.TemplateId || m.DeclarationHash != review.DeclarationHash || m.Month != request.LocationAllocations.PayrollPeriod
                || parts?.Length != 1 || receipts.Length != 1 || group!.ReferenceAssignmentId != m.ReferenceAssignmentId
                || decisions.Length != 1 || decisions[0].MatchedEmployeeId != m.EmployeeId || decisions[0].AllowCreate
                || !group.Parts.Select(p => result.LocationAssignmentReceipts.Single(r => r.RowId == p.RowId).PartKey).Order(StringComparer.Ordinal).SequenceEqual(m.CompleteSourcePartKeys)
                || !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(review.Bindings.OrderBy(b => b.Key, StringComparer.Ordinal), Json),
                    JsonSerializer.SerializeToNode(m.Cells.Select(c => new NamedFormulaInputBinding(c.Key, c.ColumnIndex, c.HeaderRow, c.HumanHeader)).OrderBy(b => b.Key, StringComparer.Ordinal), Json)))
                throw new InvalidOperationException("The first-source named approval contains an out-of-scope employee, source part, assignment or reviewer.");
        }
    }

    // A new first-source approval is its durable signed source-to-Master link.
    // Existing correction approvals retain their original ProfileLinks gate.
    private static async Task<ExcelPayrollSetupRequest?> RequireAssignmentApprovalAsync(MySqlConnection db, IDbTransaction tx,
        IConfiguration config, NamedFormulaInputManifest m)
    {
        var correction = await db.QuerySingleOrDefaultAsync<Setting>("SELECT CAST(SettingsJson AS CHAR) Value,IsEnabled Enabled FROM modulesettings WHERE client_id=@Client AND ModuleCode=@Code",
            new { Client = m.ClientId, Code = ExcelPayrollSetupRepository.LocationCorrectionReceiptPrefix + m.RequestId }, tx);
        var sourceSetup = await db.QuerySingleOrDefaultAsync<Setting>("SELECT CAST(SettingsJson AS CHAR) Value,IsEnabled Enabled FROM modulesettings WHERE client_id=@Client AND ModuleCode=@Code",
            new { Client = m.ClientId, Code = SourceSetupApprovalPrefix + m.RequestId }, tx);
        if (correction is not null && sourceSetup is not null)
            throw new InvalidOperationException("The named assignment has ambiguous approval origins.");
        if (sourceSetup is not null)
        {
            var approval = await ReadSourceSetupApprovalAsync(db, tx, config, m.ClientId, m.RequestId);
            var retained = approval.Manifests.Where(value => value.AssignmentId == m.AssignmentId).ToArray();
            if (retained.Length != 1 || !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(retained[0], Json), JsonSerializer.SerializeToNode(m, Json)))
                throw new InvalidOperationException("The exact immutable operands are not retained by the first-source approval.");
            var saved = await ReadSourceSetupReceiptAsync(db, tx, m.ClientId, approval.ImportId);
            return JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(saved!.RequestJson, Json)!;
        }
        if (correction is null || !correction.Enabled)
            throw new InvalidOperationException("The immutable named operands have no enabled authenticated approval.");
        var envelope = JsonSerializer.Deserialize<Envelope>(correction.Value, Json);
        var plain = envelope is { Version: 1 } ? Protector(config).UnprotectLocationCorrection(envelope.Payload) : null;
        var receipt = plain is null ? null : JsonNode.Parse(plain);
        var correctionRetained = receipt?["changes"]?["namedInputs"]?.AsArray().Where(n => n?["assignmentId"]?.ToString() == m.AssignmentId).ToArray();
        if (correctionRetained?.Length != 1 || !JsonNode.DeepEquals(correctionRetained[0], JsonSerializer.SerializeToNode(m, Json)))
            throw new InvalidOperationException("The immutable named operands are not authenticated by their period correction receipt.");
        return null;
    }
}

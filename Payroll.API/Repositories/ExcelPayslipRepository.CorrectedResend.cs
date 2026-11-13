using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

// Server-owned evidence stored only in send_state_json. It never replaces the saved batch.
internal sealed class ExcelPayslipCorrectionSnapshot
{
    public string Id { get; set; } = "";
    public int ClientId { get; set; }
    public string BatchId { get; set; } = "";
    public string SourceBatchSha256 { get; set; } = "";
    public string SourceCalculationSha256 { get; set; } = "";
    public string SavedSalarySha256 { get; set; } = "";
    public string RenderSha256 { get; set; } = "";
    public ExcelPayslipBatch Batch { get; set; } = new();
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed partial class ExcelPayslipRepository
{
    internal const string CorrectionSnapshotPrefix = "corrected-snapshot:";

    internal static ExcelPayslipRow MetadataOnlyCurrent(ExcelPayslipRow row)
    {
        var copy = CorrectionClone(row);
        if (ExcelPayslipPresentation.Linked(copy))
            copy.MasterDetails = copy.MasterDetails! with { ProfileOnly = true, Salary = null, SalaryInputs = [] };
        return ExcelPayslipPresentation.Current(copy);
    }

    internal static string CorrectionRequestFingerprint(string batchId, SendExcelPayslipsRequest request) =>
        CorrectionHash(new { Version = "corrected-payslip-v1", BatchId = batchId, request.ResendCorrected,
            request.AcknowledgeResend, request.Mode, request.IncludeSeal, request.AmountDecimalPlaces,
            RowIds = request.RowIds.OrderBy(id => id, StringComparer.Ordinal),
            Email = request.Email?.Trim().ToLowerInvariant() ?? "",
            Overrides = request.EmailOverrides.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new { p.Key, Value = p.Value?.Trim().ToLowerInvariant() }) });

    private async Task<ExcelPayslipBatch?> ReadCorrectionBatchAsync(MySqlConnection db, int clientId, string batchId, MySqlTransaction tx)
    {
        var raw = await ReadRawCorrectionSourceAsync(db, clientId, batchId, tx);
        if (raw is null) return null;
        var batch = CorrectionClone(raw);
        // This validates existing source/employee evidence but deliberately omits the
        // Master salary annotation/recalculation performed by the normal page read.
        await AnnotateEmploymentAsync(db, batch, tx, configuration, annotateMasterDetails: false);
        var masters = await ReadProfileMastersAsync(db, tx, clientId, batch.Month);
        await EmployeeRepository.ApplyEmploymentStatusAsync(db, masters, tx);
        await PayrollDataTableStore.ApplyEmployeeTablesAsync(db, masters, tx);
        var source = await ReadCalculationSourceAsync(db, clientId, batchId, tx);
        AnnotateCurrentProfiles(batch, source, masters);
        await AnnotateCurrentMasterAsync(db, tx, batch, source, configuration, profileOnly: true);
        AnnotateWarningReviews(batch);
        return batch;
    }

    internal static async Task<ExcelPayslipBatch?> ReadRawCorrectionSourceAsync(MySqlConnection db, int clientId, string batchId, MySqlTransaction? tx = null)
    {
        var json = await db.ExecuteScalarAsync<string?>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id",
            new { ClientId = clientId, Id = batchId }, tx);
        var batch = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ExcelPayslipBatch>(json, JsonOptions);
        return batch is not null && batch.ClientId == clientId && batch.Id == batchId ? batch : null;
    }

    internal static ExcelPayslipCorrectionSnapshot CreateCorrectionSnapshot(ExcelPayslipBatch rawBatch,
        ExcelPayslipBatch annotatedBatch, ExcelPayslipCalculationSource? source, IReadOnlyCollection<string> rowIds, string requestId, string actor)
    {
        if (!Guid.TryParseExact(requestId, "N", out _) || rawBatch.ClientId != annotatedBatch.ClientId || rawBatch.Id != annotatedBatch.Id
            || rawBatch.Month != annotatedBatch.Month || rowIds.Count == 0 || rowIds.Distinct(StringComparer.Ordinal).Count() != rowIds.Count)
            throw new InvalidOperationException("The corrected payslip source does not match this saved client and batch.");
        var frozen = CorrectionClone(rawBatch);
        frozen.Rows = [];
        foreach (var id in rowIds)
        {
            var original = rawBatch.Rows.SingleOrDefault(r => r.Id == id);
            var current = annotatedBatch.Rows.SingleOrDefault(r => r.Id == id);
            if (original is null || current is null || original.SourceRow != current.SourceRow || current.Employment?.IdentityReviewRequired == true)
                throw new InvalidOperationException("A selected employee needs identity review before a corrected payslip can be sent.");
            var row = CorrectionClone(original);
            row.Employment = current.Employment;
            if (ExcelPayslipPresentation.Linked(current))
                row.MasterDetails = current.MasterDetails! with { ProfileOnly = true, Salary = null, SalaryInputs = [] };
            else row.MasterDetails = null;
            row.WarningReview = current.WarningReview;
            row.NegativeAmountReview = current.NegativeAmountReview;
            row.Warnings = current.Warnings.ToList();
            row = CorrectionClone(ExcelPayslipPresentation.Current(row));
            row.MasterDetails = null; // Freeze the rendered values; future Master edits cannot alter a queued copy.
            frozen.Rows.Add(row);
        }
        var selectedIds = rowIds.ToHashSet(StringComparer.Ordinal);
        frozen.ProfileLinks = rawBatch.ProfileLinks?.Where(l => selectedIds.Contains(l.RowId)).ToList();
        var snapshot = new ExcelPayslipCorrectionSnapshot
        {
            Id = requestId, ClientId = rawBatch.ClientId, BatchId = rawBatch.Id,
            SourceBatchSha256 = NotificationRepository.ExcelPayslipBatchHash(rawBatch), SourceCalculationSha256 = CorrectionHash(source),
            SavedSalarySha256 = CorrectionSalaryHash(rawBatch.Rows.Where(r => selectedIds.Contains(r.Id))),
            Batch = frozen,
            CreatedBy = Limit(actor, 190), CreatedAtUtc = DateTime.UtcNow
        };
        // The original calculation source already has durable storage. Its exact
        // witness is checked by the worker; retaining another full workbook per
        // resend would needlessly duplicate megabytes of immutable source data.
        snapshot.RenderSha256 = CorrectionHash(new { snapshot.Batch, snapshot.SourceCalculationSha256 });
        ValidateCorrectionSnapshot(snapshot, rawBatch, source);
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(snapshot, JsonOptions)) > 16 * 1024 * 1024)
            throw new InvalidOperationException("The corrected payslip snapshot is too large. Select fewer employees.");
        return snapshot;
    }

    internal static void ValidateCorrectionSnapshot(ExcelPayslipCorrectionSnapshot snapshot, ExcelPayslipBatch rawBatch,
        ExcelPayslipCalculationSource? currentSource)
    {
        var ids = snapshot.Batch.Rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        if (!Guid.TryParseExact(snapshot.Id, "N", out _) || snapshot.ClientId != rawBatch.ClientId || snapshot.BatchId != rawBatch.Id
            || snapshot.Batch.ClientId != rawBatch.ClientId || snapshot.Batch.Id != rawBatch.Id || snapshot.Batch.Month != rawBatch.Month
            || ids.Count == 0 || ids.Count != snapshot.Batch.Rows.Count || snapshot.Batch.Rows.Any(r => r.MasterDetails is not null)
            || snapshot.SourceBatchSha256 != NotificationRepository.ExcelPayslipBatchHash(rawBatch)
            || snapshot.SourceCalculationSha256 != CorrectionHash(currentSource)
            || snapshot.RenderSha256 != CorrectionHash(new { snapshot.Batch, snapshot.SourceCalculationSha256 })
            || snapshot.SavedSalarySha256 != CorrectionSalaryHash(rawBatch.Rows.Where(r => ids.Contains(r.Id)))
            || snapshot.SavedSalarySha256 != CorrectionSalaryHash(snapshot.Batch.Rows))
            throw new InvalidOperationException("The corrected payslip snapshot failed its saved-source integrity check. No email was sent.");
    }

    internal static string CorrectionSalaryHash(IEnumerable<ExcelPayslipRow> rows) => CorrectionHash(rows.OrderBy(r => r.Id, StringComparer.Ordinal)
        .Select(r => new { r.Id, r.SourceRow, r.Earnings, r.Deductions, r.EmployerContributions, r.NetPay, r.DeclaredGross, r.DeclaredDeductions }));

    internal static string CorrectionHash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions))));
    private static T CorrectionClone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
}

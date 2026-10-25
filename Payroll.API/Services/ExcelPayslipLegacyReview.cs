using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

internal static class ExcelPayslipLegacyReview
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string SnapshotHash(string batchJson, string? sourceJson) => Hash(JsonSerializer.Serialize(new { batchJson, sourceJson }, Json));
    internal static string? ValidateParentGraph(string batchId, string parentId, IReadOnlyDictionary<string, string[]> graph)
    {
        if (!graph.TryGetValue(batchId, out var recorded) || !recorded.Contains(parentId, StringComparer.Ordinal)) return "The selected parent is not a persisted reference of this batch.";
        var states = new Dictionary<string, byte>(StringComparer.Ordinal);
        var pending = new Stack<(string Id, bool Exit)>(); pending.Push((batchId, false));
        while (pending.TryPop(out var next))
        {
            if (next.Exit) { states[next.Id] = 2; continue; }
            if (states.TryGetValue(next.Id, out var state)) { if (state == 1) return "Recorded ancestry contains a self/cyclic source reference."; continue; }
            if (!graph.TryGetValue(next.Id, out var parents)) return "A recorded ancestor is missing or unavailable in this client. Restore/reconcile verified historical evidence.";
            if (states.Count >= 10000) return "The recorded ancestry exceeds the bounded review limit. An explicit lineage audit is required.";
            states[next.Id] = 1; pending.Push((next.Id, true));
            foreach (var parent in parents.Distinct(StringComparer.Ordinal)) pending.Push((parent, false));
        }
        return null;
    }
    internal static bool IsEmptyUnappliedDraft(string status, string resultJson)
    {
        if (status != "Draft") return false;
        try { using var json = JsonDocument.Parse(resultJson); return json.RootElement.ValueKind == JsonValueKind.Object && !json.RootElement.EnumerateObject().Any(); }
        catch (JsonException) { return false; }
    }
    private static string FileFamily(string name) => Regex.Replace(Regex.Replace(name, @"\s*\(\d+\)(?=\.[^.]+$)", ""), "[^a-zA-Z0-9]", "").ToUpperInvariant();
    private static string RowContent(ExcelPayslipRow row) => JsonSerializer.Serialize(new
    {
        row.SourceRow, row.EmployeeCode, row.EmployeeName, row.Email, row.Information,
        row.Earnings, row.Deductions, row.EmployerContributions, row.NetPay, row.DeclaredGross, row.DeclaredDeductions, row.Warnings
    }, Json);
    private static string FinancialContent(ExcelPayslipBatch batch) => JsonSerializer.Serialize(new
    {
        batch.ClientId, batch.Month, batch.SalaryTemplateId, batch.SimpleLayout, batch.SheetName, batch.HeaderRow,
        rows = batch.Rows.OrderBy(r => r.SourceRow).ThenBy(r => r.EmployeeCode, StringComparer.Ordinal).Select(RowContent)
    }, Json);
    private static string? CalculationContent(ExcelPayslipCalculationSource? source)
    {
        if (source is null) return null;
        // Creation lineage alone is excluded. Formulas, cached values, mappings and calculation options remain compared.
        return JsonSerializer.Serialize(new { source.AttendanceColumnIndex, source.MonthDaysCell, source.SheetName, source.HeaderRow,
            source.ColumnCount, source.DateSystem, source.Columns, source.Rows, source.ExcludedSourceRows }, Json);
    }
    internal static ExcelPayslipSupersessionPreview Compare(ExcelPayslipBatch old, ExcelPayslipCalculationSource? oldSource,
        ExcelPayslipBatch keeper, ExcelPayslipCalculationSource? keeperSource)
    {
        var result = new ExcelPayslipSupersessionPreview { BatchId = old.Id, KeeperBatchId = keeper.Id, OldRows = old.Rows.Count, KeeperRows = keeper.Rows.Count };
        if (old.ClientId != keeper.ClientId || old.Id == keeper.Id) result.Blockers.Add("Choose a different retained version belonging to this exact client.");
        if (old.Month != keeper.Month) result.Blockers.Add("Retained version must cover the same payroll month.");
        if (keeper.CreatedAtUtc < old.CreatedAtUtc || keeper.CreatedAtUtc == old.CreatedAtUtc && string.CompareOrdinal(keeper.Id, old.Id) <= 0)
            result.Blockers.Add("Choose a newer retained version. Cleanup review cannot create a backwards/cyclic supersession chain.");
        if (keeper.ParentBatchId == old.Id || keeperSource?.SourceBatchId == old.Id)
            result.Blockers.Add("The chosen keeper still depends on this batch. Keep its source or choose an independent retained version.");
        result.SameCalculationSource = CalculationContent(oldSource) == CalculationContent(keeperSource);
        result.ExactSnapshot = FinancialContent(old) == FinancialContent(keeper) && result.SameCalculationSource;
        if (result.ExactSnapshot)
        {
            result.Evidence = "Exact complete financial snapshot and calculation source match; generated row IDs and creation metadata are ignored. Original creation ancestry is not inferred.";
            return result;
        }
        if (oldSource is null || keeperSource is null) result.Blockers.Add("Different versions need both saved calculation sources. Restore verified source evidence before supersession review.");
        if (FileFamily(old.SourceFileName) != FileFamily(keeper.SourceFileName) || old.SheetName.Trim() != keeper.SheetName.Trim()
            || old.HeaderRow != keeper.HeaderRow || old.SalaryTemplateId != keeper.SalaryTemplateId)
            result.Blockers.Add("Different source families, worksheets or salary mappings cannot be declared superseded automatically. Review their source/configuration evidence first.");
        if (oldSource is not null && keeperSource is not null && (oldSource.ColumnCount != keeperSource.ColumnCount
            || !oldSource.Columns.Select(c => (c.ColumnIndex, c.SourceHeader, c.Kind, c.ComponentId)).SequenceEqual(keeperSource.Columns.Select(c => (c.ColumnIndex, c.SourceHeader, c.Kind, c.ComponentId)))))
            result.Blockers.Add("Column/component bindings changed. Reconcile the mapping before declaring a final retained version.");
        var oldCodes = old.Rows.Select(r => r.EmployeeCode.Trim().ToUpperInvariant()).ToArray();
        var keeperCodes = keeper.Rows.Select(r => r.EmployeeCode.Trim().ToUpperInvariant()).ToArray();
        if (oldCodes.Any(string.IsNullOrEmpty) || keeperCodes.Any(string.IsNullOrEmpty) || oldCodes.Distinct().Count() != oldCodes.Length || keeperCodes.Distinct().Count() != keeperCodes.Length)
            result.Blockers.Add("Stable employee codes are missing or duplicated. Name-only matching cannot authorize deletion of a changed version.");
        else
        {
            var kept = keeper.Rows.ToDictionary(r => r.EmployeeCode.Trim().ToUpperInvariant(), StringComparer.Ordinal);
            if (oldCodes.Any(code => !kept.ContainsKey(code))) result.Blockers.Add("The retained version omits employees from this version. Reconcile complete employee coverage first.");
            result.ChangedRows = old.Rows.Count(r => !kept.TryGetValue(r.EmployeeCode.Trim().ToUpperInvariant(), out var retained) || RowContent(r) != RowContent(retained));
        }
        result.Evidence = "Same client/month/source/component bindings with unique stable-code coverage. Amounts/formulas may differ: an authorized operator must review the original finance source and explicitly choose the retained final version. This records supersession, not reconstructed creation ancestry.";
        return result;
    }
}

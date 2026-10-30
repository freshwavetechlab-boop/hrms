using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Services;

internal sealed record ExcelPayslipOverviewInput(ExcelPayslipBatch Batch, bool HasCalculationSource);
internal static class ExcelPayslipOverviewService
{
    private sealed record SourceAmounts(ExcelPayslipPdfService.SlipTotals Raw, ExcelPayslipPdfService.SlipTotals Display);
    // Reuse the saved-payslip renderer's G15/decimal rounding rather than summing
    // raw cached binary tails or inventing a different money display rule.
    private static decimal DisplayMoney(decimal value) => decimal.Parse(ExcelPayslipPdfService.Amount(value, 2), NumberStyles.Number, CultureInfo.GetCultureInfo("en-IN"));
    internal static void Validate(ExcelPayslipOverviewRequest request)
    {
        if (request.Scopes is null || request.Scopes.Count is < 1 or > 100 || request.Scopes.Any(s => s is null || s.ClientId <= 0 || !Guid.TryParseExact(s.BatchId, "N", out _))
            || request.Scopes.Distinct().Count() != request.Scopes.Count) throw new InvalidOperationException("Select 1–100 distinct saved client/batch pairs explicitly.");
        if (request.Page is < 1 or > 100000 || request.PageSize is < 1 or > 100 || request.Search is null || request.Search.Length > 120
            || request.Readiness is not ("All" or "Ready" or "Review" or "Blocked") || request.MasterStatus is not ("All" or "Matched" or "Unmatched" or "Conflict" or "Missing code"))
            throw new InvalidOperationException("Review the overview search, status and pagination filters.");
    }
    // DTOs expose requested financial aggregates only, never private information,
    // email addresses or individual employee salary/component amounts.
    internal static string SafeText(string value) => Regex.Replace(Regex.Replace(value ?? "", @"(?:\d[\s-]*){7,}", "[private value]"), @"\b[A-Za-z]{5}\d{4}[A-Za-z]\b", "[private value]");
    internal static string Code(string value) => value.Trim().ToUpperInvariant();
    internal static ExcelPayslipOverviewResult Build(IReadOnlyList<ExcelPayslipOverviewInput> input, ExcelPayslipOverviewRequest request,
        IReadOnlySet<(int ClientId, string Code)>? duplicateMasterCodes = null)
    {
        Validate(request);
        var requested = request.Scopes.ToHashSet();
        if (input.Count != requested.Count || input.Any(i => !requested.Contains(new(i.Batch.ClientId, i.Batch.Id)))
            || input.Select(i => (i.Batch.ClientId, i.Batch.Id)).Distinct().Count() != input.Count)
            throw new InvalidOperationException("The selected scope contains missing or unavailable batches. Refresh batch choices.");
        if (input.Sum(i => i.Batch.Rows?.Count ?? 0) > 50000) throw new InvalidOperationException("Select a smaller scope (maximum 50,000 source rows).");
        var source = input.SelectMany(i => (i.Batch.Rows ?? []).Select((row, index) => new { Input = i, Row = row, Index = index })).ToArray();
        var ambiguous = source.Where(s => !string.IsNullOrWhiteSpace(s.Row.EmployeeCode))
            .GroupBy(s => (s.Input.Batch.ClientId, Code(s.Row.EmployeeCode)))
            .Where(g => g.Select(s => ExcelPayrollSetupRepository.Normalize(s.Row.EmployeeName)).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key).ToHashSet();
        var codeOverlaps = source.Where(s => !string.IsNullOrWhiteSpace(s.Row.EmployeeCode))
            .GroupBy(s => (s.Input.Batch.ClientId, s.Input.Batch.Month, Code(s.Row.EmployeeCode))).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var masterOverlaps = source.Where(s => s.Row.Employment is { EmployeeId: > 0, IdentityReviewRequired: false })
            .GroupBy(s => (s.Input.Batch.ClientId, s.Input.Batch.Month, s.Row.Employment!.EmployeeId!.Value)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var rows = new List<ExcelPayslipOverviewRow>();
        var identities = new Dictionary<string, (int ClientId, string Code)>();
        var amounts = new Dictionary<string, SourceAmounts>();
        foreach (var item in source)
        {
            var batch = item.Input.Batch; var row = item.Row; var code = Code(row.EmployeeCode); var employment = row.Employment;
            var current = new ExcelPayslipOverviewRow
            {
                Key = $"{batch.ClientId}:{batch.Id}:{item.Index}", ClientId = batch.ClientId, ClientName = SafeText(batch.ClientName),
                Month = batch.Month, BatchId = batch.Id, FileName = SafeText(batch.SourceFileName), SheetName = SafeText(batch.SheetName), SourceRow = row.SourceRow,
                EmployeeCode = SafeText(row.EmployeeCode), EmployeeName = SafeText(row.EmployeeName), HasCalculationSource = item.Input.HasCalculationSource
            };
            var rawTotals = ExcelPayslipPdfService.TotalsFor(row);
            amounts[current.Key] = new(rawTotals, new(DisplayMoney(rawTotals.Gross), DisplayMoney(rawTotals.Deductions), DisplayMoney(rawTotals.Net)));
            var conflict = ambiguous.Contains((batch.ClientId, code)) || duplicateMasterCodes?.Contains((batch.ClientId, code)) == true || employment?.IdentityReviewRequired == true;
            if (conflict) { current.MasterStatus = "Conflict"; current.MasterReason = "Conflicting source identity or existing master matches. Resolve in reviewed Master Setup; no name-only merge."; }
            else if (employment?.EmployeeId is > 0) { current.MasterStatus = "Matched"; current.MasterEmployeeId = employment.EmployeeId; current.MasterReason = "Existing client-scoped identity checks found this master record. Update/no-op decisions are not yet previewed."; }
            else if (code.Length == 0) { current.MasterStatus = "Missing code"; current.MasterReason = "No safe source code group. Configure must review identity before creation; an existing master may still exist."; }
            else { current.MasterStatus = "Unmatched"; current.MasterReason = "No verified match from current batch identity checks. This is not proof that the employee is absent from Master Data."; }
            current.ScopeOverlap = code.Length > 0 && codeOverlaps.Contains((batch.ClientId, batch.Month, code))
                || current.MasterEmployeeId.HasValue && masterOverlaps.Contains((batch.ClientId, batch.Month, current.MasterEmployeeId.Value));
            current.Reasons.AddRange(ExcelPayslipRepository.RowWarnings(row));
            if (conflict) current.Reasons.Add("Identity conflict requires review before relying on this source row.");
            if (current.ScopeOverlap) current.Reasons.Add("Multiple selected rows cover this client/period identity. Choose intended versions or review source units; rows were not merged.");
            if (employment?.BlockedForMonth == true) { current.Readiness = "Blocked"; current.Reasons.Add("Existing employment rules block this salary month after exit."); }
            else current.Readiness = current.Reasons.Count == 0 ? "Ready" : "Review";
            if (current.Reasons.Count == 0) current.Reasons.Add("Passes saved-source PDF selection checks. This does not verify email delivery, master configuration or regular payroll parity.");
            if (!conflict && code.Length > 0) identities[current.Key] = (batch.ClientId, code);
            rows.Add(current);
        }
        var snapshot = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Rows = rows, SourceTotals = amounts })))).ToLowerInvariant();
        var search = request.Search.Trim();
        var filtered = rows.Where(r => (request.Readiness == "All" || r.Readiness == request.Readiness)
            && (request.MasterStatus == "All" || r.MasterStatus == request.MasterStatus)
            && (!request.OverlapOnly || r.ScopeOverlap)
            && (search.Length == 0 || new[] { r.ClientName, r.Month, r.BatchId, r.FileName, r.SheetName, r.EmployeeCode, r.EmployeeName }.Any(v => v.Contains(search, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(r => r.ClientName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ClientId).ThenBy(r => r.Month, StringComparer.Ordinal)
            .ThenBy(r => r.BatchId, StringComparer.Ordinal).ThenBy(r => r.SourceRow).ThenBy(r => r.Key, StringComparer.Ordinal).ToArray();
        var totals = new ExcelPayslipOverviewTotals
        {
            SourceGrossEarnings = filtered.Sum(r => amounts[r.Key].Display.Gross),
            SourceDeductions = filtered.Sum(r => amounts[r.Key].Display.Deductions),
            SourceNetPay = filtered.Sum(r => amounts[r.Key].Display.Net),
            SourceRows = filtered.Length, ReadyRows = filtered.Count(r => r.Readiness == "Ready"), ReviewRows = filtered.Count(r => r.Readiness == "Review"), BlockedRows = filtered.Count(r => r.Readiness == "Blocked"),
            UniqueScopedCodeGroups = filtered.Where(r => identities.ContainsKey(r.Key)).Select(r => identities[r.Key]).Distinct().Count(),
            UncountedEmployeeRows = filtered.Count(r => !identities.ContainsKey(r.Key)),
            MatchedMasterRecords = filtered.Where(r => r.MasterStatus == "Matched").Select(r => (r.ClientId, r.MasterEmployeeId)).Distinct().Count(),
            MatchedRows = filtered.Count(r => r.MasterStatus == "Matched"), UnmatchedRows = filtered.Count(r => r.MasterStatus == "Unmatched"), ConflictRows = filtered.Count(r => r.MasterStatus == "Conflict"),
            MissingCodeRows = filtered.Count(r => r.MasterStatus == "Missing code"), OverlapRows = filtered.Count(r => r.ScopeOverlap)
        };
        var page = Math.Min(request.Page, Math.Max(1, (filtered.Length + request.PageSize - 1) / request.PageSize));
        return new() { ScopeBatchCount = input.Count, ScopeTotalRows = rows.Count, Snapshot = snapshot, Page = page, PageSize = request.PageSize, Totals = totals,
            Rows = filtered.Skip((page - 1) * request.PageSize).Take(request.PageSize).ToList() };
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Services;

internal static class SelectedPayRunAdjustments
{
    internal sealed record SourceReference(string BatchId, string ImportId, int SourceRow);
    private static readonly Regex NotesPattern = new(@"^Reviewed salary Excel (?<batch>[a-f0-9]{32}); import (?<import>[a-f0-9]{32}); source row (?<row>[1-9][0-9]{0,5})$", RegexOptions.CultureInvariant);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Normalize(string value) => Regex.Replace(value ?? "", "[^A-Za-z0-9]", "").ToUpperInvariant();

    internal static SourceReference Reference(PayrollAdjustment adjustment)
    {
        var match = NotesPattern.Match(adjustment.Notes ?? "");
        if (!match.Success || !int.TryParse(match.Groups["row"].Value, out var row) || row > 100000)
            throw new SelectedPayRunConflictException("An EXCEL_SETUP adjustment lacks exact saved-batch/import/source-row provenance. Review its source; no adjustment was applied.");
        return new(match.Groups["batch"].Value, match.Groups["import"].Value, row);
    }

    internal static void RequireUnbound(CreatePayRunRequest request, PayrollAdjustment adjustment)
    {
        if (adjustment.Id <= 0 || adjustment.ClientId != request.ClientId || adjustment.PayPeriod != request.PayPeriod
            || !request.IncludedEmployeeIds.Contains(adjustment.EmployeeId) || adjustment.PayRunType != "Regular"
            || adjustment.Status != "Approved" || adjustment.PayRunId.HasValue || adjustment.ReasonCode != "EXCEL_SETUP"
            || adjustment.Amount <= 0 || string.IsNullOrWhiteSpace(adjustment.ComponentCode)
            || adjustment.AdjustmentType is not ("Earning" or "Reimbursement" or "Deduction"))
            throw new SelectedPayRunConflictException("Only explicitly selected Approved, unbound Regular EXCEL_SETUP adjustments in this exact employee/client/month scope can use held processing.");
    }

    internal static void RequireExactIds(CreatePayRunRequest request, IReadOnlyCollection<PayrollAdjustment> applicable)
    {
        if (request.AdjustmentIds.Any(id => id <= 0) || request.AdjustmentIds.Distinct().Count() != request.AdjustmentIds.Count
            || applicable.Select(a => a.Id).Distinct().Count() != applicable.Count
            || !request.AdjustmentIds.ToHashSet().SetEquals(applicable.Select(a => a.Id)))
            throw new SelectedPayRunConflictException("Explicit adjustment IDs must equal all applicable approved Regular adjustments for the selected employees. No implicit or unrelated adjustment is consumed.");
        foreach (var adjustment in applicable) RequireUnbound(request, adjustment);
    }

    internal static string BusinessFingerprint(PayrollAdjustment adjustment) => Hash(JsonSerializer.Serialize(new
    {
        adjustment.Id, adjustment.ClientId, adjustment.EmployeeId, adjustment.ComponentId,
        adjustment.ComponentCode, adjustment.ComponentName, adjustment.AdjustmentType, adjustment.Amount,
        adjustment.PayPeriod, adjustment.PayRunType, adjustment.ReasonCode, adjustment.Notes,
        adjustment.Taxable, adjustment.Status, adjustment.PayRunId, adjustment.CreatedAt, adjustment.UpdatedAt
    }));
    internal static string ReceiptFingerprint(string requestJson, string resultJson) => Hash(requestJson + "\n" + resultJson);
    internal static string BindingKey(SelectedPayRunAdjustmentReview review) => review.SourceBatchId + ":" + review.SourceRowId + ":" + review.ComponentCode.ToUpperInvariant();

    internal static SelectedPayRunAdjustmentReview Review(CreatePayRunRequest request, PayrollAdjustment adjustment,
        SourceReference reference, ExcelPayslipBatch batch, ExcelPayslipCalculationSource source,
        ExcelPayrollSetupRequest imported, ExcelPayrollSetupResult receipt, string currentSourceHash, string receiptHash)
    {
        RequireUnbound(request, adjustment);
        if(batch.Rows is null || batch.Rows.Any(r=>r is null) || source.Columns is null || source.Columns.Any(c=>c is null)
            || source.Rows is null || source.Rows.Any(r=>r is null) || imported.RowIds is null || imported.Components is null
            || imported.Components.Any(c=>c is null) || imported.SourceColumnRoles is null
            || imported.EmployeeDecisions is null || imported.EmployeeDecisions.Any(d=>d is null)
            || receipt.TransferRows is null || receipt.TransferRows.Any(r=>r is null) || receipt.EmployeeIds is null
            || receipt.KeptRows is null || receipt.KeptRows.Any(r=>r is null))
            throw new SelectedPayRunConflictException("Saved adjustment source/receipt collections are missing or malformed. Review provenance before payroll.");
        if (batch.ClientId != request.ClientId || batch.Id != reference.BatchId || batch.Month != request.PayPeriod
            || imported.BatchId != batch.Id || imported.TransferReviewVersion != 1 || receipt.ImportId != reference.ImportId
            || receipt.Transfer is not { Version: 1 } transfer || transfer.SourceBatchId != batch.Id
            || transfer.SourceMonth != request.PayPeriod || transfer.SourceHash != currentSourceHash)
            throw new SelectedPayRunConflictException("An Applied source receipt with unchanged current saved-batch/period/transfer provenance is required. Legacy or changed provenance remains blocked.");
        var rows = batch.Rows.Where(r => r.SourceRow == reference.SourceRow).ToArray();
        if (rows.Length != 1) throw new SelectedPayRunConflictException("The adjustment source row is missing or ambiguous.");
        var row = rows[0];
        if(!Guid.TryParse(row.Id,out _))throw new SelectedPayRunConflictException("The source row has no stable valid identity.");
        if(row.Earnings is null || row.Deductions is null || row.Earnings.Any(a=>a is null) || row.Deductions.Any(a=>a is null))
            throw new SelectedPayRunConflictException("Saved adjustment financial source entries are missing or malformed.");
        var bindings = receipt.TransferRows.Where(r => r.RowId == row.Id).ToArray();
        var decisions = imported.EmployeeDecisions.Where(d => d.RowId == row.Id).ToArray();
        if (imported.RowIds.Count(id => id == row.Id) != 1 || bindings.Length != 1 || bindings[0].EmployeeId != adjustment.EmployeeId
            || decisions.Length != 1 || decisions[0].MatchedEmployeeId != adjustment.EmployeeId
            || receipt.EmployeeIds.Count(id => id == adjustment.EmployeeId) != 1 || receipt.KeptRows.Any(r => r.RowId == row.Id))
            throw new SelectedPayRunConflictException("The source receipt does not prove this exact selected employee/source-row transfer.");
        var components = imported.Components.Where(c => string.Equals(c.Code,adjustment.ComponentCode, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (components.Length != 1) throw new SelectedPayRunConflictException("The source adjustment component is missing or ambiguous.");
        var component = components[0];
        if (!component.IsPeriodAdjustment || component.Category != adjustment.AdjustmentType || component.Taxable != adjustment.Taxable
            || component.SourceColumnIndex is not int column || component.ExpectedColumnIndex != column
            || component.Category is not ("Earning" or "Reimbursement" or "Deduction"))
            throw new SelectedPayRunConflictException("Existing adjustment classification/taxability/one-period semantics differ from the reviewed source component.");
        var columns = source.Columns.Where(c => c.ColumnIndex == column).ToArray();
        var sourceRows = source.Rows.Where(r => r.SourceRow == row.SourceRow).ToArray();
        var role = component.Category == "Deduction" ? "deduction" : "earning";
        if (columns.Length != 1 || sourceRows.Length != 1 || imported.SourceColumnRoles.GetValueOrDefault(column, columns[0].Kind) != role)
            throw new SelectedPayRunConflictException("The exact saved source adjustment column/role is unavailable.");
        if(sourceRows[0].Cells is null || bindings[0].Lines is null
            || bindings[0].Lines.Any(l=>l is null || string.IsNullOrWhiteSpace(l.Code)))
            throw new SelectedPayRunConflictException("The saved adjustment cell/receipt trace is missing or malformed.");
        var cell = sourceRows[0].Cells.ElementAtOrDefault(column);
        if (cell is null || string.IsNullOrWhiteSpace(cell.Value) || cell.Error is not null || cell.CalculationError is not null || cell.MissingCachedValue
            || !decimal.TryParse(cell.Value.Replace(",", ""), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var rawAmount))
            throw new SelectedPayRunConflictException("The source adjustment has no valid preserved financial value.");
        var mapped = (component.Category == "Deduction" ? row.Deductions : row.Earnings).Where(a => Normalize(a.Label) == Normalize(columns[0].Label)).ToArray();
        var lines = bindings[0].Lines.Where(l => l.Code.Equals(component.Code, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (mapped.Length != 1 || mapped[0].Amount != rawAmount || lines.Length != 1 || lines[0].Category != component.Category
            || lines[0].SourceAmount != rawAmount || lines[0].CalculatedAmount != adjustment.Amount
            || decimal.Round(rawAmount, 2, MidpointRounding.ToEven) != adjustment.Amount)
            throw new SelectedPayRunConflictException("Captured, receipt and approved adjustment amounts/classification differ. No expected amount is forced.");
        return new(adjustment.Id, adjustment.EmployeeId, adjustment.ComponentCode, adjustment.AdjustmentType,
            adjustment.Amount, adjustment.Taxable, batch.Id, reference.ImportId, row.Id, row.SourceRow,
            currentSourceHash, receiptHash, BusinessFingerprint(adjustment));
    }

    internal static void RequireFrozen(CreatePayRunRequest request, IReadOnlyList<SelectedPayRunAdjustmentReview> current)
    {
        var frozen = request.SelectedAdjustmentReviews ?? [];
        static bool Valid(SelectedPayRunAdjustmentReview? r) => r is not null && r.AdjustmentId>0 && r.EmployeeId>0
            && !string.IsNullOrWhiteSpace(r.ComponentCode) && r.Amount>0 && r.Category is "Earning" or "Reimbursement" or "Deduction"
            && Guid.TryParseExact(r.SourceBatchId,"N",out _) && Guid.TryParseExact(r.SourceImportId,"N",out _) && Guid.TryParse(r.SourceRowId,out _)
            && r.SourceRow is >0 and <=100000 && Regex.IsMatch(r.SourceHash??"","^[a-f0-9]{64}$")
            && Regex.IsMatch(r.ReceiptFingerprint??"","^[a-f0-9]{64}$") && Regex.IsMatch(r.BusinessFingerprint??"","^[a-f0-9]{64}$");
        if (request.AdjustmentIds is null || frozen.Any(r=>!Valid(r)) || current.Any(r=>!Valid(r))
            || request.AdjustmentIds.Any(id=>id<=0) || request.AdjustmentIds.Distinct().Count()!=request.AdjustmentIds.Count
            || frozen.Count != request.AdjustmentIds.Count || current.Count != frozen.Count
            || !frozen.Select(r => r.AdjustmentId).ToHashSet().SetEquals(request.AdjustmentIds)
            || frozen.Select(BindingKey).Distinct(StringComparer.Ordinal).Count() != frozen.Count
            || !frozen.OrderBy(r => r.AdjustmentId).SequenceEqual(current.OrderBy(r => r.AdjustmentId)))
            throw new SelectedPayRunConflictException("The reviewed source-adjustment snapshot is missing, duplicated or changed. Refresh review; no adjustment was applied.");
    }
    internal static void RequireAppliedCount(int expected, int affected)
    {
        if (expected <= 0 || affected != expected)
            throw new SelectedPayRunConflictException("The exact adjustment claim changed before commit. Payroll outputs and adjustment application must roll back together.");
    }
}

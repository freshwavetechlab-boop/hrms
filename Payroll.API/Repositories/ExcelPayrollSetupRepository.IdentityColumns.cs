using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    private static bool IsUanSourceColumn(ExcelPayslipCalculationColumn column) =>
        Normalize(column.SourceHeader) is "UAN" or "UANNUMBER" or "UANNO"
        || Regex.IsMatch(column.SourceHeader.Trim(), @"^UAN\s*-\s*[A-Z]{1,3}$", RegexOptions.IgnoreCase);

    internal static IReadOnlyList<ExcelPayrollIdentityColumnCandidate> IdentityColumnCandidates(ExcelPayslipBatch batch,
        ExcelPayslipCalculationSource source, IReadOnlyList<string> rowIds)
    {
        if (rowIds.Count == 0 || rowIds.Distinct(StringComparer.Ordinal).Count() != rowIds.Count
            || rowIds.Any(id => batch.Rows.Count(r => r.Id == id) != 1)) return [];
        var hash = ExcelPayslipRepository.EmploymentSourceHash(batch, source);
        return source.Columns.Where(IsUanSourceColumn).Select(column =>
        {
            var valid = rowIds.Count(id => IsCanonicalIdentityLiteral(source, batch.Rows.Single(r => r.Id == id).SourceRow, column.ColumnIndex));
            var fingerprint = Hash(JsonSerializer.Serialize(new { Version = 1, batch.ClientId, batch.Id, batch.Month,
                SourceHash = hash, Role = "UAN", column.ColumnIndex, RowIds = rowIds.Order(StringComparer.Ordinal).ToArray() }, Json));
            return new ExcelPayrollIdentityColumnCandidate(column.ColumnIndex, TransferLetter(column.ColumnIndex), column.SourceHeader,
                rowIds.Count, valid, rowIds.Count - valid, hash, fingerprint);
        }).ToArray();
    }

    private static bool IsCanonicalIdentityLiteral(ExcelPayslipCalculationSource source, int sourceRow, int column)
    {
        var rows = source.Rows.Where(r => r.SourceRow == sourceRow).ToArray();
        if (rows.Length != 1 || column < 0 || column >= rows[0].Cells.Count || rows[0].Cells[column] is not { } cell) return false;
        // Older captured batches omit DataType. Preserve their literal text exactly;
        // never parse/reconstruct a scientific, padded or calculated identifier.
        return cell.DataType is "" or "Text" or "Number" && string.IsNullOrWhiteSpace(cell.FormulaText)
            && !cell.MissingCachedValue && string.IsNullOrWhiteSpace(cell.Error) && string.IsNullOrWhiteSpace(cell.CalculationError)
            && Regex.IsMatch(cell.Value?.Trim() ?? "", @"^[0-9]{12}$");
    }

    internal static int? ReviewedIdentityColumn(ExcelPayslipBatch batch, ExcelPayslipCalculationSource source,
        ExcelPayrollSetupRequest request)
    {
        if (request.IdentityColumnReview is not { } review) return null;
        if (review.Version != 1 || review.Role != "UAN" || !review.Reviewed || (review.Reason?.Trim().Length ?? 0) is < 8 or > 1200
            || request.BatchId != batch.Id || request.TransferReviewVersion != 1 || request.ImportAttendance
            || request.LocationAllocations is not null || request.EmployeeDecisions.Any(d => d.AllowCreate))
            throw new InvalidOperationException("Review one UAN source column for this existing-employee salary-only selection; no identifiers or source rows are changed.");
        var candidates = IdentityColumnCandidates(batch, source, request.RowIds).Where(c => c.ColumnIndex == review.ColumnIndex).ToArray();
        if (candidates.Length != 1 || candidates[0].SourceHash != review.SourceHash
            || candidates[0].SelectionFingerprint != review.SelectionFingerprint)
            throw new InvalidOperationException("The identity-column source or selected rows changed. Reload the exact source selection and review it again.");
        if (candidates[0].InvalidOrMissingRows != 0)
            throw new InvalidOperationException("The reviewed UAN column contains missing, noncanonical, formula-derived or unreliable values. Review those rows separately; no digits are inferred.");
        return review.ColumnIndex;
    }

    internal static int? ReviewedColumnForRow(IReadOnlyDictionary<string,int>? columns, string rowId) =>
        columns is not null && columns.TryGetValue(rowId, out var column) ? column : null;

    private static async Task<Dictionary<string,int>> AppliedIdentityColumnsAsync(MySqlConnection db,
        ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, MySqlTransaction? tx = null)
    {
        if (!await HasReceiptTableAsync(db, tx)) return [];
        var saved = await db.QueryAsync<IdentityColumnReceiptJson>(@"SELECT CAST(request_json AS CHAR) RequestJson,CAST(result_json AS CHAR) ResultJson
FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND batch_id=@BatchId AND status='Applied'
AND JSON_EXTRACT(request_json,'$.identityColumnReview') IS NOT NULL
AND JSON_TYPE(JSON_EXTRACT(request_json,'$.identityColumnReview'))='OBJECT'", new { batch.ClientId, BatchId=batch.Id }, tx);
        var receipts = new List<(ExcelPayrollSetupRequest,ExcelPayrollSetupResult)>();
        foreach (var item in saved)
            try
            {
                var request=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(item.RequestJson,Json);
                var result=JsonSerializer.Deserialize<ExcelPayrollSetupResult>(item.ResultJson,Json);
                if(request is not null && result is not null) receipts.Add((request,result));
            }
            catch(JsonException) { /* Invalid historical JSON cannot establish an identity role. */ }
        return ReviewedIdentityColumns(batch,source,receipts);
    }

    private sealed class IdentityColumnReceiptJson
    {
        public string RequestJson { get; set; } = "{}";
        public string ResultJson { get; set; } = "{}";
    }

    internal static Dictionary<string,int> ReviewedIdentityColumns(ExcelPayslipBatch batch, ExcelPayslipCalculationSource source,
        IEnumerable<(ExcelPayrollSetupRequest Request, ExcelPayrollSetupResult Result)> receipts)
    {
        var accepted = new Dictionary<string,HashSet<int>>(StringComparer.Ordinal);
        var hash = ExcelPayslipRepository.EmploymentSourceHash(batch, source);
        foreach (var (request, result) in receipts)
        {
            try
            {
                if (request.IdentityColumnReview is null || result.Transfer is not { Version: 1 } transfer
                    || transfer.SourceBatchId != batch.Id || transfer.SourceMonth != batch.Month || transfer.SourceHash != hash) continue;
                if (ReviewedIdentityColumn(batch, source, request) is not int column) continue;
                foreach (var id in request.RowIds)
                {
                    var binding = result.TransferRows.Where(r => r.RowId == id).ToArray();
                    var decisions = request.EmployeeDecisions.Where(d => d.RowId == id).ToArray();
                    if (binding.Length != 1 || decisions.Length != 1 || decisions[0].MatchedEmployeeId != binding[0].EmployeeId
                        || result.EmployeeIds.Count(e => e == binding[0].EmployeeId) != 1 || result.KeptRows.Any(r => r.RowId == id)) continue;
                    if (!accepted.TryGetValue(id, out var columns)) accepted[id] = columns = [];
                    columns.Add(column);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or NullReferenceException) { /* Not a valid immutable role binding. */ }
        }
        return accepted.Where(p => p.Value.Count == 1).ToDictionary(p => p.Key, p => p.Value.Single(), StringComparer.Ordinal);
    }
}

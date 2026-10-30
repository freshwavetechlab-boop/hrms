using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal const string SourceLocationPartsReviewCode = "SourceLocationPartsReview";

    // A display group is not a monthly master assignment. Inspect the whole saved
    // batch so selecting only one transfer part cannot overwrite the same person's
    // salary with a partial-month amount or create a second employee for another part.
    internal static List<ExcelPayslipRow> ReviewSourceLocationParts(ExcelPayslipBatch batch,
        IReadOnlyList<ExcelPayslipRow> selected, ExcelPayrollSetupPreview preview)
    {
        var selectedIds = selected.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in batch.Rows.Where(row => !string.IsNullOrWhiteSpace(row.EmployeeCode))
            .GroupBy(row => row.EmployeeCode.Trim().ToUpperInvariant(), StringComparer.Ordinal)
            .Where(group => group.Count() > 1 && ExcelPayslipRowIdentity.IsVerifiedLocationSplit(group, batch.Month)))
        {
            var affected = group.Where(row => selectedIds.Contains(row.Id)).ToArray();
            if (affected.Length == 0) continue;
            var sourceRows = string.Join(", ", group.Select(row => row.SourceRow).Order());
            foreach (var row in affected)
            {
                blocked.Add(row.Id);
                preview.Issues.Add(new("Error",
                    $"This source employee has {group.Count()} reviewed location salary parts (source rows {sourceRows}). Keep one Employee ID/code and open Master location salary and source-period allocation review. Select every part, its Location Master and exact Settings template, then review component scope and attendance allocations. This legacy setup cannot import parts individually or combine their monthly rules. No employee, salary or attendance was saved.",
                    row.Id, SourceLocationPartsReviewCode));
            }
        }
        return selected.Where(row => !blocked.Contains(row.Id)).ToList();
    }
}

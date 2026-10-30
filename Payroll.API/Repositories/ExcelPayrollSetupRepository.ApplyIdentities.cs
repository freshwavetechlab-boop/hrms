using System.Collections.Frozen;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal sealed record ApplyIdentityReview(
        IReadOnlyList<ExcelPayrollSetupIssue> Issues,
        IReadOnlyDictionary<string, IReadOnlyList<ExcelPayrollSetupIssue>>? ExistingSalaryOnlyRows);

    internal static async Task<ApplyIdentityReview> ReviewApplyIdentitiesAsync(
        int clientId, int transferReviewVersion, bool importAttendance, IReadOnlyList<(string RowId, Employee Employee)> rows,
        IReadOnlySet<string> salaryOnlyBankReviewRows,
        Func<IReadOnlyList<Employee>, Task<IReadOnlyList<EmployeeImportPreflightRow>>> preview)
    {
        if (rows.Count == 0) return new(Array.Empty<ExcelPayrollSetupIssue>(), null);
        var checks = await preview(rows.Select(row => row.Employee).ToArray());
        var targets = rows.Select(row => (row.RowId, row.Employee.Id)).ToArray();
        var issues = Array.AsReadOnly(FullIdentityIssues(targets, checks, salaryOnlyBankReviewRows).ToArray());

        // Apply already locks every client employee before building this plan. An
        // all-existing salary-only cohort cannot change the identifiers checked
        // here. Freeze the results, rather than retaining mutable employee data.
        // Legacy/new/mixed cohorts still recheck each row after preceding saves.
        var reusable = transferReviewVersion == 1 && !importAttendance
            && rows.All(row => row.Employee.Id > 0 && row.Employee.ClientId == clientId)
            && rows.Select(row => row.Employee.Id).Distinct().Count() == rows.Count
            && rows.Select(row => row.RowId).Distinct(StringComparer.Ordinal).Count() == rows.Count;
        var snapshot = reusable ? rows.ToFrozenDictionary(row => row.RowId,
            row => (IReadOnlyList<ExcelPayrollSetupIssue>)Array.AsReadOnly(issues.Where(issue => issue.RowId == row.RowId).ToArray()),
            StringComparer.Ordinal) : null;
        return new(issues, snapshot);
    }
}

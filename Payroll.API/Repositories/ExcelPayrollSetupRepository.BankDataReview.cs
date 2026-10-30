using System.Text.RegularExpressions;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal const string BankDataReviewCode = "BANK_DATA_QUALITY";
    internal const string BankDataReviewMessage = "Bank account text requires separate payment-data review. This existing-employee salary-only operation preserves all bank details/history and does not certify payment readiness. No account digits are inferred or changed.";

    // This set is evidence for an existing salary-only write, never a general import override.
    // Name/location alone and a reviewer-selected ID without a unique source anchor do not qualify.
    internal static HashSet<string> SalaryOnlyBankReviewRows(ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, IReadOnlyList<Identity> all, IReadOnlyDictionary<string,int>? reviewedColumns = null)
    {
        var identities = all.Where(m => m.ClientId == batch.ClientId && m.Id > 0).ToList();
        var bound = new List<(string RowId,int Id)>();
        foreach (var row in batch.Rows)
        {
            if (batch.Rows.Count(r => r.Id == row.Id || r.SourceRow == row.SourceRow) != 1
                || source.Rows.Count(r => r.SourceRow == row.SourceRow) != 1) continue;
            var info = SourceInformation(row,source,reviewedUanColumn:ReviewedColumnForRow(reviewedColumns,row.Id));
            var issues = new List<ExcelPayrollSetupIssue>();
            var match = MatchIdentity(row,info,identities,null,issues);
            if (match is null || issues.Count > 0) continue;
            bool Strong(string pattern, Func<Identity,string> field, params string[] labels)
            {
                var value=Normalize(Info(info,labels));
                return Regex.IsMatch(value,pattern) && Normalize(field(match))==value && identities.Count(m => Normalize(field(m))==value)==1;
            }
            var anchored=identities.Count(m => m.EmployeeCode.Equals(row.EmployeeCode,StringComparison.OrdinalIgnoreCase))==1
                || Strong("^[A-Z]{5}[0-9]{4}[A-Z]$",m=>m.Pan,"PAN","PAN Number","PAN No")
                || Strong("^[2-9][0-9]{11}$",m=>m.Aadhaar,"Aadhaar","Aadhar","Aadhaar Number")
                || Strong("^[0-9]{12}$",m=>m.Uan,"UAN","UAN Number","UAN No");
            if(anchored)bound.Add((row.Id,match.Id));
        }
        var repeated=bound.GroupBy(r=>r.Id).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet();
        return bound.Where(r=>!repeated.Contains(r.Id)).Select(r=>r.RowId).ToHashSet(StringComparer.Ordinal);
    }

    internal static bool IsSalaryOnlyBankDataReview(string reason, string rowId, int employeeId,
        EmployeeImportPreflightRow check, IReadOnlySet<string>? sourceBoundRows) =>
        reason==EmployeeRepository.UnreliableBankIdentityMessage && employeeId>0 && sourceBoundRows?.Contains(rowId)==true
        && check.MatchedEmployeeId==employeeId
        && check.MatchReasons.Any(r=>r is "Exact Employee ID" or "Exact Employee Code");
}

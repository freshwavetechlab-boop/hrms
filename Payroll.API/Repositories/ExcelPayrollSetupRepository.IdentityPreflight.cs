using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    // Bind results by source-row position, not by a possibly ambiguous name or code.
    internal static List<ExcelPayrollSetupIssue> FullIdentityIssues(
        IReadOnlyList<(string RowId, int EmployeeId)> targets, IReadOnlyList<EmployeeImportPreflightRow> checks, IReadOnlySet<string>? salaryOnlySourceRows = null)
    {
        var issues = new List<ExcelPayrollSetupIssue>();
        for (var index=0; index<targets.Count; index++)
        {
            var target=targets[index];
            var matches=checks.Where(check=>check.RowNumber==index+2).ToList();
            if(matches.Count!=1)
            {
                issues.Add(new("Error","Full employee identity preflight is missing or ambiguous. Refresh this source selection before Apply.",target.RowId));
                continue;
            }
            var check=matches[0];
            foreach(var reason in check.BlockingReasons.Distinct(StringComparer.OrdinalIgnoreCase))
                if(IsSalaryOnlyBankDataReview(reason,target.RowId,target.EmployeeId,check,salaryOnlySourceRows))
                    issues.Add(new("Review",BankDataReviewMessage,target.RowId,BankDataReviewCode));
                else issues.Add(new("Error","Full employee identity preflight: "+IdentityReviewReason(reason,check),target.RowId));
            if(check.BlockingReasons.Count==0 && check.MatchedEmployeeId.GetValueOrDefault()!=target.EmployeeId)
                issues.Add(new("Error","Full employee identity preflight resolves to a different master. Review the source-to-employee match before Apply.",target.RowId));
        }
        return issues;
    }

    private static string IdentityReviewReason(string reason, EmployeeImportPreflightRow check)
    {
        // The common matcher uses a temporary Employees worksheet. Those row
        // numbers are not the preserved Excel Payslip's source-row numbers.
        if(reason.Contains("is also supplied on workbook row(s)",StringComparison.Ordinal))
            return reason.Split(" is also supplied",StringSplitOptions.None)[0]+" is repeated across the reviewed source selection. Review the duplicate identifiers before Apply.";
        if(reason.StartsWith("Identifiers point to different employees",StringComparison.Ordinal))
        {
            var candidates=check.CandidateEmployees ?? [];
            return "Identifiers point to different employees ("+string.Join("; ",candidates.Take(5).Select(candidate=>candidate.EmployeeCode+": "+string.Join(", ",candidate.MatchReasons)))+
                (candidates.Count>5?$"; and {candidates.Count-5} other master records":"")+"). Review Employee Master identity details; nothing was changed.";
        }
        return reason.Length<=800?reason:reason[..760]+"… Review the employee identity details before Apply.";
    }

    internal static ExcelPayrollRoster WithFullIdentityIssues(ExcelPayrollRoster roster, IReadOnlyList<ExcelPayrollSetupIssue> issues)
    {
        var grouped=issues.Where(issue=>issue.RowId is not null).ToLookup(issue=>issue.RowId!);
        return roster with { Rows=roster.Rows.Select(row=>row.Status!="Applied" && grouped[row.RowId].Any()
            ? row with { Status=grouped[row.RowId].Any(issue=>issue.Severity=="Error") ? "Review" : row.Status,Message=string.Join(" ",grouped[row.RowId].Select(issue=>issue.Message).Distinct()) } : row).ToList() };
    }

    private async Task<ExcelPayrollRoster> ReviewRosterIdentitiesAsync(MySqlConnector.MySqlConnection db, int clientId,
        ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, ExcelPayrollRoster roster)
    {
        var candidates=roster.Rows.Where(row=>row.Status is "Existing" or "New").ToList();
        if(candidates.Count==0)return roster;
        var current=await EmployeeRepository.ReadExcelPayrollIdentitySnapshotAsync(db,null,clientId);
        var targets=new List<(string RowId,int EmployeeId)>();
        var employeesToReview=new List<Employee>();
        var missing=new List<ExcelPayrollSetupIssue>();
        foreach(var row in candidates)
        {
            var employee=row.EmployeeId is int id ? current.SingleOrDefault(item=>item.Id==id) :
                NewEmployee(clientId,batch.Rows.Single(item=>item.Id==row.RowId),new(),SourceInformation(batch.Rows.Single(item=>item.Id==row.RowId),source));
            if(employee is null) { missing.Add(new("Error","Matched employee no longer belongs to this client. Refresh the source selection.",row.RowId));continue; }
            targets.Add((row.RowId,employee.Id)); employeesToReview.Add(employee);
        }
        if(targets.Count>0)
        {
            var checkedRows=EmployeeRepository.ExcelPayrollIdentityRows(employeesToReview,current);
            var sourceBindings=SalaryOnlyBankReviewRows(batch,source,current.Select(e=>new Identity {Id=e.Id,ClientId=e.ClientId,EmployeeCode=e.EmployeeCode,Name=e.FirstName+" "+e.LastName,Pan=e.PersonalDetails.PanNumber,Aadhaar=e.PersonalDetails.AadhaarNumber,Uan=e.PersonalDetails.UanNumber}).ToList());
            missing.AddRange(FullIdentityIssues(targets,checkedRows,sourceBindings));
        }
        return WithFullIdentityIssues(roster,missing);
    }
}

using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;
using System.Text.Json;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static int? SourceLocationColumn(ExcelPayslipCalculationSource source)
    {
        var columns = source.Columns.Where(c => Normalize(c.SourceHeader).StartsWith("WORKLOCATION")
            || Normalize(c.SourceHeader).StartsWith("LOCATIONNAME") || Normalize(c.SourceHeader) is "LOCATION" or "PLACEOFPOSTING" or "POSTING").ToArray();
        return columns.Length == 1 ? columns[0].ColumnIndex : null;
    }

    internal static Dictionary<string,string> SourceInformation(ExcelPayslipRow row, ExcelPayslipCalculationSource? source, int? locationColumn = null)
    {
        var information = ReadInformation(row);
        if (source is null) return information;
        var cells = source.Rows.SingleOrDefault(item => item.SourceRow == row.SourceRow);
        if (cells is null) return information;
        foreach (var column in source.Columns.GroupBy(column => Normalize(column.SourceHeader)).Select(group => group.First()))
        {
            var key = Normalize(column.SourceHeader);
            if (key.Length == 0 || key == "UANAB") continue;
            if (key == "UANE") key = "UAN";
            if (key == "DESIGNATIONMANPOWERCATEGORY") key = "DESIGNATION";
            information[key] = ReadCellText(cells, column.ColumnIndex);
        }
        var index = locationColumn ?? SourceLocationColumn(source);
        if (index is int location && ReadCellText(cells, location) is { Length: > 0 } name) information[Normalize("Work Location")] = name;
        if (!source.Columns.Any(column => Normalize(column.SourceHeader) is "DEPARTMENT" or "DEPT"))
        { information.Remove(Normalize("Department")); information.Remove(Normalize("Dept")); }
        return information;
    }

    internal static async Task<List<Identity>> ReadIdentitiesAsync(MySqlConnection db, MySqlTransaction? tx, int clientId, string month) =>
        (await db.QueryAsync<Identity>(@"SELECT e.Id,e.ClientId,e.EmployeeCode,CONCAT(e.FirstName,' ',e.LastName) Name,e.IsActive," + EmployeeEmploymentRules.EligibleInMonth("e", "@Month") + @" EligibleForMonth,
COALESCE(p.PanNumber,'') Pan,COALESCE(p.AadhaarNumber,'') Aadhaar,COALESCE(p.UanNumber,'') Uan,COALESCE(w.Name,'') WorkLocation
FROM employees e LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id LEFT JOIN worklocations w ON w.Id=e.WorkLocationId AND w.ClientId=e.ClientId
WHERE e.ClientId=@ClientId ORDER BY e.Id", new { ClientId=clientId, Month=month },tx)).ToList();

    internal static ExcelPayrollRoster ClassifyRoster(ExcelPayslipBatch batch, ExcelPayslipCalculationSource source,
        IReadOnlyList<Identity> identities, IReadOnlySet<string> appliedRows)
    {
        identities=identities.Where(e=>e.ClientId==batch.ClientId).ToList();
        var rows = new List<ExcelPayrollRosterRow>();
        foreach (var row in batch.Rows)
        {
            var issues = new List<ExcelPayrollSetupIssue>();
            var match = MatchIdentity(row,SourceInformation(row,source),identities,null,issues);
            var status = appliedRows.Contains(row.Id) ? "Applied" : issues.Count > 0 ? "Review" : match is null ? "New" : !match.EligibleForMonth ? "Review" : "Existing";
            rows.Add(new(row.Id,row.SourceRow,row.EmployeeName,match?.EmployeeCode ?? row.EmployeeCode,status,match?.Id,
                appliedRows.Contains(row.Id) ? "Master setup already applied for this source row." : string.Join(" ",issues.Select(i=>i.Message)) is { Length:>0 } text ? text
                : match is null ? "Not matched to Employee Master. Review creation separately." : !match.EligibleForMonth ? "Not employed in this salary month; review employment dates." : "Matched to an existing employee; their master code will be retained."));
        }
        // One employee cannot receive two salary assignments from the same source.
        var repeated = rows.Where(r=>r.EmployeeId.HasValue).GroupBy(r=>r.EmployeeId).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet();
        rows = rows.Select(r=>r.Status != "Applied" && repeated.Contains(r.EmployeeId) ? r with { Status="Review", Message="Several source rows refer to the same employee. Review the duplicate rows." } : r).ToList();
        var mentioned = rows.Where(r=>r.EmployeeId.HasValue).Select(r=>r.EmployeeId!.Value).ToHashSet();
        var names = batch.Rows.Select(r=>Normalize(r.EmployeeName)).ToHashSet();
        // A saved payslip batch can intentionally contain only five rows from a
        // much larger workbook. Those other source employees are not leavers.
        if (source.Columns.SingleOrDefault(column=>column.Kind=="employeeName") is { } nameColumn)
            foreach(var row in source.Rows.Where(row=>row.SourceRow>source.HeaderRow))
                if(ReadCellText(row,nameColumn.ColumnIndex) is {Length:>0} name) names.Add(Normalize(name));
        var missing = identities.Where(e=>e.IsActive && !mentioned.Contains(e.Id) && !names.Contains(Normalize(e.Name)))
            .Select(e=>new ExcelPayrollMissingEmployee(e.Id,e.EmployeeCode,e.Name,e.WorkLocation)).ToList();
        return new(rows,missing);
    }

    private static async Task<HashSet<string>> AppliedSourceRowsAsync(MySqlConnection db, MySqlTransaction? tx, int clientId, string batchId)
    {
        if (!await HasReceiptTableAsync(db,tx)) return [];
        var raw = await db.QueryAsync<string>("SELECT CAST(JSON_EXTRACT(request_json,'$.rowIds') AS CHAR) FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND batch_id=@BatchId AND status='Applied'",new { ClientId=clientId,BatchId=batchId },tx);
        return raw.SelectMany(value=>JsonSerializer.Deserialize<List<string>>(value,Json) ?? []).ToHashSet(StringComparer.Ordinal);
    }
}

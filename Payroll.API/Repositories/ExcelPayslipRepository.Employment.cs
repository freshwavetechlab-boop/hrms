using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    internal static async Task AnnotateEmploymentAsync(MySqlConnection db, ExcelPayslipBatch batch, MySqlTransaction? tx = null)
    {
        var masters = (await db.QueryAsync<EmploymentMaster>(@"SELECT e.Id,e.ClientId,e.EmployeeCode,e.FirstName,e.LastName,e.IsActive,
COALESCE(p.UanNumber,'') Uan,COALESCE(w.Name,'') WorkLocation," + EmployeeEmploymentRules.EligibleInMonth("e", "@Month") + @" Eligible
FROM employees e LEFT JOIN employeepersonaldetails p ON p.EmployeeId=e.Id LEFT JOIN worklocations w ON w.Id=e.WorkLocationId
WHERE e.ClientId=@ClientId", new { batch.ClientId, batch.Month }, tx)).ToList();
        await EmployeeRepository.ApplyEmploymentStatusAsync(db, masters, tx);
        // Read only identity cells from the original worksheet: simplified payslip
        // labels can hide/rename location and UAN columns.
        var sourceIdentity = (await db.QueryAsync<EmploymentSourceCell>(@"SELECT sourceRow.SourceRow,sourceColumn.ColumnIndex,sourceColumn.Header,
COALESCE(JSON_UNQUOTE(JSON_EXTRACT(sourceRow.Cells,CONCAT('$[',sourceColumn.ColumnIndex,'].value'))),'') Value
FROM excel_payslip_calculation_sources source
JOIN JSON_TABLE(source.source_json,'$.columns[*]' COLUMNS(ColumnIndex INT PATH '$.columnIndex',Header VARCHAR(512) PATH '$.sourceHeader')) sourceColumn
JOIN JSON_TABLE(source.source_json,'$.rows[*]' COLUMNS(SourceRow INT PATH '$.sourceRow',Cells JSON PATH '$.cells')) sourceRow
WHERE source.client_id=@ClientId AND source.batch_id=@Id
AND UPPER(REGEXP_REPLACE(sourceColumn.Header,'[^a-zA-Z0-9]','')) IN
('UAN','UANNUMBER','UANNO','UANE','UANAB','WORKLOCATION','LOCATION','LOCATIONNAME','LOCATIONNAMETEHSILSUBTEHSIL','PLACEOFPOSTING','POSTING')
ORDER BY sourceRow.SourceRow,sourceColumn.ColumnIndex", new { batch.ClientId, batch.Id }, tx)).ToLookup(cell => cell.SourceRow);
        foreach (var row in batch.Rows)
        {
            row.Employment = null;
            var name = ExcelPayrollSetupRepository.Normalize(row.EmployeeName);
            var code = masters.Where(master => master.EmployeeCode.Equals(row.EmployeeCode, StringComparison.OrdinalIgnoreCase)).ToList();
            var sourceCells = sourceIdentity[row.SourceRow].ToList();
            var uan = sourceCells.FirstOrDefault(cell => ExcelPayrollSetupRepository.Normalize(cell.Header).StartsWith("UAN", StringComparison.Ordinal))?.Value
                ?? row.Information.FirstOrDefault(info => ExcelPayrollSetupRepository.Normalize(info.Label) is "UAN" or "UANNUMBER" or "UANNO")?.Value ?? "";
            var location = sourceCells.FirstOrDefault(cell => !ExcelPayrollSetupRepository.Normalize(cell.Header).StartsWith("UAN", StringComparison.Ordinal))?.Value
                ?? row.Information.FirstOrDefault(info => ExcelPayrollSetupRepository.Normalize(info.Label) is "WORKLOCATION" or "LOCATION" or "LOCATIONNAMETEHSILSUBTEHSIL")?.Value ?? "";
            uan = ExcelPayrollSetupRepository.Normalize(uan);
            var strong = uan.Length == 12 ? masters.Where(master => ExcelPayrollSetupRepository.Normalize(master.Uan) == uan).ToList() : [];
            var matched = code.Count == 1 ? code[0] : strong.Count == 1 ? strong[0] : null;
            if (matched is null) continue;
            var review = code.Count > 1 || strong.Count > 1 || code.Count == 1 && strong.Count == 1 && code[0].Id != strong[0].Id
                || ExcelPayrollSetupRepository.Normalize(matched.FirstName + " " + matched.LastName) != name
                || location.Length > 0 && ExcelPayrollSetupRepository.Normalize(matched.WorkLocation) != ExcelPayrollSetupRepository.Normalize(location)
                || uan.Length > 0 && matched.Uan.Length > 0 && ExcelPayrollSetupRepository.Normalize(matched.Uan) != uan;
            row.Employment = new(review ? null : matched.Id, matched.EmployeeCode, matched.EmploymentStatus, matched.LastWorkingDate,
                matched.LastWorkingDate is not null && !matched.Eligible, review);
        }
    }

    private sealed class EmploymentMaster : Employee
    {
        public string Uan { get; set; } = "";
        public string WorkLocation { get; set; } = "";
        public bool Eligible { get; set; }
    }

    private sealed class EmploymentSourceCell
    {
        public int SourceRow { get; set; }
        public int ColumnIndex { get; set; }
        public string Header { get; set; } = "";
        public string Value { get; set; } = "";
    }
}

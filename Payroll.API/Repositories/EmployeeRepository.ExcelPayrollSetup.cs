using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Text.Json;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    internal sealed class ExcelPayrollSalaryHistory
    {
        public long Id { get; set; }
        public DateTime EffectiveFrom { get; set; }
        public string Status { get; set; } = "";
        public string SalaryStructureId { get; set; } = "";
        public decimal AnnualCtc { get; set; }
        public string SalaryJson { get; set; } = "{}";
        public string ChangeReason { get; set; } = "";
    }

    internal static bool IsUnconfiguredExcelSalaryHistory(ExcelPayrollSalaryHistory row)
    {
        if (!string.IsNullOrWhiteSpace(row.SalaryStructureId) || row.AnnualCtc != 0) return false;
        if (string.IsNullOrWhiteSpace(row.SalaryJson)) return true;
        try
        {
            using var json = JsonDocument.Parse(row.SalaryJson);
            return json.RootElement.ValueKind == JsonValueKind.Null
                || json.RootElement.ValueKind == JsonValueKind.Object && !json.RootElement.EnumerateObject().Any();
        }
        catch (JsonException) { return false; }
    }

    internal static bool OwnsExcelSalaryHistory(ExcelPayrollSalaryHistory row, string reference) =>
        !string.IsNullOrWhiteSpace(reference) && (row.ChangeReason == reference
            || row.ChangeReason.StartsWith(reference + ";", StringComparison.Ordinal));

    internal static DateTime[] ExcelSalaryHistoryMarkers(IEnumerable<ExcelPayrollSalaryHistory> history, DateTime effectiveFrom,
        string? correctionOfReference = null)
    {
        if (correctionOfReference is not null)
        {
            if (string.IsNullOrWhiteSpace(correctionOfReference))
                throw new InvalidOperationException("The exact original source reference is required for salary correction.");
            var affected = history.Where(row => row.EffectiveFrom.Date >= effectiveFrom.Date).ToArray();
            if (affected.Any(row => !IsUnconfiguredExcelSalaryHistory(row) && !OwnsExcelSalaryHistory(row, correctionOfReference)))
                throw new InvalidOperationException("A configured salary-history record is not owned by the reviewed original import. Retain it and review its effective dates before correction.");
            // Period payroll selects history by effective date, including historical rows.
            // Append corrected versions at the original source dates so an older later
            // snapshot cannot win. Empty historical markers remain untouched.
            return affected.Where(row => row.EffectiveFrom.Date > effectiveFrom.Date
                    && (OwnsExcelSalaryHistory(row, correctionOfReference) || row.Status == "Active"))
                .Select(row => row.EffectiveFrom.Date).Distinct().Order().ToArray();
        }
        var later = history.Where(row => row.EffectiveFrom.Date > effectiveFrom.Date).ToList();
        if (later.Any(row => !IsUnconfiguredExcelSalaryHistory(row)))
            throw new InvalidOperationException("A later configured basic-pay record exists. Review salary history before importing an earlier salary setup.");
        // An initial empty master snapshot must not override the imported salary later
        // in the month. Retain it and append an actual salary version at each marker.
        return later.Select(row => row.EffectiveFrom.Date).Distinct().Order().ToArray();
    }

    internal sealed class ExcelPayrollPolicyCohort
    {
        public int ClientId { get; set; }
        public int GroupId { get; set; }
        public string EmployeeIdsJson { get; set; } = "[]";
    }

    private static async Task<List<ExcelPayrollPolicyCohort>> ReadExcelPayrollPolicyCohortsAsync(MySqlConnection db, MySqlTransaction? tx, int[] clientIds)
    {
        // Backward compatible with databases where the explicit bridge migration has not run.
        if (!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='excel_payroll_setup_imports')", transaction: tx)) return [];
        return (await db.QueryAsync<ExcelPayrollPolicyCohort>(@"SELECT client_id ClientId,
CAST(JSON_UNQUOTE(JSON_EXTRACT(result_json,'$.attendanceGroupId')) AS UNSIGNED) GroupId,
COALESCE(CAST(JSON_EXTRACT(result_json,'$.employeeIds') AS CHAR),'[]') EmployeeIdsJson
FROM excel_payroll_setup_imports WHERE client_id IN @ClientIds AND status='Applied'
AND COALESCE(JSON_LENGTH(JSON_EXTRACT(result_json,'$.attendanceAssignments')),0)=0
UNION ALL
SELECT receipt.client_id ClientId,assignment.GroupId,CAST(JSON_ARRAYAGG(assignment.EmployeeId) AS CHAR) EmployeeIdsJson
FROM excel_payroll_setup_imports receipt
JOIN JSON_TABLE(COALESCE(JSON_EXTRACT(receipt.result_json,'$.attendanceAssignments'),JSON_ARRAY()),'$[*]'
COLUMNS(EmployeeId INT PATH '$.employeeId',GroupId INT PATH '$.groupId',IsSourceCohort INT PATH '$.isSourceCohort' DEFAULT '0' ON EMPTY)) assignment
WHERE receipt.client_id IN @ClientIds AND receipt.status='Applied' AND assignment.IsSourceCohort=1
GROUP BY receipt.id,receipt.client_id,assignment.GroupId", new { ClientIds = clientIds }, tx))
            .Where(c => c.GroupId > 0).ToList();
    }

    internal static int[] SelectedExcelPayrollPolicyGroups(Employee employee, IEnumerable<ExcelPayrollPolicyCohort> policies)
    {
        if (!employee.IsActive || employee.Id <= 0 || employee.ClientId <= 0) return [];
        return policies.Where(c => c.ClientId == employee.ClientId).Where(c =>
        {
            using var json = JsonDocument.Parse(c.EmployeeIdsJson);
            return json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.EnumerateArray().Any(id => id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var value) && value == employee.Id);
        }).Select(c => c.GroupId).Distinct().ToArray();
    }

    // The caller owns the transaction, client lock, reviewed identity and source snapshot.
    // This deliberately does not provision logins, publish notifications or remap other policies.
    internal static async Task<int> SaveExcelPayrollSetupEmployeeAsync(MySqlConnection db, MySqlTransaction tx,
        Employee employee, DateTime effectiveFrom, string actor, string reference, string? correctionOfReference = null)
    {
        if (employee.ClientId <= 0 || string.IsNullOrWhiteSpace(employee.EmployeeCode) || string.IsNullOrWhiteSpace(employee.FirstName))
            throw new InvalidOperationException("A reviewed client, employee code and name are required.");
        if (employee.Id > 0 && await db.ExecuteScalarAsync<int?>("SELECT ClientId FROM employees WHERE Id=@Id FOR UPDATE", new { employee.Id }, tx) != employee.ClientId)
            throw new InvalidOperationException("The reviewed employee does not belong to this client.");
        var history = employee.Id <= 0 ? [] : (await db.QueryAsync<ExcelPayrollSalaryHistory>(@"SELECT Id,EffectiveFrom,Status,SalaryStructureId,AnnualCtc,CAST(SalaryJson AS CHAR) SalaryJson,COALESCE(ChangeReason,'') ChangeReason
FROM employee_it0008_basic_pay WHERE EmployeeId=@Id ORDER BY EffectiveFrom,Id FOR UPDATE", new { employee.Id }, tx)).ToList();
        var markers = ExcelSalaryHistoryMarkers(history, effectiveFrom, correctionOfReference);
        var superseded = history.Where(row => row.Status == "Active" && row.EffectiveFrom.Date >= effectiveFrom.Date).Select(row => row.Id).ToArray();
        if (superseded.Length > 0)
        {
            // Keep original values, authors and start dates, with a valid one-day
            // history interval. New same-date versions take precedence by their ID.
            await db.ExecuteAsync(@"UPDATE employee_it0008_basic_pay SET Status='Historical',EffectiveTo=EffectiveFrom
WHERE EmployeeId=@EmployeeId AND Id IN @Ids AND Status='Active'", new { EmployeeId=employee.Id,Ids=superseded },tx);
        }
        var id = await SaveWithOpenConnectionAsync(db, employee, actor, employee.Id == 0 ? null : "0008", reference, tx,
            effectiveDate: effectiveFrom.Date, synchronizeAttendancePolicies: false, salaryOnly: employee.Id > 0);
        foreach (var marker in markers)
            await WritePhysicalInfotypeAsync(db, employee, "0008", "Master Update", marker,
                correctionOfReference is null
                    ? $"{reference}; initial salary effective {effectiveFrom:yyyy-MM-dd}; retained empty setup marker {marker:yyyy-MM-dd}."
                    : $"{reference}; correction of {correctionOfReference}; retained source effective date {marker:yyyy-MM-dd}.", actor, tx);
        return id;
    }
}

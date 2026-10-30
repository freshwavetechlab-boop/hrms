using Dapper;
using MySqlConnector;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal sealed record LocationAssignmentStorageRow(string Id, int ClientId, int EmployeeId, int WorkLocationId,
        string WorkState, string SalaryStructureId, decimal AnnualCtc, string SalaryInputsJson, string ComponentScopesJson,
        string RulesHash, string SourceHash, DateTime ConfigurationEffectiveFrom, string SetupImportId,
        string SourceBatchId, string SourceRowId, string CreatedBy)
    {
        public string Status { get; init; } = "Active";
        public DateTime CreatedAt { get; init; }
    }

    internal sealed record LocationAllocationStorageRow(int ClientId, int EmployeeId, string PayPeriod,
        string SourcePartKey, string AssignmentId, int PayrollDays, decimal PresentDays, decimal PayableDays,
        string? ReferenceAssignmentId, string SetupImportId, string SourceBatchId, string SourceRowId, string SourceHash)
    {
        public DateTime CreatedAt { get; init; }
    }

    internal sealed record LocationAllocationStorageState(IReadOnlyList<LocationAssignmentStorageRow> Assignments,
        IReadOnlyList<LocationAllocationStorageRow> Allocations);

    // This is a capability check only. No request path provisions tables or runs
    // migration SQL; an incomplete/older additive schema is unavailable.
    internal static async Task<bool> HasLocationAllocationSchemaAsync(MySqlConnection db, MySqlTransaction? tx = null) =>
        await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM information_schema.columns
WHERE table_schema=DATABASE() AND (
(table_name='employee_payroll_location_assignments' AND column_name IN
('id','client_id','employee_id','work_location_id','work_state','salary_structure_id','annual_ctc','salary_inputs_json','component_scopes_json','rules_hash','source_hash','configuration_effective_from','setup_import_id','source_batch_id','source_row_id','created_by','status','created_at'))
OR (table_name='employee_payroll_location_allocations' AND column_name IN
('client_id','employee_id','pay_period','source_part_key','assignment_id','payroll_days','present_days','payable_days','reference_assignment_id','setup_import_id','source_batch_id','source_row_id','source_hash','created_at')))", transaction: tx) == 32;

    internal static async Task<LocationAllocationStorageState> ReadLocationAllocationStateAsync(MySqlConnection db,
        MySqlTransaction tx, int clientId, IReadOnlyList<int> employeeIds, string payPeriod, bool lockRows = false)
    {
        if (employeeIds.Count == 0) return new([], []);
        var parameters = new { ClientId = clientId, EmployeeIds = employeeIds.Distinct().Order().ToArray(), PayPeriod = payPeriod };
        var suffix = lockRows ? " FOR UPDATE" : "";
        var assignments = await db.QueryAsync<LocationAssignmentDbRow>(LocationAssignmentReadColumns + @"
FROM employee_payroll_location_assignments
WHERE client_id=@ClientId AND employee_id IN @EmployeeIds ORDER BY employee_id,id" + suffix, parameters, tx);
        var allocations = await db.QueryAsync<LocationAllocationDbRow>(LocationAllocationReadColumns + @"
FROM employee_payroll_location_allocations
WHERE client_id=@ClientId AND employee_id IN @EmployeeIds AND pay_period=@PayPeriod
ORDER BY employee_id,source_part_key" + suffix, parameters, tx);
        return new(assignments.Select(row => row.Record()).ToList(), allocations.Select(row => row.Record()).ToList());
    }

    internal static IReadOnlyList<LocationAssignmentStorageRow> DistinctLocationAssignmentStorageRows(
        IReadOnlyList<LocationAssignmentStorageRow> assignments)
    {
        var distinct = new List<LocationAssignmentStorageRow>();
        foreach (var group in assignments.GroupBy(row => row.Id, StringComparer.Ordinal))
        {
            var first = group.First();
            if (group.Any(row => !SameLocationAssignmentContent(first, row)))
                throw new InvalidOperationException("A location salary assignment ID has different reviewed contents. Refresh its source-bound preview; immutable assignments cannot be overwritten.");
            distinct.Add(first);
        }
        return distinct;
    }

    internal static bool SameLocationAssignmentContent(LocationAssignmentStorageRow left, LocationAssignmentStorageRow right)
    {
        // MySQL JSON storage may normalize whitespace and object-key order.
        // Compare typed JSON content while retaining every scalar/provenance field.
        if ((left with { CreatedAt = default, SalaryInputsJson = "", ComponentScopesJson = "" })
            != (right with { CreatedAt = default, SalaryInputsJson = "", ComponentScopesJson = "" })) return false;
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(left.SalaryInputsJson), JsonNode.Parse(right.SalaryInputsJson))
                && JsonNode.DeepEquals(JsonNode.Parse(left.ComponentScopesJson), JsonNode.Parse(right.ComponentScopesJson));
        }
        catch (JsonException) { return false; }
    }

    internal static void ValidateLocationAllocationStorageKeys(IReadOnlyList<LocationAllocationStorageRow> allocations)
    {
        if (allocations.GroupBy(row => (row.ClientId, row.EmployeeId, row.PayPeriod, row.SourcePartKey)).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Each employee/source-month allocation needs a unique stable source-part key. Two parts may reference one assignment, but a source part cannot be inserted twice.");
    }

    // Caller owns the client/employee locks, reviewed plan hash and transaction.
    // Idempotent replay is handled by the existing setup receipt. This writer is
    // insert-only and never replaces a salary version or a period allocation.
    internal static async Task InsertLocationAllocationStorageAsync(MySqlConnection db, MySqlTransaction tx,
        IReadOnlyList<LocationAssignmentStorageRow> assignments, IReadOnlyList<LocationAllocationStorageRow> allocations)
    {
        var unique = DistinctLocationAssignmentStorageRows(assignments);
        var newAssignments = new List<LocationAssignmentStorageRow>();
        if (unique.Count > 0)
        {
            var existing = (await db.QueryAsync<LocationAssignmentDbRow>(LocationAssignmentReadColumns + @"
FROM employee_payroll_location_assignments WHERE id IN @Ids ORDER BY id FOR UPDATE",
                new { Ids = unique.Select(row => row.Id).ToArray() }, tx)).Select(row => row.Record()).ToDictionary(row => row.Id, StringComparer.Ordinal);
            foreach (var assignment in unique)
            {
                if (!existing.TryGetValue(assignment.Id, out var previous)) newAssignments.Add(assignment);
                else if (!SameLocationAssignmentContent(previous, assignment))
                    throw new InvalidOperationException("An immutable location salary assignment already exists with different contents. Refresh the reviewed source version; no assignment was replaced.");
            }
        }
        ValidateLocationAllocationStorageKeys(allocations);

        if (newAssignments.Count > 0) await db.ExecuteAsync(@"INSERT INTO employee_payroll_location_assignments
(id,client_id,employee_id,work_location_id,work_state,salary_structure_id,annual_ctc,salary_inputs_json,component_scopes_json,rules_hash,source_hash,configuration_effective_from,setup_import_id,source_batch_id,source_row_id,created_by,status)
VALUES(@Id,@ClientId,@EmployeeId,@WorkLocationId,@WorkState,@SalaryStructureId,@AnnualCtc,CAST(@SalaryInputsJson AS JSON),CAST(@ComponentScopesJson AS JSON),@RulesHash,@SourceHash,@ConfigurationEffectiveFrom,@SetupImportId,@SourceBatchId,@SourceRowId,@CreatedBy,@Status)", newAssignments, tx);
        if (allocations.Count > 0) await db.ExecuteAsync(@"INSERT INTO employee_payroll_location_allocations
(client_id,employee_id,pay_period,source_part_key,assignment_id,payroll_days,present_days,payable_days,reference_assignment_id,setup_import_id,source_batch_id,source_row_id,source_hash)
VALUES(@ClientId,@EmployeeId,@PayPeriod,@SourcePartKey,@AssignmentId,@PayrollDays,@PresentDays,@PayableDays,@ReferenceAssignmentId,@SetupImportId,@SourceBatchId,@SourceRowId,@SourceHash)", allocations, tx);
    }

    private const string LocationAssignmentReadColumns = @"SELECT id Id,client_id ClientId,employee_id EmployeeId,work_location_id WorkLocationId,work_state WorkState,
salary_structure_id SalaryStructureId,annual_ctc AnnualCtc,CAST(salary_inputs_json AS CHAR) SalaryInputsJson,CAST(component_scopes_json AS CHAR) ComponentScopesJson,
rules_hash RulesHash,source_hash SourceHash,configuration_effective_from ConfigurationEffectiveFrom,setup_import_id SetupImportId,source_batch_id SourceBatchId,
source_row_id SourceRowId,created_by CreatedBy,status Status,created_at CreatedAt";

    private const string LocationAllocationReadColumns = @"SELECT client_id ClientId,employee_id EmployeeId,pay_period PayPeriod,source_part_key SourcePartKey,
assignment_id AssignmentId,payroll_days PayrollDays,present_days PresentDays,payable_days PayableDays,reference_assignment_id ReferenceAssignmentId,
setup_import_id SetupImportId,source_batch_id SourceBatchId,source_row_id SourceRowId,source_hash SourceHash,created_at CreatedAt";

    private sealed class LocationAssignmentDbRow
    {
        public string Id { get; set; } = "";
        public int ClientId { get; set; }
        public int EmployeeId { get; set; }
        public int WorkLocationId { get; set; }
        public string WorkState { get; set; } = "";
        public string SalaryStructureId { get; set; } = "";
        public decimal AnnualCtc { get; set; }
        public string SalaryInputsJson { get; set; } = "{}";
        public string ComponentScopesJson { get; set; } = "{}";
        public string RulesHash { get; set; } = "";
        public string SourceHash { get; set; } = "";
        public DateTime ConfigurationEffectiveFrom { get; set; }
        public string SetupImportId { get; set; } = "";
        public string SourceBatchId { get; set; } = "";
        public string SourceRowId { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public LocationAssignmentStorageRow Record() => new(Id, ClientId, EmployeeId, WorkLocationId, WorkState, SalaryStructureId,
            AnnualCtc, SalaryInputsJson, ComponentScopesJson, RulesHash, SourceHash, ConfigurationEffectiveFrom, SetupImportId, SourceBatchId, SourceRowId, CreatedBy)
            { Status = Status, CreatedAt = CreatedAt };
    }

    private sealed class LocationAllocationDbRow
    {
        public int ClientId { get; set; }
        public int EmployeeId { get; set; }
        public string PayPeriod { get; set; } = "";
        public string SourcePartKey { get; set; } = "";
        public string AssignmentId { get; set; } = "";
        public int PayrollDays { get; set; }
        public decimal PresentDays { get; set; }
        public decimal PayableDays { get; set; }
        public string? ReferenceAssignmentId { get; set; }
        public string SetupImportId { get; set; } = "";
        public string SourceBatchId { get; set; } = "";
        public string SourceRowId { get; set; } = "";
        public string SourceHash { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public LocationAllocationStorageRow Record() => new(ClientId, EmployeeId, PayPeriod, SourcePartKey, AssignmentId, PayrollDays,
            PresentDays, PayableDays, ReferenceAssignmentId, SetupImportId, SourceBatchId, SourceRowId, SourceHash) { CreatedAt = CreatedAt };
    }
}

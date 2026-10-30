using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    // A period allocation deliberately references its immutable assignment version.
    // Its payroll period may precede the import/configuration audit date; this is
    // not a fabricated dated employee transfer and must not use a "latest" row.
    private static async Task LoadLocationPartPeriodsAsync(MySqlConnection connection,
        MySqlTransaction transaction, List<PayRunSourceEmployee> employees, string payPeriod)
    {
        if (employees.Count == 0) return;
        if (!DateTime.TryParseExact(payPeriod + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var requestedPeriodStart))
            throw LocationPartStoreError("The payroll period is invalid.");
        var allocationsExist = await TableExistsAsync(connection, transaction, "employee_payroll_location_allocations");
        var assignmentsExist = await TableExistsAsync(connection, transaction, "employee_payroll_location_assignments");
        if (!allocationsExist && !assignmentsExist) return;
        var employeeIds = employees.Select(employee => employee.Id).Distinct().ToArray();
        var allocations = allocationsExist ? (await connection.QueryAsync<LocationPartStoreRow>(@"
SELECT a.client_id ClientId,a.employee_id EmployeeId,a.pay_period PayPeriod,
       a.source_part_key PartKey,a.assignment_id AssignmentId,
       a.payroll_days PayrollDays,a.present_days PresentDays,a.payable_days PayableDays,
       a.reference_assignment_id ReferenceAssignmentId,a.source_hash AllocationSourceHash,
       a.setup_import_id AllocationImportId,a.source_batch_id AllocationBatchId,
       a.source_row_id AllocationRowId
FROM employee_payroll_location_allocations a
WHERE a.employee_id IN @EmployeeIds AND a.pay_period=@PayPeriod
ORDER BY a.employee_id,a.source_part_key", new { EmployeeIds = employeeIds, PayPeriod = payPeriod }, transaction)).ToList() : [];
        if (!assignmentsExist)
        {
            if (allocations.Count == 0) return;
            throw LocationPartStoreError("Saved period allocations exist but their salary-assignment storage is unavailable.");
        }
        var assignmentIds = allocations.Select(row => row.AssignmentId).Distinct(StringComparer.Ordinal).ToArray();
        var periodEnd = requestedPeriodStart.AddMonths(1).AddDays(-1);
        var assignments = (await connection.QueryAsync<LocationPartAssignmentStoreRow>(@"
SELECT id AssignmentId,client_id ClientId,employee_id EmployeeId,
       work_location_id WorkLocationId,work_state WorkState,salary_structure_id SalaryStructureId,
       annual_ctc AnnualCtc,CAST(salary_inputs_json AS CHAR) SalaryInputsJson,
       CAST(component_scopes_json AS CHAR) ComponentScopesJson,rules_hash RulesHash,
       source_hash SourceHash,setup_import_id ImportId,source_batch_id BatchId,
       source_row_id RowId,status Status,DATE_FORMAT(configuration_effective_from,'%Y-%m-%d') ConfigurationEffectiveFrom
FROM employee_payroll_location_assignments
WHERE id IN @AssignmentIds
   OR (employee_id IN @EmployeeIds AND status='Active' AND configuration_effective_from<=@PeriodEnd)",
            new { AssignmentIds = assignmentIds, EmployeeIds = employeeIds, PeriodEnd = periodEnd }, transaction)).ToList();
        var periods = ReadLocationPartPeriods(allocations, assignments,
            employees.ToDictionary(employee => employee.Id, employee => employee.ClientId), payPeriod);
        if (periods.Count == 0) return;
        ValidateLocationAssignmentRules(assignments.Where(assignment => assignmentIds.Contains(assignment.AssignmentId, StringComparer.Ordinal)).ToList(),
            await PayrollDataTableStore.GetSetupJsonAsync(connection, transaction));
        foreach (var employee in employees)
            if (periods.TryGetValue(employee.Id, out var period)) employee.LocationParts = period;
    }

    internal sealed class LocationPartStoreRow
    {
        public int ClientId { get; set; }
        public int EmployeeId { get; set; }
        public string PayPeriod { get; set; } = "";
        public string PartKey { get; set; } = "";
        public string AssignmentId { get; set; } = "";
        public int PayrollDays { get; set; }
        public decimal PresentDays { get; set; }
        public decimal PayableDays { get; set; }
        public string? ReferenceAssignmentId { get; set; }
        public string AllocationSourceHash { get; set; } = "";
        public string AllocationImportId { get; set; } = "";
        public string AllocationBatchId { get; set; } = "";
        public string AllocationRowId { get; set; } = "";
    }

    internal sealed class LocationPartAssignmentStoreRow
    {
        public string AssignmentId { get; set; } = "";
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
        public string ImportId { get; set; } = "";
        public string BatchId { get; set; } = "";
        public string RowId { get; set; } = "";
        public string Status { get; set; } = "";
        public string ConfigurationEffectiveFrom { get; set; } = "";
    }

    internal static IReadOnlyDictionary<int, PayrollLocationPartPeriod> ReadLocationPartPeriods(
        IReadOnlyCollection<LocationPartStoreRow> rows,
        IReadOnlyCollection<LocationPartAssignmentStoreRow> assignmentRows,
        IReadOnlyDictionary<int, int> employeeClients, string payPeriod)
    {
        if (!DateTime.TryParseExact(payPeriod + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var periodStart))
            throw LocationPartStoreError("The payroll period is invalid.");
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        var allocatedEmployeeIds = rows.Select(row => row.EmployeeId).ToHashSet();
        foreach (var assignment in assignmentRows.Where(assignment => assignment.Status == "Active"))
        {
            if (!DateTime.TryParseExact(assignment.ConfigurationEffectiveFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var effectiveFrom))
                throw LocationPartStoreError("A salary-assignment configuration date is invalid.");
            if (effectiveFrom > periodEnd) continue;
            if (!employeeClients.TryGetValue(assignment.EmployeeId, out var clientId) || clientId != assignment.ClientId)
                throw LocationPartStoreError("A location salary assignment does not match the selected employee and client.");
            if (!allocatedEmployeeIds.Contains(assignment.EmployeeId))
                throw LocationPartStoreError("Reviewed location salary assignments require attendance allocations for this payroll month. Record each location's attendance before running Regular Payroll.");
        }
        if (rows.Count == 0) return new Dictionary<int, PayrollLocationPartPeriod>();
        if (assignmentRows.Any(row => !IsStoreId(row.AssignmentId)) ||
            assignmentRows.GroupBy(row => row.AssignmentId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw LocationPartStoreError("An assignment version is duplicated.");
        var assignments = assignmentRows.ToDictionary(row => row.AssignmentId, StringComparer.Ordinal);
        var result = new Dictionary<int, PayrollLocationPartPeriod>();
        foreach (var group in rows.GroupBy(row => row.EmployeeId))
        {
            if (!employeeClients.TryGetValue(group.Key, out var clientId) || clientId <= 0 ||
                group.Any(row => row.ClientId != clientId || row.PayPeriod != payPeriod))
                throw LocationPartStoreError("An allocation does not match the selected employee, client and payroll period.");
            if (group.GroupBy(row => row.PartKey, StringComparer.Ordinal).Any(parts => parts.Count() != 1))
                throw LocationPartStoreError("A source part is duplicated within an employee's payroll period.");
            var references = group.Select(row => string.IsNullOrWhiteSpace(row.ReferenceAssignmentId)
                    ? null : row.ReferenceAssignmentId).Distinct(StringComparer.Ordinal).ToList();
            if (references.Count != 1)
                throw LocationPartStoreError("Period parts disagree about the reviewed monthly reference assignment.");
            var period = new PayrollLocationPartPeriod
            {
                ClientId = clientId, EmployeeId = group.Key, PayPeriod = payPeriod,
                ReferenceAssignmentId = references[0]
            };
            foreach (var row in group)
            {
                if (string.IsNullOrWhiteSpace(row.PartKey) || row.PartKey.Length > 100 ||
                    !IsStoreHash(row.AllocationSourceHash) || !IsStoreId(row.AllocationImportId) ||
                    !IsStoreId(row.AllocationBatchId) || string.IsNullOrWhiteSpace(row.AllocationRowId))
                    throw LocationPartStoreError("A period allocation is missing its reviewed source or import provenance.");
                if (!IsStoreId(row.AssignmentId) || !assignments.TryGetValue(row.AssignmentId, out var assignment) ||
                    assignment.ClientId != clientId || assignment.EmployeeId != group.Key)
                    throw LocationPartStoreError("A period allocation references a missing or differently scoped salary assignment.");
                if (assignment.Status != "Active" || assignment.WorkLocationId <= 0 ||
                    string.IsNullOrWhiteSpace(assignment.SalaryStructureId) || assignment.SalaryStructureId.Length > 40 ||
                    assignment.AnnualCtc is < 0 or > 1000000000m || !IsStoreHash(assignment.RulesHash) ||
                    !IsStoreHash(assignment.SourceHash) || !IsStoreId(assignment.ImportId) ||
                    !IsStoreId(assignment.BatchId) || string.IsNullOrWhiteSpace(assignment.RowId))
                    throw LocationPartStoreError("A referenced salary assignment is inactive or lacks reviewed rules and source provenance.");
                if (row.PayrollDays is < 1 or > 31 || row.PresentDays < 0 || row.PayableDays < 0 ||
                    row.PresentDays > row.PayrollDays || row.PayableDays > row.PayrollDays)
                    throw LocationPartStoreError("A location attendance allocation has invalid payroll, present or payable days.");
                period.Parts.Add(new PayrollLocationPart
                {
                    PartKey = row.PartKey, AssignmentId = assignment.AssignmentId,
                    WorkLocationId = assignment.WorkLocationId, WorkState = assignment.WorkState,
                    SalaryStructureId = assignment.SalaryStructureId, AnnualCtc = assignment.AnnualCtc,
                    SalaryInputs = ReadLocationSalaryInputs(assignment.SalaryInputsJson),
                    ComponentScopes = ReadLocationComponentScopes(assignment.ComponentScopesJson),
                    PayrollDays = row.PayrollDays, PresentDays = row.PresentDays, PayableDays = row.PayableDays
                });
            }
            if (period.ReferenceAssignmentId is not null && !period.Parts.Any(part => part.AssignmentId == period.ReferenceAssignmentId))
                throw LocationPartStoreError("The monthly reference assignment is not one of this period's reviewed parts.");
            result.Add(group.Key, period);
        }
        return result;
    }

    internal static void ValidateLocationAssignmentRules(
        IReadOnlyCollection<LocationPartAssignmentStoreRow> assignments, string setupJson)
    {
        if (assignments.Count == 0) return;
        JsonObject? setup;
        try { setup = JsonNode.Parse(setupJson) as JsonObject; }
        catch (JsonException) { throw LocationPartStoreError("The current Settings salary configuration is invalid."); }
        if (setup is null) throw LocationPartStoreError("The current Settings salary configuration is missing.");
        var templates = (setup["salaryStructures"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var masters = (setup["salaryComponents"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        foreach (var assignment in assignments)
        {
            var matches = templates.Where(template => template["id"]?.ToString() == assignment.SalaryStructureId).ToList();
            if (matches.Count != 1)
                throw LocationPartStoreError("A referenced Settings salary template is missing or duplicated.");
            var template = matches[0];
            if (template["active"] is not JsonValue activeValue || !activeValue.TryGetValue<bool>(out var active) || !active ||
                !(template["clientId"]?.ToString() ?? "").Split([';', ',', '|'])
                    .Any(value => value.Split(':')[0].Trim() == assignment.ClientId.ToString(CultureInfo.InvariantCulture)))
                throw LocationPartStoreError("A referenced Settings salary template is inactive or belongs to another client.");
            string currentHash;
            try { currentHash = ExcelPayrollSetupRepository.LocationSettingsRulesHash(template, masters); }
            catch (InvalidOperationException)
            {
                throw LocationPartStoreError("A Settings template component is missing or duplicated. Review its assignment before calculating payroll.");
            }
            if (!string.Equals(assignment.RulesHash, currentHash, StringComparison.OrdinalIgnoreCase))
                throw LocationPartStoreError("Settings salary rules have changed since this assignment was reviewed. Review a new assignment version before calculating payroll.");
        }
    }

    private static Dictionary<string, decimal> ReadLocationSalaryInputs(string json)
    {
        using var document = ReadLocationObject(json, "recurring salary inputs");
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 100 ||
                property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDecimal(out var value) ||
                value is < 0 or > 1000000000m || !result.TryAdd(property.Name, value))
                throw LocationPartStoreError("Recurring salary inputs must be unique numeric component values; text and duplicate component keys are invalid.");
        }
        return result;
    }

    private static Dictionary<string, string> ReadLocationComponentScopes(string json)
    {
        using var document = ReadLocationObject(json, "component scopes");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var scope = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 40 ||
                scope is not ("SourceFormulaPerPart" or "Monthly") || !result.TryAdd(property.Name, scope))
                throw LocationPartStoreError("Each configured component requires a unique reviewed per-part or monthly calculation scope.");
        }
        if (result.Count == 0) throw LocationPartStoreError("The salary assignment has no reviewed component scopes.");
        return result;
    }

    private static JsonDocument ReadLocationObject(string json, string label)
    {
        try
        {
            var document = JsonDocument.Parse(json ?? "");
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        throw LocationPartStoreError($"Saved {label} are invalid. Review and reimport the allocation; payroll did not fall back to an unrelated template.");
    }

    private static bool IsStoreId(string? value) => value?.Length == 32 && value.All(Uri.IsHexDigit);
    private static bool IsStoreHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static InvalidOperationException LocationPartStoreError(string message) =>
        new("Location payroll allocation: " + message);
}

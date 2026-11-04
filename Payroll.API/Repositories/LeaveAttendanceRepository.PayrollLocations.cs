using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class LeaveAttendanceRepository
{
    private static readonly JsonSerializerOptions LocationAttendanceJson = new(JsonSerializerDefaults.Web);

    public async Task<PayrollLocationAttendanceContext> GetPayrollLocationAttendanceAsync(int clientId, string month,
        string policyBatchId, List<int> groupIds)
    {
        PayrollLocationAttendancePlan.ValidateScope(clientId, month, groupIds);
        await using var db = CreateConnection(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true);
        var result = await ReadPayrollLocationAttendanceAsync(db, tx, clientId, month, policyBatchId, groupIds);
        await tx.RollbackAsync(); return result;
    }

    private static async Task<HashSet<string>> PayrollLocationTablesAsync(MySqlConnection db, MySqlTransaction tx) =>
        (await db.QueryAsync<string>(@"SELECT table_name FROM information_schema.tables WHERE table_schema=DATABASE()
AND table_name IN ('employee_payroll_location_assignments','employee_payroll_location_allocations',
'payroll_location_attendance_inputs','payroll_location_attendance_imports')", transaction: tx)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task<List<LocationAttendanceAssignment>> ReadLocationAttendanceAssignmentsAsync(MySqlConnection db,
        MySqlTransaction tx, int clientId, int[] employeeIds) => employeeIds.Length == 0 ? [] :
        (await db.QueryAsync<LocationAttendanceAssignment>(@"SELECT a.id Id,a.employee_id EmployeeId,a.work_location_id WorkLocationId,
a.salary_structure_id TemplateId,a.rules_hash RulesHash,a.source_hash SourceHash,a.source_batch_id SourceBatchId,
a.source_row_id SourceRowId,a.status Status,COALESCE(w.Name,'') WorkLocationName,a.annual_ctc AnnualCtc,
CAST(a.salary_inputs_json AS CHAR) SalaryInputsJson,COALESCE(JSON_UNQUOTE(JSON_EXTRACT(b.batch_json,'$.month')),'') SourceMonth
FROM employee_payroll_location_assignments a LEFT JOIN worklocations w ON w.Id=a.work_location_id AND w.ClientId=a.client_id
LEFT JOIN excel_payslip_batches b ON b.id=a.source_batch_id AND b.client_id=a.client_id
WHERE a.client_id=@ClientId AND a.employee_id IN @EmployeeIds ORDER BY a.configuration_effective_from,a.id",
        new { ClientId = clientId, EmployeeIds = employeeIds }, tx)).ToList();

    private async Task<PayrollLocationAttendanceContext> ReadPayrollLocationAttendanceAsync(MySqlConnection db,
        MySqlTransaction tx, int clientId, string month, string policyBatchId, List<int> groupIds)
    {
        PayrollLocationAttendancePlan.ValidateScope(clientId, month, groupIds);
        var policies = (await db.QueryAsync<LocationAttendancePolicy>(@"SELECT id Id,COALESCE(policy_batch_id,'') BatchId
FROM attendance_groups WHERE client_id=@ClientId AND is_active=TRUE", new { ClientId = clientId }, tx)).ToList();
        if (groupIds.Any(id => !policies.Any(p => p.Id == id)) || !string.IsNullOrEmpty(policyBatchId)
            && !policies.Where(p => (string.IsNullOrEmpty(p.BatchId) ? "group:" + p.Id : p.BatchId) == policyBatchId)
                .Select(p => p.Id).ToHashSet().SetEquals(groupIds))
            throw new InvalidOperationException("The attendance policy changed or belongs to another client. Select it again.");
        var start = DateTime.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture); var end = start.AddMonths(1).AddDays(-1);
        var employees = (await db.QueryAsync<LocationAttendanceEmployee>(@"SELECT e.Id EmployeeId,e.EmployeeCode,
CONCAT(e.FirstName,' ',e.LastName) EmployeeName,e.WorkLocationId,COALESCE(w.Name,'') WorkLocationName,
COALESCE((SELECT t.SalaryStructureId FROM employee_it0008_basic_pay t WHERE t.EmployeeId=e.Id
 AND t.EffectiveFrom<=@End AND (t.EffectiveTo IS NULL OR t.EffectiveTo>=@Start)
 ORDER BY t.EffectiveFrom DESC,t.Id DESC LIMIT 1),e.SalaryStructureId) TemplateId,
a.working_days WorkingDays,a.present_days PresentDays,a.payable_days PayableDays
FROM employees e LEFT JOIN worklocations w ON w.Id=e.WorkLocationId AND w.ClientId=e.ClientId
LEFT JOIN employee_monthly_attendance a ON a.client_id=e.ClientId AND a.employee_id=e.Id AND a.attendance_month=@Month
WHERE e.ClientId=@ClientId AND " + EmployeeEmploymentRules.EligibleInMonth("e", "@Month") + @"
AND EXISTS(SELECT 1 FROM attendance_group_employees m JOIN attendance_groups g ON g.id=m.attendance_group_id
 WHERE m.employee_id=e.Id AND g.client_id=@ClientId AND g.is_active=TRUE AND g.id IN @GroupIds)
ORDER BY e.EmployeeCode,e.Id", new { ClientId = clientId, Month = month, Start = start, End = end, GroupIds = groupIds.ToArray() }, tx)).ToList();
        var ids = employees.Select(e => e.EmployeeId).ToArray();
        var tables = await PayrollLocationTablesAsync(db, tx);
        var hasAssignments = tables.Contains("employee_payroll_location_assignments") && tables.Contains("employee_payroll_location_allocations");
        var assignments = hasAssignments ? await ReadLocationAttendanceAssignmentsAsync(db, tx, clientId, ids) : [];
        var allocations = !hasAssignments || ids.Length == 0 ? [] :
            (await db.QueryAsync<LocationAttendanceAllocation>(@"SELECT employee_id EmployeeId,pay_period Period,source_part_key PartKey,
assignment_id AssignmentId,reference_assignment_id ReferenceAssignmentId,payroll_days PayrollDays,present_days PresentDays,payable_days PayableDays
FROM employee_payroll_location_allocations WHERE client_id=@ClientId AND employee_id IN @Ids AND pay_period<=@Month
ORDER BY pay_period DESC,source_part_key", new { ClientId = clientId, Ids = ids, Month = month }, tx)).ToList();
        var storage = tables.Contains("payroll_location_attendance_inputs") && tables.Contains("payroll_location_attendance_imports");
        var inputs = !storage ? [] : (await db.QueryAsync<LocationAttendanceSavedInput>(@"SELECT employee_id EmployeeId,
part_key PartKey,payroll_days PayrollDays,present_days PresentDays,payable_days PayableDays FROM payroll_location_attendance_inputs
WHERE client_id=@ClientId AND pay_period=@Month", new { ClientId = clientId, Month = month }, tx)).ToList();
        var locks = ids.Length == 0 ? [] : (await db.QueryAsync<LocationAttendanceLock>(@"SELECT p.EmployeeId,r.Id PayRunId FROM payrunemployees p
JOIN payruns r ON r.Id=p.PayRunId WHERE r.ClientId=@ClientId AND r.PayPeriod=@Month AND r.RunType='Regular'
AND r.Status IN ('Draft','Pending Approval','Approved','Partially Paid','Paid','AwaitingProcessing','Queued','Processing')
AND p.IsSkipped=FALSE AND p.EmployeeId IN @Ids", new { ClientId = clientId, Month = month, Ids = ids }, tx)).ToList();
        var running = await db.ExecuteScalarAsync<int?>(@"SELECT Id FROM payruns WHERE ClientId=@ClientId AND PayPeriod=@Month
AND RunType='Regular' AND Status IN ('AwaitingProcessing','Queued','Processing') ORDER BY Id DESC LIMIT 1", new { ClientId = clientId, Month = month }, tx);
        var setupJson = await PayrollDataTableStore.GetSetupJsonAsync(db, tx);
        var setup = JsonNode.Parse(setupJson)!.AsObject();
        var templates = (setup["salaryStructures"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var masters = (setup["salaryComponents"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var reviewPriorPeriod = PayRunRepository.PriorPeriodLocationPayReviewer(clientId, setupJson, month);
        var result = new PayrollLocationAttendanceContext { ClientId = clientId, Month = month, PolicyBatchId = policyBatchId,
            GroupIds = groupIds.Order().ToList(), StorageAvailable = storage };
        foreach (var e in employees)
        {
            var latestPeriod = allocations.FirstOrDefault(a => a.EmployeeId == e.EmployeeId)?.Period;
            var parts = allocations.Where(a => a.EmployeeId == e.EmployeeId && a.Period == latestPeriod).ToList();
            var assigned = parts.Count > 0 ? parts.Select(p => assignments.SingleOrDefault(a => a.Id == p.AssignmentId)).Where(a => a is not null).Select(a => a!).ToList()
                : assignments.Where(a => a.EmployeeId == e.EmployeeId && a.Status == "Active").ToList();
            var monthly = e.WorkingDays > 0 ? (int)Math.Ceiling(e.WorkingDays.Value) : DateTime.DaysInMonth(start.Year, start.Month);
            if (assigned.Count == 0) Add(null, null);
            else if (parts.Count > 0) foreach (var part in parts) Add(assignments.SingleOrDefault(a => a.Id == part.AssignmentId), part);
            else foreach (var assignment in assigned) Add(assignment, null);
            void Add(LocationAttendanceAssignment? assignment, LocationAttendanceAllocation? sourcePart)
            {
                var native = assignment is not null;
                var key = native ? sourcePart?.PartKey.StartsWith("native:", StringComparison.Ordinal) == true ? sourcePart.PartKey
                    : "native:" + PayrollLocationAttendancePlan.Hash(assignment!.Id + ":" + (sourcePart?.PartKey ?? assignment.SourceRowId))[..32] : "employee:" + e.EmployeeId;
                var saved = inputs.SingleOrDefault(i => i.EmployeeId == e.EmployeeId && i.PartKey == key);
                var currentSource = sourcePart?.Period == month;
                var row = new PayrollLocationAttendanceRow { Key = key, AssignmentId = assignment?.Id ?? "", ReferenceAssignmentId = sourcePart?.ReferenceAssignmentId,
                    EmployeeId = e.EmployeeId, EmployeeCode = e.EmployeeCode, EmployeeName = e.EmployeeName,
                    NativePart = native, WorkLocationId = assignment?.WorkLocationId ?? e.WorkLocationId,
                    WorkLocationName = assignment?.WorkLocationName ?? e.WorkLocationName, TemplateId = assignment?.TemplateId ?? e.TemplateId,
                    PayrollDays = !native ? monthly : saved?.PayrollDays ?? (currentSource ? sourcePart!.PayrollDays : monthly), MonthlyPayrollDays = monthly,
                    PresentDays = !native ? e.PresentDays : saved?.PresentDays ?? (currentSource ? sourcePart!.PresentDays : null),
                    PayableDays = !native ? e.PayableDays : saved?.PayableDays ?? (currentSource ? sourcePart!.PayableDays : null),
                    LockedPayRunId = running ?? locks.FirstOrDefault(l => l.EmployeeId == e.EmployeeId)?.PayRunId };
                var template = templates.SingleOrDefault(t => t["id"]?.ToString() == row.TemplateId
                    && (t["clientId"]?.ToString() ?? "").Split([';', ',', '|']).Any(p => p.Split(':')[0].Trim() == clientId.ToString(CultureInfo.InvariantCulture)));
                if (row.WorkLocationId <= 0 || string.IsNullOrWhiteSpace(row.WorkLocationName)) row.Issue = "Review the employee's client Location Master mapping.";
                if (template is null || template["active"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) != true)
                    row.Issue = "Review the effective salary template; no template was guessed.";
                else
                {
                    row.TemplateName = template["name"]?.ToString() ?? row.TemplateId;
                    try { row.RulesHash = ExcelPayrollSetupRepository.LocationSettingsRulesHash(template, masters); }
                    catch (InvalidOperationException error) { row.Issue = error.Message; }
                    if (native && (assignment!.Status != "Active" || !row.RulesHash.Equals(assignment.RulesHash, StringComparison.OrdinalIgnoreCase)))
                        row.Issue = "Location salary rules changed. Review a new assignment version before importing attendance.";
                    if (native && row.Issue.Length == 0 && assignment!.SourceMonth.Length > 0)
                        try { row.Issue = reviewPriorPeriod(new() { SalaryStructureId = row.TemplateId,
                            AnnualCtc = assignment.AnnualCtc, PayrollDays = row.PayrollDays,
                            SalaryInputs = JsonSerializer.Deserialize<Dictionary<string, decimal>>(assignment.SalaryInputsJson, LocationAttendanceJson) ?? [] },
                            assignment.SourceMonth); }
                        catch (InvalidOperationException error) { row.Issue = error.Message; }
                }
                row.InputVersion = PayrollLocationAttendancePlan.InputVersion(row.Key, row.PayrollDays, row.PresentDays, row.PayableDays);
                result.Rows.Add(row);
            }
        }
        result.Version = PayrollLocationAttendancePlan.Hash(JsonSerializer.Serialize(new { clientId, month, policyBatchId,
            result.GroupIds, result.StorageAvailable, rows = result.Rows.Select(row => new { row.Key, row.AssignmentId, row.ReferenceAssignmentId,
                row.EmployeeId, row.EmployeeCode, row.WorkLocationId, row.TemplateId, row.RulesHash, row.NativePart, row.Issue }) }, LocationAttendanceJson));
        return result;
    }

    public async Task<PayrollLocationAttendanceResult> SavePayrollLocationAttendanceAsync(PayrollLocationAttendanceRequest request, string actor)
    {
        PayrollLocationAttendancePlan.ValidateScope(request.ClientId, request.Month, request.GroupIds);
        if (!Guid.TryParseExact(request.RequestId, "N", out _) || string.IsNullOrWhiteSpace(actor))
            throw new InvalidOperationException("Use a reviewed attendance request and signed-in actor.");
        var inputHash = PayrollLocationAttendancePlan.Hash(JsonSerializer.Serialize(request, LocationAttendanceJson));
        await using var db = CreateConnection(); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        // Same first lock as Regular Payroll: a queue request cannot race this save.
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId AND IsActive=TRUE FOR UPDATE", new { request.ClientId }, tx) is null)
            throw new InvalidOperationException("The active client is unavailable.");
        var tables = await PayrollLocationTablesAsync(db, tx);
        if (!tables.Contains("payroll_location_attendance_inputs") || !tables.Contains("payroll_location_attendance_imports"))
            throw new InvalidOperationException("Apply 20261103_payroll_location_attendance.sql before saving location attendance.");
        var receipt = await db.QuerySingleOrDefaultAsync<LocationAttendanceReceipt>(@"SELECT client_id ClientId,pay_period Period,input_hash InputHash,
CAST(result_json AS CHAR) ResultJson FROM payroll_location_attendance_imports WHERE id=@Id FOR UPDATE", new { Id = request.RequestId }, tx);
        if (receipt is not null)
        {
            if (receipt.ClientId != request.ClientId || receipt.Period != request.Month || receipt.InputHash != inputHash)
                throw new InvalidOperationException("This import ID already belongs to different inputs. Refresh before retrying.");
            await tx.RollbackAsync(); return JsonSerializer.Deserialize<PayrollLocationAttendanceResult>(receipt.ResultJson, LocationAttendanceJson)!;
        }
        if (request.Rows is null || request.Rows.Count is < 1 or > 1000 || request.Rows.Any(row => row is null)) throw new InvalidOperationException("Import 1–1000 reviewed attendance rows.");
        var ids = request.Rows.Select(row => row.EmployeeId).Distinct().Order().ToArray();
        await db.QueryAsync<int>("SELECT Id FROM employees WHERE ClientId=@ClientId AND Id IN @Ids ORDER BY Id FOR UPDATE", new { request.ClientId, Ids = ids }, tx);
        var context = await ReadPayrollLocationAttendanceAsync(db, tx, request.ClientId, request.Month, request.PolicyBatchId, request.GroupIds);
        var templateIds = context.Rows.Where(r => ids.Contains(r.EmployeeId)).Select(r => r.TemplateId)
            .Where(id => long.TryParse(id, out _)).Select(long.Parse).Distinct().Order().ToArray();
        if (templateIds.Length > 0)
        {
            await db.QueryAsync<long>("SELECT Id FROM salarystructures WHERE Id IN @Ids ORDER BY Id FOR UPDATE", new { Ids = templateIds }, tx);
            await db.QueryAsync<long>("SELECT Id FROM salarystructurelines WHERE StructureId IN @Ids ORDER BY Id FOR UPDATE", new { Ids = templateIds }, tx);
            await db.QueryAsync<long>(@"SELECT c.Id FROM salarycomponents c JOIN salarystructurelines l ON l.ComponentId=c.Id
WHERE l.StructureId IN @Ids ORDER BY c.Id FOR UPDATE", new { Ids = templateIds }, tx);
        }
        context = await ReadPayrollLocationAttendanceAsync(db, tx, request.ClientId, request.Month, request.PolicyBatchId, request.GroupIds);
        var plan = PayrollLocationAttendancePlan.Build(context, request);
        foreach (var row in plan.Changed)
            await db.ExecuteAsync(@"INSERT INTO payroll_location_attendance_inputs
(client_id,pay_period,employee_id,part_key,work_location_id,payroll_days,present_days,payable_days,receipt_id)
VALUES(@ClientId,@Month,@EmployeeId,@Key,@WorkLocationId,@PayrollDays,@PresentDays,@PayableDays,@Receipt)
ON DUPLICATE KEY UPDATE work_location_id=VALUES(work_location_id),payroll_days=VALUES(payroll_days),
present_days=VALUES(present_days),payable_days=VALUES(payable_days),receipt_id=VALUES(receipt_id)",
                new { request.ClientId, request.Month, row.EmployeeId, row.Key, row.WorkLocationId, row.PayrollDays, row.PresentDays, row.PayableDays, Receipt = request.RequestId }, tx);
        var publishIds = plan.Publish.Select(g => g.Key).ToArray();
        var employmentError = await EmployeeAttendanceEligibility.ValidateMonthlyWithOptionalPresenceAsync(db, tx, request.ClientId, request.Month,
            plan.Publish.Select(g => (g.Key, (decimal?)g.Sum(r => r.PresentDays!.Value), g.Sum(r => r.PayableDays!.Value))));
        if (employmentError is not null) throw new InvalidOperationException(employmentError);
        if (publishIds.Length > 0 && await WeeklyOffAttendanceService.ValidateMonthlyChangesAsync(db, tx, request.ClientId, publishIds, request.Month) is string weeklyError)
            throw new InvalidOperationException(weeklyError);
        var assignments = plan.Changed.Any(row => row.NativePart)
            ? await ReadLocationAttendanceAssignmentsAsync(db, tx, request.ClientId, ids) : [];
        foreach (var group in plan.Publish)
        {
            var first = group.First();
            if (first.NativePart)
            {
                var existing = (await db.QueryAsync<string>(@"SELECT source_part_key FROM employee_payroll_location_allocations
WHERE client_id=@ClientId AND employee_id=@EmployeeId AND pay_period=@Month FOR UPDATE",
                    new { request.ClientId, EmployeeId = group.Key, request.Month }, tx)).ToArray();
                if (existing.Any(key => !key.StartsWith("native:", StringComparison.Ordinal)) || existing.Except(group.Select(row => row.Key), StringComparer.Ordinal).Any())
                    throw new InvalidOperationException("Existing reviewed source allocations were retained. Use their source correction review instead of replacing them.");
                foreach (var row in group)
                {
                    var assignment = assignments.Single(a => a.Id == row.AssignmentId && a.EmployeeId == group.Key);
                    await db.ExecuteAsync(@"INSERT INTO employee_payroll_location_allocations
(client_id,employee_id,pay_period,source_part_key,assignment_id,payroll_days,present_days,payable_days,
reference_assignment_id,setup_import_id,source_batch_id,source_row_id,source_hash)
VALUES(@ClientId,@EmployeeId,@Month,@Key,@AssignmentId,@PayrollDays,@PresentDays,@PayableDays,
@ReferenceAssignmentId,@Receipt,@SourceBatchId,@SourceRowId,@SourceHash)
ON DUPLICATE KEY UPDATE payroll_days=VALUES(payroll_days),present_days=VALUES(present_days),payable_days=VALUES(payable_days),setup_import_id=VALUES(setup_import_id)",
                        new { request.ClientId, request.Month, row.EmployeeId, row.Key, row.AssignmentId, row.PayrollDays, row.PresentDays, row.PayableDays,
                            row.ReferenceAssignmentId, Receipt = request.RequestId, assignment.SourceBatchId, assignment.SourceRowId, assignment.SourceHash }, tx);
                }
            }
            var present = group.Sum(row => row.PresentDays!.Value); var payable = group.Sum(row => row.PayableDays!.Value);
            await db.ExecuteAsync(@"INSERT INTO employee_monthly_attendance
(client_id,employee_id,attendance_month,working_days,present_days,payable_days,lop_days,source_type,remarks)
VALUES(@ClientId,@EmployeeId,@Month,@Working,@Present,@Payable,@Lop,'Monthly',@Remarks)
ON DUPLICATE KEY UPDATE working_days=VALUES(working_days),present_days=VALUES(present_days),payable_days=VALUES(payable_days),
lop_days=VALUES(lop_days),source_type='Monthly',remarks=VALUES(remarks)", new { request.ClientId, EmployeeId = group.Key, request.Month,
                Working = first.MonthlyPayrollDays, Present = present, Payable = payable, Lop = first.MonthlyPayrollDays - payable,
                Remarks = "Reviewed location attendance receipt " + request.RequestId }, tx);
        }
        var after = await ReadPayrollLocationAttendanceAsync(db, tx, request.ClientId, request.Month, request.PolicyBatchId, request.GroupIds);
        var result = new PayrollLocationAttendanceResult(request.RequestId, plan.Changed.Count, plan.Publish.Count, plan.PendingEmployees, after);
        await db.ExecuteAsync(@"INSERT INTO payroll_location_attendance_imports
(id,client_id,pay_period,input_hash,request_json,result_json,created_by)
VALUES(@Id,@ClientId,@Month,@InputHash,@RequestJson,@ResultJson,@Actor)", new { Id = request.RequestId, request.ClientId, request.Month,
            InputHash = inputHash, RequestJson = JsonSerializer.Serialize(request, LocationAttendanceJson),
            ResultJson = JsonSerializer.Serialize(result, LocationAttendanceJson), Actor = actor }, tx);
        await tx.CommitAsync(); return result;
    }

    private sealed class LocationAttendanceEmployee
    {
        public int EmployeeId { get; set; } public string EmployeeCode { get; set; } = ""; public string EmployeeName { get; set; } = "";
        public int WorkLocationId { get; set; } public string WorkLocationName { get; set; } = ""; public string TemplateId { get; set; } = "";
        public decimal? WorkingDays { get; set; } public decimal? PresentDays { get; set; } public decimal? PayableDays { get; set; }
    }
    private sealed class LocationAttendanceAssignment
    {
        public string Id { get; set; } = ""; public int EmployeeId { get; set; } public int WorkLocationId { get; set; }
        public string WorkLocationName { get; set; } = ""; public string TemplateId { get; set; } = ""; public string RulesHash { get; set; } = "";
        public string SourceHash { get; set; } = ""; public string SourceBatchId { get; set; } = ""; public string SourceRowId { get; set; } = ""; public string Status { get; set; } = "";
        public decimal AnnualCtc { get; set; } public string SalaryInputsJson { get; set; } = "{}"; public string SourceMonth { get; set; } = "";
    }
    private sealed class LocationAttendanceAllocation
    {
        public int EmployeeId { get; set; } public string Period { get; set; } = ""; public string PartKey { get; set; } = "";
        public string AssignmentId { get; set; } = ""; public string? ReferenceAssignmentId { get; set; }
        public int PayrollDays { get; set; } public decimal PresentDays { get; set; } public decimal PayableDays { get; set; }
    }
    private sealed class LocationAttendanceSavedInput
    {
        public int EmployeeId { get; set; } public string PartKey { get; set; } = ""; public int PayrollDays { get; set; }
        public decimal PresentDays { get; set; } public decimal PayableDays { get; set; }
    }
    private sealed record LocationAttendancePolicy(int Id, string BatchId);
    private sealed record LocationAttendanceLock(int EmployeeId, int PayRunId);
    private sealed record LocationAttendanceReceipt(int ClientId, string Period, string InputHash, string ResultJson);
}

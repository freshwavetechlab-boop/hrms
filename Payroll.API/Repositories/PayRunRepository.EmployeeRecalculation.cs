using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    // This path intentionally accepts no days, component amounts, CTC or template
    // overrides. It re-reads the real dated native inputs under one transaction.
    // The old manual editor remains unchanged and retains its frozen-policy guard.
    private sealed class EmployeeRecalculationState
    {
        public required PayRun Run { get; init; }
        public PayRunEmployee? Before { get; init; }
        public PayRunEmployee? After { get; set; }
        public List<PayRunEmployee> Cohort { get; init; } = [];
        public PayRunSourceEmployee? Employee { get; set; }
        public PayRunAttendance? Attendance { get; set; }
        public string SetupJson { get; set; } = "";
        public List<PayrollAdjustment> Adjustments { get; set; } = [];
        public List<object> TaxInputs { get; set; } = [];
        public List<string> Blockers { get; } = [];
        public string OldTaxJson { get; set; } = "";
        public string OldLinesJson { get; set; } = "";
        public string OldTracesJson { get; set; } = "";
        public string PolicyScopeJson { get; set; } = "";
        public decimal ManualEarnings { get; set; }
        public decimal ManualDeductions { get; set; }
    }

    internal static void ValidateEmployeeRecalculationRequest(RecalculatePayRunEmployeeRequest request)
    {
        if (!request.Confirmed || request.ExpectedFingerprint is not { Length: 64 }
            || !request.ExpectedFingerprint.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(request.Reason)
            || request.Reason.Trim().Length > 500)
            throw new InvalidOperationException("Review this exact employee calculation and enter a correction reason before saving.");
    }

    internal static IReadOnlyList<string> EmployeeRecalculationBlockers(PayRun run, PayRunEmployee? row)
    {
        var result = new List<string>();
        if (run.Status != "Draft") result.Add("Recall an unpaid approved payroll to Draft before correcting an employee. Approval is never changed by recalculation.");
        if (run.RunType != "Regular") result.Add("This action recalculates an existing Regular payroll employee only.");
        if (row is null || row.PayRunId != run.Id || row.ClientId != run.ClientId) result.Add("The employee does not belong to this exact saved payroll and client.");
        else
        {
            if (row.IsSkipped) result.Add("Excluded employees cannot be recalculated through this correction action.");
            if (row.PaymentStatus is not ("" or "Pending") || row.PaymentDate is not null)
                result.Add("Paid or payment-in-progress employees cannot be recalculated.");
        }
        return result;
    }

    private static PayRunEmployeeRecalculationTotals RecalculationTotals(PayRunEmployee row) =>
        new(row.GrossPay + row.OneTimeEarnings, row.StatutoryDeductions + row.OneTimeDeductions, row.NetPay, row.PresentDays, row.PayableDays);

    private static string RecalculationHash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    // Bind rule identities, ceilings and cycle; amounts and recorded attendance
    // may legitimately change, but a correction must not silently drop or replace
    // a previously frozen policy or a reviewed source-period attendance proof.
    internal static string EmployeeRecalculationPolicyBinding(string details)
    {
        using var json = JsonDocument.Parse(details);
        var values = new List<object>();
        foreach (var line in json.RootElement.EnumerateArray())
        {
            if (line.TryGetProperty("pfPolicy", out var pf))
            {
                var p = JsonSerializer.Deserialize<PfComponentSnapshot>(pf.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Frozen PF policy is invalid.");
                values.Add(new { Kind = "PF", p.SchemaVersion, p.SalaryStructureId, p.ComponentId, p.ComponentCode, p.StatutoryType,
                    p.PeriodStart, p.PeriodEnd, p.PayrollDays, Segments = p.Segments.Select(s => new { s.VersionId, s.VersionNumber, s.From, s.To,
                        s.BaseComponentCode, s.CeilingBasis, s.MonthlyWageCeiling, s.RatePercent }).ToArray() });
            }
            if (line.TryGetProperty("attendancePolicy", out var attendance))
            {
                var a = JsonSerializer.Deserialize<PayrollAttendanceSnapshot>(attendance.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Frozen attendance policy is invalid.");
                values.Add(new { Kind = "Attendance", a.SchemaVersion, a.PeriodStart, a.PeriodEnd, a.Source, a.Versions });
            }
            if (line.TryGetProperty("sourceEvidence", out var evidence))
                values.Add(new { Kind = "PayableOnly", Evidence = JsonSerializer.Deserialize<SourcePayableAttendanceEvidence>(evidence.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) });
            if (line.TryGetProperty("reviewedProfessionalTax", out var pt))
                values.Add(new { Kind = "ReviewedPT", Evidence = JsonSerializer.Deserialize<ReviewedProfessionalTaxControl>(pt.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) });
            if (line.TryGetProperty("sourceTds", out var tds))
            {
                var evidenceTds = JsonSerializer.Deserialize<ReviewedSourceTdsEvidence>(tds.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Reviewed source TDS evidence is invalid.");
                values.Add(new { Kind = "SourceTDS", Evidence = evidenceTds with { ProjectedMonthlyTds = 0 } });
            }
        }
        return RecalculationHash(values);
    }

    internal static List<PayRunEmployeeRecalculationChange> EmployeeRecalculationChanges(string before, string after)
    {
        static Dictionary<string, (string Name, string Category, decimal Amount)> Lines(string value)
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.EnumerateArray().Where(line => Text(line, "Category") != "Summary")
                .GroupBy(line => Text(line, "Category") + "|" + Text(line, "Id"), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (Text(group.First(), "Name"), Text(group.First(), "Category"), group.Sum(line => NumberFrom(Text(line, "amount")))), StringComparer.Ordinal);
        }
        var old = Lines(before); var current = Lines(after);
        return old.Keys.Union(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(key =>
        {
            var left = old.GetValueOrDefault(key); var right = current.GetValueOrDefault(key);
            return new PayRunEmployeeRecalculationChange(key[(key.IndexOf('|') + 1)..], right.Name ?? left.Name ?? "", right.Category ?? left.Category ?? "", left.Amount, right.Amount);
        }).Where(change => change.Before != change.After).ToList();
    }

    private static PayRunEmployeeRecalculationPreview RecalculationPreview(EmployeeRecalculationState state, int employeeId)
    {
        var preview = new PayRunEmployeeRecalculationPreview { PayRunId = state.Run.Id, ClientId = state.Run.ClientId, PayPeriod = state.Run.PayPeriod,
            Status = state.Run.Status, EmployeeId = employeeId, EmployeeCode = state.Before?.EmployeeCode ?? "", EmployeeName = state.Before?.EmployeeName ?? "",
            Before = state.Before is null ? null : RecalculationTotals(state.Before), After = state.After is null ? null : RecalculationTotals(state.After), Blockers = state.Blockers.ToList() };
        if (state.Before is not null && state.After is not null) preview.Changes = EmployeeRecalculationChanges(state.Before.DetailsJson, state.After.DetailsJson);
        preview.ExpectedFingerprint = RecalculationHash(new { Version = 1, state.Run, state.Cohort, state.Employee, state.Attendance, state.SetupJson,
            state.Adjustments, state.TaxInputs, state.OldTaxJson, state.OldLinesJson, state.OldTracesJson, state.PolicyScopeJson,
            Policies = state.Employee is null ? null : new { state.Employee.PfPolicyPeriod, state.Employee.AttendancePolicySnapshot,
                state.Employee.PayableOnlyAttendance, state.Employee.NamedInputs }, state.After, state.Blockers });
        preview.CanApply = state.Blockers.Count == 0 && state.After is not null;
        return preview;
    }

    public async Task<PayRunEmployeeRecalculationPreview?> PreviewEmployeeRecalculationAsync(int runId, int employeeId)
    {
        await using var db = CreateConnection(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true);
        var run = await db.QuerySingleOrDefaultAsync<PayRun>("SELECT * FROM payruns WHERE Id=@Id", new { Id = runId }, tx);
        if (run is null) return null;
        var state = await ReadEmployeeRecalculationAsync(db, tx, run, employeeId);
        var preview = RecalculationPreview(state, employeeId); await tx.RollbackAsync(); return preview;
    }

    public async Task<PayRun?> RecalculateEmployeeAsync(int runId, int employeeId, RecalculatePayRunEmployeeRequest request, string actor)
    {
        ValidateEmployeeRecalculationRequest(request);
        await using var db = CreateConnection(); await db.OpenAsync();
        // Serializable reads retain the salary, attendance, source and dated rule
        // rows actually used, while the run lock serializes all standard writes.
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
        var run = await db.QuerySingleOrDefaultAsync<PayRun>("SELECT * FROM payruns WHERE Id=@Id FOR UPDATE", new { Id = runId }, tx);
        if (run is null) return null;
        var state = await ReadEmployeeRecalculationAsync(db, tx, run, employeeId);
        var preview = RecalculationPreview(state, employeeId);
        if (!preview.CanApply) throw new InvalidOperationException(string.Join(" ", preview.Blockers));
        if (!string.Equals(preview.ExpectedFingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The payroll, employee inputs or calculation changed after preview. Refresh and review the new amounts before saving.");
        var originalId = state.Before!.Id;
        // Archive before the first replacement, in the same transaction. The
        // existing audit table stores the full old calculation without new schema.
        var archive = JsonSerializer.Serialize(new { Version = 1, Reason = request.Reason.Trim(), ExpectedFingerprint = preview.ExpectedFingerprint,
            Before = state.Before, After = state.After, state.OldTaxJson, state.OldLinesJson, state.OldTracesJson,
            CohortHash = RecalculationHash(state.Cohort), OtherEmployeesUnchanged = true });
        await db.ExecuteAsync("DELETE FROM tax_computation_snapshots WHERE pay_run_id=@RunId AND employee_id=@EmployeeId", new { RunId = runId, EmployeeId = employeeId }, tx);
        var saved = await CalculateEmployeeRecalculationAsync(db, tx, state, persistTax: true);
        if (saved.DetailsJson != state.After!.DetailsJson || saved.NetPay != state.After.NetPay || saved.PresentDays != state.After.PresentDays)
            throw new InvalidOperationException("The confirmed native calculation changed while saving; no replacement was committed.");
        await SaveEmployeeAsync(db, tx, saved);
        if (saved.Id != originalId) throw new InvalidOperationException("Recalculation must update the original payroll employee row; no duplicate is accepted.");
        await db.ExecuteAsync("DELETE FROM payroll_calculation_traces WHERE PayRunId=@RunId AND EmployeeId=@EmployeeId", new { RunId = runId, EmployeeId = employeeId }, tx);
        await WriteCalculationTracesAsync(db, tx, saved, AttendanceWorkingDays(state.Attendance, run.TotalWorkingDays));
        await RefreshTotalsAsync(db, tx, runId); await WriteReconciliationResultsAsync(db, tx, runId); await EnsureReconciledAsync(db, tx, runId);
        await db.ExecuteAsync(@"INSERT INTO payrun_step_logs(PayRunId,EmployeeId,StepNumber,StepName,StartTime,EndTime,InputJson,OldValueJson,NewValueJson,OutputJson,Status,PerformedBy,MachineName)
VALUES(@RunId,@EmployeeId,61,'Reviewed single employee recalculation',UTC_TIMESTAMP(),UTC_TIMESTAMP(),@Input,@Old,@New,@Output,'Success',@Actor,@Machine)",
            new { RunId = runId, EmployeeId = employeeId, Input = JsonSerializer.Serialize(new { request.Reason, request.ExpectedFingerprint }),
                Old = archive, New = JsonSerializer.Serialize(saved), Output = JsonSerializer.Serialize(new { EmployeeCount = 1, OriginalEmployeeRowId = originalId, OtherEmployeesUnchanged = true }),
                Actor = actor, Machine = Environment.MachineName }, tx);
        await tx.CommitAsync(); return await GetAsync(runId);
    }

    private async Task<EmployeeRecalculationState> ReadEmployeeRecalculationAsync(MySqlConnection db, MySqlTransaction tx, PayRun run, int employeeId)
    {
        var cohort = (await db.QueryAsync<PayRunEmployee>("SELECT * FROM payrunemployees WHERE PayRunId=@Id ORDER BY EmployeeId", new { Id = run.Id }, tx)).ToList();
        var state = new EmployeeRecalculationState { Run = run, Cohort = cohort, Before = cohort.SingleOrDefault(row => row.EmployeeId == employeeId) };
        state.Blockers.AddRange(EmployeeRecalculationBlockers(run, state.Before));
        if (cohort.Any(row => row.PaymentStatus is not ("" or "Pending") || row.PaymentDate is not null))
            state.Blockers.Add("A payment exists or is in progress in this payroll. The saved run cannot be corrected through this action.");
        if (state.Blockers.Count > 0) return state;
        // Recall may return a Pending Approval run to Draft while its configured
        // workflow still has an open task. Never change amounts under that task's
        // old reviewed payload or reuse it as approval of the corrected result.
        if (await TableExistsAsync(db, tx, "workflowinstances") && await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM workflowinstances WHERE ResourceType='PayRun' AND ResourceId=@ResourceId AND Status='Pending'",
            new { ResourceId = run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, tx) > 0)
        { state.Blockers.Add("This payroll still has an open approval workflow. Resolve or withdraw that approval before correcting it; this action did not change its reviewed amounts."); return state; }
        if (await TableExistsAsync(db, tx, "modulesettings") && await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@Code", new { run.ClientId, Code = CurrencyPointerCode(run.Id) }, tx) > 0)
        { state.Blockers.Add("This run has a protected currency/presentation capture requiring a separate reviewed correction. This action did not change its captured evidence."); return state; }
        try
        {
            using (var stored = JsonDocument.Parse(state.Before!.DetailsJson))
            {
                if (stored.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("The saved employee calculation details are invalid; no calculation was changed.");
                if (stored.RootElement.EnumerateArray().Any(line => line.TryGetProperty("locationParts", out _)))
                    throw new InvalidOperationException("This employee has protected location-part calculations requiring a separate reviewed correction. This action did not change their allocation evidence.");
            }
            var request = JsonSerializer.Deserialize<CreatePayRunRequest>(run.RequestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("The saved payroll request is invalid; no employee policy scope can be inferred.");
            var groupIds = (request.AttendanceGroupIds is { Count: > 0 } ? request.AttendanceGroupIds : request.AttendanceGroupId > 0 ? [request.AttendanceGroupId] : new List<int>()).Distinct().ToArray();
            // Old synchronous CreateAsync stored the real policy batch on the run
            // but not its request JSON. That persisted batch is sufficient scope;
            // never manufacture a request or guess policy from today's first row.
            using var rawRequest = JsonDocument.Parse(run.RequestJson);
            var emptyLegacyRequest = rawRequest.RootElement.ValueKind == JsonValueKind.Object && !rawRequest.RootElement.EnumerateObject().Any();
            if (!emptyLegacyRequest && (request.ClientId != run.ClientId || request.PayPeriod != run.PayPeriod)
                || groupIds.Length == 0 && string.IsNullOrWhiteSpace(run.AttendancePolicyBatchId))
                throw new InvalidOperationException("The saved payroll requires an exact client, month and explicit attendance-policy scope before this correction.");
            var scopes = (await db.QueryAsync("SELECT g.id,g.policy_batch_id,g.attendance_cycle_start_day,g.attendance_cycle_end_day,g.attendance_mode FROM attendance_groups g JOIN attendance_group_employees m ON m.attendance_group_id=g.id WHERE m.employee_id=@EmployeeId AND g.client_id=@ClientId AND g.is_active=TRUE ORDER BY g.id", new { EmployeeId = employeeId, run.ClientId }, tx)).ToList();
            if (scopes.Count == 0 || groupIds.Length > 0 && !groupIds.Contains((int)scopes[0].id)
                || !string.IsNullOrWhiteSpace(run.AttendancePolicyBatchId) && (string?)scopes[0].policy_batch_id != run.AttendancePolicyBatchId)
                throw new InvalidOperationException("This employee's active attendance-policy scope differs from the saved payroll. Review the policy assignment before recalculating.");
            state.PolicyScopeJson = JsonSerializer.Serialize(scopes);
            var employee = await db.QuerySingleOrDefaultAsync<PayRunSourceEmployee>("SELECT e.*,c.Name ClientName,COALESCE(w.State,'') WorkState FROM employees e JOIN clients c ON c.Id=e.ClientId LEFT JOIN worklocations w ON w.Id=e.WorkLocationId WHERE e.Id=@EmployeeId AND e.ClientId=@ClientId AND "
                + "c.IsActive=TRUE AND " + EmployeeEmploymentRules.EligibleInMonth("e", "@PayPeriod"), new { EmployeeId = employeeId, run.ClientId, run.PayPeriod }, tx)
                ?? throw new InvalidOperationException("The payroll employee is unavailable or no longer eligible in this client and period.");
            state.Employee = employee;
            await LoadEmployeeTablesAsync(db, tx, [employee], run.PayPeriod);
            if (employee.LocationParts is not null)
                throw new InvalidOperationException("This employee has protected location allocations requiring a separate reviewed correction. This action did not change them.");
            state.SetupJson = await PayrollDataTableStore.GetSetupJsonAsync(db, tx);
            state.Attendance = (await GetPayRunAttendanceAsync(db, tx, run.ClientId, run.PayPeriod, employeeId)).SingleOrDefault()
                ?? throw new InvalidOperationException("Saved monthly or dated attendance is required. No present or payable days are inferred.");
            employee.AttendancePolicySnapshot = state.Attendance.PolicySnapshot; employee.PayableOnlyAttendance = state.Attendance.PayableOnlyEvidence;
            await LoadPfPolicyPeriodsAsync(db, tx, [employee], run.PayPeriod, state.SetupJson);
            var attendance = new Dictionary<int, PayRunAttendance> { [employeeId] = state.Attendance };
            var issues = ValidatePayRunInputs(run.Id, new() { ClientId = run.ClientId, PayPeriod = run.PayPeriod, TotalWorkingDays = run.TotalWorkingDays, IncludedEmployeeIds = [employeeId] }, "Regular", [employee], attendance, state.SetupJson);
            state.Blockers.AddRange(issues.Where(issue => issue.IsBlocking).Select(issue => issue.Message));
            var days = AttendanceWorkingDays(state.Attendance, run.TotalWorkingDays);
            if (days != run.TotalWorkingDays) throw new InvalidOperationException("Recorded salary divisor differs from this saved payroll. Review the payroll period and policy rather than replacing its basis.");
            var error = await EmployeeAttendanceEligibility.ValidateMonthlyWithOptionalPresenceAsync(db, tx, run.ClientId, run.PayPeriod,
                [(employeeId, state.Attendance.PresentDays, NativePayableUnits(state.Attendance, days))], lockEmployees: false);
            if (error is not null) state.Blockers.Add(error);
            if (state.Blockers.Count > 0) return state;
            state.Adjustments = (await db.QueryAsync<PayrollAdjustment>("SELECT * FROM payrolladjustments WHERE PayRunId=@Id AND EmployeeId=@EmployeeId AND ClientId=@ClientId AND Status='Applied' ORDER BY Id", new { Id = run.Id, EmployeeId = employeeId, run.ClientId }, tx)).ToList();
            state.ManualEarnings = state.Before!.OneTimeEarnings - state.Adjustments.Where(IsCashAdjustmentEarning).Sum(row => row.Amount);
            state.ManualDeductions = state.Before.OneTimeDeductions - state.Adjustments.Where(IsDeduction).Sum(row => row.Amount);
            if (state.ManualEarnings < 0 || state.ManualDeductions < 0) throw new InvalidOperationException("Saved one-time totals do not cover their linked applied adjustments. Review that adjustment evidence first.");
            var taxes = (await db.QueryAsync("SELECT * FROM tax_computation_snapshots WHERE pay_run_id=@RunId AND employee_id=@EmployeeId ORDER BY id", new { RunId = run.Id, EmployeeId = employeeId }, tx)).ToList();
            state.OldTaxJson = JsonSerializer.Serialize(taxes);
            // ManualTds is an aggregate legacy field, not an override provenance
            // flag. Without its tax snapshot a positive amount could be configured
            // or manually overridden; neither freezing nor replacing it is safe.
            if (taxes.Count == 0 && state.Before.ManualTds > 0)
                throw new InvalidOperationException("This employee has positive saved TDS without a calculation snapshot. Review its tax/override provenance first; recalculation cannot guess whether to preserve or replace it.");
            state.OldLinesJson = JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM payrunemployeelines WHERE PayRunId=@RunId AND EmployeeId=@EmployeeId ORDER BY Id", new { RunId = run.Id, EmployeeId = employeeId }, tx));
            state.OldTracesJson = JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM payroll_calculation_traces WHERE PayRunId=@RunId AND EmployeeId=@EmployeeId ORDER BY Id", new { RunId = run.Id, EmployeeId = employeeId }, tx));
            state.After = await CalculateEmployeeRecalculationAsync(db, tx, state, persistTax: false);
            if (EmployeeRecalculationPolicyBinding(state.Before.DetailsJson) != EmployeeRecalculationPolicyBinding(state.After.DetailsJson))
                throw new InvalidOperationException("The frozen dated policy or reviewed source-period attendance identity changed. Review that dated/source correction first; this action cannot replace its proof.");
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException or FormatException or OverflowException)
        { state.After = null; state.Blockers.Add(error.Message); }
        return state;
    }

    private async Task<PayRunEmployee> CalculateEmployeeRecalculationAsync(MySqlConnection db, MySqlTransaction tx, EmployeeRecalculationState state, bool persistTax)
    {
        var employee = state.Employee!; var attendance = state.Attendance!; var run = state.Run;
        var days = AttendanceWorkingDays(attendance, run.TotalWorkingDays);
        var present = ClampKnownPresence(attendance.PresentDays, days); var payable = NativePayableUnits(attendance, days);
        var adjustments = state.Adjustments.Where(adjustment => adjustment.ReasonCode != EmployerCostReason).ToList();
        var row = BuildEmployee(run.Id, employee, state.SetupJson, run.PayPeriod, days, present, payable, adjustments,
            state.ManualEarnings, state.ManualDeductions, -1, false);
        var taxInputs = new List<object>();
        var tax = await CalculateMonthlyTdsAsync(db, tx, employee, row, run.PayPeriod, persistSnapshot: persistTax, recordInputs: taxInputs.Add, setupJson: state.SetupJson);
        if (!persistTax) state.TaxInputs = taxInputs;
        if (tax.Calculated) row = BuildEmployee(run.Id, employee, state.SetupJson, run.PayPeriod, days, present, payable, adjustments,
            state.ManualEarnings, state.ManualDeductions, tax.Amount, false, tax.ComponentCode);
        row.DetailsJson = AppendRecalculationEmployerCosts(row.DetailsJson, state.Adjustments);
        return WithSourceTdsEvidence(row, tax);
    }

    internal static string AppendRecalculationEmployerCosts(string details, IEnumerable<PayrollAdjustment> adjustments)
    {
        var costs = adjustments.Where(adjustment => adjustment.ReasonCode == EmployerCostReason).ToArray();
        if (costs.Length == 0) return details;
        if (costs.Any(cost => NormalizeAdjustmentType(cost.AdjustmentType) != "Benefit" || cost.Taxable || cost.Amount <= 0))
            throw new InvalidOperationException("The saved reviewed period employer costs have invalid classification. Review their receipts before recalculating.");
        var lines = JsonNode.Parse(details) as JsonArray ?? throw new InvalidOperationException("Native calculation details are invalid.");
        // Do not pass the whole row through EmployerCostDetails: its replace
        // behavior would also remove independently applied Benefit adjustments.
        var additions = JsonNode.Parse(EmployerCostDetails("[]", costs))!.AsArray();
        foreach (var addition in additions) lines.Add(addition!.DeepClone());
        return lines.ToJsonString();
    }
}

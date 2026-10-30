using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static async Task<string> CurrentSourceHashAsync(MySqlConnection db, MySqlTransaction tx, int clientId, string batchId)
    {
        var (batch, source) = await ReadSourceAsync(db, tx, clientId, batchId);
        return Hash(JsonSerializer.Serialize(new { Batch = batch, Source = source }, Json));
    }
    internal static string? PayableOverrideRequestError(ExcelPayrollSetupRequest request)
    {
        if (!request.OverrideSourcePayableAttendance) return null;
        if (request.TransferReviewVersion != 1 || request.ImportAttendance || request.ImportPayableOnlyAttendance
            || request.LocationAllocations is not null || request.PresentDaysColumnIndex is not null
            || request.AttendanceReview is not { Mode: "SourceSeed", Reviewed: true, PresentMeaning: "NotMapped", PayableMeaning: "AlreadyPayable" })
            return "Excel payable override requires reviewed source-seed salary transfer, no actual-present mapping, and retained native attendance memberships. It replaces only this source month's salary payable units.";
        try { SourcePayableAttendance.EnsureIndependent(request.Components.SelectMany(c => new[] { c.Formula, c.BaseComponent }.Concat(c.RowFormulas.Values))); }
        catch (InvalidOperationException error) { return error.Message; }
        return null;
    }

    private async Task ValidatePayableOverrideAsync(MySqlConnection db, MySqlTransaction tx, int clientId, ExcelPayrollSetupRequest request, Plan plan)
    {
        if (!request.OverrideSourcePayableAttendance) return;
        void Error(string message, string? rowId = null) => plan.Preview.Issues.Add(new("Error", message, rowId, "Source payable override"));
        if (PayableOverrideRequestError(request) is string error) { Error(error); return; }
        if (plan.Rows.Any(r => r.KeepExisting || r.Employee.Id <= 0)) { Error("Source payable override requires existing, uniquely reviewed employees with an existing monthly snapshot."); return; }
        var ids = plan.Rows.Select(r => r.Employee.Id).Distinct().ToArray();
        var rows = (await db.QueryAsync<PayableOverridePrior>(@"SELECT employee_id EmployeeId, working_days WorkingDays, present_days PresentDays,
payable_days PayableDays, lop_days LopDays, source_type SourceType, COALESCE(remarks,'') Remarks
FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month AND employee_id IN @Ids",
            new { ClientId = clientId, Month = plan.Batch.Month, Ids = ids }, tx)).ToDictionary(r => r.EmployeeId);
        if (rows.Count != ids.Length || rows.Values.Any(r => !r.PresentDays.HasValue)) Error("Retain and review an existing monthly snapshot with recorded presence before overriding salary payable units. No physical presence is created.");
        var overlaps = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM payrunemployees pe JOIN payruns pr ON pr.Id=pe.PayRunId
WHERE pr.ClientId=@ClientId AND pr.PayPeriod=@Month AND pe.EmployeeId IN @Ids", new { ClientId = clientId, Month = plan.Batch.Month, Ids = ids }, tx);
        if (overlaps > 0) Error("A selected employee already has payroll for this month. Existing runs are retained; review that run before changing its attendance.");
        var employment = await EmployeeAttendanceEligibility.ValidateMonthlyWithOptionalPresenceAsync(db, tx, clientId, plan.Batch.Month,
            plan.Rows.Select(r => (r.Employee.Id, rows.GetValueOrDefault(r.Employee.Id)?.PresentDays, r.Preview.PayableDays)), lockEmployees: false);
        if (employment is not null) Error(employment);
        var pf = await PfPolicyRepository.ReadVersionsAsync(db, tx, clientId);
        var cycle = PayRunRepository.NativeAttendanceCycle(plan.Batch.Month, request.AttendanceCycleStartDay, request.AttendanceCycleEndDay);
        foreach (var row in plan.Rows)
        {
            if (PfPolicyCalculator.ApplicableVersions(pf, row.TemplateId, DateOnly.Parse(cycle.Start), DateOnly.Parse(cycle.End)).Count > 1)
                Error("Split dated PF policy requires a separate dated calculation; this monthly payable override cannot infer a daily allocation.", row.RowId);
            if (rows.TryGetValue(row.Employee.Id, out var previous) && (previous.PresentDays > row.Preview.PayrollDays || previous.WorkingDays != row.Preview.PayrollDays))
                Error("Saved native divisor/presence must fit the reviewed source divisor before a payable-only override.", row.RowId);
        }
    }

    private static async Task WritePayableOverrideAsync(MySqlConnection db, MySqlTransaction tx, int clientId,
        ExcelPayrollSetupRequest request, Plan plan, ExcelPayrollSetupResult result)
    {
        var sourceHash = Hash(JsonSerializer.Serialize(new { plan.Batch, plan.Source }, Json));
        var cycle = PayRunRepository.NativeAttendanceCycle(plan.Batch.Month, request.AttendanceCycleStartDay, request.AttendanceCycleEndDay);
        foreach (var row in plan.Rows)
        {
            var previous = await db.QuerySingleAsync<PayableOverridePrior>(@"SELECT employee_id EmployeeId, working_days WorkingDays, present_days PresentDays,
payable_days PayableDays, lop_days LopDays, source_type SourceType, COALESCE(remarks,'') Remarks FROM employee_monthly_attendance
WHERE client_id=@ClientId AND attendance_month=@Month AND employee_id=@Id FOR UPDATE", new { ClientId = clientId, Month = plan.Batch.Month, row.Employee.Id }, tx);
            if (!previous.PresentDays.HasValue) throw new InvalidOperationException("Native presence changed after source payable review.");
            var proof = ProveAttendanceInput(plan, row, request, clientId, sourceHash);
            if (proof.Status != "Reviewed source payable override") throw new InvalidOperationException(proof.Reason);
            var remarks = SourcePayableOverride.Encode(new(1, clientId, row.Employee.Id, plan.Batch.Month, plan.Batch.Id, row.RowId,
                result.ImportId, sourceHash, row.Preview.PayrollDays, row.Preview.PayableDays, previous.PresentDays.Value, cycle.Start, cycle.End));
            var count = await db.ExecuteAsync(@"UPDATE employee_monthly_attendance SET payable_days=@Payable, lop_days=@Lop, source_type=@SourceType, remarks=@Remarks
WHERE client_id=@ClientId AND attendance_month=@Month AND employee_id=@Id", new { ClientId = clientId, Month = plan.Batch.Month, row.Employee.Id,
                Payable = row.Preview.PayableDays, Lop = row.Preview.PayrollDays - row.Preview.PayableDays, SourcePayableOverride.SourceType, Remarks = remarks }, tx);
            if (count != 1) throw new InvalidOperationException("The selected monthly attendance row could not be confirmed.");
            result.PayableOverrides.Add(new(row.Employee.Id, row.RowId, plan.Batch.Month, previous.WorkingDays, previous.PresentDays,
                previous.PayableDays, previous.LopDays, previous.SourceType, previous.Remarks, row.Preview.PayrollDays, row.Preview.PayableDays));
        }
    }
    private sealed class PayableOverridePrior
    {
        public int EmployeeId { get; set; }
        public decimal WorkingDays { get; set; }
        public decimal? PresentDays { get; set; }
        public decimal PayableDays { get; set; }
        public decimal LopDays { get; set; }
        public string SourceType { get; set; } = "";
        public string Remarks { get; set; } = "";
    }
}

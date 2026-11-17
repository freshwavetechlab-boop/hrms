using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    internal sealed record LocationCorrectionNative(PayRunEmployee Row, string EvidenceHash);

    // Caller owns locks, source proof and transaction. Preview never persists tax
    // snapshots. The supplied parts are server-derived, not an API amount override.
    internal async Task<LocationCorrectionNative> CalculateLocationCorrectionAsync(MySqlConnection db,
        MySqlTransaction tx, PayRun run, PayrollLocationPartPeriod parts, string setupJson, bool requirePersistedParts = false, bool persistTax = false)
    {
        if (tx.Connection != db || run.Status != "Draft" || run.RunType != "Regular"
            || parts.ClientId != run.ClientId || parts.PayPeriod != run.PayPeriod || parts.Parts.Count == 0)
            throw new InvalidOperationException("Review one existing Regular Draft and its complete source-period location parts.");
        var employee = await db.QuerySingleOrDefaultAsync<PayRunSourceEmployee>(
            "SELECT e.*,c.Name ClientName,COALESCE(w.State,'') WorkState FROM employees e JOIN clients c ON c.Id=e.ClientId LEFT JOIN worklocations w ON w.Id=e.WorkLocationId WHERE e.Id=@EmployeeId AND e.ClientId=@ClientId AND "
            + EmployeeEmploymentRules.EligibleInMonth("e", "@PayPeriod"),
            new { parts.EmployeeId, parts.ClientId, parts.PayPeriod }, tx)
            ?? throw new InvalidOperationException("The selected source employee is unavailable for this client and period.");
        await LoadEmployeeTablesAsync(db, tx, [employee], run.PayPeriod);
        if (requirePersistedParts && !SameLocationCorrectionPeriod(employee.LocationParts,parts))
            throw new InvalidOperationException("The saved location allocation differs from the complete reviewed period; no Draft projection was replaced.");
        var attendance = (await GetPayRunAttendanceAsync(db, tx, run.ClientId, run.PayPeriod, employee.Id)).SingleOrDefault()
            ?? throw new InvalidOperationException("Known native monthly attendance is required; missing presence is not inferred.");
        if (attendance.PresentDays is null || attendance.PolicySnapshot is not null || attendance.PayableOnlyEvidence is not null
            || !string.IsNullOrWhiteSpace(attendance.PolicyError) || attendance.InputSource != "Monthly snapshot")
            throw new InvalidOperationException("This correction requires known monthly source attendance without dated, frozen or payable-only policy inputs.");
        var present = parts.Parts.Sum(part => part.PresentDays); var payable = parts.Parts.Sum(part => part.PayableDays);
        if (parts.Parts.Any(part => part.PayrollDays != run.TotalWorkingDays) || attendance.WorkingDays != run.TotalWorkingDays)
            throw new InvalidOperationException("Every location part and native monthly input must use the existing Draft divisor.");
        var employmentError = await EmployeeAttendanceEligibility.ValidateMonthlyAsync(db, tx, run.ClientId, run.PayPeriod,
            [(employee.Id, present, payable)], lockEmployees: false);
        if (employmentError is not null) throw new InvalidOperationException(employmentError);
        employee.LocationParts = parts;
        await LoadPfPolicyPeriodsAsync(db, tx, [employee], run.PayPeriod, setupJson);
        if (employee.PfPolicyPeriod is not null)
            throw new InvalidOperationException("Published PF period snapshots require their dated review; this location correction cannot bypass them.");
        var row = BuildEmployee(run.Id, employee, setupJson, run.PayPeriod, run.TotalWorkingDays, present, payable, [], 0, 0, -1, false);
        var taxInputs = new List<object>();
        var tax = await CalculateMonthlyTdsAsync(db, tx, employee, row, run.PayPeriod, persistSnapshot: persistTax, recordInputs: taxInputs.Add, setupJson: setupJson);
        if (tax.Calculated) row = BuildEmployee(run.Id, employee, setupJson, run.PayPeriod, run.TotalWorkingDays, present, payable, [], 0, 0, tax.Amount, false, tax.ComponentCode);
        row = WithSourceTdsEvidence(row, tax);
        var hash = ExcelPayrollSetupRepository.Hash(JsonSerializer.Serialize(new { employee, attendance, setupJson, taxInputs, row.DetailsJson }));
        return new(row, hash);
    }

    internal static bool CanCorrectLocationDraftRow(PayRunEmployee row) => !row.IsSkipped && row.PresentDays.HasValue
        && row.OneTimeEarnings == 0 && row.OneTimeDeductions == 0 && row.ManualTds == 0
        && row.PaymentStatus is "" or "Pending" && row.PaymentDate is null
        && !HasUnknownPresenceSnapshot(row.DetailsJson) && !HasPfPolicySnapshot(row.DetailsJson)
        && !HasAttendancePolicySnapshot(row.DetailsJson);

    internal static bool SameLocationCorrectionPeriod(PayrollLocationPartPeriod? left,PayrollLocationPartPeriod right)
    {
        if(left is null||left.Version!=right.Version||left.ClientId!=right.ClientId||left.EmployeeId!=right.EmployeeId||left.PayPeriod!=right.PayPeriod
            ||left.ReferenceAssignmentId!=right.ReferenceAssignmentId||left.Parts.Count!=right.Parts.Count)return false;
        return left.Parts.OrderBy(part=>part.PartKey,StringComparer.Ordinal).Zip(right.Parts.OrderBy(part=>part.PartKey,StringComparer.Ordinal)).All(pair=>
            pair.First.PartKey==pair.Second.PartKey&&pair.First.AssignmentId==pair.Second.AssignmentId&&pair.First.WorkLocationId==pair.Second.WorkLocationId
            &&pair.First.WorkState==pair.Second.WorkState&&pair.First.SalaryStructureId==pair.Second.SalaryStructureId&&pair.First.AnnualCtc==pair.Second.AnnualCtc
            &&pair.First.PayrollDays==pair.Second.PayrollDays&&pair.First.PresentDays==pair.Second.PresentDays&&pair.First.PayableDays==pair.Second.PayableDays
            &&pair.First.SalaryInputs.OrderBy(p=>p.Key,StringComparer.Ordinal).SequenceEqual(pair.Second.SalaryInputs.OrderBy(p=>p.Key,StringComparer.Ordinal))
            &&pair.First.ComponentScopes.OrderBy(p=>p.Key,StringComparer.Ordinal).SequenceEqual(pair.Second.ComponentScopes.OrderBy(p=>p.Key,StringComparer.Ordinal))
            &&ReviewedNamedFormulaInputs.Signature("",pair.First.NamedInputs)==ReviewedNamedFormulaInputs.Signature("",pair.Second.NamedInputs)
            &&pair.First.NamedInputs?.ManifestHash==pair.Second.NamedInputs?.ManifestHash);
    }

    // The archived receipt is written by the caller before these replace current
    // draft projections. No approval, workflow, payment, notification or send is invoked.
    internal async Task SaveLocationCorrectionRowsAsync(MySqlConnection db, MySqlTransaction tx,
        PayRun run, IReadOnlyList<PayrollLocationPartPeriod> periods, string setupJson, IReadOnlyList<ExcelPayrollSetupPreviewRow> sourceRows)
    {
        foreach (var period in periods)
        {
            var key = new { PayRunId = run.Id, run.ClientId, period.EmployeeId };
            var savedId = await db.ExecuteScalarAsync<int>("SELECT Id FROM payrunemployees WHERE PayRunId=@PayRunId AND ClientId=@ClientId AND EmployeeId=@EmployeeId FOR UPDATE", key, tx);
            await db.ExecuteAsync("DELETE FROM tax_computation_snapshots WHERE pay_run_id=@PayRunId AND employee_id=@EmployeeId; DELETE FROM payroll_calculation_traces WHERE PayRunId=@PayRunId AND EmployeeId=@EmployeeId", key, tx);
            var calculated = await CalculateLocationCorrectionAsync(db, tx, run, period, setupJson, requirePersistedParts: true, persistTax: true);
            var row = calculated.Row;
            ExcelPayrollSetupRepository.RequireLocationCorrectionParity(row, sourceRows.Where(source => source.EmployeeId == period.EmployeeId).ToArray());
            await SaveEmployeeAsync(db, tx, row);
            if (row.Id != savedId) throw new InvalidOperationException("The existing Draft employee identity changed; no replacement row is accepted.");
            await WriteCalculationTracesAsync(db, tx, row, run.TotalWorkingDays);
        }
        await RefreshTotalsAsync(db, tx, run.Id);
        await WriteReconciliationResultsAsync(db, tx, run.Id);
    }
}

using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    internal sealed record EmptySalaryCleanupPlan(EmptySalarySetupCleanupPreview Preview,
        ClosedSalaryHistoryRow? Basis, IReadOnlyList<ClosedSalaryHistoryRow> Empty, DateTime From, DateTime To);

    internal static EmptySalaryCleanupPlan PlanEmptySalaryCleanup(Employee employee,
        IReadOnlyList<ClosedSalaryHistoryRow> history, string month, DateTime today, bool savedPayroll)
    {
        if (employee.Id <= 0 || employee.ClientId <= 0 ||
            !DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || from >= new DateTime(today.Year, today.Month, 1))
            throw new InvalidOperationException("Choose one existing employee and a completed payroll month.");
        var to = from.AddMonths(1).AddDays(-1);
        var blockers = new List<string>();
        if (history.Any(row => row.EmployeeId != employee.Id || row.ClientId != employee.ClientId))
            throw new InvalidOperationException("Salary history does not belong to the selected employee/client.");
        var candidates = history.Where(row => row.Status == "Active" && row.EffectiveFrom.Date == from &&
                (row.EffectiveTo is null || row.EffectiveTo.Value.Date >= to) &&
                !IsUnconfiguredExcelSalaryHistory(ToMarker(row)))
            .OrderByDescending(row => row.Id).ToArray();
        var basis = candidates.FirstOrDefault();
        var empty = history.Where(row => row.Status == "Active" && row.EffectiveFrom.Date > from &&
                row.EffectiveFrom.Date <= to && IsUnconfiguredExcelSalaryHistory(ToMarker(row)))
            .OrderBy(row => row.EffectiveFrom).ThenBy(row => row.Id).ToArray();
        if (basis is null) blockers.Add("A configured salary effective on the first day of this month is required.");
        else if (candidates.Length != 1) blockers.Add("Multiple configured month-start salary entries need review before cleanup.");
        else if (basis.AnnualCtc <= 0 && !ReadSalaryInputs(basis.SalaryJson).Values.Any(value => value > 0))
            blockers.Add("The saved month-start salary amount is not configured. Complete that assignment before deleting empty markers.");
        else if (basis.SalaryStructureId != employee.SalaryStructureId || basis.AnnualCtc != employee.AnnualCtc ||
                 SalaryInputHash(basis.SalaryJson) != SalaryInputHash(employee.SalaryJson))
            blockers.Add("Current saved salary differs from the month-start assignment. Review the dated salary change first.");
        if (empty.Length > 20) blockers.Add("Review this unusually large number of empty setup entries separately.");
        if (history.Any(row => row.Status == "Active" && row.EffectiveFrom.Date < from))
            blockers.Add("An active salary starts before this month. Earlier history is preserved; use a dated salary review.");
        if (empty.Length > 0 && history.Any(row => row.EffectiveFrom.Date > from && row.EffectiveFrom.Date <= to &&
                !IsUnconfiguredExcelSalaryHistory(ToMarker(row))))
            blockers.Add("A configured salary change already exists inside this month. Cleanup cannot replace it.");
        if (savedPayroll) blockers.Add("This employee already has a saved payroll result in the month. Existing payroll is preserved.");
        var hash = ClosedSalaryHash(new { employee.Id, employee.ClientId, Month = month, employee.SalaryStructureId,
            employee.AnnualCtc, Salary = SalaryInputHash(employee.SalaryJson), History = history.OrderBy(row => row.Id) });
        return new(new(employee.Id, employee.ClientId, employee.EmployeeCode, month, hash,
            basis?.SalaryStructureId ?? "", basis?.AnnualCtc ?? 0,
            empty.Select(row => row.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Distinct().ToArray(), blockers),
            basis, empty, from, to);
    }

    private static ExcelPayrollSalaryHistory ToMarker(ClosedSalaryHistoryRow row) => new()
    {
        Id = row.Id, EffectiveFrom = row.EffectiveFrom, Status = row.Status,
        SalaryStructureId = row.SalaryStructureId, AnnualCtc = row.AnnualCtc, SalaryJson = row.SalaryJson
    };
    private static string SalaryInputHash(string value)
        => ClosedSalaryHash(ReadSalaryInputs(value).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new { pair.Key, Value = pair.Value.ToString("G29", CultureInfo.InvariantCulture) }));
    private static Dictionary<string, decimal> ReadSalaryInputs(string value)
    {
        Dictionary<string, decimal>? inputs;
        try { inputs = JsonSerializer.Deserialize<Dictionary<string, decimal>>(value); }
        catch (JsonException) { throw new InvalidOperationException("The saved salary inputs need review before setup cleanup."); }
        if (inputs is null || inputs.Count > 100 || inputs.Any(pair => pair.Value < 0))
            throw new InvalidOperationException("The saved salary inputs need review before setup cleanup.");
        return inputs;
    }
    private async Task<EmptySalaryCleanupPlan> ReadEmptySalaryCleanupPlanAsync(MySqlConnection db, MySqlTransaction tx,
        int employeeId, int clientId, string month, bool locked)
    {
        var employee = await LoadRecruitmentEmployeeAsync(db, employeeId, tx)
            ?? throw new InvalidOperationException("Employee not found.");
        if (employee.ClientId != clientId) throw new InvalidOperationException("Employee is outside the selected client.");
        var state = await ReadClosedSalaryHistoryStateAsync(db, tx, clientId, [employeeId], locked);
        var runs = (await db.QueryAsync<int>("SELECT Id FROM payruns WHERE ClientId=@ClientId AND PayPeriod=@Month" +
            (locked ? " FOR UPDATE" : ""), new { ClientId = clientId, Month = month }, tx)).ToArray();
        var saved = runs.Length > 0 && await db.ExecuteScalarAsync<bool>(@"SELECT EXISTS(SELECT 1 FROM payrunemployees
WHERE EmployeeId=@EmployeeId AND PayRunId IN @Runs)", new { EmployeeId = employeeId, Runs = runs }, tx);
        return PlanEmptySalaryCleanup(employee, state.History, month, EmployeeEmploymentRules.Today, saved);
    }
    public async Task<EmptySalarySetupCleanupPreview> PreviewEmptySalaryCleanupAsync(int employeeId, int clientId, string month)
    {
        await using var db = Connection(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true);
        var plan = await ReadEmptySalaryCleanupPlanAsync(db, tx, employeeId, clientId, month, false);
        await tx.RollbackAsync(); return plan.Preview;
    }
    public async Task<EmptySalarySetupCleanupResult> ApplyEmptySalaryCleanupAsync(int employeeId,
        EmptySalarySetupCleanupRequest request, string actor)
    {
        if (!request.Acknowledged || !Guid.TryParse(request.RequestId, out var parsed) ||
            request.ExpectedPlanHash?.Length != 64 || (request.Reason?.Trim().Length ?? 0) is < 12 or > 500)
            throw new InvalidOperationException("Review and acknowledge this exact empty-setup cleanup with a reason.");
        var requestId = parsed.ToString("N");
        await using var db = Connection(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@Id FOR UPDATE", new { Id = request.ClientId }, tx) is null)
            throw new InvalidOperationException("Client not found.");
        var prior = await db.ExecuteScalarAsync<string?>(@"SELECT NewValue FROM employee_audit_trail
WHERE EmployeeId=@EmployeeId AND FieldName='Empty salary setup cleanup' AND ChangedBy=@Actor
AND JSON_UNQUOTE(JSON_EXTRACT(NewValue,'$.requestId'))=@RequestId LIMIT 1 FOR UPDATE",
            new { EmployeeId = employeeId, Actor = actor, RequestId = requestId }, tx);
        if (prior is not null)
        {
            var receipt = JsonSerializer.Deserialize<EmptySalarySetupCleanupResult>(prior, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            if (receipt.ClientId != request.ClientId || receipt.PayPeriod != request.PayPeriod || receipt.PlanHash != request.ExpectedPlanHash)
                throw new InvalidOperationException("This cleanup request reference belongs to another scope.");
            await tx.RollbackAsync(); return receipt with { Replayed = true };
        }
        var plan = await ReadEmptySalaryCleanupPlanAsync(db, tx, employeeId, request.ClientId, request.PayPeriod, true);
        if (!plan.Preview.CanApply || plan.Preview.PlanHash != request.ExpectedPlanHash)
            throw new InvalidOperationException("Salary history or payroll changed. Refresh the cleanup preview; no records were changed.");
        await WriteEmptySalaryCleanupAsync(db, tx, plan);
        var result = new EmptySalarySetupCleanupResult(employeeId, request.ClientId, request.PayPeriod, requestId,
            plan.Empty.Count, false) { PlanHash = request.ExpectedPlanHash, Reason = request.Reason!.Trim() };
        await db.ExecuteAsync(@"INSERT INTO employee_audit_trail
(EmployeeId,EmployeeCode,ActionType,InfotypeCode,FieldName,OldValue,NewValue,EffectiveFrom,ChangedBy)
VALUES(@EmployeeId,@EmployeeCode,'Master Update','0008','Empty salary setup cleanup',@OldValue,@NewValue,@From,@Actor)",
            new { EmployeeId = employeeId, plan.Preview.EmployeeCode, OldValue = plan.Preview.PlanHash,
                NewValue = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)), plan.From, Actor = actor }, tx);
        await tx.CommitAsync(); return result;
    }
    internal static async Task WriteEmptySalaryCleanupAsync(IDbConnection db, IDbTransaction tx, EmptySalaryCleanupPlan plan)
    {
        if (!plan.Preview.CanApply || plan.Basis is null || tx is null || !ReferenceEquals(tx.Connection, db) || db.State != ConnectionState.Open)
            throw new InvalidOperationException("Cleanup requires its locked, reviewed plan and open transaction.");
        var employeeId = plan.Preview.EmployeeId; var clientId = plan.Preview.ClientId;
        foreach (var marker in plan.Empty.GroupBy(row => row.EffectiveFrom.Date))
        {
            // Explicit cleanup removes only still-empty in-month setup markers.
            // No backup/copy, configured history, Master, policy, attendance,
            // earlier-month record or saved payroll is written here.
            var changed = await db.ExecuteAsync(@"DELETE FROM employee_it0008_basic_pay
WHERE EmployeeId=@EmployeeId AND ClientId=@ClientId AND Id IN @Ids AND Status='Active'
AND EffectiveFrom>@From AND EffectiveFrom<=@To
AND COALESCE(TRIM(SalaryStructureId),'')='' AND AnnualCtc=0
AND (SalaryJson IS NULL OR JSON_TYPE(SalaryJson)='NULL' OR (JSON_TYPE(SalaryJson)='OBJECT' AND JSON_LENGTH(SalaryJson)=0))",
                new { EmployeeId = employeeId, ClientId = clientId, plan.From, plan.To, Ids = marker.Select(row => row.Id).ToArray() }, tx);
            if (changed != marker.Count()) throw new InvalidOperationException("An empty setup entry changed during cleanup.");
        }
    }
}

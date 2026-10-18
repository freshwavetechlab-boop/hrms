using System.Globalization;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class EmployeeRepository
{
    internal sealed class LifecycleAction
    {
        public long Id { get; set; }
        public int EmployeeId { get; set; }
        public int ClientId { get; set; }
        public string ActionType { get; set; } = "";
        public DateTime EffectiveFrom { get; set; }
    }

    internal static async Task ApplyEmploymentStatusAsync(MySqlConnection db, IEnumerable<Employee> employees, MySqlTransaction? tx = null)
    {
        var rows = employees.ToList(); if (rows.Count == 0) return;
        var actions = (await db.QueryAsync<LifecycleAction>(@"SELECT t.Id,t.EmployeeId,t.ClientId,t.ActionType,t.EffectiveFrom
FROM employee_it0000_actions t WHERE t.EmployeeId IN @Ids AND t.Status IN ('Active','Historical')
AND t.ActionType IN ('Resign','Retire','Terminate','Rehire') ORDER BY t.EffectiveFrom DESC,t.Id DESC",
            new { Ids = rows.Select(row => row.Id).ToArray() }, tx)).ToLookup(row => (row.ClientId, row.EmployeeId));
        ApplyEmploymentStatus(rows, actions.SelectMany(group => group), EmployeeEmploymentRules.Today);
    }

    internal static void ApplyEmploymentStatus(IEnumerable<Employee> employees, IEnumerable<LifecycleAction> history, DateTime today)
    {
        var actions = history.OrderByDescending(action => action.EffectiveFrom).ThenByDescending(action => action.Id).ToLookup(row => (row.ClientId, row.EmployeeId));
        foreach (var row in employees)
        {
            var current = actions[(row.ClientId, row.Id)].FirstOrDefault(action => action.EffectiveFrom.Date <= today.Date);
            var action = current is not null && EmployeeEmploymentRules.ExitActions.Contains(current.ActionType) ? current
                : actions[(row.ClientId, row.Id)].Where(action => action.EffectiveFrom.Date > today.Date).OrderBy(action => action.EffectiveFrom).FirstOrDefault();
            row.EmploymentStatus = row.IsActive ? "Active" : "Inactive";
            row.LastWorkingDate = null;
            if (action is null || action.ActionType == "Rehire") continue;
            row.LastWorkingDate = action.EffectiveFrom.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var due = action.EffectiveFrom.Date <= today.Date;
            row.EmploymentStatus = due ? action.ActionType == "Resign" ? "Resigned" : action.ActionType == "Retire" ? "Retired" : "Terminated" : "Exit scheduled";
            if (due) row.IsActive = false;
        }
    }

    public async Task<BulkEmployeeResignationResult> MarkResignedAsync(BulkEmployeeResignationRequest request, string actor)
    {
        if (request.ClientId <= 0 || request.EmployeeIds is null || request.EmployeeIds.Count is < 1 or > 1000 || request.EmployeeIds.Any(id => id <= 0) || request.EmployeeIds.Distinct().Count() != request.EmployeeIds.Count)
            throw new InvalidOperationException("Select 1–1,000 distinct employees from one client.");
        if (request.LastWorkingDates is null || request.LastWorkingDates.Keys.Any(id=>!request.EmployeeIds.Contains(id)))
            throw new InvalidOperationException("Last working dates must belong to the selected employees.");
        var inactiveDates = request.EmployeeIds.ToDictionary(id=>id,id=>EmployeeEmploymentRules.InactiveFrom(request.LastWorkingDates.GetValueOrDefault(id,request.LastWorkingDate)));
        var reason = (request.Reason ?? "").Trim();
        if (reason.Length is < 3 or > 500) throw new InvalidOperationException("Enter a resignation reason using 3–500 characters.");
        await using var db = Connection(); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        if (await db.ExecuteScalarAsync<int?>("SELECT Id FROM clients WHERE Id=@ClientId FOR UPDATE", new { request.ClientId }, tx) is null)
            throw new InvalidOperationException("Client not found.");
        var ids = request.EmployeeIds.Order().ToArray();
        var found = (await db.QueryAsync<int>("SELECT Id FROM employees WHERE ClientId=@ClientId AND Id IN @Ids ORDER BY Id FOR UPDATE", new { request.ClientId, Ids = ids }, tx)).ToArray();
        if (found.Length != ids.Length) throw new InvalidOperationException("Every selected employee must belong to this client.");
        var latestActions = (await db.QueryAsync<LifecycleAction>(@"SELECT a.EmployeeId,a.ActionType,a.EffectiveFrom FROM employee_it0000_actions a
WHERE a.ClientId=@ClientId AND a.EmployeeId IN @Ids AND a.Status IN ('Active','Historical') AND a.ActionType IN ('Resign','Retire','Terminate','Rehire') ORDER BY a.EffectiveFrom DESC,a.Id DESC", new { request.ClientId, Ids = ids }, tx))
            .GroupBy(action => action.EmployeeId).ToDictionary(group => group.Key, group => group.First());
        var pendingIds = ids.Where(id => !latestActions.TryGetValue(id, out var action) || action.ActionType != "Resign" || action.EffectiveFrom.Date != inactiveDates[id]).ToArray();
        if (pendingIds.Length == 0) { await tx.CommitAsync(); return new(0, ids.Length, request.LastWorkingDate, ids); }
        foreach (var dateGroup in pendingIds.GroupBy(id=>inactiveDates[id]))
        {
        var inactiveFrom=dateGroup.Key;
        var dateIds=dateGroup.ToArray();
        var payroll = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM payrunemployees pe JOIN payruns pr ON pr.Id=pe.PayRunId
WHERE pe.EmployeeId IN @Ids AND pr.ClientId=@ClientId AND pe.IsSkipped=FALSE AND pr.PayPeriod>=@Month
AND pr.Status IN ('Draft','Queued','Processing','Pending Approval','Approved','Partially Paid','Paid')",
            new { Ids = dateIds, request.ClientId, Month = inactiveFrom.ToString("yyyy-MM", CultureInfo.InvariantCulture) }, tx);
        if (payroll > 0) throw new InvalidOperationException("Selected employees have payroll in or after the exit period. Review that run before changing their employment dates; saved reports will not be rewritten.");
        var attendance = await db.ExecuteScalarAsync<int>(@"SELECT
(SELECT COUNT(*) FROM employee_daily_attendance WHERE client_id=@ClientId AND employee_id IN @Ids AND attendance_date>=@InactiveFrom AND payable_value>0)
+(SELECT COUNT(*) FROM employee_monthly_attendance WHERE client_id=@ClientId AND employee_id IN @Ids AND
((attendance_month>@LastMonth AND (payable_days>0 OR present_days>0)) OR (attendance_month=@LastMonth AND GREATEST(payable_days,present_days)>@LastDay)))",
            new { request.ClientId, Ids = dateIds, InactiveFrom = inactiveFrom, LastMonth = inactiveFrom.AddDays(-1).ToString("yyyy-MM"), LastDay = inactiveFrom.AddDays(-1).Day }, tx);
        if (attendance > 0) throw new InvalidOperationException("Selected employees have payable attendance after the proposed last working date. Review that attendance first; historical records will not be deleted.");
        }
        var marked = 0; var already = 0;
        foreach (var id in ids)
        {
            var inactiveFrom=inactiveDates[id];
            var latest = latestActions.GetValueOrDefault(id);
            if (latest is not null && EmployeeEmploymentRules.ExitActions.Contains(latest.ActionType))
            {
                if (latest.ActionType == "Resign" && latest.EffectiveFrom.Date == inactiveFrom) { already++; continue; }
                throw new InvalidOperationException("A selected employee already has another exit date. Review their existing action history first.");
            }
            var before = await LoadEmployeeAsync(db, id, tx) ?? throw new InvalidOperationException("Employee not found.");
            if (!before.IsActive) throw new InvalidOperationException($"{before.EmployeeCode} is already inactive. Review that employee before marking a resignation.");
            if (DateTime.TryParse(before.DateOfJoining, out var joined) && inactiveFrom <= joined.Date)
                throw new InvalidOperationException($"{before.EmployeeCode}: last working date cannot precede the joining date.");
            if (latest is not null && latest.EffectiveFrom >= inactiveFrom)
                throw new InvalidOperationException("The last working date must follow the employee's latest rehire date.");
            var snapshot = CloneEmployee(before); snapshot.IsActive = false;
            await WritePhysicalInfotypeAsync(db, snapshot, "0000", "Resign", inactiveFrom, reason, actor, tx);
            await WriteAuditRowsAsync(db, snapshot, before, "Resign", inactiveFrom, actor, ActionInfotypeCodes("Resign"), tx);
            if (inactiveFrom <= EmployeeEmploymentRules.Today)
                await db.ExecuteAsync("UPDATE employees SET IsActive=FALSE WHERE Id=@Id AND ClientId=@ClientId", new { Id = id, request.ClientId }, tx);
            marked++;
        }
        await tx.CommitAsync();
        return new(marked, already, request.LastWorkingDate, ids);
    }

    public async Task<int> ApplyDueExitsAsync(CancellationToken token)
    {
        await using var db = Connection(); await db.OpenAsync(token);
        var changed = await db.ExecuteAsync(new CommandDefinition("UPDATE employees e SET e.IsActive=FALSE WHERE e.IsActive=TRUE AND COALESCE(" + EmployeeEmploymentRules.LatestAction("e", "@Today") + ", 'Rehire') IN ('Resign','Retire','Terminate')", new { Today = EmployeeEmploymentRules.Today }, cancellationToken: token));
        // A scheduled rehire can follow a dated exit. Apply it only when it is the
        // latest due IT0000 action; an explicit later inactive master action wins.
        changed += await db.ExecuteAsync(new CommandDefinition(@"UPDATE employees e JOIN employee_it0000_actions rehire ON rehire.EmployeeId=e.Id AND rehire.ClientId=e.ClientId
SET e.IsActive=TRUE WHERE e.IsActive=FALSE AND rehire.ActionType='Rehire' AND rehire.IsActive=TRUE
AND rehire.Status IN ('Active','Historical') AND rehire.EffectiveFrom<=@Today
AND NOT EXISTS(SELECT 1 FROM employee_it0000_actions later WHERE later.EmployeeId=e.Id AND later.ClientId=e.ClientId
AND later.Status IN ('Active','Historical') AND later.EffectiveFrom<=@Today
AND (later.EffectiveFrom>rehire.EffectiveFrom OR (later.EffectiveFrom=rehire.EffectiveFrom AND later.Id>rehire.Id)))", new { Today = EmployeeEmploymentRules.Today }, cancellationToken: token));
        return changed;
    }
}

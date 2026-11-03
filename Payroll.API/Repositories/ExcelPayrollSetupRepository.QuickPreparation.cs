using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal static ExcelPayrollRoster ReuseVerifiedMasterLinks(ExcelPayrollRoster roster, ExcelPayslipBatch annotated)
    {
        var links=annotated.Rows.Where(row=>row.Employment is {EmployeeId: > 0,IdentityReviewRequired:false,BlockedForMonth:false})
            .ToDictionary(row=>row.Id,row=>row.Employment!.EmployeeId!.Value,StringComparer.Ordinal);
        return new(roster.Rows.Select(row=>
        {
            if(!links.TryGetValue(row.RowId,out var id))return row;
            if(row.EmployeeId is > 0 && row.EmployeeId!=id)
                return row with {Status="Review",Message="The saved Master link and current identity match disagree. Review this employee before preparation."};
            return row with {EmployeeId=id,Status=row.Status=="Applied"?"Applied":"Existing",Message="Existing employee resolved by the current verified source-to-Master link."};
        }).ToList(),roster.MissingEmployees.Where(employee=>!links.Values.Contains(employee.EmployeeId)).ToList());
    }

    internal sealed record QuickMonthlyAttendance(int EmployeeId, decimal WorkingDays, decimal? PresentDays, decimal PayableDays, string SourceType);
    internal sealed record QuickPreparationSnapshot(int Version, string Month, string SourceHash, string AttendanceSnapshotHash,
        string CycleStart, string CycleEnd, IReadOnlyList<QuickMonthlyAttendance> MonthlyAttendance, IReadOnlyList<int> DailyEmployeeIds);

    internal static string QuickAttendanceHash(int clientId, string batchId, string month, IReadOnlyList<string> rows,
        string start, string end, IReadOnlyList<QuickMonthlyAttendance> monthly, IReadOnlyList<int> daily) =>
        Hash(JsonSerializer.Serialize(new { clientId, batchId, month, rows=rows.Order(StringComparer.Ordinal), start, end,
            monthly=monthly.OrderBy(r=>r.EmployeeId), daily=daily.Distinct().Order() }, Json));

    private static async Task<QuickPreparationSnapshot> ReadQuickPreparationAsync(MySqlConnection db, MySqlTransaction? tx,
        int clientId, ExcelPayslipBatch batch, ExcelPayslipCalculationSource source, IReadOnlyList<string> rows,
        IReadOnlyList<int> employeeIds, int startDay, int endDay)
    {
        var cycle=PayRunRepository.NativeAttendanceCycle(batch.Month,startDay,endDay);
        var ids=employeeIds.Where(id=>id>0).Distinct().ToArray();
        var monthly=ids.Length==0 ? [] : (await db.QueryAsync<QuickMonthlyAttendance>(@"SELECT employee_id EmployeeId,
working_days WorkingDays,present_days PresentDays,payable_days PayableDays,source_type SourceType
FROM employee_monthly_attendance WHERE client_id=@ClientId AND attendance_month=@Month AND employee_id IN @Ids ORDER BY employee_id",
            new { ClientId=clientId,Month=batch.Month,Ids=ids },tx)).ToList();
        var daily=ids.Length==0 ? [] : (await db.QueryAsync<int>(@"SELECT DISTINCT employee_id FROM employee_daily_attendance
WHERE client_id=@ClientId AND employee_id IN @Ids AND attendance_date BETWEEN @Start AND @End ORDER BY employee_id",
            new { ClientId=clientId,Ids=ids,Start=cycle.Start,End=cycle.End },tx)).ToList();
        var sourceHash=Hash(JsonSerializer.Serialize(new { Batch=batch,Source=source },Json));
        return new(1,batch.Month,sourceHash,QuickAttendanceHash(clientId,batch.Id,batch.Month,rows,cycle.Start,cycle.End,monthly,daily),cycle.Start,cycle.End,monthly,daily);
    }

    internal static string? QuickPreparationConflict(ExcelPayrollQuickPreparation expected, QuickPreparationSnapshot current,
        bool importAttendance, IReadOnlyList<(int EmployeeId,decimal PayrollDays,decimal PresentDays,decimal PayableDays)> rows)
    {
        if(expected.Version!=1 || expected.SourceHash!=current.SourceHash || expected.AttendanceSnapshotHash!=current.AttendanceSnapshotHash)
            return "Source or attendance changed after the preparation preview. Refresh and review before saving.";
        if(!importAttendance)return null;
        if(current.DailyEmployeeIds.Count>0)return "Dated attendance already exists. Review it in Advanced setup; quick preparation will not replace it with monthly totals.";
        foreach(var row in rows)
        {
            var saved=current.MonthlyAttendance.SingleOrDefault(r=>r.EmployeeId==row.EmployeeId);
            if(saved is not null && (saved.WorkingDays!=row.PayrollDays || saved.PresentDays!=row.PresentDays || saved.PayableDays!=row.PayableDays))
                return "Existing monthly attendance differs from this Excel. Review the difference in Advanced setup before replacing attendance.";
        }
        return null;
    }
}

using System.Globalization;
using Dapper;
using MySqlConnector;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    private sealed record LegacyAttendanceProofResult(string? Start,string? End,string Error,object Witness);

    private static async Task<LegacyAttendanceProofResult> ReadRecordedLegacyAttendanceProofAsync(MySqlConnection db,
        MySqlTransaction tx,int clientId,string month,int employeeId,int memberships,MonthlySalaryProofGroup group,PayRunAttendance monthly)
    {
        var cycle=NativeAttendanceCycle(month,group.StartDay,group.EndDay);
        // Keep every persisted daily column, including timestamps/notes and any
        // future metadata, in the stale-proof witness. Apply already locks this
        // exact employee/client/date range before rebuilding historical proof.
        static Dictionary<string,object?> Stable(object row)=>((IDictionary<string,object>)row)
            .OrderBy(pair=>pair.Key,StringComparer.Ordinal).ToDictionary(pair=>pair.Key,pair=>pair.Value is DBNull?null:pair.Value,StringComparer.Ordinal);
        var raw=(await db.QueryAsync("SELECT * FROM employee_daily_attendance WHERE client_id=@ClientId AND employee_id=@Id AND attendance_date BETWEEN @Start AND @End ORDER BY attendance_date,id",
            new{ClientId=clientId,Id=employeeId,Start=cycle.Start,End=cycle.End},tx)).Select(row=>Stable((object)row)).ToArray();
        var rawGroup=await db.QuerySingleAsync("SELECT g.*,m.employee_id AS proof_employee_id FROM attendance_groups g JOIN attendance_group_employees m ON m.attendance_group_id=g.id WHERE g.id=@Id AND g.client_id=@ClientId AND m.employee_id=@EmployeeId AND g.is_active=TRUE",
            new{Id=group.Id,ClientId=clientId,EmployeeId=employeeId},tx);
        var witness=new{Kind=RecordedLegacyAttendanceProof.Kind,Group=Stable((object)rawGroup),Days=raw};
        try
        {
            static object Required(Dictionary<string,object?> row,string key)=>row.TryGetValue(key,out var value)&&value is not null?value:throw new InvalidOperationException("A persisted Legacy attendance field is missing.");
            var days=raw.Select(row=>new RecordedLegacyAttendanceDay(Convert.ToInt64(Required(row,"id"),CultureInfo.InvariantCulture),
                Convert.ToInt32(Required(row,"client_id"),CultureInfo.InvariantCulture),Convert.ToInt32(Required(row,"employee_id"),CultureInfo.InvariantCulture),
                Convert.ToDateTime(Required(row,"attendance_date"),CultureInfo.InvariantCulture),Convert.ToString(Required(row,"status"),CultureInfo.InvariantCulture)!,
                Convert.ToDecimal(Required(row,"payable_value"),CultureInfo.InvariantCulture),Convert.ToString(row.GetValueOrDefault("remarks"),CultureInfo.InvariantCulture)??"")).ToArray();
            var error=RecordedLegacyAttendanceProof.Validate(true,clientId,employeeId,month,memberships,
                new(group.Id,group.ClientId,group.EmployeeId,group.StartDay,group.EndDay,group.Mode),
                new(monthly.WorkingDays,monthly.PresentDays,monthly.PayableDays,monthly.InputSource,monthly.SourceType,monthly.PolicyError,monthly.PayableOnlyEvidence is not null),days,EmployeeEmploymentRules.Today);
            if(error is null && await db.QueryFirstOrDefaultAsync(EmployeeAttendanceEligibility.DateValidationSql,
                new{ClientId=clientId,Rows=System.Text.Json.JsonSerializer.Serialize(days.Select(day=>new{employeeId=day.EmployeeId,date=day.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)}))},tx) is not null)
                error="A persisted Legacy attendance date is outside the employee's verified employment dates. Review the dated ledger before historical salary proof.";
            return new(error is null?cycle.Start:null,error is null?cycle.End:null,error??"",witness);
        }
        catch(Exception error) when(error is InvalidOperationException or InvalidCastException or FormatException or OverflowException)
        {return new(null,null,"Persisted Legacy attendance cannot be verified: "+error.Message,witness);}
    }
}

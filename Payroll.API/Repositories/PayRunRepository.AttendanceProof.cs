using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;
public partial class PayRunRepository
{
    internal static (string Start,string End) NativeAttendanceCycle(string month,int start,int end) {
        var range=CycleRangeFor(month,start,end);return(range.Start.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),range.End.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
    }
    private sealed class MonthlySalaryProofGroup {public int Id {get;set;} public int StartDay {get;set;} public int EndDay {get;set;} public string Mode {get;set;}="";public string PolicyBatchId {get;set;}="";}
    internal static bool CanUseRecordedMonthlySalaryInput(bool explicitCorrection,PayRunAttendance row,int memberships,string mode)=>
        explicitCorrection&&memberships==1&&mode=="Monthly"&&row.InputSource=="Monthly snapshot"&&row.SourceType=="Monthly"
        &&string.IsNullOrEmpty(row.PolicyError)&&row.WorkingDays is >=1 and <=31&&row.WorkingDays==decimal.Truncate(row.WorkingDays);

    internal static async Task<Dictionary<int,NativePayrollAttendanceInput>> ReadNativeAttendanceProofAsync(MySqlConnection db,MySqlTransaction tx,int clientId,string month,IReadOnlyList<int> ids,bool allowRecordedMonthlyForSalaryCorrection=false) {
        if(ids.Count>100)throw new InvalidOperationException("Select at most 100 employee attendance proofs.");
        var result=new Dictionary<int,NativePayrollAttendanceInput>();
        foreach(var id in ids.Distinct().Where(i=>i>0)) {
            // Uses the same monthly/daily/published weekly-off resolution as normal payroll and Employee simulation.
            var rows=await GetPayRunAttendanceAsync(db,tx,clientId,month,id);
            if(rows.Count!=1)continue;
            var row=rows[0];var snapshot=row.PolicySnapshot;
            var memberships=await db.ExecuteScalarAsync<int>("SELECT COUNT(DISTINCT g.id) FROM attendance_group_employees m JOIN attendance_groups g ON g.id=m.attendance_group_id WHERE m.employee_id=@Id AND g.client_id=@ClientId AND g.is_active=TRUE",new {Id=id,ClientId=clientId},tx);
            var error=memberships>1?"Multiple active attendance groups are ambiguous; review the intended native cycle first.":row.PolicyError;
            var employmentError=await EmployeeAttendanceEligibility.ValidateMonthlyWithOptionalPresenceAsync(db,tx,clientId,month,[(id,row.PresentDays,row.PayableDays)],lockEmployees:false);
            if(employmentError is not null)error=employmentError;
            string? cycleStart=snapshot?.PeriodStart.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),cycleEnd=snapshot?.PeriodEnd.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            MonthlySalaryProofGroup? monthlyGroup=null;
            if(snapshot is null&&string.IsNullOrEmpty(error)&&allowRecordedMonthlyForSalaryCorrection&&memberships==1)
            {
                monthlyGroup=await db.QuerySingleOrDefaultAsync<MonthlySalaryProofGroup>(@"SELECT g.id Id,g.attendance_cycle_start_day StartDay,g.attendance_cycle_end_day EndDay,
g.attendance_mode Mode,COALESCE(g.policy_batch_id,'') PolicyBatchId FROM attendance_groups g JOIN attendance_group_employees m ON m.attendance_group_id=g.id
WHERE m.employee_id=@Id AND g.client_id=@ClientId AND g.is_active=TRUE",new{Id=id,ClientId=clientId},tx);
                if(monthlyGroup is not null&&monthlyGroup.StartDay is >=1 and <=31&&monthlyGroup.EndDay is >=1 and <=31
                    &&CanUseRecordedMonthlySalaryInput(true,row,memberships,monthlyGroup.Mode))
                    (cycleStart,cycleEnd)=NativeAttendanceCycle(month,monthlyGroup.StartDay,monthlyGroup.EndDay);
            }
            if(snapshot is null&&cycleStart is null&&string.IsNullOrEmpty(error))error="Native monthly/daily totals have no dated cycle snapshot. Complete/review native dated attendance before claiming same-cycle proof, or use reviewed source-seed mode with native verification pending.";
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {clientId,month,id,row.WorkingDays,row.PresentDays,row.PayableDays,row.InputSource,row.SourceType,snapshot,monthlyGroup,cycleStart,cycleEnd,error}))));
            result[id]=new(id,clientId,month,row.WorkingDays>=1&&row.WorkingDays<=31&&decimal.Truncate(row.WorkingDays)==row.WorkingDays?(int)row.WorkingDays:null,row.PresentDays,row.PayableDays,
                cycleStart,cycleEnd,hash,error);
        }
        return result;
    }
}

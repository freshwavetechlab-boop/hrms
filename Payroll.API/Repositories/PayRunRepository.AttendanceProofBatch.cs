using System.Globalization;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public partial class PayRunRepository
{
    // Deliberately separate from the existing singleton proof reader until
    // byte-identical evidence and all error branches are independently checked.
    internal static async Task<Dictionary<int,NativePayrollAttendanceInput>> ReadNativeAttendanceProofBatchAsync(
        MySqlConnection db,MySqlTransaction tx,int clientId,string month,IReadOnlyList<int> ids,bool allowRecordedMonthlyForSalaryCorrection=false)
    {
        if(ids.Count>100)throw new InvalidOperationException("Select at most 100 employee attendance proofs.");
        var selected=ids.Distinct().Where(id=>id>0).ToArray();
        var output=new Dictionary<int,NativePayrollAttendanceInput>();
        if(selected.Length==0)return output;
        var rawAttendance=await GetSelectedPayRunAttendanceAsync(db,tx,clientId,month,selected);
        var attendance=rawAttendance.GroupBy(row=>row.EmployeeId).Where(group=>group.Count()==1).ToDictionary(group=>group.Key,group=>group.Single());
        var groups=(await db.QueryAsync<MonthlySalaryProofGroup>(@"SELECT g.id Id,g.client_id ClientId,m.employee_id EmployeeId,g.attendance_cycle_start_day StartDay,
g.attendance_cycle_end_day EndDay,g.attendance_mode Mode,COALESCE(g.policy_batch_id,'') PolicyBatchId
FROM attendance_groups g JOIN attendance_group_employees m ON m.attendance_group_id=g.id
WHERE m.employee_id IN @Ids AND g.client_id=@ClientId AND g.is_active=TRUE",new{Ids=selected,ClientId=clientId},tx)).ToArray();
        var memberships=groups.GroupBy(group=>group.EmployeeId).ToDictionary(group=>group.Key,group=>group.Select(row=>row.Id).Distinct().Count());
        var submitted=selected.Where(attendance.ContainsKey).Select(id=>new{employeeId=id,present=attendance[id].PresentDays,payable=attendance[id].PayableDays}).ToArray();
        var monthStart=DateTime.ParseExact(month+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture);
        var invalidMonthly=submitted.Length==0?new HashSet<int>():(await db.QueryAsync<int>(WithoutFirstResultLimit(EmployeeAttendanceEligibility.MonthlyValidationSql),
            new{Rows=JsonSerializer.Serialize(submitted),ClientId=clientId,Month=month,Start=monthStart,End=monthStart.AddMonths(1).AddDays(-1)},tx)).ToHashSet();
        var plans=new List<BatchAttendanceProofRow>();
        foreach(var id in selected)
        {
            if(!attendance.TryGetValue(id,out var row))continue;
            var count=memberships.GetValueOrDefault(id);
            var error=count>1?"Multiple active attendance groups are ambiguous; review the intended native cycle first.":row.PolicyError;
            if(invalidMonthly.Contains(id))error=$"Employee {id}: monthly attendance exceeds the eligible employment days. Review the last working / rehire date before saving.";
            var plan=new BatchAttendanceProofRow(id,row,error,row.PolicySnapshot?.PeriodStart.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
                row.PolicySnapshot?.PeriodEnd.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
            if(row.PolicySnapshot is null&&string.IsNullOrEmpty(error)&&allowRecordedMonthlyForSalaryCorrection&&count==1)
            {
                var owned=groups.Where(group=>group.EmployeeId==id).ToArray();
                if(owned.Length!=1)throw new InvalidOperationException("Sequence contains more than one element");
                plan.Group=owned[0];
                if(plan.Group.StartDay is >=1 and <=31&&plan.Group.EndDay is >=1 and <=31
                    &&CanUseRecordedMonthlySalaryInput(true,row,count,plan.Group.Mode))
                    (plan.Start,plan.End)=NativeAttendanceCycle(month,plan.Group.StartDay,plan.Group.EndDay);
            }
            plans.Add(plan);
        }

        var legacy=plans.Where(plan=>plan.Group is {Mode:"Legacy",StartDay:>=1 and <=31,EndDay:>=1 and <=31}&&plan.Attendance.SourceType=="Date-wise").ToArray();
        if(legacy.Length>0)await PopulateBatchLegacyProofsAsync(db,tx,clientId,month,legacy,memberships);
        foreach(var plan in plans)
        {
            var id=plan.Id;var row=plan.Attendance;var snapshot=row.PolicySnapshot;var error=plan.Error;var cycleStart=plan.Start;var cycleEnd=plan.End;
            if(plan.Legacy is { } proof){(cycleStart,cycleEnd)=(proof.Start,proof.End);error=proof.Error;}
            if(snapshot is null&&cycleStart is null&&string.IsNullOrEmpty(error))
                error="Native monthly/daily totals have no dated cycle snapshot. Complete/review native dated attendance before claiming same-cycle proof, or use reviewed source-seed mode with native verification pending.";
            object? monthlyWitness=plan.Group is null?null:new{plan.Group.Id,plan.Group.StartDay,plan.Group.EndDay,plan.Group.Mode,plan.Group.PolicyBatchId};
            var proofJson=plan.Legacy is null
                ?JsonSerializer.Serialize(new{clientId,month,id,row.WorkingDays,row.PresentDays,row.PayableDays,row.InputSource,row.SourceType,snapshot,monthlyGroup=monthlyWitness,cycleStart,cycleEnd,error})
                :JsonSerializer.Serialize(new{clientId,month,id,row.WorkingDays,row.PresentDays,row.PayableDays,row.InputSource,row.SourceType,snapshot,monthlyGroup=monthlyWitness,legacyDated=plan.Legacy,cycleStart,cycleEnd,error});
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(proofJson)));
            output[id]=new(id,clientId,month,row.WorkingDays>=1&&row.WorkingDays<=31&&decimal.Truncate(row.WorkingDays)==row.WorkingDays?(int)row.WorkingDays:null,
                row.PresentDays,row.PayableDays,cycleStart,cycleEnd,hash,error){ProofKind=plan.Legacy is {Error:""}?RecordedLegacyAttendanceProof.Kind:null};
        }
        return output;
    }

    private static string WithoutFirstResultLimit(string sql)
    {
        const string suffix=" LIMIT 1";
        if(!sql.EndsWith(suffix,StringComparison.Ordinal))throw new InvalidOperationException("The native employment-validation query contract changed; refresh batch attendance proof.");
        return sql[..^suffix.Length];
    }

    private sealed class BatchAttendanceProofRow(int id,PayRunAttendance attendance,string error,string? start,string? end)
    {
        public int Id{get;}=id;
        public PayRunAttendance Attendance{get;}=attendance;
        public string Error{get;}=error;
        public string? Start{get;set;}=start;
        public string? End{get;set;}=end;
        public MonthlySalaryProofGroup? Group{get;set;}
        public LegacyAttendanceProofResult? Legacy{get;set;}
    }

    private static async Task PopulateBatchLegacyProofsAsync(MySqlConnection db,MySqlTransaction tx,int clientId,string month,
        IReadOnlyList<BatchAttendanceProofRow> plans,IReadOnlyDictionary<int,int> memberships)
    {
        static Dictionary<string,object?> Stable(object row)=>((IDictionary<string,object>)row).OrderBy(pair=>pair.Key,StringComparer.Ordinal)
            .ToDictionary(pair=>pair.Key,pair=>pair.Value is DBNull?null:pair.Value,StringComparer.Ordinal);
        var ranges=plans.ToDictionary(plan=>plan.Id,plan=>NativeAttendanceCycle(month,plan.Group!.StartDay,plan.Group.EndDay));
        var ids=plans.Select(plan=>plan.Id).ToArray();
        var daily=(await db.QueryAsync(@"SELECT * FROM employee_daily_attendance WHERE client_id=@ClientId AND employee_id IN @Ids
AND attendance_date BETWEEN @Start AND @End ORDER BY employee_id,attendance_date,id",
            new{ClientId=clientId,Ids=ids,Start=ranges.Values.Min(range=>range.Start),End=ranges.Values.Max(range=>range.End)},tx)).Select(row=>Stable((object)row)).ToArray();
        var rawGroups=(await db.QueryAsync(@"SELECT g.*,m.employee_id AS proof_employee_id FROM attendance_groups g
JOIN attendance_group_employees m ON m.attendance_group_id=g.id WHERE m.employee_id IN @Ids AND g.client_id=@ClientId AND g.is_active=TRUE",
            new{ClientId=clientId,Ids=ids},tx)).Select(row=>Stable((object)row)).ToArray();
        var validDates=new List<object>();
        foreach(var plan in plans)
        {
            var range=ranges[plan.Id];var group=plan.Group!;
            var raw=daily.Where(row=>Convert.ToInt32(row["employee_id"],CultureInfo.InvariantCulture)==plan.Id
                &&Convert.ToDateTime(row["attendance_date"],CultureInfo.InvariantCulture).Date>=DateTime.ParseExact(range.Start,"yyyy-MM-dd",CultureInfo.InvariantCulture)
                &&Convert.ToDateTime(row["attendance_date"],CultureInfo.InvariantCulture).Date<=DateTime.ParseExact(range.End,"yyyy-MM-dd",CultureInfo.InvariantCulture)).ToArray();
            var rawGroup=rawGroups.Single(row=>Convert.ToInt32(row["proof_employee_id"],CultureInfo.InvariantCulture)==plan.Id);
            var witness=new{Kind=RecordedLegacyAttendanceProof.Kind,Group=rawGroup,Days=raw};
            try
            {
                static object Required(Dictionary<string,object?> row,string key)=>row.TryGetValue(key,out var value)&&value is not null?value:throw new InvalidOperationException("A persisted Legacy attendance field is missing.");
                var days=raw.Select(row=>new RecordedLegacyAttendanceDay(Convert.ToInt64(Required(row,"id"),CultureInfo.InvariantCulture),
                    Convert.ToInt32(Required(row,"client_id"),CultureInfo.InvariantCulture),Convert.ToInt32(Required(row,"employee_id"),CultureInfo.InvariantCulture),
                    Convert.ToDateTime(Required(row,"attendance_date"),CultureInfo.InvariantCulture),Convert.ToString(Required(row,"status"),CultureInfo.InvariantCulture)!,
                    Convert.ToDecimal(Required(row,"payable_value"),CultureInfo.InvariantCulture),Convert.ToString(row.GetValueOrDefault("remarks"),CultureInfo.InvariantCulture)??"")).ToArray();
                var monthly=plan.Attendance;
                var error=RecordedLegacyAttendanceProof.Validate(true,clientId,plan.Id,month,memberships[plan.Id],
                    new(group.Id,group.ClientId,group.EmployeeId,group.StartDay,group.EndDay,group.Mode),
                    new(monthly.WorkingDays,monthly.PresentDays,monthly.PayableDays,monthly.InputSource,monthly.SourceType,monthly.PolicyError,monthly.PayableOnlyEvidence is not null),days,EmployeeEmploymentRules.Today);
                plan.Legacy=new(error is null?range.Start:null,error is null?range.End:null,error??"",witness);
                if(error is null)validDates.AddRange(days.Select(day=>(object)new{employeeId=day.EmployeeId,date=day.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)}));
            }
            catch(Exception error)when(error is InvalidOperationException or InvalidCastException or FormatException or OverflowException)
            {plan.Legacy=new(null,null,"Persisted Legacy attendance cannot be verified: "+error.Message,witness);}
        }
        if(validDates.Count==0)return;
        var invalid=(await db.QueryAsync<BatchInvalidDate>(WithoutFirstResultLimit(EmployeeAttendanceEligibility.DateValidationSql),
            new{ClientId=clientId,Rows=JsonSerializer.Serialize(validDates)},tx)).Select(row=>row.EmployeeId).ToHashSet();
        foreach(var plan in plans.Where(plan=>invalid.Contains(plan.Id)&&plan.Legacy is {Error:""}))
            plan.Legacy=plan.Legacy! with{Start=null,End=null,Error="A persisted Legacy attendance date is outside the employee's verified employment dates. Review the dated ledger before historical salary proof."};
    }
    private sealed class BatchInvalidDate{public int EmployeeId{get;set;}}
}

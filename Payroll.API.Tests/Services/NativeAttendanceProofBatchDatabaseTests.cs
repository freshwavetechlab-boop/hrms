using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed class NativeAttendanceProofBatchDatabaseTests:IAsyncLifetime
{
    private const int Client=97728,One=9772801,Two=9772802,Three=9772803,Zero=9772804,Missing=9772805;
    private static readonly int[] Ids=[One,Two,Three,Zero,Missing];
    private static readonly int[] Groups=[977280,977281,977282,977289];
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture=Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string connection?new(connection):null;
    private ReviewedPayrollFixture F=>fixture??throw new InvalidOperationException("Guarded owned local database required.");
    private sealed class LocalFactAttribute:FactAttribute{public LocalFactAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Explicit sentinel-guarded local fixture required.";}}
    private sealed class LocalTheoryAttribute:TheoryAttribute{public LocalTheoryAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Explicit sentinel-guarded local fixture required.";}}

    public async Task InitializeAsync()
    {
        if(fixture is null)return;await F.InitializeAsync();
        await using(var schema=await F.OpenAsync())await schema.ExecuteAsync("""
CREATE TABLE IF NOT EXISTS leave_attendance_preferences(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT,work_location_id INT,work_week VARCHAR(100),attendance_cycle_start_day INT,attendance_cycle_end_day INT);
CREATE TABLE IF NOT EXISTS leave_types(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT,code VARCHAR(50),type VARCHAR(30));
CREATE TABLE IF NOT EXISTS dropdownmasters(Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT,Type VARCHAR(80),Value VARCHAR(200),ConfigJson JSON,IsActive BOOLEAN);
""");
        await CleanupAsync();await using var db=await F.OpenAsync();
        foreach(var(table,column,definition)in new[]{("employee_daily_attendance","remarks","TEXT NULL"),
            ("employee_daily_attendance","batch_probe_note","VARCHAR(100) NULL"),("attendance_groups","batch_probe_note","VARCHAR(100) NULL")})
            if(!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table AND column_name=@column)",new{table,column}))
                await db.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
        await db.ExecuteAsync("""
CREATE TABLE IF NOT EXISTS leave_attendance_preferences(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT,work_location_id INT,work_week VARCHAR(100),attendance_cycle_start_day INT,attendance_cycle_end_day INT);
CREATE TABLE IF NOT EXISTS leave_types(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT,code VARCHAR(50),type VARCHAR(30));
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97728,'SYN-BATCH-DAYS','Synthetic attendance batch',TRUE);
INSERT INTO attendance_groups(id,client_id,policy_batch_id,name,work_week,attendance_cycle_start_day,attendance_cycle_end_day,is_active,attendance_mode,batch_probe_note) VALUES
(977280,97728,'legacy-days','Synthetic Legacy','Sunday off',1,31,TRUE,'Legacy','initial'),
(977281,97728,'monthly-days','Synthetic Monthly','Sunday off',1,31,TRUE,'Monthly','initial'),
(977282,97728,'alternative','Synthetic alternative','Sunday off',26,25,TRUE,'Legacy','initial');
""");
        await db.ExecuteAsync("INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,IsActive) VALUES(@Id,97728,@Code,'Synthetic','Attendance','2026-01-01',TRUE)",Ids.Select(id=>new{Id=id,Code="BATCH-SYN-"+id}));
        await db.ExecuteAsync("INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(@Group,@Id)",Ids.Select(id=>new{Id=id,Group=id==Two?977281:977280}));
        await db.ExecuteAsync("INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,source_type,remarks) VALUES(97728,@Id,'2026-09',30,@Present,@Payable,@Source,'')",
            new[]{new{Id=One,Present=26m,Payable=30m,Source="Date-wise"},new{Id=Two,Present=0m,Payable=0m,Source="Monthly"},
                new{Id=Three,Present=25.5m,Payable=29.5m,Source="Date-wise"},new{Id=Zero,Present=0m,Payable=0m,Source="Date-wise"}});
        var rows=new List<object>();
        foreach(var id in new[]{One,Three,Zero})foreach(var day in Enumerable.Range(1,30))
        {
            var status=id==Zero?"Absent":day is 6 or 13 or 20 or 27?"WO":"Present";
            var payable=id==Zero?0m:id==Three&&day==1?.5m:1m;
            rows.Add(new{Id=id,Date=new DateTime(2026,9,day),Status=status,Payable=payable});
        }
        await db.ExecuteAsync("INSERT INTO employee_daily_attendance(client_id,employee_id,attendance_date,status,payable_value,remarks,batch_probe_note) VALUES(97728,@Id,@Date,@Status,@Payable,'','initial')",rows);
    }
    public Task DisposeAsync()=>fixture is null?Task.CompletedTask:CleanupAsync();
    private async Task CleanupAsync()
    {
        await using var db=await F.OpenAsync();
        await db.ExecuteAsync("""
DELETE FROM modulesettings WHERE client_id=97728;
DELETE FROM employee_daily_attendance WHERE client_id=97728;
DELETE FROM employee_monthly_attendance WHERE client_id=97728;
DELETE FROM employee_it0000_actions WHERE ClientId=97728;
DELETE FROM attendance_group_employees WHERE attendance_group_id IN @Groups OR employee_id IN @Ids;
DELETE FROM attendance_groups WHERE id IN @Groups;
DELETE FROM leave_attendance_preferences WHERE client_id=97728;
DELETE FROM leave_types WHERE client_id=97728;
DELETE FROM employeepersonaldetails WHERE EmployeeId IN @Ids;
DELETE FROM employeepaymentdetails WHERE EmployeeId IN @Ids;
DELETE FROM employeesalarycomponents WHERE EmployeeId IN @Ids;
DELETE FROM employees WHERE ClientId=97728;
DELETE FROM clients WHERE Id=97728;
""",new{Groups,Ids});
    }
    private static async Task<Dictionary<int,NativePayrollAttendanceInput>> PairAsync(MySqlConnection db,MySqlTransaction tx,
        IReadOnlyList<int>? ids=null,bool reviewed=true,string month="2026-09")
    {
        var requested=ids??Ids;
        var old=await PayRunRepository.ReadNativeAttendanceProofAsync(db,tx,Client,month,requested,reviewed);
        var batch=await PayRunRepository.ReadNativeAttendanceProofBatchAsync(db,tx,Client,month,requested,reviewed);
        Assert.Equal(JsonSerializer.Serialize(old,Json),JsonSerializer.Serialize(batch,Json));
        return batch;
    }
    private async Task<string> WitnessAsync()
    {
        await using var db=await F.OpenAsync();
        return JsonSerializer.Serialize(new{Days=await db.QueryAsync("SELECT * FROM employee_daily_attendance WHERE client_id=@Client ORDER BY employee_id,attendance_date,id",new{Client}),
            Monthly=await db.QueryAsync("SELECT * FROM employee_monthly_attendance WHERE client_id=@Client ORDER BY employee_id",new{Client}),
            Groups=await db.QueryAsync("SELECT * FROM attendance_groups WHERE id IN @Groups ORDER BY id",new{Groups}),
            Employees=await db.QueryAsync("SELECT * FROM employees WHERE ClientId=@Client ORDER BY Id",new{Client}),
            Modules=await db.QueryAsync("SELECT * FROM modulesettings WHERE client_id=@Client ORDER BY ModuleCode",new{Client})},Json);
    }

    [LocalFact]
    public async Task Batch_is_byte_identical_for_legacy_monthly_fractional_zero_missing_and_duplicate_requests()
    {
        var before=await WitnessAsync();await using var db=await F.OpenAsync();
        await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        var reviewed=await PairAsync(db,tx);Assert.Equal(4,reviewed.Count);Assert.DoesNotContain(Missing,reviewed.Keys);
        Assert.All(reviewed.Values,row=>Assert.Equal("",row.Error));Assert.Equal(0,reviewed[Zero].PresentDays);Assert.Equal(0,reviewed[Zero].PayableDays);
        Assert.Equal(25.5m,reviewed[Three].PresentDays);Assert.Equal(RecordedLegacyAttendanceProof.Kind,reviewed[Three].ProofKind);
        await PairAsync(db,tx,[Three,One,One,0,-1,Missing,Zero,Two]);
        var normal=await PairAsync(db,tx,reviewed:false);Assert.All(normal.Values,row=>Assert.NotEqual("",row.Error));
        await tx.RollbackAsync();Assert.Equal(before,await WitnessAsync());
    }

    [LocalTheory]
    [InlineData("gap")][InlineData("notes")][InlineData("unknownStatus")][InlineData("unknownPresence")][InlineData("rollup")]
    [InlineData("ambiguous")][InlineData("foreignGroup")][InlineData("cycle")][InlineData("dateOverride")]
    public async Task Every_error_is_identical_and_isolated_to_its_employee(string change)
    {
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        await db.ExecuteAsync(change switch {
            "gap"=>"DELETE FROM employee_daily_attendance WHERE employee_id=@One AND attendance_date='2026-09-01'",
            "notes"=>"UPDATE employee_daily_attendance SET remarks='Manual override' WHERE employee_id=@One AND attendance_date='2026-09-01'",
            "unknownStatus"=>"UPDATE employee_daily_attendance SET status='CL' WHERE employee_id=@One AND attendance_date='2026-09-01'",
            "unknownPresence"=>"UPDATE employee_monthly_attendance SET present_days=NULL WHERE employee_id=@One",
            "rollup"=>"UPDATE employee_monthly_attendance SET payable_days=29 WHERE employee_id=@One",
            "ambiguous"=>"INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(977282,@One)",
            "foreignGroup"=>"UPDATE attendance_groups SET client_id=197728 WHERE id=977280",
            "cycle"=>"UPDATE attendance_groups SET attendance_cycle_start_day=26,attendance_cycle_end_day=25 WHERE id=977280",
            _=>"UPDATE employee_monthly_attendance SET source_type='ExcelPayableOverride' WHERE employee_id=@One"},new{One},tx);
        if(change is "unknownPresence" or "dateOverride")
        {
            var original=await Assert.ThrowsAsync<InvalidOperationException>(()=>PayRunRepository.ReadNativeAttendanceProofAsync(db,tx,Client,"2026-09",Ids,true));
            var batched=await Assert.ThrowsAsync<InvalidOperationException>(()=>PayRunRepository.ReadNativeAttendanceProofBatchAsync(db,tx,Client,"2026-09",Ids,true));
            Assert.Equal(original.Message,batched.Message);
            var healthy=await PairAsync(db,tx,[Three,Two]);Assert.All(healthy.Values,row=>Assert.Equal("",row.Error));
            await tx.RollbackAsync();return;
        }
        var result=await PairAsync(db,tx);Assert.NotEqual("",result[One].Error);
        if(change is not("foreignGroup" or "cycle"))Assert.Equal("",result[Three].Error);
        Assert.Equal("",result[Two].Error);await tx.RollbackAsync();
    }

    [LocalFact]
    public async Task Monthly_employment_and_dated_employment_errors_do_not_poison_other_rows()
    {
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        await db.ExecuteAsync("INSERT INTO employee_it0000_actions(EmployeeId,ClientId,ActionType,EffectiveFrom,Status) VALUES(@One,97728,'Resign','2026-09-27','Active')",new{One},tx);
        var monthly=await PairAsync(db,tx);Assert.Contains("monthly attendance exceeds",monthly[One].Error);Assert.Equal("",monthly[Three].Error);
        await db.ExecuteAsync("UPDATE employee_daily_attendance SET status='Absent',payable_value=0 WHERE employee_id=@One AND status='WO';UPDATE employee_monthly_attendance SET payable_days=26 WHERE employee_id=@One",new{One},tx);
        var dated=await PairAsync(db,tx);Assert.Contains("persisted Legacy attendance date",dated[One].Error);Assert.Equal("",dated[Three].Error);
        await tx.RollbackAsync();
    }

    [LocalFact]
    public async Task Different_invalid_source_branches_preserve_the_singleton_first_error_in_requested_order()
    {
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        await db.ExecuteAsync("UPDATE employee_monthly_attendance SET present_days=NULL WHERE employee_id=@One;UPDATE employee_monthly_attendance SET source_type='ExcelPayableOverride' WHERE employee_id=@Two",new{One,Two},tx);
        var messages=new List<string>();
        foreach(var ids in new[]{new[]{One,Two,Three},new[]{Two,One,Three}})
        {
            var original=await Assert.ThrowsAsync<InvalidOperationException>(()=>PayRunRepository.ReadNativeAttendanceProofAsync(db,tx,Client,"2026-09",ids,true));
            var batched=await Assert.ThrowsAsync<InvalidOperationException>(()=>PayRunRepository.ReadNativeAttendanceProofBatchAsync(db,tx,Client,"2026-09",ids,true));
            Assert.Equal(original.Message,batched.Message);messages.Add(original.Message);
        }
        Assert.NotEqual(messages[0],messages[1]);
        var healthy=await PairAsync(db,tx,[Three]);Assert.Equal("",healthy[Three].Error);await tx.RollbackAsync();
    }

    [LocalFact]
    public async Task Unknown_daily_and_group_metadata_change_the_same_hash_without_changing_money()
    {
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync();var original=await PairAsync(db,tx);
        await db.ExecuteAsync("UPDATE employee_daily_attendance SET batch_probe_note='new metadata' WHERE employee_id=@One AND attendance_date='2026-09-01'",new{One},tx);
        var day=await PairAsync(db,tx);Assert.NotEqual(original[One].SnapshotHash,day[One].SnapshotHash);Assert.Equal(original[Three].SnapshotHash,day[Three].SnapshotHash);
        await db.ExecuteAsync("UPDATE attendance_groups SET batch_probe_note='new group metadata' WHERE id=977280",transaction:tx);
        var group=await PairAsync(db,tx);Assert.NotEqual(day[One].SnapshotHash,group[One].SnapshotHash);Assert.Equal(day[One].PayableDays,group[One].PayableDays);
        await tx.RollbackAsync();
    }

    [LocalFact]
    public async Task Existing_A_zero_alias_keeps_raw_hash_distinct_and_never_approves_other_units_or_leave()
    {
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        await db.ExecuteAsync("UPDATE employee_daily_attendance SET status='Absent',payable_value=0 WHERE employee_id=@One AND attendance_date='2026-09-01';UPDATE employee_monthly_attendance SET present_days=25,payable_days=29 WHERE employee_id=@One",new{One},tx);
        var absent=await PairAsync(db,tx,[One]);var ordinaryBefore=await PairAsync(db,tx,[One],reviewed:false);
        await db.ExecuteAsync("UPDATE employee_daily_attendance SET status='A' WHERE employee_id=@One AND attendance_date='2026-09-01'",new{One},tx);
        var alias=await PairAsync(db,tx,[One]);Assert.Equal("",alias[One].Error);Assert.Equal(RecordedLegacyAttendanceProof.Kind,alias[One].ProofKind);
        Assert.Equal(absent[One].PresentDays,alias[One].PresentDays);Assert.Equal(absent[One].PayableDays,alias[One].PayableDays);
        Assert.NotEqual(absent[One].SnapshotHash,alias[One].SnapshotHash);
        var ordinaryAfter=await PairAsync(db,tx,[One],reviewed:false);Assert.Equal(ordinaryBefore[One].SnapshotHash,ordinaryAfter[One].SnapshotHash);
        Assert.Equal("A",await db.ExecuteScalarAsync<string>("SELECT status FROM employee_daily_attendance WHERE employee_id=@One AND attendance_date='2026-09-01'",new{One},tx));
        foreach(var(status,value)in new[]{("A",.5m),("A",-1m),("UNKNOWN",0m),("PL",1m),("LWP1",0m)})
        {
            await db.ExecuteAsync("UPDATE employee_daily_attendance SET status=@Status,payable_value=@Value WHERE employee_id=@One AND attendance_date='2026-09-01'",new{One,Status=status,Value=value},tx);
            var rejected=await PairAsync(db,tx,[One]);Assert.Contains("unsupported status or payable value",rejected[One].Error);
        }
        await tx.RollbackAsync();
    }

    [LocalTheory]
    [InlineData("Paid")][InlineData("Unpaid")][InlineData("BothAdjacentAbsent")]
    public async Task Published_policy_snapshots_and_different_cycles_match_singleton_hashes(string mode)
    {
        await using var db=await F.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        var settings=new{attendanceIntegration=new{WeeklyOffRuleVersions=new[]{new WeeklyOffRuleVersion{VersionId="batch-published-v1",VersionNumber=1,EffectiveFrom=new(2026,9,1),Mode=mode,
            Reason="Synthetic publication",PublishedBy="synthetic",PublishedAt=new(2026,8,31),WorkWeekConfigs=new(){["Sunday off"]="{\"workingDays\":[1,2,3,4,5,6]}"}}}}};
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(97728,'leave_attendance',TRUE,CAST(@Json AS JSON))",new{Json=JsonSerializer.Serialize(settings)},tx);
        await db.ExecuteAsync("UPDATE attendance_groups SET attendance_cycle_start_day=26,attendance_cycle_end_day=25 WHERE id=977281",transaction:tx);
        var rows=await PairAsync(db,tx);Assert.Equal(5,rows.Count);
        Assert.NotEqual("",rows[Missing].Error);Assert.NotNull(rows[One].CycleStart);
        var before=rows[One].SnapshotHash;
        await db.ExecuteAsync("INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(977282,@Missing)",new{Missing},tx);
        var selected=await PairAsync(db,tx,[One,Three]);Assert.Equal(before,selected[One].SnapshotHash);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Batch_cap_and_empty_input_stay_bounded_before_database_access()
    {
        await using var db=new MySqlConnection("Server=127.0.0.1;Port=1;Database=unused;User ID=unused");
        Assert.Empty(await PayRunRepository.ReadNativeAttendanceProofBatchAsync(db,null!,Client,"invalid",[0,-1]));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>PayRunRepository.ReadNativeAttendanceProofBatchAsync(db,null!,Client,"2026-09",Enumerable.Range(1,101).ToArray()));
    }

    [LocalFact]
    public async Task Existing_weekly_off_preference_materialization_accepts_Int32_and_Int64_and_rejects_overflow()
    {
        await using var db=await F.OpenAsync();
        var type=typeof(WeeklyOffAttendanceService).GetNestedType("Preference",System.Reflection.BindingFlags.NonPublic)!;
        var query=typeof(SqlMapper).GetMethods(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static)
            .Single(method=>method.Name=="QueryAsync"&&method.IsGenericMethodDefinition&&method.GetGenericArguments().Length==1
                &&method.GetParameters().Length==2&&method.GetParameters()[1].ParameterType==typeof(CommandDefinition)).MakeGenericMethod(type);
        async Task<object> Read(string sql)
        {
            var task=(Task)query.Invoke(null,new object[]{db,new CommandDefinition(sql)})!;await task;
            return Assert.Single(((System.Collections.IEnumerable)task.GetType().GetProperty("Result")!.GetValue(task)!).Cast<object>());
        }
        var narrow=await Read("SELECT attendance_cycle_start_day WorkLocationId,work_week WorkWeek,attendance_cycle_start_day StartDay,attendance_cycle_end_day EndDay FROM attendance_groups WHERE id=977280");
        Assert.Equal(1,type.GetProperty("WorkLocationId")!.GetValue(narrow));
        var wide=await Read("SELECT CAST(17 AS SIGNED) WorkLocationId,'Sunday off' WorkWeek,CAST(1 AS SIGNED) StartDay,CAST(31 AS SIGNED) EndDay");
        Assert.Equal(17,type.GetProperty("WorkLocationId")!.GetValue(wide));
        var error=await Assert.ThrowsAnyAsync<Exception>(()=>Read("SELECT CAST(2147483648 AS SIGNED) WorkLocationId,'Sunday off' WorkWeek,CAST(1 AS SIGNED) StartDay,CAST(31 AS SIGNED) EndDay"));
        Assert.True(error is OverflowException||error.InnerException is OverflowException,error.ToString());
    }
}

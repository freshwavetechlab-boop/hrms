using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Repositories;

public sealed class RecordedLegacyAttendanceRepositoryTests
{
    [LocalWorkflowScopeDatabaseFact]
    public async Task Explicit_historical_proof_is_select_only_and_default_proof_keeps_its_old_failure_hash()
    {
        await using var f=await Fixture.CreateAsync();var before=await f.WitnessAsync();
        var normal=await f.ProofAsync(false);
        const string error="Native monthly/daily totals have no dated cycle snapshot. Complete/review native dated attendance before claiming same-cycle proof, or use reviewed source-seed mode with native verification pending.";
        Assert.Equal(error,normal.Error);Assert.Null(normal.CycleStart);Assert.Null(normal.ProofKind);
        var stored=await f.Db.QuerySingleAsync<(decimal WorkingDays,decimal? PresentDays,decimal PayableDays)>("SELECT working_days,present_days,payable_days FROM employee_monthly_attendance WHERE employee_id=41");
        var oldShape=new{clientId=31,month="2026-09",id=41,stored.WorkingDays,stored.PresentDays,stored.PayableDays,
            InputSource="Monthly snapshot",SourceType="Date-wise",snapshot=(object?)null,monthlyGroup=(object?)null,cycleStart=(string?)null,cycleEnd=(string?)null,error};
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(oldShape)))),normal.SnapshotHash);
        Assert.DoesNotContain("proofKind",JsonSerializer.Serialize(normal,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var proof=await f.ProofAsync(true);
        Assert.Equal("",proof.Error);Assert.Equal("2026-09-01",proof.CycleStart);Assert.Equal("2026-09-30",proof.CycleEnd);
        Assert.Equal(RecordedLegacyAttendanceProof.Kind,proof.ProofKind);Assert.Equal(30,proof.PayrollDays);Assert.Equal(26,proof.PresentDays);Assert.Equal(30,proof.PayableDays);
        Assert.Equal(before,await f.WitnessAsync());
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Every_persisted_daily_column_and_group_configuration_is_in_the_stale_proof_hash()
    {
        await using var f=await Fixture.CreateAsync();var first=await f.ProofAsync(true);
        await f.Db.ExecuteAsync("UPDATE employee_daily_attendance SET extra_witness='metadata changed' WHERE id=1");
        var metadata=await f.ProofAsync(true);Assert.Equal("",metadata.Error);Assert.NotEqual(first.SnapshotHash,metadata.SnapshotHash);
        await f.Db.ExecuteAsync("UPDATE attendance_groups SET work_week='A separately reviewed week' WHERE id=51");
        var group=await f.ProofAsync(true);Assert.Equal("",group.Error);Assert.NotEqual(metadata.SnapshotHash,group.SnapshotHash);
        Assert.Equal(first.PresentDays,group.PresentDays);Assert.Equal(first.PayableDays,group.PayableDays);
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Gaps_unknown_units_notes_ambiguous_groups_and_inconsistent_rollups_are_not_proofs()
    {
        await using var f=await Fixture.CreateAsync();
        foreach(var sql in new[]{
            "DELETE FROM employee_daily_attendance WHERE id=1",
            "UPDATE employee_daily_attendance SET status='CL' WHERE id=1",
            "UPDATE employee_daily_attendance SET payable_value=0 WHERE status='WO'",
            "UPDATE employee_daily_attendance SET remarks='Override' WHERE id=1",
            "UPDATE employee_monthly_attendance SET payable_days=29 WHERE employee_id=41",
            "UPDATE attendance_groups SET attendance_cycle_start_day=26,attendance_cycle_end_day=25 WHERE id=51",
            "UPDATE attendance_groups SET client_id=32 WHERE id=51",
            "INSERT INTO attendance_groups VALUES(52,31,TRUE,1,30,'Legacy','fixture','Sunday off');INSERT INTO attendance_group_employees VALUES(52,41)"})
        {
            await using var tx=await f.Db.BeginTransactionAsync();await f.Db.ExecuteAsync(sql,transaction:tx);
            var proof=Assert.Single(await PayRunRepository.ReadNativeAttendanceProofAsync(f.Db,tx,31,"2026-09",[41],true)).Value;
            Assert.NotEqual("",proof.Error);Assert.Null(proof.ProofKind);await tx.RollbackAsync();
        }
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Historical_apply_day_range_lock_blocks_a_daily_edit_until_the_review_transaction_finishes()
    {
        await using var f=await Fixture.CreateAsync();await using var tx=await f.Db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        // Exact lock predicate used by LockHistoricalInputsAsync at apply.
        await f.Db.QueryAsync<int>("SELECT employee_id FROM employee_daily_attendance WHERE client_id=@ClientId AND employee_id IN @Ids AND attendance_date BETWEEN @Start AND @End FOR UPDATE",
            new{ClientId=31,Ids=new[]{41},Start="2026-09-01",End="2026-09-30"},tx);
        var before=Assert.Single(await PayRunRepository.ReadNativeAttendanceProofAsync(f.Db,tx,31,"2026-09",[41],true)).Value;Assert.Equal("",before.Error);
        await using var writer=await f.OpenAsync();var writerId=await writer.ExecuteScalarAsync<long>("SELECT CONNECTION_ID()");
        var update=writer.ExecuteAsync("UPDATE employee_daily_attendance SET extra_witness='after review' WHERE id=1");
        await using var observer=await f.OpenAsync();
        try{await Fixture.WaitForLockAsync(observer,writerId);}finally{await tx.CommitAsync();}
        await update.WaitAsync(TimeSpan.FromSeconds(10));
        var after=await f.ProofAsync(true);Assert.Equal("",after.Error);Assert.NotEqual(before.SnapshotHash,after.SnapshotHash);
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Matching_aggregate_units_cannot_authorize_dates_outside_employment()
    {
        await using var f=await Fixture.CreateAsync();
        // The monthly aggregate alone fits26eligible days, but the complete
        // persisted ledger still includes dates after the recorded exit.
        await f.Db.ExecuteAsync("UPDATE employee_daily_attendance SET status='Absent',payable_value=0 WHERE status='WO';UPDATE employee_monthly_attendance SET payable_days=26 WHERE employee_id=41;INSERT INTO employee_it0000_actions VALUES(1,41,31,'Active','Resign','2026-09-27')");
        var result=await f.ProofAsync(true);Assert.Contains("employment dates",result.Error);Assert.Null(result.ProofKind);
    }

    private sealed class Fixture(MySqlConnection root,MySqlConnection db,string name,string connection):IAsyncDisposable
    {
        internal MySqlConnection Db{get;}=db;
        internal static async Task<Fixture> CreateAsync()
        {
            var target=new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_WORKFLOW_SCOPE_TEST_DB")??throw new InvalidOperationException("Owned loopback fixture required."));
            Assert.Contains(target.Server,new[]{"localhost","127.0.0.1","::1"});Assert.Equal(33331u,target.Port);target.Database="";
            var root=new MySqlConnection(target.ConnectionString);await root.OpenAsync();
            Assert.Equal("frevo-workflow-scope-test-20261114",await root.ExecuteScalarAsync<string>("SELECT @@hostname"));
            var name="hrms_legacy_days_test_"+Guid.NewGuid().ToString("N");await root.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            target.Database=name;var db=new MySqlConnection(target.ConnectionString);await db.OpenAsync();
            await db.ExecuteAsync("""
CREATE TABLE modulesettings(client_id INT,ModuleCode VARCHAR(120),SettingsJson JSON);
CREATE TABLE employees(Id INT PRIMARY KEY,ClientId INT,IsActive BOOLEAN);INSERT INTO employees VALUES(41,31,TRUE);
CREATE TABLE employee_it0000_actions(Id INT,EmployeeId INT,ClientId INT,Status VARCHAR(30),ActionType VARCHAR(30),EffectiveFrom DATE);
CREATE TABLE employee_monthly_attendance(id INT PRIMARY KEY,client_id INT,employee_id INT,attendance_month CHAR(7),working_days DECIMAL(8,2),present_days DECIMAL(8,2),payable_days DECIMAL(8,2),source_type VARCHAR(80),remarks TEXT);
INSERT INTO employee_monthly_attendance VALUES(1,31,41,'2026-09',30,26,30,'Date-wise','Rolled up from date-wise attendance');
CREATE TABLE attendance_groups(id INT PRIMARY KEY,client_id INT,is_active BOOLEAN,attendance_cycle_start_day INT,attendance_cycle_end_day INT,attendance_mode VARCHAR(30),policy_batch_id VARCHAR(64),work_week VARCHAR(80));
INSERT INTO attendance_groups VALUES(51,31,TRUE,1,30,'Legacy','fixture','Sunday off');
CREATE TABLE attendance_group_employees(attendance_group_id INT,employee_id INT,PRIMARY KEY(attendance_group_id,employee_id));INSERT INTO attendance_group_employees VALUES(51,41);
CREATE TABLE employee_daily_attendance(id INT PRIMARY KEY,client_id INT,employee_id INT,attendance_date DATE,status VARCHAR(30),payable_value DECIMAL(8,2),remarks VARCHAR(100),extra_witness VARCHAR(100),UNIQUE KEY UX_Daily(client_id,employee_id,attendance_date));
""");
            await db.ExecuteAsync("INSERT INTO employee_daily_attendance VALUES(@Id,31,41,@Date,@Status,1,'','initial')",Enumerable.Range(1,30).Select(day=>new{Id=day,Date=new DateTime(2026,9,day),Status=day is 6 or 13 or 20 or 27?"WO":"Present"}));
            return new(root,db,name,target.ConnectionString);
        }
        internal async Task<MySqlConnection> OpenAsync(){var db=new MySqlConnection(connection);await db.OpenAsync();await db.ExecuteAsync("SET SESSION innodb_lock_wait_timeout=10");return db;}
        internal async Task<NativePayrollAttendanceInput> ProofAsync(bool reviewed)
        {await using var tx=await Db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);var result=Assert.Single(await PayRunRepository.ReadNativeAttendanceProofAsync(Db,tx,31,"2026-09",[41],reviewed)).Value;await tx.RollbackAsync();return result;}
        internal async Task<string> WitnessAsync()=>JsonSerializer.Serialize(new{Days=await Db.QueryAsync("SELECT * FROM employee_daily_attendance ORDER BY id"),Monthly=await Db.QueryAsync("SELECT * FROM employee_monthly_attendance"),Groups=await Db.QueryAsync("SELECT * FROM attendance_groups"),Members=await Db.QueryAsync("SELECT * FROM attendance_group_employees"),Modules=await Db.QueryAsync("SELECT * FROM modulesettings")});
        internal static async Task WaitForLockAsync(MySqlConnection observer,long id)
        {for(var n=0;n<200;n++){if(await observer.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.innodb_trx WHERE trx_mysql_thread_id=@Id AND trx_state='LOCK WAIT'",new{Id=id})>0)return;await Task.Delay(25);}throw new InvalidOperationException("Expected daily row lock was not observed.");}
        public async ValueTask DisposeAsync(){await Db.DisposeAsync();MySqlConnection.ClearAllPools();Assert.Matches("^hrms_legacy_days_test_[a-f0-9]{32}$",name);await root.ExecuteAsync($"DROP DATABASE `{name}`");await root.DisposeAsync();}
    }
}

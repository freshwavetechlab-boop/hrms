using System.Text.Json;
using System.Data;
using Dapper;
using MySqlConnector;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayrollMembershipSnapshotDatabaseTests
{
    private sealed class IsolatedFactAttribute:FactAttribute
    {
        public IsolatedFactAttribute()
        {
            if(Environment.GetEnvironmentVariable("FREVO_MEMBERSHIP_READ_TEST_DB") is null)
                Skip="Explicit owned synthetic membership database required.";
        }
    }

    [IsolatedFact]
    public async Task Shared_plan_snapshot_reads_legacy_reference_and_real_guid_as_unchanged_strings()
    {
        await using var db=await OpenFixtureAsync();
        await using var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,isReadOnly:true);
        // Reproduce the original Dapper/driver failure on the same temporary schema.
        await Assert.ThrowsAsync<FormatException>(async()=>await db.QueryAsync("SELECT policy_batch_id FROM attendance_groups WHERE id=1",transaction:tx));
        var rows=(await ExcelPayrollSetupRepository.ReadAttendanceMembershipSnapshotAsync(db,tx,97751,[101,102,103])).ToArray();
        Assert.Equal(3,rows.Length);
        var first=(IDictionary<string,object>)rows[0];var second=(IDictionary<string,object>)rows[1];var third=(IDictionary<string,object>)rows[2];
        Assert.Equal("synthetic-september-policy-2026",Assert.IsType<string>(first["policy_batch_id"]));
        Assert.Equal("4F302D17-18BA-4D79-8C46-0D138BA7A8E1",Assert.IsType<string>(second["policy_batch_id"]));
        Assert.Null(third["policy_batch_id"]);
        Assert.Equal(101,Convert.ToInt32(first["employee_id"]));
        Assert.Equal(102,Convert.ToInt32(second["employee_id"]));
        Assert.Equal(18,first.Count); // Membership identity plus every current group column.
        Assert.Equal(31,Convert.ToInt32(second["attendance_cycle_end_day"]));
        Assert.Equal(0,Convert.ToInt32(second["is_active"])); // Inactive state remains part of the stale-plan witness.
        await tx.RollbackAsync();
    }

    [IsolatedFact]
    public async Task Plan_snapshot_keeps_client_selection_order_and_raw_policy_values_without_writes()
    {
        await using var db=await OpenFixtureAsync();
        var before=await WitnessAsync(db);
        await using(var tx=await db.BeginTransactionAsync(IsolationLevel.RepeatableRead,isReadOnly:true))
        {
            var rows=(await ExcelPayrollSetupRepository.ReadAttendanceMembershipSnapshotAsync(db,tx,97751,[103,101])).ToArray();
            Assert.Equal(new[]{101,103},rows.Select(row=>Convert.ToInt32(((IDictionary<string,object>)row)["employee_id"])));
            Assert.Empty(await ExcelPayrollSetupRepository.ReadAttendanceMembershipSnapshotAsync(db,tx,97753,[101,102,103]));
            var foreign=Assert.Single(await ExcelPayrollSetupRepository.ReadAttendanceMembershipSnapshotAsync(db,tx,97752,[101]));
            Assert.Equal(4,Convert.ToInt32(((IDictionary<string,object>)foreign)["id"]));
            await tx.RollbackAsync();
        }
        Assert.Equal(before,await WitnessAsync(db));
    }

    private static async Task<string> WitnessAsync(MySqlConnection db)=>JsonSerializer.Serialize(await db.QueryAsync(
        "SELECT id,client_id,CAST(policy_batch_id AS CHAR(100)) policy_batch_id,name,is_active FROM attendance_groups ORDER BY id"));

    private static async Task<MySqlConnection> OpenFixtureAsync()
    {
        var value=Environment.GetEnvironmentVariable("FREVO_MEMBERSHIP_READ_TEST_DB")??throw new InvalidOperationException("Owned synthetic fixture missing.");
        var target=new MySqlConnectionStringBuilder(value);
        if(target.Server!="127.0.0.1"||target.Port!=33332||target.Database!="membership_read_test")
            throw new InvalidOperationException("Only the owned loopback membership regression database is allowed.");
        // Exercise the actual inference mode which caused the production read failure.
        target.GuidFormat=MySqlGuidFormat.Char36;
        var db=new MySqlConnection(target.ConnectionString);
        await db.OpenAsync();
        try
        {
            if(await db.ExecuteScalarAsync<string>("SELECT DATABASE()")!="membership_read_test"
                ||await db.ExecuteScalarAsync<string>("SELECT @@hostname")!="frevo-membership-read-test"
                ||await db.ExecuteScalarAsync<string>("SELECT guard_value FROM codex_test_database_guard WHERE owner_scope='membership-read-20261114'")!="39848778-6068-46cc-bbaa-79f54c9bc19b")
                throw new InvalidOperationException("Synthetic membership database sentinel did not match; no writes allowed.");
            // Connection-local tables shadow any application tables and disappear on close.
            await db.ExecuteAsync(@"CREATE TEMPORARY TABLE attendance_groups(
id INT PRIMARY KEY,client_id INT NOT NULL,policy_batch_id CHAR(36) NULL,name VARCHAR(180) NOT NULL,work_location_id INT NOT NULL,
department VARCHAR(150) NOT NULL DEFAULT '',designation VARCHAR(150) NOT NULL DEFAULT '',work_week VARCHAR(80) NOT NULL DEFAULT '',
attendance_cycle_start_day INT NOT NULL DEFAULT 1,attendance_cycle_end_day INT NOT NULL DEFAULT 31,payroll_report_generation_day INT NOT NULL DEFAULT 3,
is_active BOOLEAN NOT NULL DEFAULT TRUE,created_at DATETIME NOT NULL,updated_at DATETIME NOT NULL,shift_id INT NULL,attendance_mode VARCHAR(30) NOT NULL DEFAULT 'Monthly',allowed_shift_ids_json JSON NULL);
CREATE TEMPORARY TABLE attendance_group_employees(employee_id INT NOT NULL,attendance_group_id INT NOT NULL);
INSERT INTO attendance_groups(id,client_id,policy_batch_id,name,work_location_id,is_active,created_at,updated_at) VALUES
(1,97751,'synthetic-september-policy-2026','Synthetic legacy policy',1,TRUE,'2026-01-01','2026-01-02'),
(2,97751,'4F302D17-18BA-4D79-8C46-0D138BA7A8E1','Synthetic GUID policy',1,FALSE,'2026-01-01','2026-01-02'),
(3,97751,NULL,'Synthetic ungrouped policy',1,TRUE,'2026-01-01','2026-01-02'),
(4,97752,'other-client-policy','Foreign synthetic policy',1,TRUE,'2026-01-01','2026-01-02'),
(5,97751,'not-selected-policy','Unselected synthetic policy',1,TRUE,'2026-01-01','2026-01-02');
INSERT INTO attendance_group_employees VALUES(101,1),(102,2),(103,3),(101,4),(104,5);");
            return db;
        }
        catch{await db.DisposeAsync();throw;}
    }
}

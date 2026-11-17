using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class PayslipDesignLegacySettingsTests
{
    [LocalWorkflowScopeDatabaseFact]
    public async Task Stale_global_settings_preserve_atomic_designs_and_keep_ordinary_template_CRUD()
    {
        await using var f = await Fixture.CreateAsync();
        var protectedBefore = await f.ReservedSnapshotAsync();
        var witness = await f.WitnessAsync();
        await PayrollDataTableStore.SaveSetupJsonAsync(f.Db, Payload(
            Row(101,10,"Renamed legacy"), Row(103,10,"New legacy"),
            Row(1999,64,"Stale managed name", "Classic", false)));
        Assert.Equal(protectedBefore, await f.ReservedSnapshotAsync());
        Assert.Equal("Renamed legacy", await f.Db.ExecuteScalarAsync<string>("SELECT Name FROM paysliptemplates WHERE Id=101"));
        Assert.Equal(1, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM paysliptemplates WHERE Id=103"));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM paysliptemplates WHERE Id IN(102,201)"));
        Assert.Equal(witness, await f.WitnessAsync());
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Selected_client_settings_preserve_managed_and_foreign_rows_while_editing_ordinary_rows()
    {
        await using var f = await Fixture.CreateAsync();
        var protectedBefore = await f.ReservedSnapshotAsync();
        var foreignBefore = await f.Db.ExecuteScalarAsync<string>("SELECT CONCAT(Name,'|',Theme,'|',Active) FROM paysliptemplates WHERE Id=201");
        await using (var tx = await f.Db.BeginTransactionAsync())
        {
            var after = JsonNode.Parse(Payload(Row(103,10,"New scoped legacy"), Row(999,10,"Stale managed name","Modern",false)))!.AsObject();
            await PayrollDataTableStore.SaveSelectedClientSetupJsonAsync(f.Db,tx,10,new(),after);
            await tx.CommitAsync();
        }
        Assert.Equal(protectedBefore, await f.ReservedSnapshotAsync());
        Assert.Equal(foreignBefore, await f.Db.ExecuteScalarAsync<string>("SELECT CONCAT(Name,'|',Theme,'|',Active) FROM paysliptemplates WHERE Id=201"));
        Assert.Equal(0, await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM paysliptemplates WHERE Id IN(101,102)"));
        Assert.Equal(10, await f.Db.ExecuteScalarAsync<int>("SELECT ClientId FROM paysliptemplates WHERE Id=103"));

        await using var badTx = await f.Db.BeginTransactionAsync();
        var forged = JsonNode.Parse(Payload(Row(201,10,"Foreign reparent")))!.AsObject();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PayrollDataTableStore.SaveSelectedClientSetupJsonAsync(f.Db,badTx,10,new(),forged));
        await badTx.RollbackAsync();
        Assert.Equal(64, await f.Db.ExecuteScalarAsync<int>("SELECT ClientId FROM paysliptemplates WHERE Id=201"));
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Legacy_settings_cannot_create_convert_or_reuse_a_managed_or_foreign_profile_ID()
    {
        await using var f = await Fixture.CreateAsync();
        var before = await f.TemplateSnapshotAsync();
        await f.Db.ExecuteAsync("INSERT INTO modulesettings VALUES(64,'payslip.design:7000',TRUE,JSON_OBJECT('fixture','orphan'))");
        foreach(var invalid in new[] {
            Row(9000,10,"Unreviewed new design","Excel Payslip"), Row(101,10,"Unreviewed conversion","Excel Payslip"),
            Row(999,64,"Foreign managed ID","Classic"), Row(7000,10,"Foreign orphan ID","Classic") })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => PayrollDataTableStore.SaveSetupJsonAsync(f.Db,Payload(invalid)));
            Assert.Equal(before, await f.TemplateSnapshotAsync());
        }
        // A foreign profile cannot become a valid reservation for this row, but
        // the corruption also must not block an unrelated scoped client's CRUD.
        await f.Db.ExecuteAsync("INSERT INTO modulesettings VALUES(10,'payslip.design:201',TRUE,JSON_OBJECT('fixture','foreign'))");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PayrollDataTableStore.SaveSetupJsonAsync(f.Db,Payload(Row(101,10,"Do not save"))));
        Assert.Equal(before, await f.TemplateSnapshotAsync());
        await using var tx = await f.Db.BeginTransactionAsync();
        await PayrollDataTableStore.SaveSelectedClientSetupJsonAsync(f.Db,tx,10,new(),JsonNode.Parse(Payload(Row(101,10,"Scoped edit"),Row(102,10,"Legacy two")))!.AsObject());
        await tx.CommitAsync();
        Assert.Equal("Scoped edit",await f.Db.ExecuteScalarAsync<string>("SELECT Name FROM paysliptemplates WHERE Id=101"));
        Assert.Equal(64,await f.Db.ExecuteScalarAsync<int>("SELECT ClientId FROM paysliptemplates WHERE Id=201"));
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Concurrent_atomic_insert_commits_before_stale_legacy_save_without_being_deleted()
    {
        await using var f = await Fixture.CreateAsync();
        await using var atomic = await f.OpenAsync();
        await using var tx = await atomic.BeginTransactionAsync();
        // Match the atomic designer's lock order without invoking live sources.
        await atomic.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=10 FOR UPDATE",transaction:tx);
        await atomic.ExecuteScalarAsync<string>("SELECT Id FROM excel_payslip_batches WHERE Id='fixture' FOR UPDATE",transaction:tx);
        await atomic.ExecuteAsync("INSERT INTO paysliptemplates(Id,ClientId,ClientRef,Name,Theme) VALUES(9000,10,'10','Concurrent design','Excel Payslip'); INSERT INTO modulesettings VALUES(10,'payslip.design:9000',TRUE,JSON_OBJECT('fixture','concurrent'))",transaction:tx);
        await using var legacy = await f.OpenAsync();
        var connectionId = await legacy.ExecuteScalarAsync<long>("SELECT CONNECTION_ID()");
        var save = PayrollDataTableStore.SaveSetupJsonAsync(legacy,Payload(Row(101,10,"Legacy edit")));
        await using var observer = await f.OpenAsync();
        try { await f.WaitForLockAsync(connectionId,observer); }
        finally { await tx.CommitAsync(); }
        await save.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Concurrent design",await f.Db.ExecuteScalarAsync<string>("SELECT Name FROM paysliptemplates WHERE Id=9000"));
        Assert.Equal(1,await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=10 AND ModuleCode='payslip.design:9000'"));
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Legacy_template_lock_does_not_take_client_lock_and_deadlock_atomic_edit()
    {
        await using var f = await Fixture.CreateAsync();
        await using var legacyTx = await f.Db.BeginTransactionAsync();
        await PayrollDataTableStore.SaveSelectedClientSetupJsonAsync(f.Db,legacyTx,10,new(),JsonNode.Parse(Payload(Row(101,10,"Legacy edit"),Row(102,10,"Legacy two")))!.AsObject());
        await using var atomic = await f.OpenAsync();
        var connectionId = await atomic.ExecuteScalarAsync<long>("SELECT CONNECTION_ID()");
        await using var atomicTx = await atomic.BeginTransactionAsync();
        // These locks complete while the legacy transaction holds templates.
        await atomic.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=10 FOR UPDATE",transaction:atomicTx).WaitAsync(TimeSpan.FromSeconds(3));
        await atomic.ExecuteScalarAsync<string>("SELECT Id FROM excel_payslip_batches WHERE Id='fixture' FOR UPDATE",transaction:atomicTx).WaitAsync(TimeSpan.FromSeconds(3));
        var edit = atomic.ExecuteAsync("UPDATE paysliptemplates SET Active=FALSE,Note='Atomic deactivation' WHERE Id=999 AND ClientId=10",transaction:atomicTx);
        await using var observer = await f.OpenAsync();
        try { await f.WaitForLockAsync(connectionId,observer); }
        finally { await legacyTx.CommitAsync(); }
        await edit.WaitAsync(TimeSpan.FromSeconds(10));
        await atomicTx.CommitAsync();
        Assert.False(await f.Db.ExecuteScalarAsync<bool>("SELECT Active FROM paysliptemplates WHERE Id=999"));
        Assert.Equal("Atomic deactivation",await f.Db.ExecuteScalarAsync<string>("SELECT Note FROM paysliptemplates WHERE Id=999"));
    }

    [LocalWorkflowScopeDatabaseFact]
    public async Task Legacy_bootstrap_without_module_settings_keeps_normal_CRUD_and_reserves_the_new_theme()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Db.ExecuteAsync("DROP TABLE modulesettings");
        await PayrollDataTableStore.SaveSetupJsonAsync(f.Db,Payload(Row(101,10,"Legacy bootstrap edit"),Row(103,10,"Legacy bootstrap new")));
        Assert.Equal("Legacy bootstrap edit",await f.Db.ExecuteScalarAsync<string>("SELECT Name FROM paysliptemplates WHERE Id=101"));
        Assert.Equal(1,await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM paysliptemplates WHERE Id=103"));
        Assert.Equal(2,await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM paysliptemplates WHERE Id IN(999,1999) AND Theme='Excel Payslip'"));
    }

    private static object Row(long id,int client,string name,string theme="Classic",bool active=true) => new
    { id,clientId=client.ToString(System.Globalization.CultureInfo.InvariantCulture),name,theme,showLogo=false,showClient=false,showYtd=false,showBank=false,note="Stale legacy footer",active };
    private static string Payload(params object[] rows) => JsonSerializer.Serialize(new { payslipTemplates=rows });

    private sealed class Fixture(MySqlConnection root,MySqlConnection db,string name,string connection) : IAsyncDisposable
    {
        internal MySqlConnection Db { get; }=db;
        internal static async Task<Fixture> CreateAsync()
        {
            var target=new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FREVO_WORKFLOW_SCOPE_TEST_DB")??throw new InvalidOperationException("Owned loopback fixture required."));
            Assert.Contains(target.Server,new[]{"localhost","127.0.0.1","::1"});Assert.Equal(33331u,target.Port);target.Database="";
            var root=new MySqlConnection(target.ConnectionString);await root.OpenAsync();
            Assert.Equal("frevo-workflow-scope-test-20261114",await root.ExecuteScalarAsync<string>("SELECT @@hostname"));
            var name="hrms_design_legacy_test_"+Guid.NewGuid().ToString("N");
            await root.ExecuteAsync($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");target.Database=name;
            var db=new MySqlConnection(target.ConnectionString);await db.OpenAsync();
            await db.ExecuteAsync("""
CREATE TABLE paysliptemplates(Id BIGINT PRIMARY KEY,ClientId INT NOT NULL,ClientRef VARCHAR(200) DEFAULT '',Name VARCHAR(200) NOT NULL,Theme VARCHAR(80) DEFAULT 'Classic',ShowLogo BOOLEAN DEFAULT TRUE,ShowClient BOOLEAN DEFAULT TRUE,ShowYtd BOOLEAN DEFAULT FALSE,ShowBank BOOLEAN DEFAULT TRUE,Note VARCHAR(500) DEFAULT '',Active BOOLEAN DEFAULT TRUE);
CREATE TABLE modulesettings(client_id INT NOT NULL,ModuleCode VARCHAR(120) NOT NULL,IsEnabled BOOLEAN,SettingsJson JSON,PRIMARY KEY(client_id,ModuleCode));
CREATE TABLE payrollsetups(Id INT PRIMARY KEY AUTO_INCREMENT,SetupJson JSON);
CREATE TABLE clients(Id INT PRIMARY KEY,IsActive BOOLEAN);INSERT INTO clients VALUES(10,TRUE),(64,TRUE);
CREATE TABLE excel_payslip_batches(Id VARCHAR(80) PRIMARY KEY,SourceJson JSON);INSERT INTO excel_payslip_batches VALUES('fixture',JSON_OBJECT('unchanged','source salary'));
CREATE TABLE payrunemployees(Id INT PRIMARY KEY,NetPay DECIMAL(18,2));INSERT INTO payrunemployees VALUES(1,12345.67);
INSERT INTO paysliptemplates(Id,ClientId,ClientRef,Name,Theme,Note) VALUES(101,10,'10','Legacy one','Classic','Legacy'),(102,10,'10','Legacy two','Modern','Legacy'),(201,64,'64','Foreign legacy','Classic','Foreign'),(999,10,'10','Managed one','Excel Payslip','Reviewed one'),(1999,64,'64','Managed two','Excel Payslip','Reviewed two'),(2000,64,'64','Profile bound old theme','Classic','Protected profile');
INSERT INTO modulesettings VALUES(10,'payslip.design:999',TRUE,JSON_OBJECT('fixture','one')),(64,'payslip.design:1999',TRUE,JSON_OBJECT('fixture','two')),(64,'PAYSLIP.DESIGN:2000',TRUE,JSON_OBJECT('fixture','old theme'));
""");
            return new(root,db,name,target.ConnectionString);
        }
        internal async Task<MySqlConnection> OpenAsync(){var result=new MySqlConnection(connection);await result.OpenAsync();await result.ExecuteAsync("SET SESSION innodb_lock_wait_timeout=10");return result;}
        internal async Task<string> TemplateSnapshotAsync()=>JsonSerializer.Serialize(await Db.QueryAsync("SELECT * FROM paysliptemplates ORDER BY Id"));
        internal async Task<string> ReservedSnapshotAsync()=>JsonSerializer.Serialize(await Db.QueryAsync("SELECT * FROM paysliptemplates WHERE Id IN(999,1999,2000) ORDER BY Id"));
        internal async Task<string> WitnessAsync()=>JsonSerializer.Serialize(new { source=await Db.ExecuteScalarAsync<string>("SELECT CAST(SourceJson AS CHAR) FROM excel_payslip_batches WHERE Id='fixture'"),salary=await Db.ExecuteScalarAsync<decimal>("SELECT NetPay FROM payrunemployees WHERE Id=1"),profiles=await Db.QueryAsync("SELECT * FROM modulesettings ORDER BY client_id,ModuleCode") });
        internal async Task WaitForLockAsync(long id,MySqlConnection? observer=null)
        {
            var db=observer??Db;
            for(var attempt=0;attempt<200;attempt++)
            {
                if(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.innodb_trx WHERE trx_mysql_thread_id=@Id AND trx_state='LOCK WAIT'",new{Id=id})>0)return;
                await Task.Delay(25);
            }
            throw new InvalidOperationException("The intended concurrent template lock was not observed: "+JsonSerializer.Serialize(await db.QueryAsync("SELECT trx_mysql_thread_id,trx_state,trx_query FROM information_schema.innodb_trx")));
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();MySqlConnection.ClearAllPools();Assert.Matches("^hrms_design_legacy_test_[a-f0-9]{32}$",name);
            await root.ExecuteAsync($"DROP DATABASE `{name}`");await root.DisposeAsync();
        }
    }
}

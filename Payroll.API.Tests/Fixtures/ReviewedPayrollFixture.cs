using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Fixtures;

// Additive bootstrap for one root-owned synthetic container. No global reset,
// application startup/migration, production credentials or business data access.
public sealed class ReviewedPayrollFixture
{
    private static readonly SemaphoreSlim SchemaGate = new(1);
    private static bool schemaReady;
    public string Connection { get; }
    public IConfiguration Config { get; }
    public PayRunRepository Runs { get; }
    public ReviewedPayrollFixture(string connection)
    {
        var target = new MySqlConnectionStringBuilder(connection);
        if(target.Server != "127.0.0.1" || target.Port != 33330 || target.Database != "attendance_location_test")
            throw new InvalidOperationException("An explicitly owned loopback synthetic payroll database is required.");
        Connection=connection;
        Config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["ConnectionStrings:Default"]=connection,["Database:AutoMigrate"]="false",
            ["BackgroundWorkers:Enabled"]="false",["OutboundDelivery:Suppressed"]="true",
            ["IntegrationCredentialEncryption:MasterKey"]="synthetic-reviewed-payroll-evidence-key"
        }).Build();
        Runs=new(Config,new TaxEngineRepository(Config));
    }
    public async Task<MySqlConnection> OpenAsync()
    {
        var db=new MySqlConnection(Connection); await db.OpenAsync();
        try
        {
            if(await db.ExecuteScalarAsync<string>("SELECT DATABASE()")!="attendance_location_test"
                || await db.ExecuteScalarAsync<string>("SELECT @@hostname")!="1bd7258b208b"
                || await db.ExecuteScalarAsync<string>("SELECT guard_value FROM codex_test_database_guard WHERE owner_scope='excel-core-goal-20261114'")!="9e84ec2a-f9dc-46ce-a6ca-426d9b2aaf19")
                throw new InvalidOperationException("Synthetic payroll database identity/sentinel did not match. No test writes are allowed.");
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    public async Task InitializeAsync()
    {
        await SchemaGate.WaitAsync();
        try
        {
            await using var db=await OpenAsync();
            if(schemaReady)return;
            await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","EssProfile","profile-schema.sql")));
            await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","EssProfile","payslip-schema.sql")));
            await PayrollDataTableStore.EnsureAsync(db);
            // Native employee infotypes are schema-only; this initializer has no
            // seed/backfill or delete path and does not touch a normal API host.
            await new EmployeeRepository(Config,null!,null!).InitializeAsync();
            await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS modulesettings(client_id INT NOT NULL,ModuleCode VARCHAR(120) NOT NULL,IsEnabled BOOLEAN NOT NULL DEFAULT TRUE,SettingsJson JSON,PRIMARY KEY(client_id,ModuleCode));
CREATE TABLE IF NOT EXISTS payrollsetups(Id INT PRIMARY KEY AUTO_INCREMENT,SetupJson JSON);
CREATE TABLE IF NOT EXISTS tax_client_settings(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT NOT NULL,financial_year VARCHAR(10),enabled BOOLEAN DEFAULT FALSE,project_monthly_tds BOOLEAN DEFAULT FALSE,tax_deduction_component_code VARCHAR(80) DEFAULT 'TDS',active BOOLEAN DEFAULT TRUE);
CREATE TABLE IF NOT EXISTS tax_rule_versions(id INT PRIMARY KEY AUTO_INCREMENT,financial_year VARCHAR(10),active BOOLEAN DEFAULT FALSE);
CREATE TABLE IF NOT EXISTS employee_monthly_attendance(id BIGINT PRIMARY KEY AUTO_INCREMENT,client_id INT NOT NULL,employee_id INT NOT NULL,attendance_month CHAR(7) NOT NULL,working_days DECIMAL(8,2),present_days DECIMAL(8,2) NULL,payable_days DECIMAL(8,2),source_type VARCHAR(80) DEFAULT '',remarks TEXT,UNIQUE KEY UX_ReviewedMonthly(client_id,employee_id,attendance_month));
CREATE TABLE IF NOT EXISTS employee_daily_attendance(id BIGINT PRIMARY KEY AUTO_INCREMENT,client_id INT NOT NULL,employee_id INT NOT NULL,attendance_date DATE,status VARCHAR(40),payable_value DECIMAL(8,2));
CREATE TABLE IF NOT EXISTS payruns(Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,ClientName VARCHAR(250),PayPeriod CHAR(7),RunCode VARCHAR(40),RunType VARCHAR(30) DEFAULT 'Regular',RunName VARCHAR(120),Reason VARCHAR(500),PayDate DATE,TotalWorkingDays INT,Status VARCHAR(30),RequestJson JSON,PayrollCost DECIMAL(18,2) DEFAULT 0,NetPay DECIMAL(18,2) DEFAULT 0);
CREATE TABLE IF NOT EXISTS payrunemployees(Id INT PRIMARY KEY AUTO_INCREMENT,PayRunId INT NOT NULL,EmployeeId INT NOT NULL,ClientId INT NOT NULL DEFAULT 0,EmployeeCode VARCHAR(80),EmployeeName VARCHAR(250),Department VARCHAR(100),PresentDays DECIMAL(8,2) NULL,PayableDays DECIMAL(8,2),MonthlyGross DECIMAL(18,2),GrossPay DECIMAL(18,2),StatutoryDeductions DECIMAL(18,2),OneTimeEarnings DECIMAL(18,2),OneTimeDeductions DECIMAL(18,2),NetPay DECIMAL(18,2),IsSkipped BOOLEAN DEFAULT FALSE,DetailsJson JSON);
CREATE TABLE IF NOT EXISTS payrolladjustments(Id INT PRIMARY KEY AUTO_INCREMENT,ClientId INT NOT NULL,EmployeeId INT NOT NULL,EmployeeName VARCHAR(250) DEFAULT '',EmployeeCode VARCHAR(80) DEFAULT '',ComponentId INT DEFAULT 0,ComponentCode VARCHAR(80) DEFAULT '',ComponentName VARCHAR(150) DEFAULT '',AdjustmentType VARCHAR(30) DEFAULT 'Earning',Amount DECIMAL(18,2),PayPeriod CHAR(7),PayRunType VARCHAR(30) DEFAULT 'Regular',ReasonCode VARCHAR(80) DEFAULT '',Notes VARCHAR(500) DEFAULT '',Taxable BOOLEAN DEFAULT TRUE,Status VARCHAR(30) DEFAULT 'Approved',PayRunId INT NULL);");
            foreach(var (table,column,definition) in new (string,string,string)[]
            {
                ("clients","Code","VARCHAR(80) NOT NULL DEFAULT ''"),("worklocations","ClientId","INT NOT NULL DEFAULT 0"),
                ("worklocations","State","VARCHAR(100) NOT NULL DEFAULT ''"),("worklocations","City","VARCHAR(100) NOT NULL DEFAULT ''"),
                ("employees","PersonalJson","JSON NULL"),("employees","PaymentJson","JSON NULL"),("employees","Grade","VARCHAR(100) DEFAULT ''"),
                ("employees","PortalAccess","BOOLEAN DEFAULT FALSE"),
                ("employeepersonaldetails","State","VARCHAR(100) NOT NULL DEFAULT ''"),("employeepersonaldetails","EsicEmployee","DECIMAL(18,4) DEFAULT 0"),
                ("employeepersonaldetails","PtLwfWorkmenComp","DECIMAL(18,4) DEFAULT 0"),("employeepersonaldetails","Tds","DECIMAL(18,4) DEFAULT 0"),
                ("employeepersonaldetails","Recovery","DECIMAL(18,4) DEFAULT 0")
            })
                if(!await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name=@table AND column_name=@column)",new{table,column}))
                    await db.ExecuteAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}");
            schemaReady=true;
        }
        finally { SchemaGate.Release(); }
    }
}

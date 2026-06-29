using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class OrganizationRepository
{
    private readonly IConfiguration _configuration;

    public OrganizationRepository(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    private MySqlConnection CreateConnection()
    {
        var connectionString = _configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Default' is not configured.");
        }

        return new MySqlConnection(connectionString);
    }

    public async Task InitializeAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("CREATE DATABASE IF NOT EXISTS payroll;");
        await connection.ExecuteAsync("USE payroll;");

        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS Organizations (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Name VARCHAR(250) NOT NULL,
    LegalName VARCHAR(250),
    BusinessType VARCHAR(150),
    BusinessLocation VARCHAR(100) NOT NULL DEFAULT 'India',
    Industry VARCHAR(150),
    HasRunPayrollThisYear BOOLEAN NOT NULL DEFAULT FALSE,
    SetupCompleted BOOLEAN NOT NULL DEFAULT FALSE,
    LogoDataUrl LONGTEXT,
    PAN VARCHAR(50),
    GSTIN VARCHAR(50),
    FiscalYearStart VARCHAR(50),
    AddressLine1 VARCHAR(255),
    AddressLine2 VARCHAR(255),
    City VARCHAR(100),
    State VARCHAR(100),
    PostalCode VARCHAR(30),
    Country VARCHAR(100),
    BankName VARCHAR(200),
    AccountNumber VARCHAR(100),
    IFSCCode VARCHAR(50),
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);" );

        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS PayrollSetups (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    SetupJson JSON NOT NULL,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);" );

        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS Clients (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Name VARCHAR(250) NOT NULL,
    Code VARCHAR(50),
    ContactPerson VARCHAR(150),
    Email VARCHAR(150),
    Phone VARCHAR(50),
    Address VARCHAR(500),
    PayScheduleJson JSON NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);" );

        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS WorkLocations (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Name VARCHAR(200) NOT NULL,
    Address VARCHAR(500),
    City VARCHAR(100),
    State VARCHAR(100),
    PostalCode VARCHAR(30),
    IsPrimary BOOLEAN NOT NULL DEFAULT FALSE,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);" );

        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS DropdownMasters (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Type VARCHAR(100) NOT NULL,
    Value VARCHAR(200) NOT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_DropdownMasters_Type_Value (Type, Value)
);" );

        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS Employees (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    EmployeeCode VARCHAR(50) NOT NULL,
    FirstName VARCHAR(100) NOT NULL,
    LastName VARCHAR(100),
    Gender VARCHAR(30),
    DateOfJoining VARCHAR(30),
    WorkEmail VARCHAR(150),
    Department VARCHAR(100),
    Designation VARCHAR(100),
    WorkLocationId INT NOT NULL DEFAULT 0,
    ReportingManagerId INT NOT NULL DEFAULT 0,
    PortalAccess BOOLEAN NOT NULL DEFAULT FALSE,
    SalaryStructureId VARCHAR(50),
    AnnualCtc DECIMAL(18,2) NOT NULL DEFAULT 0,
    SalaryJson JSON NOT NULL,
    PersonalJson JSON NOT NULL,
    PaymentJson JSON NOT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_Employees_Client_Code (ClientId, EmployeeCode)
);" );

        await SeedAsync(connection);

        await EnsureColumnAsync(connection, "BusinessLocation", "VARCHAR(100) NOT NULL DEFAULT 'India'");
        await EnsureColumnAsync(connection, "Industry", "VARCHAR(150) NULL");
        await EnsureColumnAsync(connection, "HasRunPayrollThisYear", "BOOLEAN NOT NULL DEFAULT FALSE");
        await EnsureColumnAsync(connection, "SetupCompleted", "BOOLEAN NOT NULL DEFAULT FALSE");
        await EnsureColumnAsync(connection, "LogoDataUrl", "LONGTEXT NULL");
        await EnsureTableColumnAsync(connection, "Clients", "PayScheduleJson", "JSON NULL");
        await PayrollDataTableStore.EnsureAsync(connection);
        await SeedLocationDropdownMastersAsync(connection);
    }

    private static async Task EnsureColumnAsync(MySqlConnection connection, string columnName, string definition)
    {
        const string existsSql = @"
SELECT COUNT(*)
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'payroll'
  AND TABLE_NAME = 'Organizations'
  AND COLUMN_NAME = @ColumnName;";

        var exists = await connection.ExecuteScalarAsync<int>(existsSql, new { ColumnName = columnName });
        if (exists == 0)
        {
            await connection.ExecuteAsync($"ALTER TABLE Organizations ADD COLUMN `{columnName}` {definition};");
        }
    }

    private static async Task EnsureTableColumnAsync(MySqlConnection connection, string tableName, string columnName, string definition)
    {
        var exists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'payroll' AND TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName", new { TableName = tableName, ColumnName = columnName });
        if (exists == 0) await connection.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition};");
    }

    private static async Task SeedLocationDropdownMastersAsync(MySqlConnection connection)
    {
        await connection.ExecuteAsync(@"
INSERT INTO dropdownmasters (Type, Value, IsActive)
SELECT 'State', State, TRUE FROM (
    SELECT DISTINCT TRIM(State) State FROM worklocations WHERE TRIM(COALESCE(State, '')) <> ''
    UNION SELECT DISTINCT TRIM(State) FROM organizations WHERE TRIM(COALESCE(State, '')) <> ''
) s WHERE NOT EXISTS (SELECT 1 FROM dropdownmasters d WHERE d.Type = 'State' AND d.Value = s.State);

INSERT INTO dropdownmasters (Type, Value, IsActive)
SELECT CONCAT('City:', State), City, TRUE FROM (
    SELECT DISTINCT TRIM(State) State, TRIM(City) City FROM worklocations WHERE TRIM(COALESCE(State, '')) <> '' AND TRIM(COALESCE(City, '')) <> ''
    UNION SELECT DISTINCT TRIM(State), TRIM(City) FROM organizations WHERE TRIM(COALESCE(State, '')) <> '' AND TRIM(COALESCE(City, '')) <> ''
) c WHERE NOT EXISTS (SELECT 1 FROM dropdownmasters d WHERE d.Type = CONCAT('City:', c.State) AND d.Value = c.City);");
    }

    private async Task PrepareDatabaseAsync(MySqlConnection connection)
    {
        await connection.ExecuteAsync("USE payroll;");
    }

    private static async Task SeedAsync(MySqlConnection connection)
    {
        var hasOrg = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Organizations");
        if (hasOrg == 0)
            await connection.ExecuteAsync(@"INSERT INTO Organizations (Name, LegalName, BusinessType, BusinessLocation, Industry, HasRunPayrollThisYear, SetupCompleted, AddressLine1, City, State, PostalCode, Country) VALUES ('Demo Payroll Pvt Ltd', 'Demo Payroll Private Limited', 'Private Limited Company', 'India', 'Information Technology', FALSE, TRUE, '221B Business Park', 'Bengaluru', 'Karnataka', '560001', 'India');");

        var hasClients = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Clients");
        if (hasClients == 0)
            await connection.ExecuteAsync(@"INSERT INTO Clients (Name, Code, ContactPerson, Email, Phone, Address) VALUES ('Acme Technologies', 'ACME', 'Riya Sharma', 'hr@acme.test', '9876543210', 'Bengaluru'), ('Northwind Services', 'NORTH', 'Arjun Mehta', 'ops@northwind.test', '9123456780', 'Pune');");

        var hasLocations = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM WorkLocations");
        if (hasLocations == 0)
            await connection.ExecuteAsync(@"INSERT INTO WorkLocations (Name, Address, City, State, PostalCode, IsPrimary) VALUES ('Head Office', '221B Business Park', 'Bengaluru', 'Karnataka', '560001', TRUE), ('Pune Branch', 'Tower 4, Hinjewadi', 'Pune', 'Maharashtra', '411057', FALSE);");

        var hasDrops = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DropdownMasters");
        if (hasDrops == 0)
            await connection.ExecuteAsync(@"INSERT INTO DropdownMasters (Type, Value) VALUES ('Department','Engineering'),('Department','Finance'),('Department','HR'),('Designation','Software Engineer'),('Designation','Payroll Executive'),('Designation','Manager'),('Employment Type','Full Time'),('Employee Grade','L1'),('Employee Grade','L2'),('Cost Center','CC-TECH');");

        var setupJson = @"{""salaryComponents"":[{""id"":101,""code"":""BASIC"",""componentType"":""Basic"",""category"":""Earning"",""name"":""Basic"",""payType"":""Fixed Pay"",""calculationType"":""Percentage of CTC"",""value"":""40"",""formula"":""CTC * 40%"",""baseComponent"":""CTC"",""taxable"":true,""ctc"":true,""proRata"":true,""fbp"":false,""restrictFbp"":false,""epf"":""Always"",""esi"":true,""recurring"":true,""scheduled"":false,""investmentType"":"""",""correctionOf"":"""",""active"":true,""priority"":""10""},{""id"":102,""code"":""HRA"",""componentType"":""House Rent Allowance"",""category"":""Earning"",""name"":""House Rent Allowance"",""payType"":""Fixed Pay"",""calculationType"":""Formula"",""value"":"""",""formula"":""BASIC * 50%"",""baseComponent"":""BASIC"",""taxable"":true,""ctc"":true,""proRata"":true,""fbp"":false,""restrictFbp"":false,""epf"":""Never"",""esi"":true,""recurring"":true,""scheduled"":false,""investmentType"":"""",""correctionOf"":"""",""active"":true,""priority"":""20""},{""id"":103,""code"":""SPAL"",""componentType"":""Custom Allowance"",""category"":""Earning"",""name"":""Special Allowance"",""payType"":""Fixed Pay"",""calculationType"":""Balancing Amount"",""value"":"""",""formula"":""CTC - SUM(Fixed Earnings)"",""baseComponent"":""CTC"",""taxable"":true,""ctc"":true,""proRata"":true,""fbp"":false,""restrictFbp"":false,""epf"":""Never"",""esi"":true,""recurring"":true,""scheduled"":false,""investmentType"":"""",""correctionOf"":"""",""active"":true,""priority"":""90""},{""id"":104,""code"":""PF"",""componentType"":""Provident Fund"",""category"":""Deduction"",""name"":""Provident Fund"",""payType"":""Fixed Pay"",""calculationType"":""Formula"",""value"":"""",""formula"":""MIN(BASIC,15000)*12%"",""baseComponent"":""BASIC"",""taxable"":false,""ctc"":true,""proRata"":true,""fbp"":false,""restrictFbp"":false,""epf"":""Never"",""esi"":false,""recurring"":true,""scheduled"":false,""investmentType"":"""",""correctionOf"":"""",""active"":true,""priority"":""110""}],""salaryStructures"":[{""id"":201,""clientId"":""1:Acme Technologies"",""name"":""Acme Default CTC"",""annualCtc"":""900000"",""lines"":[{""componentId"":""101"",""value"":""40% of CTC""},{""componentId"":""102"",""value"":""50% of BASIC""},{""componentId"":""103"",""value"":""Balance""},{""componentId"":""104"",""value"":""MIN(BASIC,15000)*12%""}],""active"":true}],""payslipTemplates"":[{""id"":301,""clientId"":""1:Acme Technologies"",""name"":""Acme Classic Payslip"",""theme"":""Classic"",""showLogo"":true,""showClient"":true,""showYtd"":true,""showBank"":true,""note"":""This is a system generated payslip."",""active"":true}],""tax"":{""pan"":""ABCDE1234F"",""tan"":""ABCD12345E"",""aoCode"":""BLR/W/123/1"",""frequency"":""Monthly""},""schedule"":{""workWeek"":""Monday - Friday"",""salaryDays"":""Actual days"",""fixedDays"":""30"",""payDay"":""Last working day"",""firstPayPeriod"":""2026-06""},""statutory"":{""epf"":true,""epfNumber"":""BG/BNG/1234567"",""epfCtc"":true,""abry"":false,""epfContribution"":""Both Employee and Employer"",""restrictPf"":true,""esi"":false,""esiNumber"":"""",""pt"":true,""ptNumber"":""PT-KA-12345"",""ptState"":""Karnataka"",""ptCycle"":""Monthly"",""ptSlabs"":""Up to 15000: 0\n15001 and above: 200"",""lwf"":true,""lwfState"":""Karnataka"",""lwfCycle"":""Half-yearly"",""lwfEligibilityLimit"":""15000"",""lwfEmployeeContribution"":""20"",""lwfEmployerContribution"":""40""}}";
        setupJson = DefaultSetupJson;
        var hasSetup = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PayrollSetups");
        if (hasSetup == 0)
            await connection.ExecuteAsync("INSERT INTO PayrollSetups (SetupJson) VALUES (@setupJson)", new { setupJson });

        await connection.ExecuteAsync(@"INSERT IGNORE INTO Employees (ClientId, EmployeeCode, FirstName, LastName, Gender, DateOfJoining, WorkEmail, Department, Designation, WorkLocationId, SalaryStructureId, AnnualCtc, SalaryJson, PersonalJson, PaymentJson, IsActive) VALUES
(1,'ACME001','Amit','Verma','Male','2026-04-01','amit.verma@acme.test','Engineering','Software Engineer',1,'201',900000,'{""101"":""30000"",""102"":""15000"",""103"":""25000"",""104"":""1800""}','{}','{}',TRUE),
(1,'ACME002','Neha','Kapoor','Female','2026-04-01','neha.kapoor@acme.test','Engineering','Software Engineer',1,'201',840000,'{""101"":""28000"",""102"":""14000"",""103"":""22000"",""104"":""1800""}','{}','{}',TRUE),
(1,'ACME003','Rahul','Singh','Male','2026-04-01','rahul.singh@acme.test','Finance','Payroll Executive',1,'201',720000,'{""101"":""24000"",""102"":""12000"",""103"":""18000"",""104"":""1800""}','{}','{}',TRUE),
(1,'ACME004','Priya','Nair','Female','2026-04-01','priya.nair@acme.test','HR','Manager',1,'201',960000,'{""101"":""32000"",""102"":""16000"",""103"":""27000"",""104"":""1800""}','{}','{}',TRUE),
(1,'ACME005','Vikram','Das','Male','2026-04-01','vikram.das@acme.test','Engineering','Software Engineer',1,'201',780000,'{""101"":""26000"",""102"":""13000"",""103"":""20000"",""104"":""1800""}','{}','{}',TRUE),
(2,'NORTH001','Ananya','Rao','Female','2026-04-01','ananya.rao@north.test','Engineering','Software Engineer',2,'',900000,'{""101"":""30000"",""102"":""15000"",""103"":""25000"",""104"":""1800""}','{}','{}',TRUE),
(2,'NORTH002','Karan','Mehta','Male','2026-04-01','karan.mehta@north.test','Finance','Payroll Executive',2,'',750000,'{""101"":""25000"",""102"":""12500"",""103"":""19000"",""104"":""1800""}','{}','{}',TRUE),
(2,'NORTH003','Sneha','Iyer','Female','2026-04-01','sneha.iyer@north.test','HR','Manager',2,'',840000,'{""101"":""28000"",""102"":""14000"",""103"":""22000"",""104"":""1800""}','{}','{}',TRUE),
(2,'NORTH004','Arjun','Malik','Male','2026-04-01','arjun.malik@north.test','Engineering','Software Engineer',2,'',810000,'{""101"":""27000"",""102"":""13500"",""103"":""21000"",""104"":""1800""}','{}','{}',TRUE),
(2,'NORTH005','Meera','Shah','Female','2026-04-01','meera.shah@north.test','Finance','Payroll Executive',2,'',870000,'{""101"":""29000"",""102"":""14500"",""103"":""23000"",""104"":""1800""}','{}','{}',TRUE);");
    }

    public async Task<Organization?> GetAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);

        return await connection.QueryFirstOrDefaultAsync<Organization>("SELECT * FROM Organizations ORDER BY Id LIMIT 1");
    }

    public async Task<int> SaveAsync(Organization organization)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);

        var existing = await connection.QueryFirstOrDefaultAsync<Organization>(
            "SELECT * FROM Organizations ORDER BY Id LIMIT 1");
        if (existing is null)
        {
            const string insertSql = @"
INSERT INTO Organizations (
    Name,
    LegalName,
    BusinessType,
    BusinessLocation,
    Industry,
    HasRunPayrollThisYear,
    SetupCompleted,
    LogoDataUrl,
    PAN,
    GSTIN,
    FiscalYearStart,
    AddressLine1,
    AddressLine2,
    City,
    State,
    PostalCode,
    Country,
    BankName,
    AccountNumber,
    IFSCCode
) VALUES (
    @Name,
    @LegalName,
    @BusinessType,
    @BusinessLocation,
    @Industry,
    @HasRunPayrollThisYear,
    @SetupCompleted,
    @LogoDataUrl,
    @Pan,
    @Gstin,
    @FiscalYearStart,
    @AddressLine1,
    @AddressLine2,
    @City,
    @State,
    @PostalCode,
    @Country,
    @BankName,
    @AccountNumber,
    @IfscCode
);";

            await connection.ExecuteAsync(insertSql, organization);
            var insertId = await connection.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID();");
            return (int)insertId;
        }

        organization.Id = existing.Id;

        const string updateSql = @"
UPDATE Organizations SET
    Name = @Name,
    LegalName = @LegalName,
    BusinessType = @BusinessType,
    BusinessLocation = @BusinessLocation,
    Industry = @Industry,
    HasRunPayrollThisYear = @HasRunPayrollThisYear,
    SetupCompleted = @SetupCompleted,
    LogoDataUrl = @LogoDataUrl,
    PAN = @Pan,
    GSTIN = @Gstin,
    FiscalYearStart = @FiscalYearStart,
    AddressLine1 = @AddressLine1,
    AddressLine2 = @AddressLine2,
    City = @City,
    State = @State,
    PostalCode = @PostalCode,
    Country = @Country,
    BankName = @BankName,
    AccountNumber = @AccountNumber,
    IFSCCode = @IfscCode
WHERE Id = @Id;";

        await connection.ExecuteAsync(updateSql, organization);
        return existing.Id;
    }

    public async Task<string> GetSetupAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        return await PayrollDataTableStore.GetSetupJsonAsync(connection);
    }

    public async Task SaveSetupAsync(string setupJson)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        await PayrollDataTableStore.SaveSetupJsonAsync(connection, setupJson);
    }

    public async Task<IEnumerable<Client>> GetClientsAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        var clients = (await connection.QueryAsync<Client>("SELECT * FROM Clients ORDER BY Name")).ToList();
        await PayrollDataTableStore.ApplyClientPaySchedulesAsync(connection, clients);
        return clients;
    }

    public async Task<int> SaveClientAsync(Client client)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        if (client.Id == 0)
        {
            const string sql = "INSERT INTO Clients (Name, Code, ContactPerson, Email, Phone, Address, PayScheduleJson, IsActive) VALUES (@Name, @Code, @ContactPerson, @Email, @Phone, @Address, @PayScheduleJson, @IsActive); SELECT LAST_INSERT_ID();";
            client.Id = (int)await connection.ExecuteScalarAsync<long>(sql, client);
            await PayrollDataTableStore.SyncClientPayScheduleAsync(connection, client.Id, client.PayScheduleJson);
            return client.Id;
        }

        await connection.ExecuteAsync("UPDATE Clients SET Name=@Name, Code=@Code, ContactPerson=@ContactPerson, Email=@Email, Phone=@Phone, Address=@Address, PayScheduleJson=@PayScheduleJson, IsActive=@IsActive WHERE Id=@Id", client);
        await PayrollDataTableStore.SyncClientPayScheduleAsync(connection, client.Id, client.PayScheduleJson);
        return client.Id;
    }

    public async Task<IEnumerable<WorkLocation>> GetWorkLocationsAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        return await connection.QueryAsync<WorkLocation>("SELECT * FROM WorkLocations ORDER BY IsPrimary DESC, Name");
    }

    public async Task<int> SaveWorkLocationAsync(WorkLocation location)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        if (location.IsPrimary)
            await connection.ExecuteAsync("UPDATE WorkLocations SET IsPrimary = FALSE");
        if (location.Id == 0)
            return (int)await connection.ExecuteScalarAsync<long>("INSERT INTO WorkLocations (Name, Address, City, State, PostalCode, IsPrimary, IsActive) VALUES (@Name, @Address, @City, @State, @PostalCode, @IsPrimary, @IsActive); SELECT LAST_INSERT_ID();", location);
        await connection.ExecuteAsync("UPDATE WorkLocations SET Name=@Name, Address=@Address, City=@City, State=@State, PostalCode=@PostalCode, IsPrimary=@IsPrimary, IsActive=@IsActive WHERE Id=@Id", location);
        return location.Id;
    }

    public async Task<IEnumerable<DropdownMaster>> GetDropdownMastersAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        return await connection.QueryAsync<DropdownMaster>("SELECT * FROM DropdownMasters ORDER BY Type, Value");
    }

    public async Task<int> SaveDropdownMasterAsync(DropdownMaster item)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        if (item.Id == 0)
            return (int)await connection.ExecuteScalarAsync<long>("INSERT INTO DropdownMasters (Type, Value, IsActive) VALUES (@Type, @Value, @IsActive); SELECT LAST_INSERT_ID();", item);
        await connection.ExecuteAsync("UPDATE DropdownMasters SET Type=@Type, Value=@Value, IsActive=@IsActive WHERE Id=@Id", item);
        return item.Id;
    }

    public async Task<IEnumerable<Employee>> GetEmployeesAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        var employees = (await connection.QueryAsync<Employee>("SELECT * FROM Employees ORDER BY FirstName, LastName")).ToList();
        await PayrollDataTableStore.ApplyEmployeeTablesAsync(connection, employees);
        return employees;
    }

    public async Task<int> SaveEmployeeAsync(Employee employee)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        if (employee.Id == 0)
            employee.Id = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO Employees (ClientId, EmployeeCode, FirstName, LastName, Gender, DateOfJoining, WorkEmail, Department, Designation, WorkLocationId, ReportingManagerId, PortalAccess, SalaryStructureId, AnnualCtc, SalaryJson, PersonalJson, PaymentJson, IsActive) VALUES (@ClientId, @EmployeeCode, @FirstName, @LastName, @Gender, @DateOfJoining, @WorkEmail, @Department, @Designation, @WorkLocationId, @ReportingManagerId, @PortalAccess, @SalaryStructureId, @AnnualCtc, @SalaryJson, @PersonalJson, @PaymentJson, @IsActive); SELECT LAST_INSERT_ID();", employee);
        else
            await connection.ExecuteAsync(@"UPDATE Employees SET ClientId=@ClientId, EmployeeCode=@EmployeeCode, FirstName=@FirstName, LastName=@LastName, Gender=@Gender, DateOfJoining=@DateOfJoining, WorkEmail=@WorkEmail, Department=@Department, Designation=@Designation, WorkLocationId=@WorkLocationId, ReportingManagerId=@ReportingManagerId, PortalAccess=@PortalAccess, SalaryStructureId=@SalaryStructureId, AnnualCtc=@AnnualCtc, SalaryJson=@SalaryJson, PersonalJson=@PersonalJson, PaymentJson=@PaymentJson, IsActive=@IsActive WHERE Id=@Id", employee);
        await PayrollDataTableStore.SyncEmployeeTablesAsync(connection, employee);
        return employee.Id;
    }

    private static string DefaultSetupJson => """
{"salaryComponents":[{"id":101,"code":"BASIC","componentType":"Basic","category":"Earning","name":"Basic","payType":"Fixed Pay","calculationType":"Formula","value":"","formula":"GROSS * 50%","baseComponent":"GROSS","taxable":true,"ctc":true,"proRata":true,"fbp":false,"restrictFbp":false,"epf":"Always","esi":true,"recurring":true,"scheduled":false,"investmentType":"","correctionOf":"","active":true,"priority":"10"},{"id":102,"code":"HRA","componentType":"House Rent Allowance","category":"Earning","name":"HRA","payType":"Fixed Pay","calculationType":"Formula","value":"","formula":"BASIC * 40%","baseComponent":"BASIC","taxable":true,"ctc":true,"proRata":true,"fbp":false,"restrictFbp":false,"epf":"Never","esi":true,"recurring":true,"scheduled":false,"investmentType":"","correctionOf":"","active":true,"priority":"20"},{"id":103,"code":"TEL_ALLOW","componentType":"Telephone","category":"Earning","name":"Telephonic Allowance","payType":"Fixed Pay","calculationType":"Fixed Amount","value":"2000","formula":"","baseComponent":"","taxable":true,"ctc":true,"proRata":true,"fbp":false,"restrictFbp":false,"epf":"Never","esi":true,"recurring":true,"scheduled":false,"investmentType":"","correctionOf":"","active":true,"priority":"30"},{"id":104,"code":"STAT_BONUS","componentType":"Bonus","category":"Earning","name":"Statutory Bonus","payType":"Fixed Pay","calculationType":"Formula","value":"","formula":"ROUNDDOWN(BASIC * 8.33%)","baseComponent":"BASIC","taxable":true,"ctc":true,"proRata":true,"fbp":false,"restrictFbp":false,"epf":"Never","esi":true,"recurring":true,"scheduled":false,"investmentType":"","correctionOf":"","active":true,"priority":"40"},{"id":105,"code":"MED_ALLOW","componentType":"Medical Allowance","category":"Earning","name":"Medical Allowance","payType":"Fixed Pay","calculationType":"Fixed Amount","value":"1250","formula":"","baseComponent":"","taxable":true,"ctc":true,"proRata":true,"fbp":false,"restrictFbp":false,"epf":"Never","esi":true,"recurring":true,"scheduled":false,"investmentType":"","correctionOf":"","active":true,"priority":"50"},{"id":106,"code":"OTHER_ALLOW","componentType":"Custom Allowance","category":"Earning","name":"Other Allowance","payType":"Fixed Pay","calculationType":"Residual / Balancing","value":"","formula":"GROSS - SUM(Fixed Earnings)","baseComponent":"GROSS","taxable":true,"ctc":true,"proRata":true,"fbp":false,"restrictFbp":false,"epf":"Never","esi":true,"recurring":true,"scheduled":false,"investmentType":"","correctionOf":"","active":true,"priority":"60"},{"id":107,"code":"LAPTOP_ALLOW","componentType":"Custom Allowance","category":"Earning","name":"Laptop Allowance","payType":"Fixed Pay","calculationType":"Fixed Amount","value":"2000","formula":"","baseComponent":"","taxable":true,"ctc":true,"proRata":true,"fbp":false,"restrictFbp":false,"epf":"Never","esi":true,"recurring":true,"scheduled":false,"investmentType":"","correctionOf":"","active":true,"priority":"70"},{"id":108,"code":"TA_DA","componentType":"Custom Allowance","category":"Earning","name":"TA/DA","payType":"Variable Pay","calculationType":"Manual / Variable","value":"","formula":"","baseComponent":"","taxable":true,"ctc":false,"proRata":true,"fbp":false,"restrictFbp":false,"epf":"Never","esi":true,"recurring":false,"scheduled":true,"investmentType":"","correctionOf":"","active":true,"priority":"80"},{"id":109,"code":"PF","componentType":"Provident Fund","category":"Deduction","name":"Provident Fund","payType":"Fixed Pay","calculationType":"Formula","value":"","formula":"MIN(BASIC, 15000) * 12%","baseComponent":"BASIC","taxable":false,"ctc":true,"proRata":true,"fbp":false,"restrictFbp":false,"epf":"Never","esi":false,"recurring":true,"scheduled":false,"investmentType":"","correctionOf":"","active":true,"priority":"110"}],"salaryStructures":[{"id":201,"clientId":"1:Acme Technologies","name":"Acme Default CTC","annualCtc":"900000","lines":[{"componentId":"101","value":"GROSS * 50%"},{"componentId":"102","value":"BASIC * 40%"},{"componentId":"103","value":"2000"},{"componentId":"104","value":"ROUNDDOWN(BASIC * 8.33%)"},{"componentId":"105","value":"1250"},{"componentId":"106","value":"GROSS - SUM(Fixed Earnings)"},{"componentId":"107","value":"2000"},{"componentId":"108","value":""},{"componentId":"109","value":"MIN(BASIC,15000)*12%"}],"active":true}],"payslipTemplates":[{"id":301,"clientId":"1:Acme Technologies","name":"Acme Classic Payslip","theme":"Classic","showLogo":true,"showClient":true,"showYtd":true,"showBank":true,"note":"This is a system generated payslip.","active":true}],"tax":{"pan":"ABCDE1234F","tan":"ABCD12345E","aoCode":"BLR/W/123/1","frequency":"Monthly"},"schedule":{"workWeek":"Monday - Friday","salaryDays":"Actual days","fixedDays":"30","payDay":"Last working day","firstPayPeriod":"2026-06"},"statutory":{"epf":true,"epfNumber":"BG/BNG/1234567","epfCtc":true,"abry":false,"epfContribution":"Both Employee and Employer","restrictPf":true,"esi":false,"esiNumber":"","pt":true,"ptNumber":"PT-KA-12345","ptState":"Karnataka","ptCycle":"Monthly","ptSlabs":"Up to 15000: 0\n15001 and above: 200","lwf":true,"lwfState":"Karnataka","lwfCycle":"Half-yearly","lwfEligibilityLimit":"15000","lwfEmployeeContribution":"20","lwfEmployerContribution":"40"}}
""";
}

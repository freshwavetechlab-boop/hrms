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

    private static string QuoteIdentifier(string identifier) => $"`{identifier.Replace("`", "``")}`";

    public async Task InitializeAsync()
    {
        await EnsureConfiguredDatabaseExistsAsync();
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS organizations (
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
    ProfessionalTaxNumber VARCHAR(100),
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);" );

        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS payrollsetups (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    SetupJson JSON NOT NULL,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);" );

        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS clients (
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
CREATE TABLE IF NOT EXISTS worklocations (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL DEFAULT 0,
    ClientName VARCHAR(250),
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
CREATE TABLE IF NOT EXISTS dropdownmasters (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    Type VARCHAR(100) NOT NULL,
    Value VARCHAR(200) NOT NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_DropdownMasters_Type_Value (Type, Value)
);" );

        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS employees (
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

        await EnsureColumnAsync(connection, "BusinessLocation", "VARCHAR(100) NOT NULL DEFAULT 'India'");
        await EnsureColumnAsync(connection, "Industry", "VARCHAR(150) NULL");
        await EnsureColumnAsync(connection, "HasRunPayrollThisYear", "BOOLEAN NOT NULL DEFAULT FALSE");
        await EnsureColumnAsync(connection, "SetupCompleted", "BOOLEAN NOT NULL DEFAULT FALSE");
        await EnsureColumnAsync(connection, "LogoDataUrl", "LONGTEXT NULL");
        await EnsureColumnAsync(connection, "ProfessionalTaxNumber", "VARCHAR(100) NULL");
        await EnsureTableColumnAsync(connection, "clients", "PayScheduleJson", "JSON NULL");
        await EnsureTableColumnAsync(connection, "worklocations", "ClientId", "INT NOT NULL DEFAULT 0 AFTER Id");
        await EnsureTableColumnAsync(connection, "worklocations", "ClientName", "VARCHAR(250) NULL AFTER ClientId");
        await PayrollDataTableStore.EnsureAsync(connection);
        await SeedLocationDropdownMastersAsync(connection);
    }

    private static async Task EnsureColumnAsync(MySqlConnection connection, string columnName, string definition)
    {
        const string existsSql = @"
SELECT COUNT(*)
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'organizations'
  AND COLUMN_NAME = @ColumnName;";

        var exists = await connection.ExecuteScalarAsync<int>(existsSql, new { ColumnName = columnName });
        if (exists == 0)
        {
            await connection.ExecuteAsync($"ALTER TABLE organizations ADD COLUMN `{columnName}` {definition};");
        }
    }

    private static async Task EnsureTableColumnAsync(MySqlConnection connection, string tableName, string columnName, string definition)
    {
        var exists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName", new { TableName = tableName, ColumnName = columnName });
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

    private static Task PrepareDatabaseAsync(MySqlConnection connection) => Task.CompletedTask;

    public async Task<Organization?> GetAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);

        return await connection.QueryFirstOrDefaultAsync<Organization>("SELECT * FROM organizations ORDER BY Id LIMIT 1");
    }

    public async Task<int> SaveAsync(Organization organization)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);

        var existing = await connection.QueryFirstOrDefaultAsync<Organization>(
            "SELECT * FROM organizations ORDER BY Id LIMIT 1");
        if (existing is null)
        {
            const string insertSql = @"
INSERT INTO organizations (
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
    IFSCCode,
    ProfessionalTaxNumber
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
    @IfscCode,
    @ProfessionalTaxNumber
);";

            await connection.ExecuteAsync(insertSql, organization);
            var insertId = await connection.ExecuteScalarAsync<long>("SELECT LAST_INSERT_ID();");
            return (int)insertId;
        }

        organization.Id = existing.Id;

        const string updateSql = @"
UPDATE organizations SET
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
    IFSCCode = @IfscCode,
    ProfessionalTaxNumber = @ProfessionalTaxNumber
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
        var clients = (await connection.QueryAsync<Client>("SELECT * FROM clients ORDER BY Name")).ToList();
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
            const string sql = "INSERT INTO clients (Name, Code, ContactPerson, Email, Phone, Address, PayScheduleJson, IsActive) VALUES (@Name, @Code, @ContactPerson, @Email, @Phone, @Address, @PayScheduleJson, @IsActive); SELECT LAST_INSERT_ID();";
            client.Id = (int)await connection.ExecuteScalarAsync<long>(sql, client);
            await PayrollDataTableStore.SyncClientPayScheduleAsync(connection, client.Id, client.PayScheduleJson);
            return client.Id;
        }

        await connection.ExecuteAsync("UPDATE clients SET Name=@Name, Code=@Code, ContactPerson=@ContactPerson, Email=@Email, Phone=@Phone, Address=@Address, PayScheduleJson=@PayScheduleJson, IsActive=@IsActive WHERE Id=@Id", client);
        await PayrollDataTableStore.SyncClientPayScheduleAsync(connection, client.Id, client.PayScheduleJson);
        return client.Id;
    }

    public async Task<IEnumerable<WorkLocation>> GetWorkLocationsAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        return await connection.QueryAsync<WorkLocation>("SELECT * FROM worklocations ORDER BY ClientName, IsPrimary DESC, Name");
    }

    public async Task<int> SaveWorkLocationAsync(WorkLocation location)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        location.ClientName = await connection.ExecuteScalarAsync<string?>("SELECT Name FROM clients WHERE Id=@ClientId", new { location.ClientId }) ?? location.ClientName;
        if (location.IsPrimary)
            await connection.ExecuteAsync("UPDATE worklocations SET IsPrimary = FALSE WHERE ClientId=@ClientId", new { location.ClientId });
        if (location.Id == 0)
            return (int)await connection.ExecuteScalarAsync<long>("INSERT INTO worklocations (ClientId, ClientName, Name, Address, City, State, PostalCode, IsPrimary, IsActive) VALUES (@ClientId, @ClientName, @Name, @Address, @City, @State, @PostalCode, @IsPrimary, @IsActive); SELECT LAST_INSERT_ID();", location);
        await connection.ExecuteAsync("UPDATE worklocations SET ClientId=@ClientId, ClientName=@ClientName, Name=@Name, Address=@Address, City=@City, State=@State, PostalCode=@PostalCode, IsPrimary=@IsPrimary, IsActive=@IsActive WHERE Id=@Id", location);
        return location.Id;
    }

    public async Task<IEnumerable<DropdownMaster>> GetDropdownMastersAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        return await connection.QueryAsync<DropdownMaster>("SELECT * FROM dropdownmasters ORDER BY Type, Value");
    }

    public async Task<int> SaveDropdownMasterAsync(DropdownMaster item)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        if (item.Id == 0)
            return (int)await connection.ExecuteScalarAsync<long>("INSERT INTO dropdownmasters (Type, Value, IsActive) VALUES (@Type, @Value, @IsActive); SELECT LAST_INSERT_ID();", item);
        await connection.ExecuteAsync("UPDATE dropdownmasters SET Type=@Type, Value=@Value, IsActive=@IsActive WHERE Id=@Id", item);
        return item.Id;
    }

    public async Task<IEnumerable<Employee>> GetEmployeesAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        var employees = (await connection.QueryAsync<Employee>("SELECT * FROM employees ORDER BY FirstName, LastName")).ToList();
        await PayrollDataTableStore.ApplyEmployeeTablesAsync(connection, employees);
        return employees;
    }

    public async Task<int> SaveEmployeeAsync(Employee employee)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        if (employee.Id == 0)
            employee.Id = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO employees (ClientId, EmployeeCode, FirstName, LastName, Gender, DateOfJoining, WorkEmail, Department, Designation, WorkLocationId, ReportingManagerId, PortalAccess, SalaryStructureId, AnnualCtc, SalaryJson, PersonalJson, PaymentJson, IsActive) VALUES (@ClientId, @EmployeeCode, @FirstName, @LastName, @Gender, @DateOfJoining, @WorkEmail, @Department, @Designation, @WorkLocationId, @ReportingManagerId, @PortalAccess, @SalaryStructureId, @AnnualCtc, @SalaryJson, @PersonalJson, @PaymentJson, @IsActive); SELECT LAST_INSERT_ID();", employee);
        else
            await connection.ExecuteAsync(@"UPDATE employees SET ClientId=@ClientId, EmployeeCode=@EmployeeCode, FirstName=@FirstName, LastName=@LastName, Gender=@Gender, DateOfJoining=@DateOfJoining, WorkEmail=@WorkEmail, Department=@Department, Designation=@Designation, WorkLocationId=@WorkLocationId, ReportingManagerId=@ReportingManagerId, PortalAccess=@PortalAccess, SalaryStructureId=@SalaryStructureId, AnnualCtc=@AnnualCtc, SalaryJson=@SalaryJson, PersonalJson=@PersonalJson, PaymentJson=@PaymentJson, IsActive=@IsActive WHERE Id=@Id", employee);
        await PayrollDataTableStore.SyncEmployeeTablesAsync(connection, employee);
        return employee.Id;
    }

    private async Task EnsureConfiguredDatabaseExistsAsync()
    {
        var connectionString = _configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Default' is not configured.");
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = builder.Database;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException("Connection string 'Default' must specify a database.");
        }

        builder.Database = string.Empty;
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS {QuoteIdentifier(databaseName)};");
    }
}

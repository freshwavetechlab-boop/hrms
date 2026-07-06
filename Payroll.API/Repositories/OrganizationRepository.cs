using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Payroll.API.Repositories;

public class OrganizationRepository
{
    private static readonly ConcurrentDictionary<Guid, ClientImportJobStatus> ClientImportJobs = new();
    private static readonly ConcurrentDictionary<Guid, ClientImportJobStatus> WorkLocationImportJobs = new();
    private static readonly ConcurrentDictionary<Guid, ClientImportJobStatus> DropdownImportJobs = new();
    private static readonly ConcurrentDictionary<Guid, ClientImportJobStatus> SalaryComponentImportJobs = new();
    private static readonly ConcurrentDictionary<Guid, ClientImportJobStatus> SalaryTemplateImportJobs = new();
    private static readonly JsonSerializerOptions SetupJsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
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
    TanNumber VARCHAR(50),
    FiscalYearStart VARCHAR(50),
    AddressLine1 VARCHAR(255),
    AddressLine2 VARCHAR(255),
    RegisteredOfficeAddress TEXT,
    CorporateOfficeAddress TEXT,
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
    GSTIN VARCHAR(50),
    IsPrimary BOOLEAN NOT NULL DEFAULT FALSE,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);" );

        await connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS dropdownmasters (
    Id INT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL DEFAULT 0,
    Type VARCHAR(100) NOT NULL,
    Value VARCHAR(200) NOT NULL,
    ConfigJson JSON NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY UX_DropdownMasters_Client_Type_Value (ClientId, Type, Value)
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
    Grade VARCHAR(100),
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
        await EnsureColumnAsync(connection, "TanNumber", "VARCHAR(50) NULL AFTER GSTIN");
        await EnsureColumnAsync(connection, "RegisteredOfficeAddress", "TEXT NULL AFTER AddressLine2");
        await EnsureColumnAsync(connection, "CorporateOfficeAddress", "TEXT NULL AFTER RegisteredOfficeAddress");
        await EnsureTableColumnAsync(connection, "clients", "PayScheduleJson", "JSON NULL");
        await EnsureTableColumnAsync(connection, "worklocations", "ClientId", "INT NOT NULL DEFAULT 0 AFTER Id");
        await EnsureTableColumnAsync(connection, "worklocations", "ClientName", "VARCHAR(250) NULL AFTER ClientId");
        await EnsureTableColumnAsync(connection, "worklocations", "GSTIN", "VARCHAR(50) NULL AFTER PostalCode");
        await EnsureTableColumnAsync(connection, "employees", "Grade", "VARCHAR(100) NULL AFTER Designation");
        await EnsureTableColumnAsync(connection, "dropdownmasters", "ClientId", "INT NOT NULL DEFAULT 0 AFTER Id");
        await EnsureTableColumnAsync(connection, "dropdownmasters", "ConfigJson", "JSON NULL AFTER Value");
        await DropIndexIfExistsAsync(connection, "dropdownmasters", "UX_DropdownMasters_Type_Value");
        await EnsureIndexAsync(connection, "dropdownmasters", "UX_DropdownMasters_Client_Type_Value", "CREATE UNIQUE INDEX UX_DropdownMasters_Client_Type_Value ON dropdownmasters (ClientId, Type, Value)");
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

    private static async Task DropIndexIfExistsAsync(MySqlConnection connection, string tableName, string indexName)
    {
        var exists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName AND INDEX_NAME = @IndexName", new { TableName = tableName, IndexName = indexName });
        if (exists > 0) await connection.ExecuteAsync($"DROP INDEX `{indexName}` ON `{tableName}`;");
    }

    private static async Task EnsureIndexAsync(MySqlConnection connection, string tableName, string indexName, string createSql)
    {
        var exists = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName AND INDEX_NAME = @IndexName", new { TableName = tableName, IndexName = indexName });
        if (exists == 0) await connection.ExecuteAsync(createSql);
    }

    private static async Task SeedLocationDropdownMastersAsync(MySqlConnection connection)
    {
        string[] states =
        [
            "Andhra Pradesh", "Arunachal Pradesh", "Assam", "Bihar", "Chhattisgarh", "Goa", "Gujarat", "Haryana",
            "Himachal Pradesh", "Jharkhand", "Karnataka", "Kerala", "Madhya Pradesh", "Maharashtra", "Manipur",
            "Meghalaya", "Mizoram", "Nagaland", "Odisha", "Punjab", "Rajasthan", "Sikkim", "Tamil Nadu",
            "Telangana", "Tripura", "Uttar Pradesh", "Uttarakhand", "West Bengal", "Andaman and Nicobar Islands",
            "Chandigarh", "Dadra and Nagar Haveli and Daman and Diu", "Delhi", "Jammu and Kashmir", "Ladakh",
            "Lakshadweep", "Puducherry"
        ];
        foreach (var state in states)
            await connection.ExecuteAsync(@"INSERT INTO dropdownmasters (ClientId, Type, Value, IsActive)
SELECT 0, 'State', @State, TRUE WHERE NOT EXISTS (SELECT 1 FROM dropdownmasters WHERE ClientId=0 AND Type='State' AND Value=@State);", new { State = state });

        await connection.ExecuteAsync(@"
INSERT INTO dropdownmasters (ClientId, Type, Value, IsActive)
SELECT 0, 'State', State, TRUE FROM (
    SELECT DISTINCT TRIM(State) State FROM worklocations WHERE TRIM(COALESCE(State, '')) <> ''
    UNION SELECT DISTINCT TRIM(State) FROM organizations WHERE TRIM(COALESCE(State, '')) <> ''
) s WHERE NOT EXISTS (SELECT 1 FROM dropdownmasters d WHERE d.ClientId = 0 AND d.Type = 'State' AND d.Value = s.State);

INSERT INTO dropdownmasters (ClientId, Type, Value, IsActive)
SELECT 0, CONCAT('City:', State), City, TRUE FROM (
    SELECT DISTINCT TRIM(State) State, TRIM(City) City FROM worklocations WHERE TRIM(COALESCE(State, '')) <> '' AND TRIM(COALESCE(City, '')) <> ''
    UNION SELECT DISTINCT TRIM(State), TRIM(City) FROM organizations WHERE TRIM(COALESCE(State, '')) <> '' AND TRIM(COALESCE(City, '')) <> ''
) c WHERE NOT EXISTS (SELECT 1 FROM dropdownmasters d WHERE d.ClientId = 0 AND d.Type = CONCAT('City:', c.State) AND d.Value = c.City);");
    }

    private static async Task EnsureLocationDropdownMastersAsync(MySqlConnection connection, string? state, string? city, MySqlTransaction? transaction = null)
    {
        var cleanState = state?.Trim() ?? "";
        var cleanCity = city?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(cleanState)) return;

        await connection.ExecuteAsync(@"
INSERT INTO dropdownmasters (ClientId, Type, Value, IsActive)
VALUES (0, 'State', @State, TRUE)
ON DUPLICATE KEY UPDATE IsActive=TRUE;", new { State = cleanState }, transaction);

        if (string.IsNullOrWhiteSpace(cleanCity)) return;
        await connection.ExecuteAsync(@"
INSERT INTO dropdownmasters (ClientId, Type, Value, IsActive)
VALUES (0, @Type, @City, TRUE)
ON DUPLICATE KEY UPDATE IsActive=TRUE;", new { Type = $"City:{cleanState}", City = cleanCity }, transaction);
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
    TanNumber,
    FiscalYearStart,
    AddressLine1,
    AddressLine2,
    RegisteredOfficeAddress,
    CorporateOfficeAddress,
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
    @TanNumber,
    @FiscalYearStart,
    @AddressLine1,
    @AddressLine2,
    @RegisteredOfficeAddress,
    @CorporateOfficeAddress,
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
    TanNumber = @TanNumber,
    FiscalYearStart = @FiscalYearStart,
    AddressLine1 = @AddressLine1,
    AddressLine2 = @AddressLine2,
    RegisteredOfficeAddress = @RegisteredOfficeAddress,
    CorporateOfficeAddress = @CorporateOfficeAddress,
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
        await connection.ExecuteAsync("UPDATE worklocations SET ClientName=@Name WHERE ClientId=@Id", client);
        await PayrollDataTableStore.SyncClientPayScheduleAsync(connection, client.Id, client.PayScheduleJson);
        return client.Id;
    }

    public Task<byte[]> BuildClientImportTemplateAsync()
    {
        var headers = new[] { "Client Name", "Code", "Contact Person", "Email", "Phone", "Address", "Active" };
        var example = new[] { "Acme Services Pvt Ltd", "ACME", "Priya Sharma", "priya@example.com", "9876543210", "Registered office address", "TRUE" };
        var instructions = new List<string[]>
        {
            new[] { "Field", "Required", "Notes" },
            new[] { "Client Name", "Yes", "Unique client display name. Existing names will be updated." },
            new[] { "Code", "No", "Used to update an existing client when matched." },
            new[] { "Contact Person", "No", "Primary client contact." },
            new[] { "Email", "No", "Must be a valid email when filled." },
            new[] { "Phone", "No", "Primary phone number." },
            new[] { "Address", "No", "Client address." },
            new[] { "Active", "No", "TRUE/FALSE. Blank means TRUE." }
        };
        return Task.FromResult(BuildXlsx(("Clients", new[] { headers, example }), ("Instructions", instructions)));
    }

    public async Task<ClientImportJobStatus> StartClientImportJobAsync(IFormFile file)
    {
        var rows = await ParseImportFileAsync(file);
        var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
        var job = new ClientImportJobStatus(Guid.NewGuid(), "Queued", totalRows, 0, 0, 0, []);
        ClientImportJobs[job.JobId] = job;
        _ = Task.Run(async () =>
        {
            SetClientJob(job.JobId, current => current with { State = "Processing" });
            try
            {
                var result = await ImportClientRowsAsync(rows, (completed, inserted, updated) => SetClientJob(job.JobId, current => current with { CompletedRows = completed, Inserted = inserted, Updated = updated }));
                SetClientJob(job.JobId, current => current with { State = result.Errors.Count > 0 ? "Failed" : "Completed", TotalRows = result.TotalRows, CompletedRows = result.TotalRows, Inserted = result.Inserted, Updated = result.Updated, Errors = result.Errors });
            }
            catch (Exception ex)
            {
                SetClientJob(job.JobId, current => current with { State = "Failed", Errors = [$"Import failed: {ex.Message}"] });
            }
        });
        return job;
    }

    public ClientImportJobStatus? GetClientImportJob(Guid jobId) => ClientImportJobs.TryGetValue(jobId, out var job) ? job : null;

    private async Task<ClientImportResult> ImportClientRowsAsync(List<List<string>> rows, Action<int, int, int>? progress = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        await using var tx = await connection.BeginTransactionAsync();
        try
        {
            var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
            if (rows.Count < 2 || totalRows == 0)
                return new ClientImportResult(0, 0, 0, ["Import file has no data rows."]);

            var header = rows[0].Select(Norm).ToList();
            var inserted = 0;
            var updated = 0;
            var completed = 0;
            var errors = new List<string>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.All(string.IsNullOrWhiteSpace)) continue;

                string V(string name)
                {
                    var ix = header.IndexOf(Norm(name));
                    return ix >= 0 && ix < row.Count ? row[ix].Trim() : "";
                }

                var rowNumber = i + 1;
                var rowErrors = new List<string>();
                var name = V("Client Name");
                var code = V("Code");
                var contact = V("Contact Person");
                var email = V("Email");
                var phone = V("Phone");
                var address = V("Address");
                var active = ParseClientActive(V("Active"));

                if (string.IsNullOrWhiteSpace(name)) rowErrors.Add($"Row {rowNumber}: Client Name is required.");
                if (!string.IsNullOrWhiteSpace(name) && !seenNames.Add(name)) rowErrors.Add($"Row {rowNumber}: Client Name \"{name}\" is repeated in the file.");
                if (!string.IsNullOrWhiteSpace(code) && !seenCodes.Add(code)) rowErrors.Add($"Row {rowNumber}: Code \"{code}\" is repeated in the file.");
                if (!string.IsNullOrWhiteSpace(email) && (!email.Contains('@') || !email.Contains('.'))) rowErrors.Add($"Row {rowNumber}: Email is invalid.");
                ValidateLength(name, "Client Name", 250, rowNumber, rowErrors);
                ValidateLength(code, "Code", 50, rowNumber, rowErrors);
                ValidateLength(contact, "Contact Person", 150, rowNumber, rowErrors);
                ValidateLength(email, "Email", 150, rowNumber, rowErrors);
                ValidateLength(phone, "Phone", 50, rowNumber, rowErrors);
                ValidateLength(address, "Address", 500, rowNumber, rowErrors);

                Client? existingByCode = null;
                if (!string.IsNullOrWhiteSpace(code))
                    existingByCode = await connection.QueryFirstOrDefaultAsync<Client>("SELECT * FROM clients WHERE Code=@code LIMIT 1", new { code }, tx);
                var existingByName = !string.IsNullOrWhiteSpace(name)
                    ? await connection.QueryFirstOrDefaultAsync<Client>("SELECT * FROM clients WHERE Name=@name LIMIT 1", new { name }, tx)
                    : null;
                if (existingByCode is not null && existingByName is not null && existingByCode.Id != existingByName.Id)
                    rowErrors.Add($"Row {rowNumber}: Code \"{code}\" belongs to another client.");

                if (rowErrors.Count > 0)
                {
                    errors.AddRange(rowErrors);
                    completed++;
                    progress?.Invoke(completed, inserted, updated);
                    continue;
                }

                var existing = existingByCode ?? existingByName;
                var args = new
                {
                    Id = existing?.Id ?? 0,
                    Name = name,
                    Code = code,
                    ContactPerson = contact,
                    Email = email,
                    Phone = phone,
                    Address = address,
                    PayScheduleJson = string.IsNullOrWhiteSpace(existing?.PayScheduleJson) ? "{}" : existing.PayScheduleJson,
                    IsActive = active
                };

                if (existing is null)
                {
                    await connection.ExecuteScalarAsync<long>("INSERT INTO clients (Name, Code, ContactPerson, Email, Phone, Address, PayScheduleJson, IsActive) VALUES (@Name, @Code, @ContactPerson, @Email, @Phone, @Address, @PayScheduleJson, @IsActive); SELECT LAST_INSERT_ID();", args, tx);
                    inserted++;
                }
                else
                {
                    await connection.ExecuteAsync("UPDATE clients SET Name=@Name, Code=@Code, ContactPerson=@ContactPerson, Email=@Email, Phone=@Phone, Address=@Address, IsActive=@IsActive WHERE Id=@Id", args, tx);
                    await connection.ExecuteAsync("UPDATE worklocations SET ClientName=@Name WHERE ClientId=@Id", args, tx);
                    updated++;
                }

                completed++;
                progress?.Invoke(completed, inserted, updated);
            }

            if (errors.Count > 0)
            {
                await tx.RollbackAsync();
                return new ClientImportResult(totalRows, 0, 0, errors);
            }

            await tx.CommitAsync();
            return new ClientImportResult(totalRows, inserted, updated, []);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return new ClientImportResult(0, 0, 0, [$"Import failed: {ex.Message}"]);
        }
    }

    public async Task<IEnumerable<WorkLocation>> GetWorkLocationsAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        return await connection.QueryAsync<WorkLocation>(@"SELECT
    w.Id,
    w.ClientId,
    COALESCE(c.Name, w.ClientName, '') AS ClientName,
    w.Name,
    w.Address,
    w.City,
    w.State,
    w.PostalCode,
    w.GSTIN AS Gstin,
    w.IsPrimary,
    w.IsActive
FROM worklocations w
LEFT JOIN clients c ON c.Id = w.ClientId
ORDER BY COALESCE(c.Name, w.ClientName, ''), w.IsPrimary DESC, w.Name");
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
        {
            var id = (int)await connection.ExecuteScalarAsync<long>("INSERT INTO worklocations (ClientId, ClientName, Name, Address, City, State, PostalCode, GSTIN, IsPrimary, IsActive) VALUES (@ClientId, @ClientName, @Name, @Address, @City, @State, @PostalCode, @Gstin, @IsPrimary, @IsActive); SELECT LAST_INSERT_ID();", location);
            await EnsureLocationDropdownMastersAsync(connection, location.State, location.City);
            return id;
        }
        await connection.ExecuteAsync("UPDATE worklocations SET ClientId=@ClientId, ClientName=@ClientName, Name=@Name, Address=@Address, City=@City, State=@State, PostalCode=@PostalCode, GSTIN=@Gstin, IsPrimary=@IsPrimary, IsActive=@IsActive WHERE Id=@Id", location);
        await EnsureLocationDropdownMastersAsync(connection, location.State, location.City);
        return location.Id;
    }

    public async Task<byte[]> BuildWorkLocationImportTemplateAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        var clients = (await connection.QueryAsync<(int Id, string Name, string Code)>("SELECT Id, Name, COALESCE(Code, '') Code FROM clients WHERE IsActive=TRUE ORDER BY Name")).ToList();
        var headers = new[] { "Client Id", "Location Name", "Address", "State", "City", "PIN", "GST Number", "Primary", "Active" };
        var exampleClient = clients.FirstOrDefault();
        var example = new[] { exampleClient.Id > 0 ? exampleClient.Id.ToString() : "", "Head Office", "Registered office address", "Delhi", "New Delhi", "110001", "", "TRUE", "TRUE" };
        var clientSheet = new List<string[]> { new[] { "Client Id", "Client Name", "Client Code" } };
        clientSheet.AddRange(clients.Select(client => new[] { client.Id.ToString(), client.Name, client.Code }));
        return BuildXlsx(("Work Locations", new[] { headers, example }), ("Clients", clientSheet));
    }

    public async Task<ClientImportJobStatus> StartWorkLocationImportJobAsync(IFormFile file)
    {
        var rows = await ParseImportFileAsync(file);
        var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
        var job = new ClientImportJobStatus(Guid.NewGuid(), "Queued", totalRows, 0, 0, 0, []);
        WorkLocationImportJobs[job.JobId] = job;
        _ = Task.Run(async () =>
        {
            SetImportJob(WorkLocationImportJobs, job.JobId, current => current with { State = "Processing" });
            try
            {
                var result = await ImportWorkLocationRowsAsync(rows, (completed, inserted, updated) => SetImportJob(WorkLocationImportJobs, job.JobId, current => current with { CompletedRows = completed, Inserted = inserted, Updated = updated }));
                SetImportJob(WorkLocationImportJobs, job.JobId, current => current with { State = result.Errors.Count > 0 ? "Failed" : "Completed", TotalRows = result.TotalRows, CompletedRows = result.TotalRows, Inserted = result.Inserted, Updated = result.Updated, Errors = result.Errors });
            }
            catch (Exception ex)
            {
                SetImportJob(WorkLocationImportJobs, job.JobId, current => current with { State = "Failed", Errors = [$"Import failed: {ex.Message}"] });
            }
        });
        return job;
    }

    public ClientImportJobStatus? GetWorkLocationImportJob(Guid jobId) => WorkLocationImportJobs.TryGetValue(jobId, out var job) ? job : null;

    private async Task<ClientImportResult> ImportWorkLocationRowsAsync(List<List<string>> rows, Action<int, int, int>? progress = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        await using var tx = await connection.BeginTransactionAsync();
        try
        {
            var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
            if (rows.Count < 2 || totalRows == 0)
                return new ClientImportResult(0, 0, 0, ["Import file has no data rows."]);

            var header = rows[0].Select(Norm).ToList();
            var clients = (await connection.QueryAsync<(int Id, string Name)>("SELECT Id, Name FROM clients WHERE IsActive=TRUE", transaction: tx)).ToDictionary(client => client.Id, client => client.Name);
            var inserted = 0;
            var updated = 0;
            var completed = 0;
            var errors = new List<string>();
            var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.All(string.IsNullOrWhiteSpace)) continue;

                string V(string name)
                {
                    var ix = header.IndexOf(Norm(name));
                    return ix >= 0 && ix < row.Count ? row[ix].Trim() : "";
                }

                var rowNumber = i + 1;
                var rowErrors = new List<string>();
                var clientText = V("Client Id");
                var name = V("Location Name");
                var address = V("Address");
                var state = V("State");
                var city = V("City");
                var postalCode = V("PIN");
                var gstin = V("GST Number").ToUpperInvariant();
                var primaryText = V("Primary");
                var activeText = V("Active");
                var clientId = int.TryParse(clientText, out var parsedClientId) ? parsedClientId : 0;
                var isPrimary = ParseImportFlag(primaryText, false);
                var isActive = ParseImportFlag(activeText, true);

                if (clientId <= 0) rowErrors.Add($"Row {rowNumber}: Client Id is required.");
                else if (!clients.ContainsKey(clientId)) rowErrors.Add($"Row {rowNumber}: Client Id {clientId} was not found.");
                if (string.IsNullOrWhiteSpace(name)) rowErrors.Add($"Row {rowNumber}: Location Name is required.");
                if (!string.IsNullOrWhiteSpace(name) && clientId > 0 && !seenLocations.Add($"{clientId}:{name}")) rowErrors.Add($"Row {rowNumber}: Location Name \"{name}\" is repeated for Client Id {clientId}.");
                if (!string.IsNullOrWhiteSpace(postalCode) && !System.Text.RegularExpressions.Regex.IsMatch(postalCode, @"^[1-9][0-9]{5}$")) rowErrors.Add($"Row {rowNumber}: PIN must be a valid 6-digit PIN code.");
                if (!IsImportFlag(primaryText)) rowErrors.Add($"Row {rowNumber}: Primary must be TRUE/FALSE.");
                if (!IsImportFlag(activeText)) rowErrors.Add($"Row {rowNumber}: Active must be TRUE/FALSE.");
                ValidateLength(name, "Location Name", 200, rowNumber, rowErrors);
                ValidateLength(address, "Address", 500, rowNumber, rowErrors);
                ValidateLength(city, "City", 100, rowNumber, rowErrors);
                ValidateLength(state, "State", 100, rowNumber, rowErrors);
                ValidateLength(postalCode, "PIN", 30, rowNumber, rowErrors);
                ValidateLength(gstin, "GST Number", 50, rowNumber, rowErrors);

                if (rowErrors.Count > 0)
                {
                    errors.AddRange(rowErrors);
                    completed++;
                    progress?.Invoke(completed, inserted, updated);
                    continue;
                }

                var clientName = clients[clientId];
                var args = new { ClientId = clientId, ClientName = clientName, Name = name, Address = address, City = city, State = state, PostalCode = postalCode, Gstin = gstin, IsPrimary = isPrimary, IsActive = isActive };
                var existingId = await connection.ExecuteScalarAsync<int?>("SELECT Id FROM worklocations WHERE ClientId=@ClientId AND Name=@Name LIMIT 1", args, tx);

                if (isPrimary)
                    await connection.ExecuteAsync("UPDATE worklocations SET IsPrimary=FALSE WHERE ClientId=@ClientId", args, tx);

                if (existingId is null)
                {
                    await connection.ExecuteScalarAsync<long>("INSERT INTO worklocations (ClientId, ClientName, Name, Address, City, State, PostalCode, GSTIN, IsPrimary, IsActive) VALUES (@ClientId, @ClientName, @Name, @Address, @City, @State, @PostalCode, @Gstin, @IsPrimary, @IsActive); SELECT LAST_INSERT_ID();", args, tx);
                    inserted++;
                }
                else
                {
                    await connection.ExecuteAsync("UPDATE worklocations SET ClientName=@ClientName, Address=@Address, City=@City, State=@State, PostalCode=@PostalCode, GSTIN=@Gstin, IsPrimary=@IsPrimary, IsActive=@IsActive WHERE Id=@Id", new { Id = existingId.Value, args.ClientName, args.Address, args.City, args.State, args.PostalCode, args.Gstin, args.IsPrimary, args.IsActive }, tx);
                    updated++;
                }

                await EnsureLocationDropdownMastersAsync(connection, state, city, tx);

                completed++;
                progress?.Invoke(completed, inserted, updated);
            }

            if (errors.Count > 0)
            {
                await tx.RollbackAsync();
                return new ClientImportResult(totalRows, 0, 0, errors);
            }

            await tx.CommitAsync();
            return new ClientImportResult(totalRows, inserted, updated, []);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return new ClientImportResult(0, 0, 0, [$"Import failed: {ex.Message}"]);
        }
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
            return (int)await connection.ExecuteScalarAsync<long>("INSERT INTO dropdownmasters (ClientId, Type, Value, ConfigJson, IsActive) VALUES (@ClientId, @Type, @Value, NULLIF(@ConfigJson, ''), @IsActive); SELECT LAST_INSERT_ID();", item);
        await connection.ExecuteAsync("UPDATE dropdownmasters SET ClientId=@ClientId, Type=@Type, Value=@Value, ConfigJson=NULLIF(@ConfigJson, ''), IsActive=@IsActive WHERE Id=@Id", item);
        return item.Id;
    }

    public async Task<byte[]> BuildDropdownImportTemplateAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        var clients = (await connection.QueryAsync<(int Id, string Name, string Code)>("SELECT Id, Name, COALESCE(Code, '') Code FROM clients WHERE IsActive=TRUE ORDER BY Name")).ToList();
        var firstClientId = clients.FirstOrDefault().Id > 0 ? clients.First().Id.ToString(CultureInfo.InvariantCulture) : "1";
        var reference = new List<string[]>
        {
            new[] { "Clients", "", "", "" },
            new[] { "Client Id", "Client Name", "Client Code", "" }
        };
        reference.AddRange(clients.Select(client => new[] { client.Id.ToString(), client.Name, client.Code, "" }));
        reference.Add([]);
        reference.Add(new[] { "Sheet", "Required Columns", "Example Row", "Notes" });
        reference.AddRange(DropdownImportTypes.Select(type => new[] { type, string.Join(", ", DropdownSheetHeaders(type)), string.Join(" | ", DropdownSheetExample(type, firstClientId)), type == "Employee Grade" ? "Client Id must match Clients sheet." : type == "City" ? "State must be filled." : "Value is the dropdown text." }));
        reference.Add([]);
        reference.Add(new[] { "Work Week Examples", "", "", "" });
        reference.Add(new[] { "Pattern", "Working Days", "Off Saturdays", "Result" });
        reference.Add(new[] { "Monday - Friday", "Mon, Tue, Wed, Thu, Fri", "", "Saturday and Sunday off" });
        reference.Add(new[] { "Monday - Saturday", "Mon, Tue, Wed, Thu, Fri, Sat", "", "Only Sunday off" });
        reference.Add(new[] { "2nd/4th Saturday off", "Mon, Tue, Wed, Thu, Fri, Sat", "2nd, 4th", "Sunday + selected Saturdays off" });
        reference.Add(new[] { "All Saturdays off", "Mon, Tue, Wed, Thu, Fri", "", "Do not include Sat in Working Days" });
        var sheets = DropdownImportTypes.Select(type => (Name: type, Rows: (IEnumerable<string[]>)new[] { DropdownSheetHeaders(type), DropdownSheetExample(type, firstClientId) })).ToList();
        sheets.Add((Name: "Reference", Rows: reference));
        return BuildXlsx(sheets.ToArray());
    }

    public async Task<ClientImportJobStatus> StartDropdownImportJobAsync(IFormFile file)
    {
        var rows = await ParseDropdownImportFileAsync(file);
        var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
        var job = new ClientImportJobStatus(Guid.NewGuid(), "Queued", totalRows, 0, 0, 0, []);
        DropdownImportJobs[job.JobId] = job;
        _ = Task.Run(async () =>
        {
            SetImportJob(DropdownImportJobs, job.JobId, current => current with { State = "Processing" });
            try
            {
                var result = await ImportDropdownRowsAsync(rows, (completed, inserted, updated) => SetImportJob(DropdownImportJobs, job.JobId, current => current with { CompletedRows = completed, Inserted = inserted, Updated = updated }));
                SetImportJob(DropdownImportJobs, job.JobId, current => current with { State = result.Errors.Count > 0 ? "Failed" : "Completed", TotalRows = result.TotalRows, CompletedRows = result.TotalRows, Inserted = result.Inserted, Updated = result.Updated, Errors = result.Errors });
            }
            catch (Exception ex)
            {
                SetImportJob(DropdownImportJobs, job.JobId, current => current with { State = "Failed", Errors = [$"Import failed: {ex.Message}"] });
            }
        });
        return job;
    }

    public ClientImportJobStatus? GetDropdownImportJob(Guid jobId) => DropdownImportJobs.TryGetValue(jobId, out var job) ? job : null;

    private async Task<ClientImportResult> ImportDropdownRowsAsync(List<List<string>> rows, Action<int, int, int>? progress = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        await using var tx = await connection.BeginTransactionAsync();
        try
        {
            var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
            if (rows.Count < 2 || totalRows == 0)
                return new ClientImportResult(0, 0, 0, ["Import file has no data rows."]);

            var header = rows[0].Select(Norm).ToList();
            var clients = (await connection.QueryAsync<int>("SELECT Id FROM clients WHERE IsActive=TRUE", transaction: tx)).ToHashSet();
            var inserted = 0;
            var updated = 0;
            var completed = 0;
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.All(string.IsNullOrWhiteSpace)) continue;

                string V(string name)
                {
                    var ix = header.IndexOf(Norm(name));
                    return ix >= 0 && ix < row.Count ? row[ix].Trim() : "";
                }

                var rowNumber = i + 1;
                var rowErrors = new List<string>();
                var type = V("Master Type");
                var value = V("Value");
                var state = V("State");
                var activeText = V("Active");
                var configJson = V("Config Json");
                var clientId = int.TryParse(V("Client Id"), out var parsedClientId) ? parsedClientId : 0;
                var normalizedType = NormalizeDropdownImportType(type);
                var actualType = normalizedType == "City" ? state.Trim() == "" ? "" : $"City:{state.Trim()}" : normalizedType;
                var actualClientId = normalizedType == "Employee Grade" ? clientId : 0;
                var isActive = ParseImportFlag(activeText, true);

                if (string.IsNullOrWhiteSpace(normalizedType)) rowErrors.Add($"Row {rowNumber}: Master Type is required.");
                else if (!DropdownImportTypes.Contains(normalizedType)) rowErrors.Add($"Row {rowNumber}: Master Type \"{type}\" is invalid.");
                if (string.IsNullOrWhiteSpace(value)) rowErrors.Add($"Row {rowNumber}: Value is required.");
                if (normalizedType == "City" && string.IsNullOrWhiteSpace(state)) rowErrors.Add($"Row {rowNumber}: State is required for City.");
                if (normalizedType == "Employee Grade")
                {
                    if (clientId <= 0) rowErrors.Add($"Row {rowNumber}: Client Id is required for Employee Grade.");
                    else if (!clients.Contains(clientId)) rowErrors.Add($"Row {rowNumber}: Client Id {clientId} was not found.");
                }
                if (!IsImportFlag(activeText)) rowErrors.Add($"Row {rowNumber}: Active must be TRUE/FALSE.");
                ValidateDropdownConfigJson(configJson, normalizedType, rowNumber, rowErrors);
                ValidateLength(normalizedType == "City" ? actualType : normalizedType, "Master Type", 100, rowNumber, rowErrors);
                ValidateLength(value, "Value", 200, rowNumber, rowErrors);
                if (!seen.Add($"{actualClientId}:{actualType}:{value}")) rowErrors.Add($"Row {rowNumber}: {type} \"{value}\" is repeated in the file.");

                if (rowErrors.Count > 0)
                {
                    errors.AddRange(rowErrors);
                    completed++;
                    progress?.Invoke(completed, inserted, updated);
                    continue;
                }

                var args = new { ClientId = actualClientId, Type = actualType, Value = value, ConfigJson = configJson, IsActive = isActive };
                var existingId = await connection.ExecuteScalarAsync<int?>("SELECT Id FROM dropdownmasters WHERE ClientId=@ClientId AND Type=@Type AND Value=@Value LIMIT 1", args, tx);
                if (existingId is null)
                {
                    await connection.ExecuteScalarAsync<long>("INSERT INTO dropdownmasters (ClientId, Type, Value, ConfigJson, IsActive) VALUES (@ClientId, @Type, @Value, NULLIF(@ConfigJson, ''), @IsActive); SELECT LAST_INSERT_ID();", args, tx);
                    inserted++;
                }
                else
                {
                    await connection.ExecuteAsync("UPDATE dropdownmasters SET ConfigJson=NULLIF(@ConfigJson, ''), IsActive=@IsActive WHERE Id=@Id", new { Id = existingId.Value, args.ConfigJson, args.IsActive }, tx);
                    updated++;
                }

                completed++;
                progress?.Invoke(completed, inserted, updated);
            }

            if (errors.Count > 0)
            {
                await tx.RollbackAsync();
                return new ClientImportResult(totalRows, 0, 0, errors);
            }

            await tx.CommitAsync();
            return new ClientImportResult(totalRows, inserted, updated, []);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return new ClientImportResult(0, 0, 0, [$"Import failed: {ex.Message}"]);
        }
    }

    public async Task<byte[]> BuildSalaryComponentImportTemplateAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        var setup = ParseJsonObject(await PayrollDataTableStore.GetSetupJsonAsync(connection));
        var components = setup["salaryComponents"] as JsonArray ?? [];
        var componentRows = new List<string[]> { SalaryComponentImportHeaders };
        componentRows.AddRange(components.OfType<JsonObject>().Select(component => new[]
        {
            JsonText(component, "code"),
            JsonText(component, "category", "Earning"),
            JsonText(component, "name"),
            JsonText(component, "componentType"),
            JsonText(component, "payType", "Fixed Pay"),
            JsonText(component, "calculationType", "Fixed Amount"),
            JsonText(component, "value"),
            JsonText(component, "formula"),
            JsonText(component, "baseComponent"),
            BoolText(JsonBool(component, "taxable", true)),
            BoolText(JsonBool(component, "ctc", true)),
            BoolText(JsonBool(component, "proRata", true)),
            BoolText(JsonBool(component, "fbp", false)),
            BoolText(JsonBool(component, "restrictFbp", false)),
            JsonText(component, "epf", "Never"),
            BoolText(JsonBool(component, "esi", false)),
            BoolText(JsonBool(component, "recurring", true)),
            BoolText(JsonBool(component, "scheduled", false)),
            JsonText(component, "investmentType"),
            JsonText(component, "correctionOf"),
            JsonText(component, "priority", "999"),
            BoolText(JsonBool(component, "active", true))
        }));
        if (componentRows.Count == 1)
            componentRows.Add(new[] { "BASIC", "Earning", "Basic Salary", "Basic", "Fixed Pay", "Fixed Amount", "0", "", "", "TRUE", "TRUE", "TRUE", "FALSE", "FALSE", "Never", "FALSE", "TRUE", "FALSE", "", "", "100", "TRUE" });

        var reference = new List<string[]>
        {
            new[] { "Categories", string.Join(", ", SalaryComponentCategories), "" },
            new[] { "Calculation Types", string.Join(", ", SalaryCalculationTypes), "" },
            new[] { "Pay Types", string.Join(", ", SalaryPayTypes), "" },
            new[] { "EPF Options", string.Join(", ", SalaryEpfOptions), "" },
            new[] { "", "", "" },
            new[] { "Existing Code", "Name", "Category" }
        };
        reference.AddRange(components.OfType<JsonObject>().Select(component => new[] { JsonText(component, "code"), JsonText(component, "name"), JsonText(component, "category") }));
        return BuildXlsx(("Salary Components", componentRows), ("Reference", reference));
    }

    public async Task<ClientImportJobStatus> StartSalaryComponentImportJobAsync(IFormFile file)
    {
        var rows = await ParseImportFileAsync(file);
        var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
        var job = new ClientImportJobStatus(Guid.NewGuid(), "Queued", totalRows, 0, 0, 0, []);
        SalaryComponentImportJobs[job.JobId] = job;
        _ = Task.Run(async () =>
        {
            SetImportJob(SalaryComponentImportJobs, job.JobId, current => current with { State = "Processing" });
            try
            {
                var result = await ImportSalaryComponentRowsAsync(rows, (completed, inserted, updated) => SetImportJob(SalaryComponentImportJobs, job.JobId, current => current with { CompletedRows = completed, Inserted = inserted, Updated = updated }));
                SetImportJob(SalaryComponentImportJobs, job.JobId, current => current with { State = result.Errors.Count > 0 ? "Failed" : "Completed", TotalRows = result.TotalRows, CompletedRows = result.TotalRows, Inserted = result.Inserted, Updated = result.Updated, Errors = result.Errors });
            }
            catch (Exception ex)
            {
                SetImportJob(SalaryComponentImportJobs, job.JobId, current => current with { State = "Failed", Errors = [$"Import failed: {ex.Message}"] });
            }
        });
        return job;
    }

    public ClientImportJobStatus? GetSalaryComponentImportJob(Guid jobId) => SalaryComponentImportJobs.TryGetValue(jobId, out var job) ? job : null;

    private async Task<ClientImportResult> ImportSalaryComponentRowsAsync(List<List<string>> rows, Action<int, int, int>? progress = null)
    {
        try
        {
            var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
            if (rows.Count < 2 || totalRows == 0)
                return new ClientImportResult(0, 0, 0, ["Import file has no data rows."]);

            var header = rows[0].Select(Norm).ToList();
            var importedCodes = rows.Skip(1)
                .Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value)))
                .Select(row =>
                {
                    var ix = header.IndexOf(Norm("Code"));
                    return ix >= 0 && ix < row.Count ? row[ix].Trim().ToUpperInvariant() : "";
                })
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await PrepareDatabaseAsync(connection);
            var setup = ParseJsonObject(await PayrollDataTableStore.GetSetupJsonAsync(connection));
            var componentArray = setup["salaryComponents"] as JsonArray ?? [];
            var components = componentArray.OfType<JsonObject>().Select(component => (JsonObject)component.DeepClone()).ToList();
            var byCode = components.Where(component => !string.IsNullOrWhiteSpace(JsonText(component, "code"))).GroupBy(component => JsonText(component, "code").ToUpperInvariant()).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var validFormulaCodes = byCode.Keys.Concat(importedCodes).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nextId = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), components.Select(component => JsonLong(component, "id", 0)).DefaultIfEmpty(0).Max() + 1);
            var inserted = 0;
            var updated = 0;
            var completed = 0;
            var errors = new List<string>();
            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.All(string.IsNullOrWhiteSpace)) continue;

                string V(string name)
                {
                    var ix = header.IndexOf(Norm(name));
                    return ix >= 0 && ix < row.Count ? row[ix].Trim() : "";
                }

                var rowNumber = i + 1;
                var rowErrors = new List<string>();
                var code = V("Code").ToUpperInvariant();
                var categoryText = V("Category");
                var category = NormalizeSalaryComponentCategory(categoryText);
                var name = V("Name");
                var componentType = V("Component Type");
                var payTypeText = V("Pay Type");
                var calculationTypeText = V("Calculation Type");
                var calculationType = NormalizeSalaryCalculationType(calculationTypeText);
                var value = V("Value");
                var formula = V("Formula");
                var baseComponent = V("Base Component").ToUpperInvariant();
                var epfText = V("EPF");
                var epf = NormalizeSalaryEpf(epfText);
                var investmentType = V("Investment Type");
                var correctionOf = V("Correction Of").ToUpperInvariant();
                var priorityText = V("Priority");
                var activeText = V("Active");
                var existing = !string.IsNullOrWhiteSpace(code) && byCode.TryGetValue(code, out var found) ? found : null;
                var payType = string.IsNullOrWhiteSpace(payTypeText) ? JsonText(existing, "payType", calculationType == "Manual / Variable" ? "Variable Pay" : "Fixed Pay") : NormalizeSalaryPayType(payTypeText);
                var priority = int.TryParse(priorityText, out var parsedPriority) ? parsedPriority : JsonInt(existing, "priority", 999);

                if (string.IsNullOrWhiteSpace(code)) rowErrors.Add($"Row {rowNumber}: Code is required.");
                else if (!System.Text.RegularExpressions.Regex.IsMatch(code, @"^[A-Z0-9_]+$")) rowErrors.Add($"Row {rowNumber}: Code can use only letters, numbers and underscore.");
                else if (!seenCodes.Add(code)) rowErrors.Add($"Row {rowNumber}: Code \"{code}\" is repeated in the file.");
                if (string.IsNullOrWhiteSpace(categoryText)) rowErrors.Add($"Row {rowNumber}: Category is required.");
                else if (!SalaryComponentCategories.Contains(category)) rowErrors.Add($"Row {rowNumber}: Category \"{categoryText}\" is invalid.");
                if (string.IsNullOrWhiteSpace(name)) rowErrors.Add($"Row {rowNumber}: Name is required.");
                if (!string.IsNullOrWhiteSpace(payTypeText) && !SalaryPayTypes.Contains(payType)) rowErrors.Add($"Row {rowNumber}: Pay Type \"{payTypeText}\" is invalid.");
                if (string.IsNullOrWhiteSpace(calculationTypeText)) rowErrors.Add($"Row {rowNumber}: Calculation Type is required.");
                else if (!SalaryCalculationTypes.Contains(calculationType)) rowErrors.Add($"Row {rowNumber}: Calculation Type \"{calculationTypeText}\" is invalid.");
                if (!string.IsNullOrWhiteSpace(epfText) && !SalaryEpfOptions.Contains(epf)) rowErrors.Add($"Row {rowNumber}: EPF \"{epfText}\" is invalid.");
                if (calculationType == "Fixed Amount" && string.IsNullOrWhiteSpace(value)) rowErrors.Add($"Row {rowNumber}: Value is required for Fixed Amount.");
                if (calculationType == "Formula" && string.IsNullOrWhiteSpace(formula)) rowErrors.Add($"Row {rowNumber}: Formula is required for Formula calculation.");
                if (calculationType == "Slab Based" && string.IsNullOrWhiteSpace(formula) && string.IsNullOrWhiteSpace(value)) rowErrors.Add($"Row {rowNumber}: Formula or Value is required for Slab Based.");
                if (!string.IsNullOrWhiteSpace(priorityText) && !int.TryParse(priorityText, out _)) rowErrors.Add($"Row {rowNumber}: Priority must be a number.");
                foreach (var (label, text) in new[] { ("Taxable", V("Taxable")), ("Part Of CTC", V("Part Of CTC")), ("Pro Rata", V("Pro Rata")), ("FBP", V("FBP")), ("Restrict FBP", V("Restrict FBP")), ("ESI", V("ESI")), ("Recurring", V("Recurring")), ("Scheduled", V("Scheduled")), ("Active", activeText) })
                    if (!IsImportFlag(text)) rowErrors.Add($"Row {rowNumber}: {label} must be TRUE/FALSE.");
                ValidateSalaryFormula(formula, code, validFormulaCodes, rowNumber, rowErrors);
                ValidateLength(code, "Code", 80, rowNumber, rowErrors);
                ValidateLength(name, "Name", 160, rowNumber, rowErrors);
                ValidateLength(componentType, "Component Type", 120, rowNumber, rowErrors);
                ValidateLength(value, "Value", 500, rowNumber, rowErrors);
                ValidateLength(formula, "Formula", 1000, rowNumber, rowErrors);

                if (rowErrors.Count > 0)
                {
                    errors.AddRange(rowErrors);
                    completed++;
                    progress?.Invoke(completed, inserted, updated);
                    continue;
                }

                var target = existing ?? new JsonObject { ["id"] = nextId++ };
                target["code"] = code;
                target["category"] = category;
                target["name"] = name;
                target["componentType"] = string.IsNullOrWhiteSpace(componentType) ? DefaultSalaryComponentType(category) : componentType;
                target["payType"] = payType;
                target["calculationType"] = calculationType;
                target["value"] = value;
                target["formula"] = formula;
                target["baseComponent"] = baseComponent;
                target["taxable"] = ParseImportFlag(V("Taxable"), JsonBool(existing, "taxable", true));
                target["ctc"] = ParseImportFlag(V("Part Of CTC"), JsonBool(existing, "ctc", true));
                target["proRata"] = ParseImportFlag(V("Pro Rata"), JsonBool(existing, "proRata", true));
                target["fbp"] = ParseImportFlag(V("FBP"), JsonBool(existing, "fbp", false));
                target["restrictFbp"] = ParseImportFlag(V("Restrict FBP"), JsonBool(existing, "restrictFbp", false));
                target["epf"] = string.IsNullOrWhiteSpace(epfText) ? JsonText(existing, "epf", "Never") : epf;
                target["esi"] = ParseImportFlag(V("ESI"), JsonBool(existing, "esi", false));
                target["recurring"] = ParseImportFlag(V("Recurring"), JsonBool(existing, "recurring", true));
                target["scheduled"] = ParseImportFlag(V("Scheduled"), JsonBool(existing, "scheduled", false));
                target["investmentType"] = investmentType;
                target["correctionOf"] = correctionOf;
                target["priority"] = priority.ToString();
                target["active"] = ParseImportFlag(activeText, JsonBool(existing, "active", true));

                if (existing is null)
                {
                    components.Add(target);
                    byCode[code] = target;
                    inserted++;
                }
                else updated++;

                completed++;
                progress?.Invoke(completed, inserted, updated);
            }

            if (errors.Count > 0)
                return new ClientImportResult(totalRows, 0, 0, errors);

            var saved = new JsonArray();
            foreach (var component in components.OrderBy(component => JsonInt(component, "priority", 999)).ThenBy(component => JsonText(component, "code")))
                saved.Add(component.DeepClone());
            setup["salaryComponents"] = saved;
            await PayrollDataTableStore.SaveSetupJsonAsync(connection, setup.ToJsonString(SetupJsonOptions));
            return new ClientImportResult(totalRows, inserted, updated, []);
        }
        catch (Exception ex)
        {
            return new ClientImportResult(0, 0, 0, [$"Import failed: {ex.Message}"]);
        }
    }

    public async Task<byte[]> BuildSalaryTemplateImportTemplateAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await PrepareDatabaseAsync(connection);
        var setup = ParseJsonObject(await PayrollDataTableStore.GetSetupJsonAsync(connection));
        var clients = (await connection.QueryAsync<(int Id, string Name, string Code)>("SELECT Id, Name, COALESCE(Code, '') Code FROM clients WHERE IsActive=TRUE ORDER BY Name")).ToList();
        var components = setup["salaryComponents"] as JsonArray ?? new JsonArray();
        var structures = setup["salaryStructures"] as JsonArray ?? new JsonArray();
        var componentsById = components.OfType<JsonObject>()
            .Where(component => !string.IsNullOrWhiteSpace(JsonText(component, "id")))
            .GroupBy(component => JsonText(component, "id"))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var templateRows = new List<string[]> { SalaryTemplateImportHeaders };

        foreach (var structure in structures.OfType<JsonObject>())
        {
            var lines = structure["lines"] as JsonArray ?? new JsonArray();
            foreach (var line in lines.OfType<JsonObject>())
            {
                var componentId = JsonText(line, "componentId");
                var code = componentsById.TryGetValue(componentId, out var component) ? JsonText(component, "code") : componentId;
                templateRows.Add(new[] { JsonText(structure, "clientId"), JsonText(structure, "name"), JsonText(structure, "annualCtc", "0"), BoolText(JsonBool(structure, "active", true)), code, JsonText(line, "value") });
            }
        }

        if (templateRows.Count == 1)
        {
            var firstClient = clients.FirstOrDefault();
            var basic = components.OfType<JsonObject>().FirstOrDefault(component => JsonText(component, "code").Equals("BASIC", StringComparison.OrdinalIgnoreCase)) ?? components.OfType<JsonObject>().FirstOrDefault();
            templateRows.Add(new[] { firstClient.Id > 0 ? firstClient.Id.ToString() : "ALL", "Standard Salary", "600000", "TRUE", basic is null ? "BASIC" : JsonText(basic, "code"), basic is null ? "CTC * 40%" : JsonText(basic, "formula", JsonText(basic, "value")) });
        }

        var reference = new List<string[]>
        {
            new[] { "Clients", "", "" },
            new[] { "Client Id", "Client Name", "Client Code" }
        };
        reference.AddRange(clients.Select(client => new[] { client.Id.ToString(), client.Name, client.Code }));
        reference.Add(new[] { "", "", "" });
        reference.Add(new[] { "Components", "", "" });
        reference.Add(new[] { "Component Code", "Name", "Category" });
        reference.AddRange(components.OfType<JsonObject>().Select(component => new[] { JsonText(component, "code"), JsonText(component, "name"), JsonText(component, "category") }));
        return BuildXlsx(("Salary Templates", templateRows), ("Reference", reference));
    }

    public async Task<ClientImportJobStatus> StartSalaryTemplateImportJobAsync(IFormFile file)
    {
        var rows = await ParseImportFileAsync(file);
        var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
        var job = new ClientImportJobStatus(Guid.NewGuid(), "Queued", totalRows, 0, 0, 0, []);
        SalaryTemplateImportJobs[job.JobId] = job;
        _ = Task.Run(async () =>
        {
            SetImportJob(SalaryTemplateImportJobs, job.JobId, current => current with { State = "Processing" });
            try
            {
                var result = await ImportSalaryTemplateRowsAsync(rows, (completed, inserted, updated) => SetImportJob(SalaryTemplateImportJobs, job.JobId, current => current with { CompletedRows = completed, Inserted = inserted, Updated = updated }));
                SetImportJob(SalaryTemplateImportJobs, job.JobId, current => current with { State = result.Errors.Count > 0 ? "Failed" : "Completed", TotalRows = result.TotalRows, CompletedRows = result.TotalRows, Inserted = result.Inserted, Updated = result.Updated, Errors = result.Errors });
            }
            catch (Exception ex)
            {
                SetImportJob(SalaryTemplateImportJobs, job.JobId, current => current with { State = "Failed", Errors = [$"Import failed: {ex.Message}"] });
            }
        });
        return job;
    }

    public ClientImportJobStatus? GetSalaryTemplateImportJob(Guid jobId) => SalaryTemplateImportJobs.TryGetValue(jobId, out var job) ? job : null;

    private async Task<ClientImportResult> ImportSalaryTemplateRowsAsync(List<List<string>> rows, Action<int, int, int>? progress = null)
    {
        try
        {
            var totalRows = Math.Max(0, rows.Skip(1).Count(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
            if (rows.Count < 2 || totalRows == 0)
                return new ClientImportResult(0, 0, 0, ["Import file has no data rows."]);

            await using var connection = CreateConnection();
            await connection.OpenAsync();
            await PrepareDatabaseAsync(connection);
            var clients = (await connection.QueryAsync<int>("SELECT Id FROM clients WHERE IsActive=TRUE")).ToHashSet();
            var setup = ParseJsonObject(await PayrollDataTableStore.GetSetupJsonAsync(connection));
            var componentArray = setup["salaryComponents"] as JsonArray ?? new JsonArray();
            var componentsByCode = componentArray.OfType<JsonObject>()
                .Where(component => !string.IsNullOrWhiteSpace(JsonText(component, "code")) && JsonLong(component, "id", 0) > 0)
                .GroupBy(component => JsonText(component, "code").ToUpperInvariant())
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var structures = (setup["salaryStructures"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(structure => (JsonObject)structure.DeepClone()).ToList();
            var existingByKey = structures
                .Where(structure => !string.IsNullOrWhiteSpace(JsonText(structure, "name")))
                .GroupBy(structure => SalaryTemplateKey(ImportRefId(JsonText(structure, "clientId")).ToString(), JsonText(structure, "name")))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var nextId = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), structures.Select(structure => JsonLong(structure, "id", 0)).DefaultIfEmpty(0).Max() + 1);
            var header = rows[0].Select(Norm).ToList();
            var errors = new List<string>();
            var completed = 0;
            var groups = new Dictionary<string, SalaryTemplateImportDraft>(StringComparer.OrdinalIgnoreCase);
            var seenLines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.All(string.IsNullOrWhiteSpace)) continue;

                string V(params string[] names)
                {
                    foreach (var name in names)
                    {
                        var ix = header.IndexOf(Norm(name));
                        if (ix >= 0 && ix < row.Count) return row[ix].Trim();
                    }
                    return "";
                }

                var rowNumber = i + 1;
                var rowErrors = new List<string>();
                var clientText = V("Client Ids", "Client Id");
                var templateName = V("Template Name", "Template").Trim();
                var annualCtc = V("Annual CTC");
                var activeText = V("Active");
                var componentCode = V("Component Code").ToUpperInvariant();
                var value = V("Value");
                var targetClientIds = ResolveSalaryTemplateClientIds(clientText, clients);
                var active = ParseImportFlag(activeText, true);

                if (targetClientIds.Count == 0) rowErrors.Add($"Row {rowNumber}: Client Ids is required. Use client ids or ALL.");
                else
                {
                    foreach (var clientId in targetClientIds)
                        if (!clients.Contains(clientId)) rowErrors.Add($"Row {rowNumber}: Client Id {clientId} was not found.");
                }
                if (string.IsNullOrWhiteSpace(templateName)) rowErrors.Add($"Row {rowNumber}: Template Name is required.");
                if (!string.IsNullOrWhiteSpace(annualCtc) && !decimal.TryParse(annualCtc, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) rowErrors.Add($"Row {rowNumber}: Annual CTC must be numeric.");
                if (!IsImportFlag(activeText)) rowErrors.Add($"Row {rowNumber}: Active must be TRUE/FALSE.");
                if (string.IsNullOrWhiteSpace(componentCode)) rowErrors.Add($"Row {rowNumber}: Component Code is required.");
                else if (!componentsByCode.ContainsKey(componentCode)) rowErrors.Add($"Row {rowNumber}: Component Code {componentCode} was not found.");
                ValidateLength(templateName, "Template Name", 200, rowNumber, rowErrors);
                ValidateLength(value, "Value", 1000, rowNumber, rowErrors);

                if (rowErrors.Count == 0)
                {
                    var component = componentsByCode[componentCode];
                    var componentId = JsonText(component, "id");
                    foreach (var clientId in targetClientIds)
                    {
                        var key = SalaryTemplateKey(clientId.ToString(), templateName);
                        if (!groups.TryGetValue(key, out var draft))
                        {
                            var existing = existingByKey.GetValueOrDefault(key);
                            draft = new SalaryTemplateImportDraft(clientId, templateName, string.IsNullOrWhiteSpace(annualCtc) ? JsonText(existing, "annualCtc", "0") : annualCtc, ParseImportFlag(activeText, JsonBool(existing, "active", true)), []);
                            groups[key] = draft;
                        }
                        else
                        {
                            if (!string.IsNullOrWhiteSpace(annualCtc)) draft.AnnualCtc = annualCtc;
                            draft.Active = active;
                        }

                        var lineKey = $"{key}:{componentId}";
                        if (!seenLines.Add(lineKey)) rowErrors.Add($"Row {rowNumber}: Component Code {componentCode} is repeated for {templateName} / Client Id {clientId}.");
                        else draft.Lines.Add(new SalaryTemplateLineDraft(componentId, value));
                    }
                }

                if (rowErrors.Count > 0) errors.AddRange(rowErrors);
                completed++;
                progress?.Invoke(completed, 0, 0);
            }

            if (errors.Count > 0)
                return new ClientImportResult(totalRows, 0, 0, errors);

            var inserted = 0;
            var updated = 0;
            foreach (var (key, draft) in groups)
            {
                if (draft.Lines.Count == 0)
                {
                    errors.Add($"{draft.Name} / Client Id {draft.ClientId}: At least one component line is required.");
                    continue;
                }

                var existing = existingByKey.GetValueOrDefault(key);
                var target = existing ?? new JsonObject { ["id"] = nextId++ };
                target["clientId"] = draft.ClientId.ToString();
                target["name"] = draft.Name;
                target["annualCtc"] = draft.AnnualCtc;
                target["active"] = draft.Active;
                var lines = new JsonArray();
                foreach (var line in draft.Lines)
                    lines.Add(new JsonObject { ["componentId"] = line.ComponentId, ["value"] = line.Value });
                target["lines"] = lines;
                if (existing is null)
                {
                    structures.Add(target);
                    existingByKey[key] = target;
                    inserted++;
                }
                else updated++;
            }

            if (errors.Count > 0)
                return new ClientImportResult(totalRows, 0, 0, errors);

            var savedStructures = new JsonArray();
            foreach (var structure in structures.OrderBy(structure => ImportRefId(JsonText(structure, "clientId"))).ThenBy(structure => JsonText(structure, "name")))
                savedStructures.Add(structure.DeepClone());
            setup["salaryStructures"] = savedStructures;
            await PayrollDataTableStore.SaveSetupJsonAsync(connection, setup.ToJsonString(SetupJsonOptions));
            return new ClientImportResult(totalRows, inserted, updated, []);
        }
        catch (Exception ex)
        {
            return new ClientImportResult(0, 0, 0, [$"Import failed: {ex.Message}"]);
        }
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
            employee.Id = (int)await connection.ExecuteScalarAsync<long>(@"INSERT INTO employees (ClientId, EmployeeCode, FirstName, LastName, Gender, DateOfJoining, WorkEmail, Department, Designation, Grade, WorkLocationId, ReportingManagerId, PortalAccess, SalaryStructureId, AnnualCtc, SalaryJson, PersonalJson, PaymentJson, IsActive) VALUES (@ClientId, @EmployeeCode, @FirstName, @LastName, @Gender, @DateOfJoining, @WorkEmail, @Department, @Designation, @Grade, @WorkLocationId, @ReportingManagerId, @PortalAccess, @SalaryStructureId, @AnnualCtc, @SalaryJson, @PersonalJson, @PaymentJson, @IsActive); SELECT LAST_INSERT_ID();", employee);
        else
            await connection.ExecuteAsync(@"UPDATE employees SET ClientId=@ClientId, EmployeeCode=@EmployeeCode, FirstName=@FirstName, LastName=@LastName, Gender=@Gender, DateOfJoining=@DateOfJoining, WorkEmail=@WorkEmail, Department=@Department, Designation=@Designation, Grade=@Grade, WorkLocationId=@WorkLocationId, ReportingManagerId=@ReportingManagerId, PortalAccess=@PortalAccess, SalaryStructureId=@SalaryStructureId, AnnualCtc=@AnnualCtc, SalaryJson=@SalaryJson, PersonalJson=@PersonalJson, PaymentJson=@PaymentJson, IsActive=@IsActive WHERE Id=@Id", employee);
        await PayrollDataTableStore.SyncEmployeeTablesAsync(connection, employee);
        return employee.Id;
    }

    private static void SetClientJob(Guid jobId, Func<ClientImportJobStatus, ClientImportJobStatus> update) =>
        ClientImportJobs.AddOrUpdate(jobId, _ => update(new ClientImportJobStatus(jobId, "Processing", 0, 0, 0, 0, [])), (_, current) => update(current));

    private static void SetImportJob(ConcurrentDictionary<Guid, ClientImportJobStatus> jobs, Guid jobId, Func<ClientImportJobStatus, ClientImportJobStatus> update) =>
        jobs.AddOrUpdate(jobId, _ => update(new ClientImportJobStatus(jobId, "Processing", 0, 0, 0, 0, [])), (_, current) => update(current));

    private static void ValidateLength(string value, string label, int max, int row, List<string> errors)
    {
        if (value.Length > max) errors.Add($"Row {row}: {label} must be {max} characters or less.");
    }

    private static bool ParseClientActive(string value) =>
        string.IsNullOrWhiteSpace(value) || !new[] { "false", "no", "inactive", "0" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    private static readonly string[] DropdownImportTypes = ["Department", "Designation", "Work Week", "Employment Type", "Employee Grade", "Cost Center", "Location Tag", "State", "City"];
    private static string[] DropdownSheetHeaders(string type) =>
        type == "Employee Grade" ? ["Client Id", "Value", "Active"] :
        type == "City" ? ["State", "Value", "Active"] :
        type == "Work Week" ? ["Value", "Active", "Working Days", "Off Saturdays"] :
        ["Value", "Active"];
    private static string[] DropdownSheetExample(string type, string clientId) =>
        type == "Employee Grade" ? [clientId, "G1", "TRUE"] :
        type == "City" ? ["Delhi", "New Delhi", "TRUE"] :
        type == "Work Week" ? ["Monday - Saturday with 1st-4th Saturdays off", "TRUE", "Mon, Tue, Wed, Thu, Fri, Sat", "1st, 2nd, 3rd, 4th"] :
        [type == "State" ? "Delhi" : type == "Department" ? "Finance" : type == "Designation" ? "Manager" : type == "Employment Type" ? "Full Time" : type == "Cost Center" ? "CC-001" : "Head Office", "TRUE"];
    private static readonly string[] SalaryComponentImportHeaders = ["Code", "Category", "Name", "Component Type", "Pay Type", "Calculation Type", "Value", "Formula", "Base Component", "Taxable", "Part Of CTC", "Pro Rata", "FBP", "Restrict FBP", "EPF", "ESI", "Recurring", "Scheduled", "Investment Type", "Correction Of", "Priority", "Active"];
    private static readonly string[] SalaryTemplateImportHeaders = ["Client Ids", "Template Name", "Annual CTC", "Active", "Component Code", "Value"];
    private static readonly string[] SalaryComponentCategories = ["Earning", "Deduction", "Reimbursement", "Benefit", "Correction"];
    private static readonly string[] SalaryCalculationTypes = ["Fixed Amount", "Formula", "Residual / Balancing", "Manual / Variable", "Slab Based"];
    private static readonly string[] SalaryPayTypes = ["Fixed Pay", "Variable Pay"];
    private static readonly string[] SalaryEpfOptions = ["Never", "Always", "Only if employee is PF eligible"];
    private static readonly HashSet<string> SalaryFormulaReservedWords = new(StringComparer.OrdinalIgnoreCase) { "GROSS", "CTC", "MONTHLY_CTC", "ANNUAL_CTC", "PAYROLL_DAYS", "TOTAL_DAYS", "WORKING_DAYS", "PAYABLE_DAYS", "PRESENT_DAYS", "LOP_DAYS", "GROSS_EARNED", "NET_PAY", "EMPLOYER_COST", "MIN", "MAX", "ROUND", "ROUNDDOWN", "ROUNDUP", "SUM", "FIXED", "EARNINGS", "EARNINGS_BEFORE_THIS", "OF" };

    private static string NormalizeDropdownImportType(string value) =>
        DropdownImportTypes.FirstOrDefault(type => type.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value.Trim();

    private static string NormalizeSalaryComponentCategory(string value) =>
        SalaryComponentCategories.FirstOrDefault(type => type.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value.Trim();

    private static string NormalizeSalaryPayType(string value) =>
        SalaryPayTypes.FirstOrDefault(type => type.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value.Trim();

    private static string NormalizeSalaryEpf(string value) =>
        SalaryEpfOptions.FirstOrDefault(type => type.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value.Trim();

    private static string NormalizeSalaryCalculationType(string value)
    {
        var clean = value.Trim();
        if (clean.Equals("Percentage of CTC", StringComparison.OrdinalIgnoreCase) || clean.Equals("Percentage of Component", StringComparison.OrdinalIgnoreCase))
            return "Formula";
        if (clean.Equals("Balancing Amount", StringComparison.OrdinalIgnoreCase))
            return "Residual / Balancing";
        if (clean.Equals("Manual Entry", StringComparison.OrdinalIgnoreCase) || clean.Equals("Manual Override", StringComparison.OrdinalIgnoreCase))
            return "Manual / Variable";
        return SalaryCalculationTypes.FirstOrDefault(type => type.Equals(clean, StringComparison.OrdinalIgnoreCase)) ?? clean;
    }

    private static string DefaultSalaryComponentType(string category) => category switch
    {
        "Deduction" => "Custom Deduction",
        "Reimbursement" => "Reimbursement",
        "Benefit" => "Benefit",
        "Correction" => "Correction",
        _ => "Custom Allowance"
    };

    private static bool IsImportFlag(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        new[] { "true", "yes", "active", "1" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ||
        new[] { "false", "no", "inactive", "0" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    private static bool ParseImportFlag(string value, bool defaultValue) =>
        string.IsNullOrWhiteSpace(value) ? defaultValue :
        new[] { "true", "yes", "active", "1" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ? true :
        new[] { "false", "no", "inactive", "0" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ? false : defaultValue;

    private static void ValidateDropdownConfigJson(string configJson, string masterType, int rowNumber, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            if (masterType.Equals("Work Week", StringComparison.OrdinalIgnoreCase))
                errors.Add($"Row {rowNumber}: Work Week requires Working Days or Config Json.");
            return;
        }
        JsonNode? node;
        try { node = JsonNode.Parse(configJson); }
        catch { errors.Add($"Row {rowNumber}: Config Json must be valid JSON."); return; }
        if (node is not JsonObject json)
        {
            errors.Add($"Row {rowNumber}: Config Json must be a JSON object.");
            return;
        }
        if (!masterType.Equals("Work Week", StringComparison.OrdinalIgnoreCase)) return;

        if (!json.TryGetPropertyValue("workingDays", out var workingNode) || workingNode is not JsonArray workingDays)
            errors.Add($"Row {rowNumber}: Config Json workingDays must be an array.");
        else ValidateIntegerArray(workingDays, rowNumber, "workingDays", 0, 6, true, errors);

        if (!json.TryGetPropertyValue("offSaturdays", out var offNode) || offNode is not JsonArray offSaturdays)
            errors.Add($"Row {rowNumber}: Config Json offSaturdays must be an array.");
        else ValidateIntegerArray(offSaturdays, rowNumber, "offSaturdays", 1, 5, false, errors);
    }

    private static void ValidateIntegerArray(JsonArray values, int rowNumber, string label, int min, int max, bool requireOne, List<string> errors)
    {
        if (requireOne && values.Count == 0) errors.Add($"Row {rowNumber}: Config Json {label} must have at least one value.");
        foreach (var item in values)
        {
            var text = item?.ToString() ?? "";
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            {
                errors.Add($"Row {rowNumber}: Config Json {label} values must be numbers between {min} and {max}.");
                return;
            }
        }
    }

    private static string BuildWorkWeekConfigJson(string workingDaysText, string offSaturdaysText)
    {
        var dayAliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["sun"] = 0, ["sunday"] = 0, ["0"] = 0,
            ["mon"] = 1, ["monday"] = 1, ["1"] = 1,
            ["tue"] = 2, ["tuesday"] = 2, ["2"] = 2,
            ["wed"] = 3, ["wednesday"] = 3, ["3"] = 3,
            ["thu"] = 4, ["thursday"] = 4, ["4"] = 4,
            ["fri"] = 5, ["friday"] = 5, ["5"] = 5,
            ["sat"] = 6, ["saturday"] = 6, ["6"] = 6
        };
        var saturdayAliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["first"] = 1, ["1st"] = 1, ["1"] = 1,
            ["second"] = 2, ["2nd"] = 2, ["2"] = 2,
            ["third"] = 3, ["3rd"] = 3, ["3"] = 3,
            ["fourth"] = 4, ["4th"] = 4, ["4"] = 4,
            ["fifth"] = 5, ["5th"] = 5, ["5"] = 5
        };
        var workingDays = ParseNamedNumbers(workingDaysText, dayAliases, 0, 6);
        if (workingDays.Count == 0) return "";
        var offSaturdays = ParseNamedNumbers(offSaturdaysText, saturdayAliases, 1, 5);
        return JsonSerializer.Serialize(new { workingDays, offSaturdays });
    }

    private static List<int> ParseNamedNumbers(string text, Dictionary<string, int> aliases, int min, int max) =>
        text.Split([',', ';', '|', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Select(item => aliases.TryGetValue(Norm(item), out var value) ? value : aliases.TryGetValue(item, out value) ? value : (int?)null)
            .Where(value => value.HasValue && value.Value >= min && value.Value <= max)
            .Select(value => value!.Value)
            .Distinct()
            .OrderBy(value => value)
            .ToList();

    private static JsonObject ParseJsonObject(string? json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json)?.AsObject() ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private static string JsonText(JsonObject? root, string key, string fallback = "") =>
        root is not null && root.TryGetPropertyValue(key, out var value) && value is not null ? value.ToString() : fallback;

    private static bool JsonBool(JsonObject? root, string key, bool fallback)
    {
        if (root is null || !root.TryGetPropertyValue(key, out var value) || value is null) return fallback;
        try { return value.GetValue<bool>(); } catch { return bool.TryParse(value.ToString(), out var result) ? result : fallback; }
    }

    private static int JsonInt(JsonObject? root, string key, int fallback) =>
        int.TryParse(JsonText(root, key), out var value) ? value : fallback;

    private static long JsonLong(JsonObject? root, string key, long fallback) =>
        long.TryParse(JsonText(root, key), out var value) ? value : fallback;

    private static string BoolText(bool value) => value ? "TRUE" : "FALSE";

    private static void ValidateSalaryFormula(string formula, string currentCode, HashSet<string> validCodes, int rowNumber, List<string> errors)
    {
        var text = formula.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[^A-Z0-9_+\-*/().,%\s]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            errors.Add($"Row {rowNumber}: Formula has unsupported characters.");
        var depth = 0;
        foreach (var ch in text)
        {
            if (ch == '(') depth++;
            if (ch == ')') depth--;
            if (depth < 0) break;
        }
        if (depth != 0) errors.Add($"Row {rowNumber}: Formula brackets are not balanced.");
        var tokens = System.Text.RegularExpressions.Regex.Matches(text.ToUpperInvariant(), @"\b[A-Z_][A-Z0-9_]*\b").Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            if (SalaryFormulaReservedWords.Contains(token)) continue;
            if (token.Equals(currentCode, StringComparison.OrdinalIgnoreCase)) errors.Add($"Row {rowNumber}: Formula cannot reference itself ({token}).");
            else if (!validCodes.Contains(token)) errors.Add($"Row {rowNumber}: Formula references unknown component code {token}.");
        }
    }

    private static List<int> ResolveSalaryTemplateClientIds(string value, HashSet<int> activeClientIds)
    {
        if (value.Trim().Equals("ALL", StringComparison.OrdinalIgnoreCase))
            return activeClientIds.OrderBy(id => id).ToList();
        return value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(ImportRefId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
    }

    private static int ImportRefId(string? value) =>
        int.TryParse((value ?? "").Split(':')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;

    private static string SalaryTemplateKey(string clientId, string name) =>
        $"{ImportRefId(clientId)}:{name.Trim().ToLowerInvariant()}";

    private static string Norm(string value) => value.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
    private static string DropdownTypeFromSheet(string value) =>
        DropdownImportTypes.FirstOrDefault(type => Norm(type) == Norm(value) || Norm($"{type}s") == Norm(value)) ?? "";

    private static async Task<List<List<string>>> ParseDropdownImportFileAsync(IFormFile file)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return ParseCsv(Encoding.UTF8.GetString(bytes));

        var sheets = ParseXlsxSheets(bytes);
        var first = sheets.FirstOrDefault(sheet => sheet.Rows.Any(row => row.Any(value => !string.IsNullOrWhiteSpace(value))));
        var firstHeader = first?.Rows.FirstOrDefault(row => row.Any(value => !string.IsNullOrWhiteSpace(value)))?.Select(Norm).ToList() ?? [];
        if (firstHeader.Contains(Norm("Master Type"))) return first?.Rows ?? [];

        var combined = new List<List<string>> { new() { "Master Type", "Value", "Client Id", "State", "Active", "Config Json" } };
        foreach (var sheet in sheets)
        {
            var type = DropdownTypeFromSheet(sheet.Name);
            if (string.IsNullOrWhiteSpace(type)) continue;
            var headerIndex = sheet.Rows.FindIndex(row => row.Any(value => !string.IsNullOrWhiteSpace(value)));
            if (headerIndex < 0) continue;
            var header = sheet.Rows[headerIndex].Select(Norm).ToList();

            string V(List<string> row, string name)
            {
                var ix = header.IndexOf(Norm(name));
                return ix >= 0 && ix < row.Count ? row[ix].Trim() : "";
            }

            foreach (var row in sheet.Rows.Skip(headerIndex + 1))
            {
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                var configJson = type == "Work Week"
                    ? V(row, "Config Json").Trim()
                    : "";
                if (type == "Work Week" && string.IsNullOrWhiteSpace(configJson))
                    configJson = BuildWorkWeekConfigJson(V(row, "Working Days"), V(row, "Off Saturdays"));
                combined.Add(new List<string> {
                    type,
                    V(row, "Value"),
                    type == "Employee Grade" ? V(row, "Client Id") : "",
                    type == "City" ? V(row, "State") : "",
                    V(row, "Active"),
                    configJson
                });
            }
        }
        return combined;
    }

    private static async Task<List<List<string>>> ParseImportFileAsync(IFormFile file)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        return file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? ParseXlsx(bytes) : ParseCsv(Encoding.UTF8.GetString(bytes));
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted && ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
            else if (ch == '"') quoted = !quoted;
            else if (!quoted && ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (!quoted && (ch == '\n' || ch == '\r'))
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else cell.Append(ch);
        }
        row.Add(cell.ToString());
        if (row.Any(value => value.Length > 0)) rows.Add(row);
        return rows;
    }

    private sealed record ImportSheet(string Name, List<List<string>> Rows);

    private static List<List<string>> ParseXlsx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var shared = ReadSharedStrings(zip);
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml") ?? throw new InvalidDataException("Import sheet not found.");
        return ParseXlsxSheet(sheet, shared);
    }

    private static List<ImportSheet> ParseXlsxSheets(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var shared = ReadSharedStrings(zip);
        var workbookSheets = ReadWorkbookSheets(zip);
        if (workbookSheets.Count == 0)
            workbookSheets = zip.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).OrderBy(entry => entry.FullName).Select((entry, index) => (Name: $"Sheet {index + 1}", Path: entry.FullName)).ToList();
        return workbookSheets.Select(sheet => {
            var entry = zip.GetEntry(sheet.Path);
            return entry is null ? null : new ImportSheet(sheet.Name, ParseXlsxSheet(entry, shared));
        }).Where(sheet => sheet is not null).Cast<ImportSheet>().ToList();
    }

    private static List<List<string>> ParseXlsxSheet(ZipArchiveEntry sheet, List<string> shared)
    {
        using var stream = sheet.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = new List<List<string>>();
        foreach (var row in doc.Descendants(ns + "row"))
        {
            var values = new List<string>();
            foreach (var cell in row.Elements(ns + "c"))
            {
                var index = CellIndex((string?)cell.Attribute("r") ?? "A1");
                while (values.Count < index) values.Add("");
                var type = (string?)cell.Attribute("t") ?? "";
                var raw = type == "inlineStr" ? cell.Descendants(ns + "t").FirstOrDefault()?.Value ?? "" : cell.Element(ns + "v")?.Value ?? "";
                values.Add(type == "s" && int.TryParse(raw, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < shared.Count ? shared[sharedIndex] : raw);
            }
            rows.Add(values);
        }
        return rows;
    }

    private static List<(string Name, string Path)> ReadWorkbookSheets(ZipArchive zip)
    {
        var workbookEntry = zip.GetEntry("xl/workbook.xml");
        if (workbookEntry is null) return [];
        using var workbookStream = workbookEntry.Open();
        var workbook = XDocument.Load(workbookStream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var rels = ReadWorkbookRelationships(zip);
        return workbook.Descendants(ns + "sheet").Select((sheet, index) => {
            var name = (string?)sheet.Attribute("name") ?? $"Sheet {index + 1}";
            var relId = (string?)sheet.Attribute(relNs + "id") ?? $"rId{index + 1}";
            var target = rels.GetValueOrDefault(relId, $"worksheets/sheet{index + 1}.xml").Replace('\\', '/');
            var path = target.StartsWith('/') ? target.TrimStart('/') : target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : $"xl/{target}";
            return (name, path);
        }).ToList();
    }

    private static Dictionary<string, string> ReadWorkbookRelationships(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (entry is null) return [];
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        return doc.Descendants(ns + "Relationship").ToDictionary(rel => (string?)rel.Attribute("Id") ?? "", rel => (string?)rel.Attribute("Target") ?? "");
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return doc.Descendants(ns + "si").Select(item => string.Concat(item.Descendants(ns + "t").Select(text => text.Value))).ToList();
    }

    private static int CellIndex(string reference)
    {
        var n = 0;
        foreach (var ch in reference.TakeWhile(char.IsLetter)) n = n * 26 + char.ToUpperInvariant(ch) - 'A' + 1;
        return Math.Max(0, n - 1);
    }

    private static byte[] BuildXlsx(params (string Name, IEnumerable<string[]> Rows)[] sheets)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var sheetOverrides = string.Concat(sheets.Select((_, index) => $"""<Override PartName="/xl/worksheets/sheet{index + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>"""));
            var sheetRelationships = string.Concat(sheets.Select((_, index) => $"""<Relationship Id="rId{index + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{index + 1}.xml"/>"""));
            Add(zip, "[Content_Types].xml", $"""<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>{sheetOverrides}</Types>""");
            Add(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/_rels/workbook.xml.rels", $"""<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{sheetRelationships}<Relationship Id="rId{sheets.Length + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Add(zip, "xl/styles.xml", """<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="1"><fill><patternFill patternType="none"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="1"><xf/></cellXfs></styleSheet>""");
            Add(zip, "xl/workbook.xml", WorkbookXml(sheets.Select((sheet, index) => (sheet.Name, index + 1))));
            foreach (var (sheet, index) in sheets.Select((sheet, index) => (sheet, index + 1)))
                Add(zip, $"xl/worksheets/sheet{index}.xml", SheetXml(sheet.Rows));
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string text)
    {
        var entry = zip.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(text);
    }

    private static string WorkbookXml(IEnumerable<(string Name, int Index)> sheets) =>
        new XDocument(new XElement(XName.Get("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
            new XAttribute(XNamespace.Xmlns + "r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"),
            new XElement(XName.Get("sheets", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                sheets.Select(sheet => new XElement(XName.Get("sheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                    new XAttribute("name", sheet.Name),
                    new XAttribute("sheetId", sheet.Index),
                    new XAttribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"), $"rId{sheet.Index}")))))).ToString(SaveOptions.DisableFormatting);

    private static string SheetXml(IEnumerable<string[]> rows) =>
        new XDocument(new XElement(XName.Get("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
            new XElement(XName.Get("sheetData", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                rows.Select((row, rowIndex) => new XElement(XName.Get("row", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                    new XAttribute("r", rowIndex + 1),
                    row.Select((cell, colIndex) => new XElement(XName.Get("c", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                        new XAttribute("r", $"{Col(colIndex + 1)}{rowIndex + 1}"),
                        new XAttribute("t", "inlineStr"),
                        new XElement(XName.Get("is", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"),
                            new XElement(XName.Get("t", "http://schemas.openxmlformats.org/spreadsheetml/2006/main"), cell ?? ""))))))))).ToString(SaveOptions.DisableFormatting);

    private static string Col(int n)
    {
        var value = "";
        while (n > 0)
        {
            n--;
            value = (char)('A' + n % 26) + value;
            n /= 26;
        }
        return value;
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

public record ClientImportResult(int TotalRows, int Inserted, int Updated, List<string> Errors);
public record ClientImportJobStatus(Guid JobId, string State, int TotalRows, int CompletedRows, int Inserted, int Updated, List<string> Errors);
public sealed class SalaryTemplateImportDraft
{
    public SalaryTemplateImportDraft(int clientId, string name, string annualCtc, bool active, List<SalaryTemplateLineDraft> lines)
    {
        ClientId = clientId;
        Name = name;
        AnnualCtc = annualCtc;
        Active = active;
        Lines = lines;
    }

    public int ClientId { get; }
    public string Name { get; }
    public string AnnualCtc { get; set; }
    public bool Active { get; set; }
    public List<SalaryTemplateLineDraft> Lines { get; }
}
public record SalaryTemplateLineDraft(string ComponentId, string Value);

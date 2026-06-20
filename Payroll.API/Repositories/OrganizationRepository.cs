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

        await EnsureColumnAsync(connection, "BusinessLocation", "VARCHAR(100) NOT NULL DEFAULT 'India'");
        await EnsureColumnAsync(connection, "Industry", "VARCHAR(150) NULL");
        await EnsureColumnAsync(connection, "HasRunPayrollThisYear", "BOOLEAN NOT NULL DEFAULT FALSE");
        await EnsureColumnAsync(connection, "SetupCompleted", "BOOLEAN NOT NULL DEFAULT FALSE");
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

    private async Task PrepareDatabaseAsync(MySqlConnection connection)
    {
        await connection.ExecuteAsync("USE payroll;");
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
}

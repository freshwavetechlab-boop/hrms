using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public class ClientBillingRepository(IConfiguration configuration)
{
    private MySqlConnection Connection() => new(configuration.GetConnectionString("Default"));

    public async Task InitializeAsync()
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
    }

    public async Task<ClientBillingModule> GetModuleAsync()
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        var enabled = await db.ExecuteScalarAsync<bool?>("SELECT IsEnabled FROM client_billing_settings WHERE Id=1");
        return new ClientBillingModule { IsEnabled = enabled ?? false };
    }

    public async Task SaveModuleAsync(ClientBillingModule module)
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        await db.ExecuteAsync(@"INSERT INTO client_billing_settings (Id,IsEnabled) VALUES (1,@IsEnabled)
ON DUPLICATE KEY UPDATE IsEnabled=@IsEnabled, UpdatedAt=CURRENT_TIMESTAMP", module);
    }

    public async Task<IEnumerable<ClientBillingConfiguration>> GetAsync()
    {
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        return await db.QueryAsync<ClientBillingConfiguration>(@"SELECT b.*, c.Name AS ClientName, COALESCE(w.Name,'All locations') AS WorkLocationName
FROM client_billing_configurations b
JOIN clients c ON c.Id=b.ClientId
LEFT JOIN worklocations w ON w.Id=b.WorkLocationId
ORDER BY c.Name, WorkLocationName, b.RateCardType, b.EffectiveFrom DESC, b.Id DESC");
    }

    public async Task<(long Id, string Error)> SaveAsync(ClientBillingConfiguration row)
    {
        var error = Validate(row);
        if (!string.IsNullOrWhiteSpace(error)) return (0, error);
        await using var db = Connection(); await db.OpenAsync(); await EnsureTablesAsync(db);
        var clientExists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM clients WHERE Id=@ClientId AND IsActive=TRUE", row);
        if (clientExists == 0) return (0, "Select an active client.");
        if (row.WorkLocationId is > 0)
        {
            var locationExists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM worklocations WHERE Id=@WorkLocationId AND ClientId=@ClientId AND IsActive=TRUE", row);
            if (locationExists == 0) return (0, "Select an active work location for the selected client.");
        }
        row.WorkLocationId = row.WorkLocationId is > 0 ? row.WorkLocationId : null;
        if (row.Id <= 0)
        {
            var id = await db.ExecuteScalarAsync<long>(@"INSERT INTO client_billing_configurations (ClientId,WorkLocationId,RateCardType,RateType,Value,TaxInclusive,GstRatePercent,EffectiveFrom,EffectiveTo,IsActive)
VALUES (@ClientId,@WorkLocationId,@RateCardType,@RateType,@Value,@TaxInclusive,@GstRatePercent,@EffectiveFrom,@EffectiveTo,@IsActive); SELECT LAST_INSERT_ID();", row);
            return (id, "");
        }
        await db.ExecuteAsync(@"UPDATE client_billing_configurations SET ClientId=@ClientId,WorkLocationId=@WorkLocationId,RateCardType=@RateCardType,RateType=@RateType,Value=@Value,TaxInclusive=@TaxInclusive,GstRatePercent=@GstRatePercent,EffectiveFrom=@EffectiveFrom,EffectiveTo=@EffectiveTo,IsActive=@IsActive,UpdatedAt=CURRENT_TIMESTAMP WHERE Id=@Id", row);
        return (row.Id, "");
    }

    static string Validate(ClientBillingConfiguration row)
    {
        string[] cardTypes = ["All", "Service Charge", "Reimbursement", "Bonus", "Statutory Compliance Charges"];
        string[] rateTypes = ["Percentage", "Fixed"];
        if (row.ClientId <= 0) return "Select a client.";
        if (!cardTypes.Contains(row.RateCardType)) return "Select a valid rate card type.";
        if (!rateTypes.Contains(row.RateType)) return "Select a valid rate type.";
        if (row.Value < 0) return "Value cannot be negative.";
        if (row.GstRatePercent < 0 || row.GstRatePercent > 100) return "GST rate must be between 0 and 100.";
        if (row.EffectiveFrom == default) return "Effective from date is required.";
        if (row.EffectiveTo.HasValue && row.EffectiveTo.Value.Date < row.EffectiveFrom.Date) return "Effective to date cannot be before effective from.";
        return "";
    }

    static async Task EnsureTablesAsync(MySqlConnection db)
    {
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS client_billing_settings (
    Id TINYINT PRIMARY KEY,
    IsEnabled BOOLEAN NOT NULL DEFAULT FALSE,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS client_billing_configurations (
    Id BIGINT PRIMARY KEY AUTO_INCREMENT,
    ClientId INT NOT NULL,
    WorkLocationId INT NULL,
    RateCardType VARCHAR(80) NOT NULL,
    RateType VARCHAR(30) NOT NULL,
    Value DECIMAL(18,4) NOT NULL DEFAULT 0,
    TaxInclusive BOOLEAN NOT NULL DEFAULT FALSE,
    GstRatePercent DECIMAL(8,4) NOT NULL DEFAULT 18,
    EffectiveFrom DATE NOT NULL,
    EffectiveTo DATE NULL,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX IX_ClientBilling_Client (ClientId, IsActive, EffectiveFrom),
    INDEX IX_ClientBilling_Location (WorkLocationId, IsActive, EffectiveFrom),
    INDEX IX_ClientBilling_Type (RateCardType, RateType)
);");
        await EnsureColumnAsync(db, "client_billing_configurations", "GstRatePercent", "DECIMAL(8,4) NOT NULL DEFAULT 18 AFTER TaxInclusive");
    }

    static async Task EnsureColumnAsync(MySqlConnection db, string tableName, string columnName, string definition)
    {
        var exists = await db.ExecuteScalarAsync<int>(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName", new { TableName = tableName, ColumnName = columnName });
        if (exists == 0) await db.ExecuteAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition}");
    }
}

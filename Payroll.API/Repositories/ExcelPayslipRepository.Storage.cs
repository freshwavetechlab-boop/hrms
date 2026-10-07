using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

internal sealed class ExcelPayslipSendState
{
    public Dictionary<string, JsonElement> Requests { get; set; } = [];
    public Dictionary<string, NotificationRepository.ExcelPayslipMailDocument> Deliveries { get; set; } = [];
}

public sealed partial class ExcelPayslipRepository
{
    // Explicit migration only: never called by a page read, batch save, or notification worker.
    public async Task<int> MigrateStorageAsync()
    {
        await using var db = Db();
        await db.OpenAsync();
        await using var script = typeof(ExcelPayslipRepository).Assembly.GetManifestResourceStream("Payroll.API.Database.ExcelPayslips.sql")
            ?? throw new InvalidOperationException("The Excel payslip migration script is missing.");
        using var reader = new StreamReader(script);
        await db.ExecuteAsync(await reader.ReadToEndAsync());

        await using var tx = await db.BeginTransactionAsync();
        var legacy = (await db.QueryAsync<LegacyPayslipSetting>(@"SELECT client_id AS ClientId,ModuleCode,
CAST(SettingsJson AS CHAR) AS Json FROM modulesettings
WHERE LEFT(ModuleCode,14)='excel_payslip:' OR LEFT(ModuleCode,19)='excel_payslip_send:'
OR LEFT(ModuleCode,19)='excel_payslip_mail:' ORDER BY client_id,ModuleCode FOR UPDATE", transaction: tx)).ToArray();
        EnsureLegacyDeliveryCanMigrate(legacy.Select(row => row.ModuleCode));

        foreach (var source in legacy)
        {
            var batch = ValidateLegacyBatch(source.ClientId, source.ModuleCode, source.Json);
            var saved = await db.ExecuteScalarAsync<string?>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id FOR UPDATE", new { source.ClientId, batch.Id }, tx);
            EnsureSameSnapshot(saved, source.Json);
            if (saved is null)
                await db.ExecuteAsync(@"INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at)
VALUES(@ClientId,@Id,CAST(@Json AS JSON),JSON_OBJECT(),@CreatedAtUtc)", new { source.ClientId, batch.Id, source.Json, batch.CreatedAtUtc }, tx);

            // Verify the stored JSON before removing only this feature's exact legacy batch key.
            saved = await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@ClientId AND id=@Id", new { source.ClientId, batch.Id }, tx);
            if (saved is null) throw new InvalidOperationException("Excel payslip batch copy verification failed. Existing data was retained.");
            EnsureSameSnapshot(saved, source.Json);
            await db.ExecuteAsync("DELETE FROM modulesettings WHERE client_id=@ClientId AND ModuleCode=@ModuleCode", new { source.ClientId, source.ModuleCode }, tx);
        }
        await tx.CommitAsync();
        return legacy.Length;
    }

    internal static void EnsureLegacyDeliveryCanMigrate(IEnumerable<string> codes)
    {
        // Earlier email snapshots omit seal/rounding options and request receipts omit batch ID.
        // Guessing either could change a retried attachment or duplicate a queued message.
        if (codes.Any(code => code.StartsWith("excel_payslip_mail:", StringComparison.Ordinal) || code.StartsWith("excel_payslip_send:", StringComparison.Ordinal)))
            throw new InvalidOperationException("Legacy Excel payslip email records need conversion review before migration: their saved PDF options are unavailable. No existing records were changed. Keep the API stopped and retain those records; do not delete them to bypass this check.");
    }

    internal static ExcelPayslipBatch ValidateLegacyBatch(int clientId, string code, string json)
    {
        var id = code.StartsWith(BatchPrefix, StringComparison.Ordinal) ? code[BatchPrefix.Length..] : "";
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(json, JsonOptions);
        if (!Guid.TryParseExact(id, "N", out _) || clientId <= 0 || batch is null || batch.ClientId != clientId
            || batch.Id != id || batch.CreatedAtUtc.Year < 1000 || batch.Rows is null || batch.Rows.Count == 0)
            throw new InvalidOperationException("An existing Excel payslip batch has an invalid client, ID, timestamp or snapshot. No existing records were changed.");
        return batch;
    }

    internal static void EnsureSameSnapshot(string? saved, string source)
    {
        if (saved is not null && !JsonNode.DeepEquals(JsonNode.Parse(saved), JsonNode.Parse(source)))
            throw new InvalidOperationException("An Excel payslip batch ID already exists with different data. No existing records were changed.");
    }

    private sealed class LegacyPayslipSetting
    {
        public int ClientId { get; set; }
        public string ModuleCode { get; set; } = "";
        public string Json { get; set; } = "";
    }
}

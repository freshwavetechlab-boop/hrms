using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Services;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    public async Task<ExcelPayslipCalculationSource?> GetCalculationSourceAsync(int clientId, string batchId)
    {
        if (!Guid.TryParseExact(batchId, "N", out _)) return null;
        await using var db = Db(); await db.OpenAsync();
        if (!await SupportedAsync(db, clientId)) return null;
        return await ReadCalculationSourceAsync(db, clientId, batchId);
    }

    internal static async Task<ExcelPayslipCalculationSource?> ReadCalculationSourceAsync(MySqlConnection db, int clientId, string batchId, MySqlTransaction? tx = null)
    {
        var payload = await db.ExecuteScalarAsync<byte[]?>("SELECT COMPRESS(CAST(source_json AS CHAR)) FROM excel_payslip_calculation_sources WHERE client_id=@ClientId AND batch_id=@Id", new { ClientId = clientId, Id = batchId }, tx);
        var json = ExcelPayslipConsolidationCatalog.DecodeSnapshot(payload,true);
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(json, JsonOptions);
    }
}

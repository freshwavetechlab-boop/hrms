using System.Text.Json;
using Dapper;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayslipRepository
{
    public async Task<ExcelPayslipCalculationSource?> GetCalculationSourceAsync(int clientId, string batchId)
    {
        if (!Guid.TryParseExact(batchId, "N", out _)) return null;
        await using var db = Db(); await db.OpenAsync();
        if (!await SupportedAsync(db, clientId)) return null;
        var json = await db.ExecuteScalarAsync<string?>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=@ClientId AND batch_id=@Id", new { ClientId = clientId, Id = batchId });
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(json, JsonOptions);
    }
}

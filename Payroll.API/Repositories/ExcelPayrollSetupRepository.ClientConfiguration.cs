using System.Text.Json;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;

namespace Payroll.API.Repositories;

public sealed partial class ExcelPayrollSetupRepository
{
    internal sealed record ClientConfigurationBasis(int ClientId,string Month,List<ExcelPayslipCalculationColumn> SourceColumns,ExcelPayrollSetupRequest SavedRequest);
    internal static bool CompatibleConfigurationColumns(ExcelPayslipCalculationSource current,ExcelPayslipCalculationSource previous)
    {
        if(current.ColumnCount!=previous.ColumnCount || current.Columns.Count!=previous.Columns.Count)return false;
        return current.Columns.OrderBy(column=>column.ColumnIndex).Zip(previous.Columns.OrderBy(column=>column.ColumnIndex))
            .All(pair=>pair.First.ColumnIndex==pair.Second.ColumnIndex && Normalize(pair.First.SourceHeader)==Normalize(pair.Second.SourceHeader)
                && pair.First.Kind==pair.Second.Kind && pair.First.BlankAsZero==pair.Second.BlankAsZero && pair.First.ComponentId==pair.Second.ComponentId);
    }
    private static async Task<ClientConfigurationBasis?> ReadClientConfigurationAsync(MySqlConnection db,int clientId,ExcelPayslipCalculationSource source)
    {
        var ids=(await db.QueryAsync<string>(@"SELECT id FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND status='Applied'
AND JSON_EXTRACT(request_json,'$.transferReviewVersion')=1
AND (JSON_EXTRACT(request_json,'$.locationAllocations') IS NULL OR JSON_TYPE(JSON_EXTRACT(request_json,'$.locationAllocations'))='NULL')
ORDER BY updated_at DESC,id DESC LIMIT 6",new {ClientId=clientId})).ToArray();
        foreach(var id in ids)
        {
            var raw=await db.ExecuteScalarAsync<string>("SELECT CAST(request_json AS CHAR) FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND id=@Id AND status='Applied'",new {ClientId=clientId,Id=id});
            if(string.IsNullOrWhiteSpace(raw))continue;
            ExcelPayrollSetupRequest? request;
            try { request=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(raw,Json); } catch(JsonException) {continue;}
            if(request is null || request.LocationAllocations is not null || request.CorrectionOfImportId is not null)continue;
            try
            {
                var previous=await ReadSourceAsync(db,null,clientId,request.BatchId);
                if(CompatibleConfigurationColumns(source,previous.Source))return new(clientId,previous.Batch.Month,previous.Source.Columns,request);
            }
            catch(InvalidOperationException) { /* An unavailable old source cannot supply a configuration basis. */ }
        }
        return null;
    }
}

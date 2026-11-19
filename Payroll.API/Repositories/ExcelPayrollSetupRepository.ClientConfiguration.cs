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
    internal static async Task<ClientConfigurationBasis?> ReadClientConfigurationAsync(MySqlConnection db,int clientId,ExcelPayslipCalculationSource source)
    {
        // A client may have more than six location layouts or saved employee cohorts.
        // Search lightweight column metadata in bounded pages, rather than losing an
        // older valid layout or downloading every captured workbook/employee row.
        const int pageSize=32;
        for(var offset=0;;offset+=pageSize)
        {
            var candidates=(await db.QueryAsync<ConfigurationCandidate>(@"SELECT i.id Id,i.batch_id BatchId,
JSON_UNQUOTE(JSON_EXTRACT(b.batch_json,'$.month')) Month,
CAST(JSON_OBJECT('columnCount',JSON_EXTRACT(s.source_json,'$.columnCount'),
'columns',JSON_EXTRACT(s.source_json,'$.columns')) AS CHAR) SourceShapeJson
FROM excel_payroll_setup_imports i
JOIN excel_payslip_batches b ON b.client_id=i.client_id AND b.id=i.batch_id
JOIN excel_payslip_calculation_sources s ON s.client_id=i.client_id AND s.batch_id=i.batch_id
WHERE i.client_id=@ClientId AND i.status='Applied'
AND JSON_EXTRACT(i.request_json,'$.transferReviewVersion')=1
AND (JSON_EXTRACT(i.request_json,'$.locationAllocations') IS NULL OR JSON_TYPE(JSON_EXTRACT(i.request_json,'$.locationAllocations'))='NULL')
AND (JSON_EXTRACT(i.request_json,'$.correctionOfImportId') IS NULL OR JSON_TYPE(JSON_EXTRACT(i.request_json,'$.correctionOfImportId'))='NULL')
ORDER BY i.updated_at DESC,i.id DESC LIMIT @PageSize OFFSET @Offset",new {ClientId=clientId,PageSize=pageSize,Offset=offset})).ToArray();
            foreach(var candidate in candidates)
            {
                ExcelPayslipCalculationSource? previous;
                try { previous=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(candidate.SourceShapeJson,Json); }
                catch(JsonException) {continue;}
                if(previous is null || !CompatibleConfigurationColumns(source,previous))continue;
                var raw=await db.ExecuteScalarAsync<string>("SELECT CAST(request_json AS CHAR) FROM excel_payroll_setup_imports WHERE client_id=@ClientId AND id=@Id AND status='Applied'",new {ClientId=clientId,candidate.Id});
                if(string.IsNullOrWhiteSpace(raw))continue;
                ExcelPayrollSetupRequest? request;
                try { request=JsonSerializer.Deserialize<ExcelPayrollSetupRequest>(raw,Json); } catch(JsonException) {continue;}
                if(request is null || request.BatchId!=candidate.BatchId || request.TransferReviewVersion!=1
                    || request.LocationAllocations is not null || request.CorrectionOfImportId is not null)continue;
                return new(clientId,candidate.Month,previous.Columns,request);
            }
            if(candidates.Length<pageSize)return null;
        }
    }

    private sealed class ConfigurationCandidate
    {
        public string Id {get;set;}="";
        public string BatchId {get;set;}="";
        public string Month {get;set;}="";
        public string SourceShapeJson {get;set;}="";
    }
}

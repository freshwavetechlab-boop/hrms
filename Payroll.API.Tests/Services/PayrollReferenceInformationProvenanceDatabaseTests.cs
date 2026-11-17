using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed partial class PayrollReferenceInformationDatabaseTests
{
    private async Task AddCanonicalProfileLinksAsync()
    {
        await using var db=await F.OpenAsync();
        foreach(var id in new[]{Batch,OtherBatch})
        {
            var raw=await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97790 AND id=@Id",new{Id=id});
            var linked=JsonSerializer.Deserialize<ExcelPayslipBatch>(raw!,Json)!;var row=linked.Rows.Single(value=>value.EmployeeCode=="SYN-REF-2");
            var links=JsonSerializer.Serialize(new[]{new{rowId=row.Id,sourceRow=row.SourceRow,employeeId=Two,
                sourceIdentityHash=ExcelPayslipMasterSynchronization.ProfileIdentityHash(linked,row,source)}},Json);
            await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=JSON_SET(batch_json,'$.profileLinks',CAST(@Links AS JSON)) WHERE client_id=97790 AND id=@Id",new{Id=id,Links=links});
        }
    }
    [LocalFact]
    public async Task Only_canonical_server_profile_links_preserve_existing_original_receipt_budget_normal_PDF_ESS_and_reviewed_refresh()
    {
        var initial=await InitialReferenceAsync();await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE payruns SET Status='Approved' WHERE ClientId=97790");
        var normalBefore=ReferencePdfContent((await Repo(false).CreatePdfAsync(Client,runId,Two))!);
        await AddCanonicalProfileLinksAsync();var before=await WitnessAsync();
        Assert.Equal(25000,(await ReadAsync(false)).Value);Assert.Equal(normalBefore,ReferencePdfContent((await Repo(false).CreatePdfAsync(Client,runId,Two))!));
        var ess=new EssMssRepository(Config(false),Protector);Assert.True((await ess.GetPayslipDocumentAsync(Two,Client,runId))!.PdfPreferred);
        Assert.Equal(2,(await ess.GetLocationPayslipDocumentsAsync(Client,runId)).Count);Assert.Equal(before,await WitnessAsync());
        var preview=await Repo().PreviewReferenceInformationAsync(Request with{RefreshExisting=true},Actor);Assert.Equal(initial.ManifestHash,preview.PreviousManifestHash);
        var id=Guid.NewGuid().ToString("N");var archive=$"prfi:old:{runId}:{initial.ManifestHash}";
        var retained=await WitnessAsync($"prfi:run:{runId}",archive,$"prfi:req:{id}");
        await Repo().ApplyScopedReferenceInformationAsync(Client,new(preview.ReviewToken,id,true),Actor);
        Assert.Equal(retained,await WitnessAsync($"prfi:run:{runId}",archive,$"prfi:req:{id}"));
        Assert.Equal(normalBefore,ReferencePdfContent((await Repo(false).CreatePdfAsync(Client,runId,Two))!));
        await using(var db=await F.OpenAsync())
        {
            var raw=JsonNode.Parse((await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97790 AND ModuleCode=@Code",new{Code=$"prfi:run:{runId}"}))!)!;
            var manifest=JsonNode.Parse(Protector.UnprotectPayrollReferenceInformation(raw["value"]!.ToString())!)!;
            Assert.Equal(sourceHash,manifest["sourceHash"]!.ToString()); // Original signed canonical source, not rebuilt links.
        }
    }
    [LocalTheory][InlineData("batchUnknown")][InlineData("sourceUnknown")][InlineData("financialTail")]
    public async Task Original_historical_raw_unknown_properties_and_tiny_financial_tail_changes_hold_reader_and_refresh(string change)
    {
        await InitialReferenceAsync();await using(var db=await F.OpenAsync())
        {
            var sql=change switch{
                "batchUnknown"=>"UPDATE excel_payslip_batches SET batch_json=JSON_SET(batch_json,'$.unknownHistoricalSource','retain exact unknown data') WHERE client_id=97790 AND id=@Id",
                "sourceUnknown"=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.unknownHistoricalSource','retain exact unknown data') WHERE client_id=97790 AND batch_id=@Id",
                _=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.rows[1].cells[4].value','22500.000000000000001') WHERE client_id=97790 AND batch_id=@Id"};
            await db.ExecuteAsync(sql,new{Id=Batch});await db.ExecuteAsync("UPDATE payruns SET Status='Approved' WHERE ClientId=97790");
        }
        var before=await WitnessAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>ReadAsync(false));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Repo(false).CreatePdfAsync(Client,runId,Two));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Repo().PreviewReferenceInformationAsync(Request with{RefreshExisting=true},Actor));Assert.Equal(before,await WitnessAsync());
    }
    [LocalFact]
    public async Task Initial_capture_requires_fresh_full_canonical_signed_match_without_inventing_an_old_link_snapshot()
    {
        await AddCanonicalProfileLinksAsync();var before=await WitnessAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Repo().PreviewReferenceInformationAsync(Request,Actor));Assert.Equal(before,await WitnessAsync());
    }
}

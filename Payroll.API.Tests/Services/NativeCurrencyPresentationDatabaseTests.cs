using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;
using PdfSharp.Pdf.IO;

namespace Payroll.API.Tests.Services;

public sealed partial class NativeCurrencyStageDatabaseTests
{
    private static string CurrencyPdfContent(PayslipDesignPdf value)
    {
        using var document=PdfReader.Open(new MemoryStream(value.Bytes),PdfDocumentOpenMode.Import);
        return Encoding.Latin1.GetString(document.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue);
    }
    private async Task<(long Template,int Run)> CapturedScopedRunAsync()
    {
        var seed=await SeedScopedClassicAsync();await ScopedDesigns.SaveAsync(await ScopeRequestAsync(seed.Id,["2026-09"]),Actor);
        var repo=Runs(true);var run=await repo.CreateAsync(Request());Assert.NotNull(run);
        Assert.NotNull(await repo.SubmitForApprovalAsync(run.Id));Assert.NotNull(await repo.ApproveAsync(run.Id));return(seed.Id,run.Id);
    }

    [LocalFact]
    public async Task Currency_receipt_survives_cosmetics_Info_visibility_order_and_adding_a_different_payroll_month()
    {
        var saved=await CapturedScopedRunAsync();var before=await CompleteRecaptureWitness(saved.Run);
        var profile=await StoredDesignProfileAsync(saved.Template);var request=await ScopeRequestAsync(saved.Template,["2026-09","2026-10"]);
        request.ExistingProfileHash=profile.RequestHash;request.Template.Name="Cosmetic title";request.Template.Note="Cosmetic footer";
        request.Template.ShowLogo=false;request.Template.ShowClient=false;request.Template.ShowYtd=true;request.Template.ShowBank=true;
        request.Fields.Reverse();foreach(var field in request.Fields.Where(f=>f.Kind=="info"))field.Visible=false;
        request.Fields.Single(f=>f.Label=="ESI"&&f.Kind=="deduction").Visible=false;
        await ScopedDesigns.SaveAsync(request,Actor);
        var pdf=await new PayslipDesignRepository(Config(true),Protector,new()).CreatePdfAsync(Client,saved.Run,One);Assert.NotNull(pdf);
        var content=CurrencyPdfContent(pdf);Assert.Contains("(986)",content);Assert.DoesNotContain("(987)",content);
        Assert.Equal(before,await CompleteRecaptureWitness(saved.Run));
    }

    [LocalFact]
    public async Task Currency_receipt_reads_its_original_source_after_same_schema_future_blueprint_selection()
    {
        var saved=await CapturedScopedRunAsync();var before=await CompleteRecaptureWitness(saved.Run);
        var future=JsonSerializer.Deserialize<ExcelPayslipBatch>(JsonSerializer.Serialize(batch,Json),Json)!;
        future.Id="97728000000000000000000000000003";future.Month="2026-10";future.SourceFileName="Synthetic future October.xlsx";
        await using var db=await F.OpenAsync();
        await db.ExecuteAsync("INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(97728,@Id,CAST(@Batch AS JSON),JSON_OBJECT(),UTC_TIMESTAMP());INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(97728,@Id,CAST(@Source AS JSON),UTC_TIMESTAMP())",
            new{Id=future.Id,Batch=JsonSerializer.Serialize(future,Json),Source=JsonSerializer.Serialize(source,Json)});
        var catalog=await ScopedDesigns.PreviewAsync(new(){ClientId=Client,SourceBatchId=future.Id});
        var update=await ScopeRequestAsync(saved.Template,["2026-09","2026-10"]);
        update.ExistingProfileHash=(await StoredDesignProfileAsync(saved.Template)).RequestHash;update.SourceBatchId=future.Id;update.SourceHash=catalog.SourceHash;
        update.Fields=catalog.Fields.Select(f=>new PayslipDesignField{SourceColumn=f.SourceColumn,Label=f.Label,Kind=f.Kind,InfoToken=f.InfoToken,
            Visible=f.Kind!="info"||f.InfoToken.Length>0,NativeCode=f.Kind=="earning"?Wage:f.Kind=="deduction"?f.Label=="ESI"?Esi:Pf:""}).ToList();
        await ScopedDesigns.SaveAsync(update,Actor);
        var pdf=await new PayslipDesignRepository(Config(true),Protector,new()).CreatePdfAsync(Client,saved.Run,One);Assert.NotNull(pdf);
        Assert.Contains("(986)",CurrencyPdfContent(pdf));Assert.Equal(before,await CompleteRecaptureWitness(saved.Run));
        // The CURRENT blueprint is not a replacement for the receipt's ORIGINAL
        // raw financial source. Only a change to the original must break it.
        await db.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.originalTamper',TRUE) WHERE client_id=97728 AND batch_id=@Id",new{Id=BatchId});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new PayslipDesignRepository(Config(true),Protector,new()).CreatePdfAsync(Client,saved.Run,One));
    }

    [LocalFact]
    public async Task Existing_protected_currency_remains_mandatory_after_the_new_capture_flag_is_disabled()
    {
        var saved=await CapturedScopedRunAsync();var before=await CompleteRecaptureWitness(saved.Run);
        var designs=new PayslipDesignRepository(Config(false),Protector,new());
        var pdf=await designs.CreatePdfAsync(Client,saved.Run,One);Assert.NotNull(pdf);Assert.Contains("(986)",CurrencyPdfContent(pdf));
        var row=await ReadAsync(Runs(false),saved.Run);Assert.NotNull(row);
        await using var db=await F.OpenAsync();await db.ExecuteAsync("UPDATE modulesettings SET IsEnabled=FALSE WHERE client_id=97728 AND ModuleCode=@Code",new{Code="pcst.active:"+saved.Run});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>designs.CreatePdfAsync(Client,saved.Run,One));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new EssMssRepository(Config(false),Protector).GetPayslipDocumentAsync(One,Client,saved.Run));
        Assert.Equal(before,await CompleteRecaptureWitness(saved.Run));
    }

    [LocalFact]
    public async Task Currency_raw_provenance_allows_profile_link_maintenance_but_holds_unknown_batch_metadata()
    {
        var saved=await CapturedScopedRunAsync();await using var db=await F.OpenAsync();
        var link=new ExcelPayslipProfileLink(batch.Rows[0].Id,batch.Rows[0].SourceRow,One,new string('a',64));
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=JSON_SET(batch_json,'$.profileLinks',CAST(@Links AS JSON)) WHERE client_id=97728 AND id=@Id",new{Id=BatchId,Links=JsonSerializer.Serialize(new[]{link},Json)});
        var designs=new PayslipDesignRepository(Config(false),Protector,new());Assert.Contains("(986)",CurrencyPdfContent((await designs.CreatePdfAsync(Client,saved.Run,One))!));
        link=link with{SourceIdentityHash=new string('b',64)};
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=JSON_SET(batch_json,'$.profileLinks',CAST(@Links AS JSON)) WHERE client_id=97728 AND id=@Id",new{Id=BatchId,Links=JsonSerializer.Serialize(new[]{link},Json)});
        Assert.Contains("(986)",CurrencyPdfContent((await designs.CreatePdfAsync(Client,saved.Run,One))!));
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=JSON_SET(batch_json,'$.unknownOriginalMetadata',TRUE) WHERE client_id=97728 AND id=@Id",new{Id=BatchId});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>designs.CreatePdfAsync(Client,saved.Run,One));
    }

    [LocalTheory]
    [InlineData("scope")][InlineData("theme")][InlineData("mapping")]
    public async Task Captured_currency_cannot_be_bypassed_by_current_scope_theme_or_financial_mapping_change(string change)
    {
        var saved=await CapturedScopedRunAsync();var before=await CompleteRecaptureWitness(saved.Run);
        await using var db=await F.OpenAsync();
        if(change=="theme")await db.ExecuteAsync("UPDATE paysliptemplates SET Theme='Classic' WHERE Id=@Id AND ClientId=97728",new{Id=saved.Template});
        else
        {
            var update=await ScopeRequestAsync(saved.Template,change=="scope"?["2026-10"]:["2026-09"]);
            update.ExistingProfileHash=(await StoredDesignProfileAsync(saved.Template)).RequestHash;
            if(change=="mapping")
            {
                update.Fields.Single(f=>f.Label=="PF"&&f.Kind=="deduction").NativeCode=Esi;
                update.Fields.Single(f=>f.Label=="ESI"&&f.Kind=="deduction").NativeCode=Pf;
            }
            await ScopedDesigns.SaveAsync(update,Actor);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new PayslipDesignRepository(Config(false),Protector,new()).CreatePdfAsync(Client,saved.Run,One));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new EssMssRepository(Config(false),Protector).GetPayslipDocumentAsync(One,Client,saved.Run));
        Assert.Equal(before,await CompleteRecaptureWitness(saved.Run));
    }
}

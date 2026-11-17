using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed partial class PayrollReferenceInformationDatabaseTests
{
    private async Task<PayrollReferenceInformationReceipt> InitialReferenceAsync()
    {var preview=await Repo().PreviewReferenceInformationAsync(Request,Actor);return await Repo().ApplyReferenceInformationAsync(new(preview.ReviewToken,Guid.NewGuid().ToString(),true),Actor);}
    [LocalFact]
    public async Task Real_cosmetic_save_audit_visibility_order_precision_logo_footer_does_not_require_reference_refresh()
    {
        var initial=await InitialReferenceAsync();await using var db=await F.OpenAsync();
        var pointer=await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97790 AND ModuleCode=@Code",new{Code=$"prfi:run:{runId}"});
        PayslipDesignProfile profile;PayslipDesignTemplate template;
        await using(var tx=await db.BeginTransactionAsync()){var design=await DesignAsync(db,tx);profile=design.Profile;template=design.Template;await tx.RollbackAsync();}
        template.Note="Reviewed cosmetic footer";template.ShowLogo=false;template.ShowClient=false;template.Name="Cosmetic display name";
        profile.Fields.Single(field=>field.SourceColumn==3).Visible=false;profile.Fields.Reverse();
        var metadata=await Repo().PreviewAsync(new(){ClientId=Client,SourceBatchId=OtherBatch});
        await Repo().SaveAsync(new(){RequestId=Guid.NewGuid().ToString(),Reviewed=true,Template=template,SourceBatchId=OtherBatch,
            SourceHash=metadata.SourceHash,ExistingProfileHash=profile.RequestHash,AmountDecimalPlaces=0,Fields=profile.Fields},Actor);
        await db.ExecuteAsync("UPDATE payruns SET Status='Approved' WHERE ClientId=97790");var before=await WitnessAsync();
        Assert.Equal(25000,(await ReadAsync(false)).Value);var content=ReferencePdfContent((await Repo(false).CreatePdfAsync(Client,runId,Two))!);
        Assert.DoesNotContain("Current CTC",content);Assert.Contains("Reviewed cosmetic footer",content);
        Assert.Equal(pointer,await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97790 AND ModuleCode=@Code",new{Code=$"prfi:run:{runId}"}));
        Assert.Equal(before,await WitnessAsync());Assert.False(initial.Refreshed);
    }
    [LocalFact]
    public async Task Adding_another_month_and_selecting_same_target_schema_new_blueprint_preserves_bound_old_month()
    {
        await InitialReferenceAsync();await using var db=await F.OpenAsync();
        var code=$"payslip.design:{designId}";var envelope=JsonNode.Parse((await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97790 AND ModuleCode=@Code",new{Code=code}))!)!;
        var profile=JsonNode.Parse(Protector.UnprotectPayslipDesign(envelope["value"]!.ToString())!)!;
        var prior=await db.QuerySingleAsync<PayslipDesignTemplate>("SELECT * FROM paysliptemplates WHERE Id=@Id AND ClientId=97790",new{Id=designId});prior.Theme="Classic";
        profile["payPeriods"]=new JsonArray("2026-09","2026-10");profile["previousClassicTemplate"]=JsonSerializer.SerializeToNode(prior,Json);
        envelope["value"]=Protector.ProtectPayslipDesign(profile.ToJsonString());await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Json AS JSON) WHERE client_id=97790 AND ModuleCode=@Code",new{Code=code,Json=envelope.ToJsonString()});
        Assert.Equal(25000,(await ReadAsync(false)).Value);
        const string next="97790000000000000000000000000004";
        var nextBatch=JsonSerializer.Deserialize<ExcelPayslipBatch>(JsonSerializer.Serialize(batch,Json),Json)!;nextBatch.Id=next;nextBatch.Month="2026-11";
        await StoreSource(db,nextBatch,source);var metadata=await Repo().PreviewAsync(new(){ClientId=Client,SourceBatchId=next});
        profile["sourceBatchId"]=next;profile["sourceHash"]=metadata.SourceHash;profile["payPeriods"]=new JsonArray("2026-09","2026-10","2026-11");
        envelope["value"]=Protector.ProtectPayslipDesign(profile.ToJsonString());await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Json AS JSON) WHERE client_id=97790 AND ModuleCode=@Code",new{Code=code,Json=envelope.ToJsonString()});
        var before=await WitnessAsync();Assert.Equal(25000,(await ReadAsync(false)).Value);Assert.Equal(before,await WitnessAsync());
    }
    [LocalFact]
    public async Task Major_monthly_target_correction_holds_then_explicit_refresh_updates_same_key_archives_old_and_old_replay_never_restores()
    {
        var firstPreview=await Repo().PreviewReferenceInformationAsync(Request,Actor);var oldCommand=new PayrollReferenceInformationApplyRequest(firstPreview.ReviewToken,Guid.NewGuid().ToString("N"),true);
        var old=await Repo().ApplyReferenceInformationAsync(oldCommand,Actor);await MutateAsync("profile");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ReadAsync(false));
        var preview=await Repo().PreviewReferenceInformationAsync(Request with{RefreshExisting=true},Actor);Assert.Equal(old.ManifestHash,preview.PreviousManifestHash);Assert.True(preview.RefreshExisting);
        var id=Guid.NewGuid().ToString("N");var archive=$"prfi:old:{runId}:{old.ManifestHash}";
        var before=await WitnessAsync($"prfi:run:{runId}",archive,$"prfi:req:{id}");
        var command=new PayrollReferenceInformationApplyRequest(preview.ReviewToken,id,true);var updated=await Repo().ApplyScopedReferenceInformationAsync(Client,command,Actor);
        Assert.True(updated.Refreshed);Assert.Equal(old.ManifestHash,updated.PreviousManifestHash);Assert.Equal(archive,updated.PreviousManifestArchiveCode);
        Assert.Equal(before,await WitnessAsync($"prfi:run:{runId}",archive,$"prfi:req:{id}"));Assert.Equal("Reviewed monthly earnings budget",(await ReadAsync(false)).Label);
        var after=await WitnessAsync();Assert.Equal(old,await Repo().ApplyReferenceInformationAsync(oldCommand,Actor));Assert.Equal(updated,await Repo().ApplyReferenceInformationAsync(command,Actor));Assert.Equal(after,await WitnessAsync());
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE payruns SET Status='Approved' WHERE ClientId=97790");
        var beforePdf=await WitnessAsync();var content=ReferencePdfContent((await Repo(false).CreatePdfAsync(Client,runId,Two))!);
        await using(var db=await F.OpenAsync())await using(var tx=await db.BeginTransactionAsync())
        {
            var design=await DesignAsync(db,tx);var expected=JsonSerializer.Deserialize<ExcelPayslipRow>(JsonSerializer.Serialize(batch.Rows[1],Json),Json)!;
            expected.Information[0].Label=preview.Label;
            var expectedPdf=new Payroll.API.Services.ExcelPayslipPdfService().Create(batch,[expected],amountDecimalPlaces:design.Profile.AmountDecimalPlaces,
                presentation:Payroll.API.Services.NativePayslipDesignService.Presentation(design.Profile,design.Template));
            Assert.Equal(ReferencePdfContent(new(expectedPdf,"reviewed-source.pdf")),content);await tx.RollbackAsync();
        }
        Assert.Contains("25000",content);Assert.Equal(beforePdf,await WitnessAsync());
    }
    [LocalFact]
    public async Task Refresh_final_receipt_failure_rolls_back_archive_and_same_key_replacement_exactly()
    {
        await InitialReferenceAsync();await MutateAsync("profile");var preview=await Repo().PreviewReferenceInformationAsync(Request with{RefreshExisting=true},Actor);
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("CREATE TRIGGER codex_prfi_fail_97790 BEFORE INSERT ON modulesettings FOR EACH ROW BEGIN IF NEW.client_id=97790 AND NEW.ModuleCode LIKE 'prfi:req:%' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='owned reference refresh rollback'; END IF; END");
        var before=await WitnessAsync();await Assert.ThrowsAsync<MySqlConnector.MySqlException>(()=>Repo().ApplyReferenceInformationAsync(new(preview.ReviewToken,Guid.NewGuid().ToString(),true),Actor));Assert.Equal(before,await WitnessAsync());
    }
    [LocalTheory][InlineData(false)][InlineData(true)]
    public async Task Concurrent_refresh_is_one_old_hash_winner_or_stable_same_guid_replay(bool sameGuid)
    {
        await InitialReferenceAsync();await MutateAsync("profile");var preview=await Repo().PreviewReferenceInformationAsync(Request with{RefreshExisting=true},Actor);
        var one=new PayrollReferenceInformationApplyRequest(preview.ReviewToken,Guid.NewGuid().ToString("N"),true);var two=one with{RequestId=sameGuid?one.RequestId:Guid.NewGuid().ToString("N")};
        var permitted=new[]{$"prfi:run:{runId}",$"prfi:old:{runId}:{preview.PreviousManifestHash}",$"prfi:req:{one.RequestId}",$"prfi:req:{two.RequestId}"};var before=await WitnessAsync(permitted);
        var first=Record.ExceptionAsync(()=>Repo().ApplyReferenceInformationAsync(one,Actor));var second=Record.ExceptionAsync(()=>Repo().ApplyReferenceInformationAsync(two,Actor));var results=await Task.WhenAll(first,second);
        Assert.Equal(sameGuid?0:1,results.Count(error=>error is not null));if(!sameGuid)Assert.IsType<InvalidOperationException>(results.Single(error=>error is not null));
        Assert.Equal(25000,(await ReadAsync(false)).Value);
        Assert.Equal(before,await WitnessAsync(permitted));
        await using var db=await F.OpenAsync();Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97790 AND ModuleCode LIKE 'prfi:old:%'"));
        Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97790 AND ModuleCode IN @Codes",new{Codes=new[]{$"prfi:req:{one.RequestId}",$"prfi:req:{two.RequestId}"}}));
    }
    [LocalTheory][InlineData("CURRENT_ANNUAL_CTC","Current CTC (source)")][InlineData("SAVED_MONTHLY_GROSS","Annual CTC")][InlineData("SAVED_MONTHLY_GROSS","CTC (P.A.)")]
    public async Task Monthly_budget_never_relabels_as_current_annual_ctc_or_an_annual_amount(string token,string label)
    {
        await using(var db=await F.OpenAsync())
        {
            var code=$"payslip.design:{designId}";var envelope=JsonNode.Parse((await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97790 AND ModuleCode=@Code",new{Code=code}))!)!;
            var profile=JsonNode.Parse(Protector.UnprotectPayslipDesign(envelope["value"]!.ToString())!)!;var field=profile["fields"]!.AsArray().Single(value=>value!["sourceColumn"]!.GetValue<int>()==3)!;
            field["label"]=label;field["infoToken"]=token;envelope["value"]=Protector.ProtectPayslipDesign(profile.ToJsonString());
            await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Json AS JSON) WHERE client_id=97790 AND ModuleCode=@Code",new{Code=code,Json=envelope.ToJsonString()});
        }
        var before=await WitnessAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>Repo().PreviewReferenceInformationAsync(Request,Actor));Assert.Equal(before,await WitnessAsync());
    }
}

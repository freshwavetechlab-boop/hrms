using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using PdfSharp.Pdf.IO;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Payroll.API.Tests.Services;

public sealed partial class NativeCurrencyStageDatabaseTests
{
    private PayslipDesignRepository ScopedDesigns => new(new ConfigurationBuilder().AddConfiguration(Config(true))
        .AddInMemoryCollection(new Dictionary<string,string?>{[PayslipDesignRepository.ScopedWritesFlag]="true"}).Build(),Protector,new());
    private async Task<(long Id,int June)> SeedScopedClassicAsync()
    {
        await using var db=await F.OpenAsync();
        var id=await db.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(Id),0)+1 FROM paysliptemplates");
        await db.ExecuteAsync("INSERT INTO paysliptemplates(Id,ClientId,ClientRef,Name,Theme,ShowLogo,ShowClient,ShowYtd,ShowBank,Note,Active) VALUES(@Id,97728,'97728','Retained June Classic','Classic',FALSE,FALSE,TRUE,TRUE,'Retained June footer',TRUE)",new{Id=id});
        var june=await db.ExecuteScalarAsync<int>("INSERT INTO payruns(ClientId,ClientName,PayPeriod,RunCode,RunType,RunName,PayDate,TotalWorkingDays,Status,PayrollCost,NetPay) VALUES(97728,'Synthetic currency client','2026-06',@Code,'Regular','Retained June','2026-07-01',30,'Approved',1000,890);SELECT LAST_INSERT_ID();",new{Code="CURRENCY-JUNE-"+Guid.NewGuid().ToString("N")[..8]});
        var details=JsonSerializer.Serialize(new object[]{
            new{Id="BASIC",Name="June BASIC retained",Category="Earning",monthlyAmount=1000,amount=1000,ProRata=false},
            new{Id="PF",Name="June PF retained",Category="Deduction",monthlyAmount=100,amount=100,ProRata=false},
            new{Id="ESIC",Name="June ESIC retained",Category="Deduction",monthlyAmount=10,amount=10,ProRata=false},
            new{Id="EPS",Name="June EPS retained",Category="Benefit",monthlyAmount=12,amount=12,ProRata=false}},Json);
        await db.ExecuteAsync("INSERT INTO payrunemployees(PayRunId,EmployeeId,ClientId,EmployeeCode,EmployeeName,Department,PresentDays,PayableDays,MonthlyGross,GrossPay,StatutoryDeductions,OneTimeEarnings,OneTimeDeductions,NetPay,IsSkipped,PaymentStatus,DetailsJson) VALUES(@June,9772801,97728,'CURR-SYN-1','Person One','Synthetic',26,26,1000,1000,110,0,0,890,FALSE,'Pending',CAST(@Details AS JSON))",new{June=june,Details=details});
        return(id,june);
    }

    private async Task<PayslipDesignSaveRequest> ScopeRequestAsync(long id,List<string>? periods)
    {
        var catalog=await Designs.PreviewAsync(new(){ClientId=Client,SourceBatchId=BatchId});
        return new(){RequestId=Guid.NewGuid().ToString("N"),Reviewed=true,Template=new(){Id=id,ClientId=Client,Name="September source design",Theme=NativePayslipDesignService.Theme,
            ShowLogo=true,ShowClient=true,ShowYtd=false,ShowBank=false,Note="September source footer"},SourceBatchId=BatchId,SourceHash=catalog.SourceHash,
            PayPeriods=periods,AmountDecimalPlaces=0,Fields=catalog.Fields.Select(f=>new PayslipDesignField{SourceColumn=f.SourceColumn,Label=f.Label,Kind=f.Kind,
                InfoToken=f.InfoToken,Visible=f.Kind!="info"||f.InfoToken.Length>0,NativeCode=f.Kind=="earning"?Wage:f.Kind=="deduction"?f.Label=="ESI"?Esi:Pf:""}).ToList()};
    }
    private async Task<PayslipDesignProfile> StoredDesignProfileAsync(long id)
    {
        await using var db=await F.OpenAsync();var envelope=await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97728 AND ModuleCode=@Code",new{Code="payslip.design:"+id});
        return JsonSerializer.Deserialize<PayslipDesignProfile>(Protector.UnprotectPayslipDesign(JsonNode.Parse(envelope)!["value"]!.GetValue<string>())!,Json)!;
    }

    [LocalFact]
    public async Task Scoped_conversion_preserves_exact_approved_June_Classic_HTML_YTD_split_output_and_template_count()
    {
        var seed=await SeedScopedClassicAsync();var ess=new EssMssRepository(Config(true),Protector);
        var before=await ess.GetPayslipDocumentAsync(One,Client,seed.June);Assert.NotNull(before);Assert.False(before.PdfPreferred);
        Assert.Contains("Retained June footer",before.Html);Assert.Contains("June BASIC retained",before.Html);Assert.Contains("YTD Gross:",before.Html);
        var savedBefore=await CompleteRecaptureWitness(seed.June);
        var splitBefore=JsonSerializer.Serialize(await ess.GetLocationPayslipDocumentsAsync(Client,seed.June),Json);
        await using var db=await F.OpenAsync();var count=await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM paysliptemplates WHERE ClientId=97728");
        var originals=JsonSerializer.Serialize(await db.QuerySingleAsync<PayslipDesignTemplate>("SELECT * FROM paysliptemplates WHERE Id=@Id",new{Id=seed.Id}),Json);
        var other=JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM paysliptemplates WHERE ClientId=97728 AND Id<>@Id ORDER BY Id",new{Id=seed.Id}),Json);
        var request=await ScopeRequestAsync(seed.Id,["2026-09"]);await ScopedDesigns.SaveAsync(request,Actor);
        var after=await ess.GetPayslipDocumentAsync(One,Client,seed.June);Assert.NotNull(after);Assert.False(after.PdfPreferred);Assert.Equal(before.Html,after.Html);
        Assert.Equal(splitBefore,JsonSerializer.Serialize(await ess.GetLocationPayslipDocumentsAsync(Client,seed.June),Json));
        Assert.Equal(savedBefore,await CompleteRecaptureWitness(seed.June));
        Assert.Null(await new PayslipDesignRepository(Config(true),Protector,new()).CreatePdfAsync(Client,seed.June,One));
        Assert.Equal(count,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM paysliptemplates WHERE ClientId=97728"));
        Assert.Equal(other,JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM paysliptemplates WHERE ClientId=97728 AND Id<>@Id ORDER BY Id",new{Id=seed.Id}),Json));
        var profile=await StoredDesignProfileAsync(seed.Id);Assert.Equal(new[]{"2026-09"},profile.PayPeriods);Assert.NotNull(profile.PreviousClassicTemplate);
        Assert.True(NativeRecaptureJson.Equivalent(originals,JsonSerializer.Serialize(profile.PreviousClassicTemplate,Json)));
        // Outside-scope rendering does not require a now-stale source or any
        // currency/reference provider. Exact protected Classic settings suffice.
        await db.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.staleSource',TRUE) WHERE client_id=97728");
        Assert.Equal(before.Html,(await ess.GetPayslipDocumentAsync(One,Client,seed.June))!.Html);
        await using var tx=await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,isReadOnly:true);
        Assert.Null(await new PayslipDesignRepository(Config(true),Protector,new()).ReadNativeSourceDesignBindingAsync(db,tx,Client,payPeriod:"2026-06"));
        Assert.Null(await RecaptureRuns().ReadNativeCurrencyRunStageAsync(db,tx,Client,seed.June));await tx.RollbackAsync();
    }

    [LocalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task Scoped_September_normal_capture_or_Draft_recapture_still_generates_the_approved_frozen_PDF(bool recapture)
    {
        var seed=await SeedScopedClassicAsync();await ScopedDesigns.SaveAsync(await ScopeRequestAsync(seed.Id,["2026-09"]),Actor);
        var repo=RecaptureRuns();PayRun run;
        if(recapture)
        {
            run=await LegacyDraftAsync();var preview=await repo.PreviewNativeCurrencyRecaptureAsync(run.Id,Client,"2026-09",RecaptureActor);
            await repo.ApplyNativeCurrencyRecaptureAsync(run.Id,Command(preview),RecaptureActor);
        }
        else run=(await Runs(true).CreateAsync(Request()))!;
        Assert.NotNull(await repo.SubmitForApprovalAsync(run.Id));Assert.NotNull(await repo.ApproveAsync(run.Id));
        var ess=new EssMssRepository(Config(true),Protector);Assert.True((await ess.GetPayslipDocumentAsync(One,Client,run.Id))!.PdfPreferred);
        var pdf=await new PayslipDesignRepository(Config(true),Protector,new()).CreatePdfAsync(Client,run.Id,One);Assert.NotNull(pdf);
        using var document=PdfReader.Open(new MemoryStream(pdf.Bytes),PdfDocumentOpenMode.Import);
        var content=Encoding.Latin1.GetString(document.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue);
        Assert.Contains("(986)",content);Assert.DoesNotContain("(987)",content);Assert.Contains("7,173",content);
        Assert.NotNull(await ReadAsync(repo,run.Id));
    }

    [LocalFact]
    public async Task Scoped_updates_preserve_omitted_scope_and_server_fallback_and_require_explicit_All_to_widen()
    {
        var seed=await SeedScopedClassicAsync();var request=await ScopeRequestAsync(seed.Id,["2026-09"]);await ScopedDesigns.SaveAsync(request,Actor);
        Assert.Equal(seed.Id,(await ScopedDesigns.SaveAsync(request,Actor)).Id);var first=await StoredDesignProfileAsync(seed.Id);
        var update=await ScopeRequestAsync(seed.Id,null);update.ExistingProfileHash=first.RequestHash;update.Template.Note="Edited September footer";
        await ScopedDesigns.SaveAsync(update,Actor);var second=await StoredDesignProfileAsync(seed.Id);
        Assert.Equal(first.PayPeriods,second.PayPeriods);Assert.Equal(JsonSerializer.Serialize(first.PreviousClassicTemplate,Json),JsonSerializer.Serialize(second.PreviousClassicTemplate,Json));
        var stale=await ScopeRequestAsync(seed.Id,["2026-10"]);stale.ExistingProfileHash=first.RequestHash;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ScopedDesigns.SaveAsync(stale,Actor));
        var widen=await ScopeRequestAsync(seed.Id,null);widen.ExistingProfileHash=second.RequestHash;widen.AllPayPeriods=true;
        var held=await Assert.ThrowsAsync<InvalidOperationException>(()=>ScopedDesigns.SaveAsync(widen,Actor));Assert.Contains("BASIC",held.Message);Assert.Contains("EPS",held.Message);
        var third=await StoredDesignProfileAsync(seed.Id);Assert.Equal(second.PayPeriods,third.PayPeriods);
        Assert.Equal(JsonSerializer.Serialize(first.PreviousClassicTemplate,Json),JsonSerializer.Serialize(third.PreviousClassicTemplate,Json));
    }

    [LocalFact]
    public async Task Initial_All_conversion_holds_unmapped_approved_June_but_selected_September_preserves_Classic()
    {
        var seed=await SeedScopedClassicAsync();await using var db=await F.OpenAsync();
        var before=JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM paysliptemplates WHERE ClientId=97728 ORDER BY Id"),Json);
        var all=await ScopeRequestAsync(seed.Id,null);all.AllPayPeriods=true;
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>ScopedDesigns.SaveAsync(all,Actor));Assert.Contains("BASIC",error.Message);Assert.Contains("EPS",error.Message);Assert.Contains("Selected payroll months",error.Message);
        Assert.Equal(before,JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM paysliptemplates WHERE ClientId=97728 ORDER BY Id"),Json));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97728 AND ModuleCode=@Code",new{Code="payslip.design:"+seed.Id}));
        await ScopedDesigns.SaveAsync(await ScopeRequestAsync(seed.Id,["2026-09"]),Actor);
        Assert.False((await new EssMssRepository(Config(true),Protector).GetPayslipDocumentAsync(One,Client,seed.June))!.PdfPreferred);
    }

    [LocalFact]
    public async Task Explicit_All_widening_allows_complete_compatible_money_even_when_mapped_fields_are_hidden()
    {
        var seed=await SeedScopedClassicAsync();await using var db=await F.OpenAsync();
        var details=JsonSerializer.Serialize(new object[]{
            new{Id=Wage,Name="Compatible wages",Category="Earning",monthlyAmount=1000,amount=1000},
            new{Id=Pf,Name="Compatible PF",Category="Deduction",monthlyAmount=100,amount=100},
            new{Id=Esi,Name="Compatible ESI",Category="Deduction",monthlyAmount=10,amount=10},
            new{Id="UNMAPPED_ZERO",Name="Zero is not missing money",Category="Benefit",monthlyAmount=0,amount=0},
            new{Id="SUMMARY_ONLY",Name="Summary is not a component",Category="Summary",monthlyAmount=99999,amount=99999}},Json);
        await db.ExecuteAsync("UPDATE payrunemployees SET DetailsJson=CAST(@Details AS JSON) WHERE PayRunId=@Id",new{Id=seed.June,Details=details});
        await ScopedDesigns.SaveAsync(await ScopeRequestAsync(seed.Id,["2026-09"]),Actor);
        var original=await StoredDesignProfileAsync(seed.Id);var all=await ScopeRequestAsync(seed.Id,null);all.AllPayPeriods=true;all.ExistingProfileHash=original.RequestHash;
        foreach(var field in all.Fields.Where(f=>f.Kind!="info"))field.Visible=false;
        await ScopedDesigns.SaveAsync(all,Actor);var current=await StoredDesignProfileAsync(seed.Id);Assert.Null(current.PayPeriods);
        Assert.Equal(JsonSerializer.Serialize(original.PreviousClassicTemplate,Json),JsonSerializer.Serialize(current.PreviousClassicTemplate,Json));
    }

    [LocalTheory]
    [InlineData("[]")][InlineData("{}")][InlineData("[{\"Id\":\"BASIC\",\"Category\":\"Earning\",\"amount\":1000}]")]
    public async Task Initial_conversion_holds_malformed_or_incomplete_approved_money_evidence(string details)
    {
        var seed=await SeedScopedClassicAsync();await using var db=await F.OpenAsync();
        await db.ExecuteAsync("UPDATE payrunemployees SET DetailsJson=CAST(@Details AS JSON) WHERE PayRunId=@Id",new{Id=seed.June,Details=details});
        var request=await ScopeRequestAsync(seed.Id,null);request.AllPayPeriods=true;
        await Assert.ThrowsAnyAsync<Exception>(()=>ScopedDesigns.SaveAsync(request,Actor));
        Assert.Equal("Classic",await db.ExecuteScalarAsync<string>("SELECT Theme FROM paysliptemplates WHERE Id=@Id",new{Id=seed.Id}));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97728 AND ModuleCode=@Code",new{Code="payslip.design:"+seed.Id}));
    }

    [LocalFact]
    public async Task Scoped_save_rejects_missing_original_Classic_forged_fallback_invalid_months_and_missing_protection()
    {
        var noOriginal=await ScopeRequestAsync(0,["2026-09"]);await Assert.ThrowsAsync<InvalidOperationException>(()=>ScopedDesigns.SaveAsync(noOriginal,Actor));
        var seed=await SeedScopedClassicAsync();var request=await ScopeRequestAsync(seed.Id,["2026-09","2026-09"]);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ScopedDesigns.SaveAsync(request,Actor));request.PayPeriods=["2026-09"];
        var node=JsonSerializer.SerializeToNode(request,Json)!.AsObject();node["previousClassicTemplate"]=new JsonObject{{"note","Forged fallback"},{"theme","Classic"},{"id",seed.Id},{"clientId",Client}};
        await ScopedDesigns.SaveAsync(node.Deserialize<PayslipDesignSaveRequest>(Json)!,Actor);
        var profile=await StoredDesignProfileAsync(seed.Id);Assert.Equal("Retained June footer",profile.PreviousClassicTemplate!.Note);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new EssMssRepository(Config(true)).GetPayslipDocumentAsync(One,Client,seed.June));
        await using var db=await F.OpenAsync();await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=JSON_OBJECT('value','tampered') WHERE client_id=97728 AND ModuleCode=@Code",new{Code="payslip.design:"+seed.Id});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new EssMssRepository(Config(true),Protector).GetPayslipDocumentAsync(One,Client,seed.June));
    }

    [LocalFact]
    public async Task Scoped_write_gate_is_default_off_preserves_Classic_and_does_not_disable_authenticated_existing_readers()
    {
        var seed=await SeedScopedClassicAsync();var request=await ScopeRequestAsync(seed.Id,["2026-09"]);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>Designs.SaveAsync(request,Actor));Assert.Contains("compatible",error.Message);
        await using var db=await F.OpenAsync();Assert.Equal("Classic",await db.ExecuteScalarAsync<string>("SELECT Theme FROM paysliptemplates WHERE Id=@Id",new{Id=seed.Id}));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97728 AND ModuleCode=@Code",new{Code="payslip.design:"+seed.Id}));
        var ess=new EssMssRepository(F.Config,Protector);var before=(await ess.GetPayslipDocumentAsync(One,Client,seed.June))!.Html;
        await ScopedDesigns.SaveAsync(request,Actor);var profile=await StoredDesignProfileAsync(seed.Id);
        Assert.Equal(before,(await ess.GetPayslipDocumentAsync(One,Client,seed.June))!.Html);
        var edit=await ScopeRequestAsync(seed.Id,null);edit.ExistingProfileHash=profile.RequestHash;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Designs.SaveAsync(edit,Actor));
        edit.AllPayPeriods=true;await Assert.ThrowsAsync<InvalidOperationException>(()=>Designs.SaveAsync(edit,Actor));
        Assert.Equal(profile.RequestHash,(await StoredDesignProfileAsync(seed.Id)).RequestHash);
        // Legacy all-month profiles keep their former writer behavior without
        // requiring the scoped rollout switch (IIM and other existing clients).
        var legacy=await ScopeRequestAsync(designId,null);legacy.ExistingProfileHash=(await StoredDesignProfileAsync(designId)).RequestHash;
        legacy.Template.Active=false;await Designs.SaveAsync(legacy,Actor);Assert.Null((await StoredDesignProfileAsync(designId)).PayPeriods);
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using PdfSharp.Pdf.IO;

namespace Payroll.API.Tests.Services;

public sealed partial class PayrollReferenceInformationDatabaseTests
{
    private static string ReferencePdfContent(PayslipDesignPdf result)
    {
        using var document=PdfReader.Open(new MemoryStream(result.Bytes),PdfDocumentOpenMode.Import);Assert.Equal(1,document.PageCount);
        return Encoding.Latin1.GetString(document.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue);
    }
    [LocalTheory][InlineData(25000,"25000")][InlineData(33.335,"33.335")]
    public async Task Actual_normal_native_PDF_matches_complete_source_Info_keeps_full_fractional_budget_and_preserves_financials_and_other_month(decimal budget,string display)
    {
        if(budget!=25000)await SetReviewedSyntheticBudgetAsync(budget);
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE payruns SET Status='Approved' WHERE ClientId=97790");
        var repo=Repo();var septemberBefore=ReferencePdfContent((await repo.CreatePdfAsync(Client,runId,Two))!);
        var octoberBefore=ReferencePdfContent((await repo.CreatePdfAsync(Client,octoberRunId,Two))!);
        Assert.Contains("22500",septemberBefore);
        var review=await repo.PreviewReferenceInformationAsync(Request,Actor);var id=Guid.NewGuid().ToString("N");var before=await WitnessAsync();
        await repo.ApplyScopedReferenceInformationAsync(Client,new(review.ReviewToken,id,true),Actor);
        Assert.Equal(before,await WitnessAsync($"prfi:run:{runId}",$"prfi:req:{id}"));var saved=await WitnessAsync();
        var actual=ReferencePdfContent((await Repo(false).CreatePdfAsync(Client,runId,Two))!);
        Assert.Contains(display,actual);Assert.Equal(octoberBefore,ReferencePdfContent((await Repo(false).CreatePdfAsync(Client,octoberRunId,Two))!));
        await using(var db=await F.OpenAsync())await using(var tx=await db.BeginTransactionAsync())
        {
            var design=await DesignAsync(db,tx);var sourceRow=batch.Rows.Single(row=>row.EmployeeCode=="SYN-REF-2");
            var sourceBytes=new ExcelPayslipPdfService().Create(batch,[sourceRow],amountDecimalPlaces:design.Profile.AmountDecimalPlaces,
                presentation:NativePayslipDesignService.Presentation(design.Profile,design.Template));
            Assert.Equal(ReferencePdfContent(new(sourceBytes,"source.pdf")),actual);
            Assert.Equal(22500,await db.ExecuteScalarAsync<decimal>("SELECT NetPay FROM payrunemployees WHERE ClientId=97790 AND PayRunId=@Id AND EmployeeId=9779002",new{Id=runId},tx));
            await tx.RollbackAsync();
        }
        Assert.Equal(saved,await WitnessAsync());
    }
    private async Task SetReviewedSyntheticBudgetAsync(decimal budget)
    {
        var display=budget.ToString("0.############################",System.Globalization.CultureInfo.InvariantCulture);
        batch.Rows[1].Information[0].Value=display;source.Rows[1].Cells[3]!.Value=display;
        sourceHash=PayrollReferenceInformation.Hash(new{Batch=batch,Source=source});
        await using var db=await F.OpenAsync();
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Batch AS JSON) WHERE client_id=97790 AND id=@Id;UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=97790 AND batch_id=@Id;UPDATE employee_it0008_basic_pay SET AnnualCtc=@Annual WHERE ClientId=97790 AND EmployeeId=9779002 AND ActionType='Closed Excel Salary'",
            new{Id=Batch,Batch=JsonSerializer.Serialize(batch,Json),Source=JsonSerializer.Serialize(source,Json),Annual=budget*12});
        var code=$"closed_salary_receipt:{Client}:{ReceiptId}";
        var envelope=JsonNode.Parse((await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97790 AND ModuleCode=@Code",new{Code=code}))!)!;
        var stored=JsonNode.Parse(Protector.UnprotectHistoricalSalary(envelope["payload"]!.ToString())!)!;stored["sourceHash"]=sourceHash;
        stored["reviewedRequest"]!["initialSourceTargetReview"]!["sourceHash"]=sourceHash;
        foreach(var target in stored["receipt"]!["initialSourceTargets"]!.AsArray())
        {target!["sourceHash"]=sourceHash;if(target["employeeId"]!.GetValue<int>()==Two){target["sourceMonthlyBudget"]=budget;target["annualSalaryTarget"]=budget*12;}}
        foreach(var identity in stored["receipt"]!["sourceIdentities"]!.AsArray())identity!["sourceHash"]=sourceHash;
        envelope["payload"]=Protector.ProtectHistoricalSalary(stored.ToJsonString());
        await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Json AS JSON) WHERE client_id=97790 AND ModuleCode=@Code",new{Code=code,Json=envelope.ToJsonString()});
    }
    [LocalTheory][InlineData("/api/payslip-designs/reference-information/apply")][InlineData("/api/payslip-designs/{module}/{action}")]
    public async Task Configured_apply_workflow_is_held_in_same_transaction_without_notifications_or_metadata(string path)
    {
        var review=await Repo().PreviewReferenceInformationAsync(Request,Actor);
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("INSERT INTO workflow_action_rules(ActivityCode,HttpMethod,PathPattern,ResourceType,IsActive) VALUES('ReferenceInformation97790','POST',@Path,'ReferenceInformation',TRUE)",new{Path=path});
        var before=await WitnessAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>Repo().ApplyScopedReferenceInformationAsync(Client,new(review.ReviewToken,Guid.NewGuid().ToString(),true),Actor));Assert.Equal(before,await WitnessAsync());
    }
    [LocalFact]
    public async Task Query_client_and_protected_token_client_cannot_diverge_even_for_all_client_admin()
    {
        var review=await Repo().PreviewReferenceInformationAsync(Request,Actor);var actor=Actor;actor.ClientAccessMode="All";
        var before=await WitnessAsync();await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>Repo().ApplyScopedReferenceInformationAsync(Client+1,new(review.ReviewToken,Guid.NewGuid().ToString(),true),actor));Assert.Equal(before,await WitnessAsync());
    }
    [LocalTheory][InlineData("scope")][InlineData("theme")][InlineData("inactive")]
    public async Task Manifested_run_cannot_silently_switch_layout_after_profile_scope_theme_or_active_template_changes(string change)
    {
        var review=await Repo().PreviewReferenceInformationAsync(Request,Actor);await Repo().ApplyReferenceInformationAsync(new(review.ReviewToken,Guid.NewGuid().ToString(),true),Actor);
        await using(var db=await F.OpenAsync())
        {
            await db.ExecuteAsync("UPDATE payruns SET Status='Approved' WHERE ClientId=97790");
            if(change=="scope")
            {
                var code=$"payslip.design:{designId}";var envelope=JsonNode.Parse((await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97790 AND ModuleCode=@Code",new{Code=code}))!)!;
                var profile=JsonNode.Parse(Protector.UnprotectPayslipDesign(envelope["value"]!.ToString())!)!;
                var prior=await db.QuerySingleAsync<PayslipDesignTemplate>("SELECT * FROM paysliptemplates WHERE Id=@Id AND ClientId=97790",new{Id=designId});prior.Theme="Classic";
                profile["payPeriods"]=new JsonArray("2026-10");profile["previousClassicTemplate"]=JsonSerializer.SerializeToNode(prior,Json);
                envelope["value"]=Protector.ProtectPayslipDesign(profile.ToJsonString());
                await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Json AS JSON) WHERE client_id=97790 AND ModuleCode=@Code",new{Code=code,Json=envelope.ToJsonString()});
            }
            else await db.ExecuteAsync(change=="theme"?"UPDATE paysliptemplates SET Theme='Classic' WHERE ClientId=97790":"UPDATE paysliptemplates SET Active=FALSE WHERE ClientId=97790");
        }
        var before=await WitnessAsync();await Assert.ThrowsAsync<InvalidOperationException>(()=>Repo(false).CreatePdfAsync(Client,runId,Two));
        var ess=new EssMssRepository(Config(false),Protector);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ess.GetPayslipDocumentAsync(Two,Client,runId));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ess.GetLocationPayslipDocumentsAsync(Client,runId));Assert.Equal(before,await WitnessAsync());
    }
}

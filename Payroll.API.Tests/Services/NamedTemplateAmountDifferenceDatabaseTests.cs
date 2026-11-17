using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

// The sentinel-guarded historical fixture already owns this family and its
// cleanup. No production profile, worker or financial data is used here.
public sealed partial class HistoricalProfessionalTaxDatabaseTests
{
    private async Task ReloadNamedTemplateSourceAsync()
    {
        await using var db=await F.OpenAsync();
        batch=JsonSerializer.Deserialize<ExcelPayslipBatch>((await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@Client AND id=@BatchId",new{Client,BatchId}))!,Json)!;
        source=JsonSerializer.Deserialize<ExcelPayslipCalculationSource>((await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=@Client AND batch_id=@BatchId",new{Client,BatchId}))!,Json)!;
    }
    private async Task<NamedFormulaTemplateRequest> RoundedNamedTemplateRequestAsync(decimal delta=.29m)
    {
        var history=await NamedHistoryRequestAsync();
        source.Rows.Single(r=>r.SourceRow==2).Cells[11]!.Value=(8000m+delta).ToString(CultureInfo.InvariantCulture);
        batch.Rows[0].Information.Single(i=>i.Label=="Last Month Salary").Value=(8000m+delta).ToString(CultureInfo.InvariantCulture);
        await SaveSourceAsync();await ReloadNamedTemplateSourceAsync();
        var hash=ExcelPayslipRepository.EmploymentSourceHash(batch,source);
        history.Setup.AttendanceReview!.SourceHash=hash;
        history.Setup.EmployeeDecisions=[new(){RowId="hist-row-2",MatchedEmployeeId=One,AllowCreate=false}];
        F.Config["Payroll:HistoricalAmountDifferenceReviewEnabled"]="true";
        return new(){ClientId=Client,ExistingTemplateId=NamedHistoryTemplate,Name="One reviewed prior salary operand family",
            SourceRowId="hist-row-2",SourceHash=hash,SourceReview=history.Setup,HistoricalSalaryUse=true,
            Definitions=[new(PriorSalary,"Prior salary base","Amount",8,0,1000000000)],Bindings=history.NamedInputReview!.Bindings,
            AmountDifferenceReview=new(){Version=1,Reviewed=true,SourceHash=hash,MaxAbsoluteDifference=1,
                Reason="Reviewed each native line, every total and source aggregate independently; preserve both original source and native amounts."}};
    }

    [IsolatedFact]
    public async Task Named_template_bounded_configuration_is_strict_by_default_and_retains_authenticated_full_ledger_and_replay()
    {
        try
        {
            var q=await RoundedNamedTemplateRequestAsync();var review=q.AmountDifferenceReview;q.AmountDifferenceReview=null;
            var before=await WitnessAsync();
            var strict=await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.PreviewNamedFormulaTemplateAsync(q,Actor));
            Assert.Contains("every captured source output",strict.Message);q.AmountDifferenceReview=review;
            F.Config[ExcelPayrollSetupRepository.HistoricalNamedInputsFlag]="false";
            var blocked=await Repository.PreviewNamedFormulaTemplateAsync(q,Actor);
            Assert.False(blocked.CanSave);Assert.Single(blocked.Blockers);Assert.NotNull(blocked.AmountDifferenceEvidence);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.SaveNamedFormulaTemplateAsync(new(q,blocked.PlanHash,Guid.NewGuid().ToString("N"),true),Actor));
            F.Config[ExcelPayrollSetupRepository.HistoricalNamedInputsFlag]="true";
            var preview=await Repository.PreviewNamedFormulaTemplateAsync(q,Actor);Assert.True(preview.CanSave,string.Join(";",preview.Blockers));
            var evidence=preview.AmountDifferenceEvidence!;Assert.Equal(3,evidence.Lines.Count);Assert.Equal(4,evidence.Totals.Count);Assert.Equal(3,evidence.SourceAggregates.Count);
            Assert.All(evidence.Lines,line=>Assert.InRange(Math.Abs(line.RawDelta),0,1));Assert.Equal(64,evidence.ProofHash.Length);
            Assert.Equal(8000m,evidence.Lines.Single(l=>l.Code=="H_WAGES_97726").SourceRaw);
            Assert.Equal(8000.29m,evidence.Lines.Single(l=>l.Code=="H_WAGES_97726").Calculated);
            var id=Guid.NewGuid().ToString("N");var command=new NamedFormulaTemplateSaveRequest(q,preview.PlanHash,id,true);
            var saved=await Repository.SaveNamedFormulaTemplateAsync(command,Actor);Assert.False(saved.Replayed);Assert.Equal(NamedHistoryTemplate,saved.TemplateId);
            Assert.Equal(before,await WitnessAsync());Assert.Equal(0,await WrittenAsync());
            await using(var db=await F.OpenAsync())
            {
                var raw=await db.QuerySingleAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@Client AND ModuleCode=@Code",new{Client,Code="named-input-save:"+id});
                Assert.DoesNotContain("prior salary operand family",raw,StringComparison.OrdinalIgnoreCase);
                var stored=NamedFormulaInputStore.Read<JsonObject>(raw,F.Config,"TemplateReceipt");
                Assert.Equal(evidence.ProofHash,stored["amountDifferenceEvidence"]!["proofHash"]!.ToString());
                Assert.True(stored["historicalSalaryUse"]!.GetValue<bool>());
                Assert.Equal(3,stored["amountDifferenceEvidence"]!["lines"]!.AsArray().Count);
                Assert.Equal(4,stored["amountDifferenceEvidence"]!["totals"]!.AsArray().Count);
                Assert.Equal(3,stored["amountDifferenceEvidence"]!["sourceAggregates"]!.AsArray().Count);
                Assert.Equal(3,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructures WHERE ClientId=@Client",new{Client}));
            }
            F.Config[ExcelPayrollSetupRepository.HistoricalNamedInputsFlag]="false";F.Config["Payroll:HistoricalAmountDifferenceReviewEnabled"]="false";
            var replay=await Repository.SaveNamedFormulaTemplateAsync(command,Actor);Assert.True(replay.Replayed);Assert.Equal(saved.TemplateId,replay.TemplateId);
            q.AmountDifferenceReview!.Reason+=" Changed.";
            await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.SaveNamedFormulaTemplateAsync(command,Actor));
            Assert.Equal(before,await WitnessAsync());Assert.Equal(0,await WrittenAsync());
        }
        finally{await CleanNamedHistoryTemplateAsync();}
    }

    [IsolatedTheory]
    [InlineData("lineAboveOne")][InlineData("unsupportedFormula")][InlineData("sourceHash")][InlineData("stalePreview")]
    public async Task Named_template_bounded_configuration_never_bypasses_large_or_stale_source_rules(string change)
    {
        try
        {
            var q=await RoundedNamedTemplateRequestAsync(change=="lineAboveOne"?1.01m:.29m);var before=await WitnessAsync();
            if(change=="sourceHash")q.AmountDifferenceReview!.SourceHash=new string('a',64);
            if(change=="unsupportedFormula")
            {
                source.Rows.Single(r=>r.SourceRow==2).Cells[6]!.FormulaText="UNSUPPORTED(L2)";await SaveSourceAsync();await ReloadNamedTemplateSourceAsync();
                q.SourceHash=q.AmountDifferenceReview!.SourceHash=ExcelPayslipRepository.EmploymentSourceHash(batch,source);
                before=await WitnessAsync();
            }
            if(change=="stalePreview")
            {
                var preview=await Repository.PreviewNamedFormulaTemplateAsync(q,Actor);
                source.Rows.Single(r=>r.SourceRow==2).Cells[11]!.Value="8000.30";await SaveSourceAsync();before=await WitnessAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.SaveNamedFormulaTemplateAsync(new(q,preview.PlanHash,Guid.NewGuid().ToString("N"),true),Actor));
            }
            else await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.PreviewNamedFormulaTemplateAsync(q,Actor));
            Assert.Equal(before,await WitnessAsync());Assert.Equal(0,await WrittenAsync());
            await using var db=await F.OpenAsync();Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=@Client AND ModuleCode LIKE 'named-input-save:%'",new{Client}));
        }
        finally{await CleanNamedHistoryTemplateAsync();}
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;

namespace Payroll.API.Tests.Services;

public sealed partial class HistoricalProfessionalTaxDatabaseTests
{
    private const string NamedHistoryTemplate="9772611", PriorSalary="PRIOR_SALARY_BASE";
    private async Task<HistoricalPeriodSalaryRequest> NamedHistoryRequestAsync(bool both=false,bool pt=false,bool initial=false)
    {
        F.Config[ExcelPayrollSetupRepository.HistoricalNamedInputsFlag]="true";
        await using var db=await F.OpenAsync();
        await db.ExecuteAsync(@"DELETE FROM salarystructurelines WHERE StructureId=9772611;DELETE FROM salarystructures WHERE Id=9772611;
INSERT INTO salarystructures(Id,ClientId,Name,AnnualCtc,Active) VALUES(9772611,97726,'Reviewed generic prior salary operand',120000,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,Formula,ProRataOverride,SortOrder) VALUES
(9772611,'9772601','0','Formula','PRIOR_SALARY_BASE/PAYROLL_DAYS*PAYABLE_DAYS','false',1),
(9772611,'9772602','0','Formula','H_WAGES_97726_EARNED*12%','false',2),
(9772611,'9772603','0','Formula','H_WAGES_97726_EARNED*13%','false',3);");
        source.ColumnCount=12;
        source.Columns.AddRange([new(){ColumnIndex=10,Label="Current CTC",SourceHeader="Current CTC",Kind="info"},new(){ColumnIndex=11,Label="Last Month Salary",SourceHeader="Last Month Salary",Kind="ignore"}]);
        foreach(var row in source.Rows)
        {
            var amount=row.SourceRow==2?8000m:6500m;
            row.Cells.Add(new(){Value="10000"});row.Cells.Add(new(){Value=amount.ToString(CultureInfo.InvariantCulture)});
            row.Cells[6]!.FormulaText=$"L{row.SourceRow}/D{row.SourceRow}*E{row.SourceRow}";
            row.Cells[6]!.Value=amount.ToString(CultureInfo.InvariantCulture);row.Cells[7]!.Value=(amount*.12m).ToString(CultureInfo.InvariantCulture);
            row.Cells[8]!.Value=(amount*.13m).ToString(CultureInfo.InvariantCulture);row.Cells[9]!.Value=(amount*.88m).ToString(CultureInfo.InvariantCulture);
            var saved=batch.Rows.Single(r=>r.SourceRow==row.SourceRow);
            saved.Earnings[0].Amount=amount;saved.Deductions[0].Amount=amount*.12m;saved.EmployerContributions[0].Amount=amount*.13m;
            saved.DeclaredGross=amount;saved.DeclaredDeductions=amount*.12m;saved.NetPay=amount*.88m;
            saved.Information.AddRange([new(){Label="Current CTC",Value="10000"},new(){Label="Last Month Salary",Value=amount.ToString(CultureInfo.InvariantCulture)}]);
        }
        source.Rows.Insert(0,new(){SourceRow=1,Cells=source.Columns.Select(c=>(ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell{Value=c.SourceHeader}).ToList()});
        await SaveSourceAsync();
        batch=JsonSerializer.Deserialize<ExcelPayslipBatch>((await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@Client AND id=@BatchId",new{Client,BatchId}))!,Json)!;
        source=(await ExcelPayslipRepository.ReadCalculationSourceAsync(db,Client,BatchId))!;
        var setup=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db))!;
        var template=setup["salaryStructures"]!.AsArray().OfType<JsonObject>().Single(t=>t["id"]!.ToString()==NamedHistoryTemplate);
        var rules=ExcelPayrollSetupRepository.LocationSettingsRulesHash(template,setup["salaryComponents"]!.AsArray().OfType<JsonObject>().ToArray());
        List<NamedFormulaInputDefinition> definitions=[new(PriorSalary,"Prior salary base","Amount",8,0,1000000000)];
        var declaration=ReviewedNamedFormulaInputs.DeclarationHash(Client,NamedHistoryTemplate,rules,definitions);
        var profile=new NamedFormulaInputProfile(1,Client,NamedHistoryTemplate,rules,definitions,declaration);
        await db.ExecuteAsync("INSERT INTO modulesettings(client_id,ModuleCode,IsEnabled,SettingsJson) VALUES(@Client,@Code,TRUE,CAST(@Value AS JSON))",new{Client,Code=ReviewedNamedFormulaInputs.ProfileKey(NamedHistoryTemplate),Value=NamedFormulaInputStore.Protect(profile,F.Config,"Template")});
        var q=pt?await PtRequestAsync(both):Request(both);
        q.Assignments=q.Assignments.Select(a=>a with{TemplateId=NamedHistoryTemplate}).ToList();
        q.Setup.TemplateDecisions=[new(){VariantKey=NamedHistoryTemplate,ExistingTemplateId=NamedHistoryTemplate,ScopeAction="AttestLegacyScope",ScopeReason="Reviewed generic independent prior salary operand for exact closed source period"}];
        var earning=q.Setup.Components[0];earning.CalculationType="Formula";earning.Formula="PRIOR_SALARY_BASE/PAYROLL_DAYS*PAYABLE_DAYS";earning.ProRata=false;earning.SourceColumnIndex=null;
        q.Setup.SourceColumnRoles[11]="info";
        q.NamedInputReview=new(){TemplateId=NamedHistoryTemplate,DeclarationHash=declaration,Bindings=[new(PriorSalary,11,1,"Last Month Salary")]};
        if(initial)
        {
            await db.ExecuteAsync("DELETE FROM employee_it0008_basic_pay WHERE ClientId=@Client AND ActionType='Legacy salary';UPDATE employees SET AnnualCtc=120000,DateOfJoining='' WHERE ClientId=@Client",new{Client});
            q.SalaryTargetMode="ReviewedInitialSourceTarget";q.Setup.MonthlyCtcColumnIndex=10;
            q.InitialSourceTargetReview=new(){Version=1,Reviewed=true,SourceHash=q.Setup.AttendanceReview!.SourceHash,MonthlyCtcColumnIndex=10,
                Meaning="MonthlyPayrollEarningsBudget",ActualJoiningDateNotEstablished=true};
        }
        return q;
    }
    private async Task CleanNamedHistoryTemplateAsync()
    {await using var db=await F.OpenAsync();await db.ExecuteAsync("DELETE FROM salarystructurelines WHERE StructureId=9772611;DELETE FROM salarystructures WHERE Id=9772611");}

    [IsolatedTheory]
    [InlineData(false,false,false)][InlineData(true,true,false)][InlineData(true,true,true)]
    public async Task Closed_named_operand_is_authenticated_by_actual_history_and_receipt_without_current_master_inputs(bool batchProof,bool pt,bool initial)
    {
        try
        {
            var q=await NamedHistoryRequestAsync(both:true,pt:pt,initial:initial);F.Config["Payroll:HistoricalBatchProofEnabled"]=batchProof.ToString();
            var before=await WitnessAsync();var preview=await Repository.PreviewHistoricalSalaryAsync(Client,q,Actor);
            Assert.True(preview.CanApply,string.Join(";",preview.Blockers));Assert.Equal(2,preview.Rows.Count);
            Assert.All(preview.Rows,r=>{Assert.Empty(r.SourceMonthlyInputs);Assert.NotNull(r.NamedInputs);Assert.Single(r.NamedInputs!.Values);});
            Assert.Equal(before,await WitnessAsync());var id=Guid.NewGuid().ToString("N");var receipt=await ApplyAsync(preview,id);
            Assert.Equal(2,receipt.NamedInputs!.Count);Assert.All(receipt.NamedInputs,m=>Assert.Equal(120000,m.AnnualSalaryTarget));Assert.Equal(before,await WitnessAsync());
            F.Config[ExcelPayrollSetupRepository.HistoricalNamedInputsFlag]="false";
            Assert.True((await ApplyAsync(preview,id)).Replayed);
            foreach(var employee in new[]{One,Two})
            {
                var expected=employee==One?8000m:6500m;var native=await NativeAsync(employee);
                Assert.True(native.Complete,string.Join(";",native.Issues.Select(i=>i.Message)));Assert.Equal(expected,native.Gross);Assert.Equal(expected*.88m,native.Net);
                Assert.Equal(expected*.13m,native.Benefits);Assert.DoesNotContain(native.Lines,l=>l.Code==PriorSalary);
                var wage=native.Lines.Single(l=>l.Code=="H_WAGES_97726");Assert.Equal(expected,wage.Earned);
                Assert.Equal(expected,wage.NamedOperands!.Values[PriorSalary]);
            }
            var callerContext=new NamedFormulaInputContext{Definitions=[new(PriorSalary,"Untrusted caller","Amount",8,0,1000000000)],Values=new(){[PriorSalary]=1},DeclarationHash=new('a',64)};
            var withoutOverride=await F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=One,PayPeriod="2026-09",NamedInputs=callerContext},default);
            Assert.Equal(8000,withoutOverride.Gross);
            var future=await NativeAsync(period:"2026-10");Assert.True(future.Complete);Assert.Equal(77777,future.Gross);
            await using var db=await F.OpenAsync();Assert.All(await db.QueryAsync<string>("SELECT CAST(SalaryJson AS CHAR) FROM employee_it0008_basic_pay WHERE ClientId=@Client AND ActionType='Closed Excel Salary'",new{Client}),s=>Assert.Empty(JsonSerializer.Deserialize<Dictionary<string,decimal>>(s)!));
            Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=@Client",new{Client}));
        }
        finally{await CleanNamedHistoryTemplateAsync();}
    }

    [IsolatedTheory]
    [InlineData("missing")][InlineData("disabled")][InlineData("payload")][InlineData("source")][InlineData("header")][InlineData("history")][InlineData("template")][InlineData("receipt")][InlineData("identity")]
    public async Task Closed_named_ordinary_reader_fails_closed_on_any_lost_or_stale_authority(string change)
    {
        try
        {
            var q=await NamedHistoryRequestAsync();var preview=await Repository.PreviewHistoricalSalaryAsync(Client,q,Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));await ApplyAsync(preview);
            await using var db=await F.OpenAsync();await db.ExecuteAsync(change switch{
                "missing"=>"DELETE FROM modulesettings WHERE client_id=@Client AND ModuleCode LIKE 'named-input-h:%'",
                "disabled"=>"UPDATE modulesettings SET IsEnabled=FALSE WHERE client_id=@Client AND ModuleCode LIKE 'named-input-h:%'",
                "payload"=>"UPDATE modulesettings SET SettingsJson=JSON_SET(SettingsJson,'$.payload','forged') WHERE client_id=@Client AND ModuleCode LIKE 'named-input-h:%'",
                "source"=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.unknownWitness','changed') WHERE client_id=@Client",
                "header"=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.rows[0].cells[11].value','Different salary') WHERE client_id=@Client",
                "history"=>"UPDATE employee_it0008_basic_pay SET AnnualCtc=AnnualCtc+1 WHERE ClientId=@Client AND ActionType='Closed Excel Salary'",
                "template"=>"UPDATE salarystructurelines SET Formula='PRIOR_SALARY_BASE' WHERE StructureId=9772611 AND ComponentId='9772601'",
                "receipt"=>"DELETE FROM modulesettings WHERE client_id=@Client AND ModuleCode LIKE 'closed_salary_receipt:%'",
                _=>"UPDATE employeepersonaldetails SET UanNumber='101234560000' WHERE EmployeeId=@One"},new{Client,One});
            await Assert.ThrowsAnyAsync<Exception>(()=>NativeAsync());
        }
        finally{await CleanNamedHistoryTemplateAsync();}
    }

    [IsolatedFact]
    public async Task Closed_named_writer_gate_cannot_be_bypassed_by_omitting_the_review()
    {
        try
        {
            var q=await NamedHistoryRequestAsync();F.Config[ExcelPayrollSetupRepository.HistoricalNamedInputsFlag]="false";
            await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.PreviewHistoricalSalaryAsync(Client,q,Actor));
            q.NamedInputReview=null;await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.PreviewHistoricalSalaryAsync(Client,q,Actor));Assert.Equal(0,await WrittenAsync());
        }
        finally{await CleanNamedHistoryTemplateAsync();}
    }

    [IsolatedFact]
    public async Task Closed_named_late_failure_rolls_back_all_history_manifests_receipt_tax_and_audit()
    {
        const string trigger="codex_historical_named_97726_late";
        try
        {
            var q=await NamedHistoryRequestAsync(both:true,pt:true);F.Config["Payroll:HistoricalBatchProofEnabled"]="true";
            var preview=await Repository.PreviewHistoricalSalaryAsync(Client,q,Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
            var before=await WitnessAsync();await using var db=await F.OpenAsync();
            await db.ExecuteAsync("CREATE TRIGGER "+trigger+" AFTER INSERT ON modulesettings FOR EACH ROW BEGIN IF NEW.client_id=97726 AND NEW.ModuleCode LIKE 'closed_salary_receipt:%' THEN UPDATE employee_monthly_attendance SET payable_days=29 WHERE client_id=97726 AND employee_id=9772602 AND attendance_month='2026-09'; END IF; END");
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(()=>ApplyAsync(preview));Assert.Equal(0,await WrittenAsync());Assert.Equal(before,await WitnessAsync());
                Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=@Client",new{Client}));
                Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail WHERE EmployeeId IN (@One,@Two)",new{One,Two}));
            }
            finally{await db.ExecuteAsync("DROP TRIGGER IF EXISTS "+trigger);}
        }
        finally{await CleanNamedHistoryTemplateAsync();}
    }

    [IsolatedFact]
    public async Task Closed_named_normal_regular_draft_calculates_from_saved_operands_with_writer_disabled()
    {
        try
        {
            var q=await NamedHistoryRequestAsync(both:true,pt:true);var preview=await Repository.PreviewHistoricalSalaryAsync(Client,q,Actor);
            Assert.True(preview.CanApply,string.Join(";",preview.Blockers));await ApplyAsync(preview);F.Config[ExcelPayrollSetupRepository.HistoricalNamedInputsFlag]="false";
            await using var db=await F.OpenAsync();
            await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","NativeCurrency","pipeline-schema.sql")));
            var draft=await F.Runs.CreateAsync(new(){ClientId=Client,PayPeriod="2026-09",PayDate=new(2026,10,1),TotalWorkingDays=30,IncludedEmployeeIds=[One,Two],AttendanceGroupId=Client},"Synthetic historical named-operand test");
            Assert.NotNull(draft);Assert.Equal("Draft",draft!.Status);Assert.Equal(2,draft.Employees.Count);
            Assert.Equal(12760,draft.NetPay);Assert.Equal(7040,draft.Employees.Single(e=>e.EmployeeId==One).NetPay);Assert.Equal(5720,draft.Employees.Single(e=>e.EmployeeId==Two).NetPay);
            Assert.Equal(2,await WrittenAsync());
        }
        finally
        {
            await using var db=await F.OpenAsync();
            foreach(var table in new[]{"payrunemployeelines","payrun_step_logs","payroll_calculation_traces","payroll_reconciliation_results"})
                if(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name=@Table)",new{Table=table}))
                    await db.ExecuteAsync($"DELETE p FROM `{table}` p JOIN payruns r ON r.Id=p.PayRunId WHERE r.ClientId=@Client",new{Client});
            if(await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='tax_computation_snapshots')"))
                await db.ExecuteAsync("DELETE p FROM tax_computation_snapshots p JOIN payruns r ON r.Id=p.pay_run_id WHERE r.ClientId=@Client",new{Client});
            await CleanNamedHistoryTemplateAsync();
        }
    }

    [IsolatedTheory][InlineData(false)][InlineData(true)]
    public async Task Closed_named_nonempty_profile_links_keep_full_historical_hash_and_allow_master_contact_updates(bool pt)
    {
        try
        {
            var q=await NamedHistoryRequestAsync(pt:pt);
            batch.ProfileLinks=[new(batch.Rows[0].Id,batch.Rows[0].SourceRow,One,ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,batch.Rows[0],source))];
            await SaveSourceAsync();await using var db=await F.OpenAsync();
            batch=JsonSerializer.Deserialize<ExcelPayslipBatch>((await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@Client AND id=@BatchId",new{Client,BatchId}))!,Json)!;
            var historicalHash=ReviewedNamedFormulaInputs.Hash(new{Batch=batch,Source=source});
            var employmentHash=ExcelPayslipRepository.EmploymentSourceHash(batch,source);
            Assert.NotEqual(employmentHash,historicalHash);
            q.Setup.AttendanceReview!.SourceHash=historicalHash;
            if(q.PeriodProfessionalTaxReview is{} review)review.SourceHash=historicalHash;
            var preview=await Repository.PreviewHistoricalSalaryAsync(Client,q,Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
            Assert.Equal(historicalHash,preview.SourceHash);var receipt=await ApplyAsync(preview);
            Assert.Equal(historicalHash,Assert.Single(receipt.NamedInputs!).SourceHash);
            var sourceBefore=await db.ExecuteScalarAsync<string>("SELECT CONCAT(SHA2(CAST(b.batch_json AS CHAR),256),':',SHA2(CAST(s.source_json AS CHAR),256)) FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@BatchId",new{Client,BatchId});
            await db.ExecuteAsync("UPDATE employees SET WorkEmail='corrected.contact@example.test' WHERE Id=@One;UPDATE employeepersonaldetails SET Mobile='9000012345' WHERE EmployeeId=@One",new{One});
            F.Config[ExcelPayrollSetupRepository.HistoricalNamedInputsFlag]="false";
            var native=await NativeAsync();Assert.True(native.Complete,string.Join(";",native.Issues.Select(i=>i.Message)));
            Assert.Equal(8000,native.Gross);Assert.Equal(7040,native.Net);Assert.Equal(8000,native.Lines.Single(l=>l.Code=="H_WAGES_97726").NamedOperands!.Values[PriorSalary]);
            Assert.Equal("corrected.contact@example.test",await db.ExecuteScalarAsync<string>("SELECT WorkEmail FROM employees WHERE Id=@One",new{One}));
            Assert.Equal("9000012345",await db.ExecuteScalarAsync<string>("SELECT Mobile FROM employeepersonaldetails WHERE EmployeeId=@One",new{One}));
            Assert.Equal(sourceBefore,await db.ExecuteScalarAsync<string>("SELECT CONCAT(SHA2(CAST(b.batch_json AS CHAR),256),':',SHA2(CAST(s.source_json AS CHAR),256)) FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=@Client AND b.id=@BatchId",new{Client,BatchId}));
            // A genuine stored snapshot change is still stale even when the
            // generic employment hash intentionally ignores its profile links.
            await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=JSON_REMOVE(batch_json,'$.profileLinks') WHERE client_id=@Client AND id=@BatchId",new{Client,BatchId});
            var altered=JsonSerializer.Deserialize<ExcelPayslipBatch>((await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=@Client AND id=@BatchId",new{Client,BatchId}))!,Json)!;
            Assert.Equal(employmentHash,ExcelPayslipRepository.EmploymentSourceHash(altered,source));
            await Assert.ThrowsAnyAsync<Exception>(()=>NativeAsync());
        }
        finally{await CleanNamedHistoryTemplateAsync();}
    }
}

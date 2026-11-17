using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;
using Xunit;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed class ReportedClosedSalaryDatabaseTests:IAsyncLifetime
{
    private const int Client=97961,One=9796101,Two=9796102,OldTemplate=9796100;
    private const string BatchId="97961000000000000000000000000001",OriginalId="97961000000000000000000000000002";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture=Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is{} c?new(c):null;
    private ReviewedPayrollFixture F=>fixture??throw new InvalidOperationException("Owned loopback fixture required.");
    private ExcelPayrollSetupRepository Repo=>new(F.Config,new EmployeeRepository(F.Config,null!,null!),F.Runs);
    private static AuthUser Actor=>new(){Id=Client,Email="synthetic-reported-owner@example.invalid",IsActive=true,Roles=["super_admin"],Permissions=["settings.manage","employees.manage","payroll.run"]};
    private sealed class IsolatedFactAttribute:FactAttribute{public IsolatedFactAttribute(){if(Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null)Skip="Explicit owned sentinel fixture required.";}}
    private ExcelPayslipBatch batch=null!;private ExcelPayslipCalculationSource source=null!;private ReportedPeriodInputReview review=null!;private List<NamedFormulaInputDefinition> definitions=[];
    private List<ExcelPayrollSetupComponent> components=[];private long[] oldHistoryIds=[];private bool seeded;
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async Task InitializeAsync()
    {
        if(fixture is null)return;
        // No Initialize/reset/DDL: the root-owned fixture schema is existing.
        await using var db=await F.OpenAsync();
        Assert.Equal(0,await db.ExecuteScalarAsync<int>(@"SELECT (SELECT COUNT(*) FROM clients WHERE Id=97961)+(SELECT COUNT(*) FROM employees WHERE ClientId=97961 OR Id IN(9796101,9796102))
+(SELECT COUNT(*) FROM salarycomponents WHERE Id BETWEEN 9796101 AND 9796112)+(SELECT COUNT(*) FROM salarystructures WHERE ClientId=97961)
+(SELECT COUNT(*) FROM employee_it0008_basic_pay WHERE ClientId=97961)+(SELECT COUNT(*) FROM modulesettings WHERE client_id=97961)
+(SELECT COUNT(*) FROM excel_payslip_batches WHERE client_id=97961)+(SELECT COUNT(*) FROM excel_payslip_calculation_sources WHERE client_id=97961)"));
        BuildSource();
        foreach(var key in new[]{"Payroll:HistoricalNamedInputsEnabled",ReportedPeriodInputProof.WriterFlag,"Payroll:PayableOnlyAttendanceEnabled","Payroll:HistoricalAmountDifferenceReviewEnabled"})F.Config[key]="true";
        F.Config["Payroll:HistoricalBatchProofEnabled"]="false";
        seeded=true;
        await db.ExecuteAsync(@"INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97961,'REPORTED-CLOSED','Synthetic reported closed salary',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,City,State) VALUES(97961,97961,'Synthetic existing office','Synthetic city','Synthetic state');
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,WorkLocationId,PortalAccess,IsActive) VALUES
(9796101,97961,'REP-ONE','Source','One','2026-01-01',600000,'9796100',JSON_OBJECT('9796101',42000),JSON_OBJECT(),JSON_OBJECT(),97961,FALSE,TRUE),
(9796102,97961,'REP-TWO','Source','Two','2026-01-01',600000,'9796100',JSON_OBJECT('9796101',43000),JSON_OBJECT(),JSON_OBJECT(),97961,FALSE,TRUE);
INSERT INTO employeepersonaldetails(EmployeeId,PanNumber) VALUES(9796101,'XQAAB6101C'),(9796102,'XQAAB6102C');
INSERT INTO employeepaymentdetails(EmployeeId,BankAccountNo,IfscCode) VALUES(9796101,'979610100001','TEST0000001'),(9796102,'979610200002','TEST0000001');
INSERT INTO employeesalarycomponents(EmployeeId,ComponentId,Amount) VALUES(9796101,'9796101',42000),(9796102,'9796101',43000);
INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,Status,SalaryStructureId,AnnualCtc,SalaryJson,ChangeReason,CreatedBy) VALUES
(9796101,97961,'Master Update','2026-10-01','Active','9796100',600000,JSON_OBJECT('9796101',42000),'Original configured salary','Synthetic owner'),
(9796102,97961,'Master Update','2026-10-01','Active','9796100',600000,JSON_OBJECT('9796101',43000),'Original configured salary','Synthetic owner'),
(9796101,97961,'Future salary','2026-12-01','Active','9796100',720000,JSON_OBJECT('9796101',50000),'Original future salary','Synthetic owner'),
(9796102,97961,'Future salary','2026-12-01','Active','9796100',720000,JSON_OBJECT('9796101',51000),'Original future salary','Synthetic owner');
INSERT INTO salarystructures(Id,ClientId,ClientRef,Name,AnnualCtc,Active) VALUES(9796100,97961,'97961','Existing configured salary; do not edit',600000,TRUE);
INSERT INTO attendance_groups(id,client_id,name,policy_batch_id,attendance_mode,work_week,attendance_cycle_start_day,attendance_cycle_end_day,payroll_report_generation_day,is_active)
VALUES(97961,97961,'Existing Monthly policy','synthetic-97961','Monthly','Sunday off',26,25,28,TRUE);
INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(97961,9796101),(97961,9796102);
INSERT INTO tax_client_settings(client_id,financial_year,enabled,project_monthly_tds,active) VALUES(97961,'2026-27',FALSE,FALSE,TRUE);
INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,lop_days,source_type,remarks) VALUES
(97961,9796101,'2026-12',30,30,30,0,'Monthly','Independent synthetic future attendance'),(97961,9796102,'2026-12',30,30,30,0,'Monthly','Independent synthetic future attendance');");
        foreach(var c in components)
        {
            await db.ExecuteAsync(@"INSERT INTO salarycomponents(Id,Code,Name,Category,ComponentRole,StatutoryType,CalculationType,ValueText,Formula,BaseComponent,ProRata,Taxable,Priority,PayType,Active)
VALUES(@Id,@Code,@Name,@Category,@ComponentRole,@StatutoryType,@CalculationType,'0','1000','',FALSE,@Taxable,@Priority,@PayType,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,Formula,BaseComponent,ProRataOverride,RoundingMode,SortOrder)
VALUES(9796100,@Id,'0',@CalculationType,@Formula,'','false','',@Priority)",new{Id=long.Parse(c.ExistingComponentId!,CultureInfo.InvariantCulture),c.Code,c.Name,c.Category,c.ComponentRole,c.StatutoryType,c.CalculationType,c.Taxable,c.Priority,c.PayType,Formula=c.CalculationType=="Manual / Variable"?"":"1000"});
        }
        var attachment=await db.ExecuteScalarAsync<long>(@"INSERT INTO entity_attachments(public_id,client_id,attachment_attribute_id,field_configuration_id,storage_server_id,entity_type,entity_id,original_file_name,stored_file_name,storage_key,file_extension,detected_mime_type,file_size_bytes,sha256_hash,uploaded_by_user_id)
VALUES(UUID(),97961,97961,97961,97961,'SyntheticPayroll',97961,'synthetic.xlsx','synthetic.xlsx','synthetic97961/original.xlsx','.xlsx','application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',10,REPEAT('a',64),97961);SELECT LAST_INSERT_ID();");
        await db.ExecuteAsync(@"INSERT INTO excel_payslip_originals(client_id,id,attachment_id,sha256_hash) VALUES(97961,@OriginalId,@Attachment,REPEAT('a',64));
INSERT INTO excel_payslip_batch_originals(client_id,batch_id,original_id,original_file_name) VALUES(97961,@BatchId,@OriginalId,'synthetic.xlsx');
INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(97961,@BatchId,CAST(@Batch AS JSON),JSON_OBJECT('jobs',JSON_OBJECT('old',JSON_OBJECT('rowIds',JSON_ARRAY('97961000-0000-0000-0000-000000000001'))),'requests',JSON_OBJECT('old',JSON_OBJECT('retained',TRUE)),'deliveries',JSON_OBJECT()),UTC_TIMESTAMP());
INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(97961,@BatchId,CAST(@Source AS JSON),UTC_TIMESTAMP())",new{OriginalId,Attachment=attachment,BatchId,Batch=JsonSerializer.Serialize(batch,Json),Source=JsonSerializer.Serialize(source,Json)});
        oldHistoryIds=(await db.QueryAsync<long>("SELECT Id FROM employee_it0008_basic_pay WHERE ClientId=97961")).ToArray();
        // Native snapshot lexical hashes must bind the actual MySQL JSON text.
        var raw=await db.QuerySingleAsync<(string BatchHash,string SourceHash)>("SELECT SHA2(CAST(b.batch_json AS CHAR),256) BatchHash,SHA2(CAST(s.source_json AS CHAR),256) SourceHash FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=97961 AND b.id=@BatchId",new{BatchId});
        review.RawBatchHash=raw.BatchHash;review.RawSourceHash=raw.SourceHash;
    }

    public async Task DisposeAsync()
    {
        if(fixture is null||!seeded)return;
        await using var db=await F.OpenAsync();await db.ExecuteAsync(@"DROP TRIGGER IF EXISTS codex_reported97961_late;
DELETE FROM payroll_calculation_traces WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97961);
DELETE FROM payrunemployeelines WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97961);
DELETE FROM payrun_step_logs WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97961);
DELETE FROM payroll_validation_issues WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97961);
DELETE FROM payroll_reconciliation_results WHERE PayRunId IN(SELECT Id FROM payruns WHERE ClientId=97961);
DELETE FROM tax_computation_snapshots WHERE pay_run_id IN(SELECT Id FROM payruns WHERE ClientId=97961);
DELETE FROM payrunemployees WHERE ClientId=97961;DELETE FROM payruns WHERE ClientId=97961;DELETE FROM payrolladjustments WHERE ClientId=97961;
DELETE FROM modulesettings WHERE client_id=97961;DELETE FROM employee_monthly_attendance WHERE client_id=97961;DELETE FROM employee_daily_attendance WHERE client_id=97961;
DELETE FROM excel_payroll_setup_imports WHERE client_id=97961;DELETE FROM excel_payslip_batch_originals WHERE client_id=97961;DELETE FROM excel_payslip_originals WHERE client_id=97961;
DELETE FROM entity_attachments WHERE client_id=97961 AND entity_type='SyntheticPayroll' AND entity_id=97961;
DELETE FROM excel_payslip_calculation_sources WHERE client_id=97961;DELETE FROM excel_payslip_batches WHERE client_id=97961;
DELETE FROM employee_audit_trail WHERE EmployeeId IN(9796101,9796102);DELETE FROM employee_it0008_basic_pay WHERE ClientId=97961;
DELETE FROM employeesalarycomponents WHERE EmployeeId IN(9796101,9796102);DELETE FROM employeepersonaldetails WHERE EmployeeId IN(9796101,9796102);DELETE FROM employeepaymentdetails WHERE EmployeeId IN(9796101,9796102);
DELETE FROM tax_client_settings WHERE client_id=97961;DELETE FROM attendance_group_employees WHERE employee_id IN(9796101,9796102);DELETE FROM attendance_groups WHERE client_id=97961;
DELETE FROM employees WHERE ClientId=97961;DELETE FROM salarystructurelines WHERE StructureId IN(SELECT Id FROM salarystructures WHERE ClientId=97961);DELETE FROM salarystructures WHERE ClientId=97961;
DELETE FROM salarycomponents WHERE Id BETWEEN 9796101 AND 9796112;DELETE FROM worklocations WHERE ClientId=97961;DELETE FROM clients WHERE Id=97961;");
    }

    private void BuildSource()
    {
        var first=ReportedPeriodInputV2Tests.Fixture.Create(4.5m,24);var second=ReportedPeriodInputV2Tests.Fixture.Create(5,25);
        source=first.Source;source.HeaderRow=6;source.SheetName="Synthetic reported salary";source.CompleteSheetCapture=true;
        source.WorkbookFiles[0].OriginalId=OriginalId;
        source.Columns[2].SourceHeader=source.Columns[2].Label="PAN";source.Columns[2].Kind="info";
        source.Rows.Single(r=>r.SourceRow==6).Cells[2]=new(){DataType="Text",Value="PAN"};
        var secondCells=JsonSerializer.Deserialize<ExcelPayslipCalculationSourceRow>(JsonSerializer.Serialize(second.Source.Rows.Single(r=>r.SourceRow==8),Json),Json)!;
        secondCells.SourceRow=9;foreach(var cell in secondCells.Cells.Where(c=>c?.FormulaText is not null))cell!.FormulaText=Regex.Replace(cell.FormulaText!,@"(?<=[A-Z])8\b","9");source.Rows.Add(secondCells);
        var rows=new List<ExcelPayslipRow>();
        foreach(var(id,num,code,name,pan)in new[]{("97961000-0000-0000-0000-000000000001",8,"REP-ONE","Source One","XQAAB6101C"),("97961000-0000-0000-0000-000000000002",9,"REP-TWO","Source Two","XQAAB6102C")})
        {
            var cells=source.Rows.Single(r=>r.SourceRow==num).Cells;
            // Synthetic cached cash uses the same fifteen significant digits as
            // an Excel numeric cache. Independent source operands retain their
            // complete thirteen decimal places, and original formulas stay exact.
            // This avoids manufacturing >IEEE precision in numeric batch JSON.
            foreach(var cashColumn in source.Columns.Where(c=>c.Kind is "earning" or "deduction" or "employer" or "grossTotal" or "netPay"))
                cells[cashColumn.ColumnIndex]!.Value=decimal.Parse(cells[cashColumn.ColumnIndex]!.Value,CultureInfo.InvariantCulture).ToString("G15",CultureInfo.InvariantCulture);
            cells[0]=new(){DataType="Text",Value=code};cells[1]=new(){DataType="Text",Value=name};cells[2]=new(){DataType="Text",Value=pan};
            decimal Amount(int c)=>decimal.Parse(cells[c]!.Value,CultureInfo.InvariantCulture);
            rows.Add(new(){Id=id,SourceRow=num,EmployeeCode=code,EmployeeName=name,
                Information=source.Columns.Where(c=>c.Kind=="info").Select(c=>new ExcelPayslipText{Label=c.Label,Value=cells[c.ColumnIndex]?.Value??""}).ToList(),
                Earnings=source.Columns.Where(c=>c.Kind=="earning").Select(c=>new ExcelPayslipAmount{Label=c.Label,Amount=Amount(c.ColumnIndex)}).ToList(),
                Deductions=source.Columns.Where(c=>c.Kind=="deduction").Select(c=>new ExcelPayslipAmount{Label=c.Label,Amount=Amount(c.ColumnIndex)}).ToList(),
                EmployerContributions=source.Columns.Where(c=>c.Kind=="employer").Select(c=>new ExcelPayslipAmount{Label=c.Label,Amount=Amount(c.ColumnIndex)}).ToList(),DeclaredGross=Amount(32),NetPay=Amount(37)});
        }
        // Even an empty retained profile-link list distinguishes the full reported
        // snapshot from the employment-only hash; exercise the real template loader.
        batch=new(){Id=BatchId,ClientId=Client,Month="2026-10",SheetName=source.SheetName,HeaderRow=6,SourceFileName="synthetic.xlsx",Rows=rows,ProfileLinks=[]};source.SourceBatchId="";
        definitions=first.Profile.Definitions;review=first.Review;review.ClientId=Client;review.BatchId=BatchId;review.Rows=[];review.SourceHash=ReportedPeriodInputProof.SourceHash(batch,source);
        foreach(var row in rows)
        {
            var cells=source.Rows.Single(r=>r.SourceRow==row.SourceRow).Cells;var rr=new ReportedPeriodInputRowReview{RowId=row.Id,SourceRow=row.SourceRow,SourceIdentityHash=ExcelPayslipMasterSynchronization.ProfileIdentityHash(batch,row,source)};
            foreach(var binding in review.Bindings){rr.ExpectedValues[binding.Key]=decimal.Parse(cells[binding.ColumnIndex]!.Value,CultureInfo.InvariantCulture);rr.CellHashes[binding.Key]=ReportedPeriodInputProof.CellHash(cells[binding.ColumnIndex]!);}review.Rows.Add(rr);
        }
        var cash=source.Columns.Where(c=>c.Kind is "earning" or "deduction" or "employer").ToArray();
        components=cash.Select((c,i)=>new ExcelPayrollSetupComponent{Key="reported-"+c.ColumnIndex,ExistingComponentId=(9796101+i).ToString(CultureInfo.InvariantCulture),Code="RP"+c.ColumnIndex,Name="Synthetic cash "+c.ColumnIndex,
            Category=c.Kind switch{"earning"=>"Earning","deduction"=>"Deduction",_=>"Benefit"},ComponentRole=c.ColumnIndex==35?"Statutory Deduction":c.Kind switch{"earning"=>"Regular Earning","deduction"=>"Regular Deduction",_=>"Employer Benefit"},StatutoryType=c.ColumnIndex==35?"Professional Tax":"None",CalculationType=c.ColumnIndex==36?"Manual / Variable":"Formula",Value="0",Formula="1000",ProRata=false,Taxable=c.Kind=="earning",Priority=c.ColumnIndex,PayType=c.ColumnIndex==36?"Variable Pay":"Fixed Pay",ExpectedColumnIndex=c.ColumnIndex}).ToList();
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source,batch));
    }

    private ExcelPayrollSetupRequest Setup(string[] rows)=>new(){BatchId=BatchId,RowIds=rows.ToList(),TemplateName="One generic reported split-rate family",EffectiveFrom="2026-10-01",FixedPayrollDays=30,PayableDaysColumnIndex=27,PresentDaysColumnIndex=null,
        ImportAttendance=false,ImportPayableOnlyAttendance=false,PreserveExistingAttendancePolicies=true,WorkWeek="Sunday off",AttendanceCycleStartDay=26,AttendanceCycleEndDay=25,PayrollReportGenerationDay=28,
        ReviewedFixedInputs=true,ReviewedPolicyScope=true,TransferReviewVersion=1,TemplateScopeReviewVersion=1,ReportedPeriodInputs=review,
        TemplateScope=new(){Reviewed=true,OpenEnded=false,EffectiveTo="2026-10-31",LocationMode="Listed",WorkLocationIds=[Client],CategoryKind="AllClientCategories",DivisorMeaning="ContractFixedDays",PayableMeaning="AlreadyPayable",PolicyBasis="SavedSourceRules"},
        Components=JsonSerializer.Deserialize<List<ExcelPayrollSetupComponent>>(JsonSerializer.Serialize(components,Json),Json)!,SourceColumnRoles=source.Columns.ToDictionary(c=>c.ColumnIndex,c=>c.Kind),
        EmployeeDecisions=rows.Select(r=>new ExcelPayrollSetupEmployeeDecision{RowId=r,MatchedEmployeeId=r==batch.Rows[0].Id?One:Two}).ToList()};
    private HistoricalAmountDifferenceReview MoneyReview()=>new(){Version=1,Reviewed=true,SourceHash=review.SourceHash,MaxAbsoluteDifference=1,Reason="Explicit synthetic source/native currency differences up to one rupee; no source amount is changed."};
    private async Task<HistoricalPeriodSalaryRequest> ConfigureAsync()
    {
        var q=Setup([batch.Rows[0].Id]);
        var request=new NamedFormulaTemplateRequest{ClientId=Client,ExistingTemplateId=OldTemplate.ToString(CultureInfo.InvariantCulture),Name="One reusable generic reported-day family",SourceRowId=batch.Rows[0].Id,SourceHash=review.SourceHash,SourceReview=q,Definitions=definitions,AmountDifferenceReview=MoneyReview(),HistoricalSalaryUse=true};
        var preview=await Repo.PreviewNamedFormulaTemplateAsync(request,Actor);Assert.True(preview.CanSave,string.Join(";",preview.Blockers));
        var save=new NamedFormulaTemplateSaveRequest(request,preview.PlanHash,Guid.NewGuid().ToString("N"),true);
        var saved=await Repo.SaveNamedFormulaTemplateAsync(save,Actor);Assert.True((await Repo.SaveNamedFormulaTemplateAsync(save,Actor)).Replayed);
        var historical=Setup(batch.Rows.Select(r=>r.Id).ToArray());
        await using var db=await F.OpenAsync();var setup=JsonNode.Parse(await PayrollDataTableStore.GetSetupJsonAsync(db))!;
        var template=setup["salaryStructures"]!.AsArray().OfType<JsonObject>().Single(t=>t["id"]!.ToString()==saved.TemplateId);
        var lines=template["lines"]!.AsArray().OfType<JsonObject>().ToArray();for(var i=0;i<lines.Length;i++)historical.Components[i].Formula=lines[i]["formula"]!.ToString();
        historical.TemplateDecisions=[new(){VariantKey=saved.TemplateId,ExistingTemplateId=saved.TemplateId,ScopeAction="AttestLegacyScope",ScopeReason="Exact existing synthetic source cohort and closed-month applicability reviewed."}];
        historical.AttendanceReview=new(){Mode="SourceSeed",Reviewed=true,SourceHash=review.SourceHash,SourceMonth="2026-10",CycleStart="2026-09-26",CycleEnd="2026-10-25",PresentMeaning="NotMapped",PayableMeaning="AlreadyPayable"};
        historical.AttendanceReview.BoundSourceInputs=ExcelPayrollSetupRepository.AttendanceBinding(historical);
        return new(){Setup=historical,Assignments=[new(batch.Rows[0].Id,One,saved.TemplateId),new(batch.Rows[1].Id,Two,saved.TemplateId)],AmountDifferenceReview=MoneyReview(),PayableOnlySeedReview=new(){Reviewed=true,Reason="Explicit reported salary-period quantities; actual worked days were not supplied. Preserve the existing policy and current/future salary."}};
    }

    private async Task<string> PreservedAsync()
    {
        await using var db=await F.OpenAsync();return Hash(JsonSerializer.Serialize(new{
            Master=await db.QueryAsync("SELECT * FROM employees WHERE ClientId=97961 ORDER BY Id"),Personal=await db.QueryAsync("SELECT * FROM employeepersonaldetails WHERE EmployeeId IN(9796101,9796102) ORDER BY EmployeeId"),Bank=await db.QueryAsync("SELECT * FROM employeepaymentdetails WHERE EmployeeId IN(9796101,9796102) ORDER BY EmployeeId"),
            Physical=await db.QueryAsync("SELECT * FROM employeesalarycomponents WHERE EmployeeId IN(9796101,9796102) ORDER BY EmployeeId,ComponentId"),
            OldHistory=await db.QueryAsync("SELECT * FROM employee_it0008_basic_pay WHERE Id IN @Ids ORDER BY Id",new{Ids=oldHistoryIds}),
            Policy=await db.QueryAsync("SELECT * FROM attendance_groups WHERE client_id=97961"),Membership=await db.QueryAsync("SELECT * FROM attendance_group_employees WHERE employee_id IN(9796101,9796102) ORDER BY employee_id"),
            Source=await db.QuerySingleAsync("SELECT CAST(batch_json AS CHAR) Batch,CAST(send_state_json AS CHAR) Mail FROM excel_payslip_batches WHERE client_id=97961"),Calculation=await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97961"),
            OldTemplate=await db.QueryAsync("SELECT * FROM salarystructurelines WHERE StructureId=9796100 ORDER BY Id"),FutureAttendance=await db.QueryAsync("SELECT * FROM employee_monthly_attendance WHERE client_id=97961 AND attendance_month<>'2026-10' ORDER BY employee_id,attendance_month")},Json));
    }
    private async Task<HistoricalPeriodSalaryPreview> ReadyAsync(HistoricalPeriodSalaryRequest request)
    {
        var first=await Repo.PreviewHistoricalSalaryAsync(Client,request,Actor);
        foreach(var row in first.Rows.Where(r=>r.AmountReview is{Status:"Explained rounding" or ExcelPayrollSetupRepository.BoundedHistoricalDifferenceStatus}))request.Setup.RoundingReviews[row.RowId]=row.AmountReview!.ProofHash;
        var ready=await Repo.PreviewHistoricalSalaryAsync(Client,request,Actor);Assert.True(ready.CanApply,string.Join(";",ready.Blockers));return ready;
    }

    [IsolatedFact]
    public async Task Genuine_closed_seed_history_receipt_native_reader_replay_tamper_and_rollback_preserve_future_and_source()
    {
        var request=await ConfigureAsync();var preserved=await PreservedAsync();
        await using(var sourceDb=await F.OpenAsync())
        {
            var captured=await ExcelPayslipRepository.ReadCalculationSourceAsync(sourceDb,Client,BatchId);
            Assert.Equal(JsonSerializer.Serialize(source,Json),JsonSerializer.Serialize(captured,Json));
            Assert.Null(await ExcelPayslipRepository.ReadCalculationSourceAsync(sourceDb,Client+1,BatchId));
            Assert.Null(await ExcelPayslipRepository.ReadCalculationSourceAsync(sourceDb,Client,Guid.NewGuid().ToString("N")));
        }
        var futureBefore=await F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=One,PayPeriod="2026-12"},default);
        Assert.True(futureBefore.Complete,string.Join(";",futureBefore.Issues.Select(i=>i.Message)));
        var sequential=await ReadyAsync(request);
        F.Config["Payroll:HistoricalBatchProofEnabled"]="true";
        var ready=await ReadyAsync(request);
        Assert.Equal(sequential.Fingerprint,ready.Fingerprint);
        Assert.Equal(JsonSerializer.Serialize(sequential.Rows,Json),JsonSerializer.Serialize(ready.Rows,Json));
        Assert.All(ready.Rows,r=>{Assert.Empty(r.SourceMonthlyInputs);Assert.NotNull(r.NamedInputs);Assert.Equal(600000,r.RetainedAnnualSalaryTarget);});
        await using var db=await F.OpenAsync();Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_monthly_attendance WHERE client_id=97961 AND attendance_month='2026-10'"));
        var command=new HistoricalPeriodSalaryApplyRequest{ReviewToken=ready.ReviewToken,RequestId=Guid.NewGuid().ToString("N"),Acknowledged=true};
        // A late failure after seed/history/sidecar insertion must roll back all.
        await db.ExecuteAsync("CREATE TRIGGER codex_reported97961_late AFTER INSERT ON modulesettings FOR EACH ROW BEGIN IF NEW.client_id=97961 AND NEW.ModuleCode LIKE 'reported-input-e:%' THEN UPDATE employee_monthly_attendance SET payable_days=27 WHERE client_id=97961 AND attendance_month='2026-10'; END IF; END");
        try{await Assert.ThrowsAnyAsync<Exception>(()=>Repo.ApplyHistoricalSalaryAsync(Client,command,Actor));}
        finally{await db.ExecuteAsync("DROP TRIGGER IF EXISTS codex_reported97961_late");}
        Assert.Equal(preserved,await PreservedAsync());Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay WHERE ClientId=97961 AND ActionType='Closed Excel Salary'"));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_monthly_attendance WHERE client_id=97961 AND attendance_month='2026-10'"));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97961 AND (ModuleCode LIKE 'reported-input-h:%' OR ModuleCode LIKE 'reported-input-e:%' OR ModuleCode LIKE 'closed_salary_receipt:%')"));
        var receipt=await Repo.ApplyHistoricalSalaryAsync(Client,command,Actor);Assert.Equal(2,receipt.ReportedInputs!.Count);Assert.Null(receipt.NamedInputs);
        Assert.Equal(preserved,await PreservedAsync());
        Assert.Equal(2,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_monthly_attendance WHERE client_id=97961 AND attendance_month='2026-10' AND present_days IS NULL AND source_type='ExcelPayableOnly'"));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM excel_payroll_setup_imports WHERE client_id=97961"));
        foreach(var key in new[]{"Payroll:HistoricalNamedInputsEnabled",ReportedPeriodInputProof.WriterFlag,"Payroll:PayableOnlyAttendanceEnabled"})F.Config[key]="false";
        Assert.True((await Repo.ApplyHistoricalSalaryAsync(Client,command,Actor)).Replayed);
        var expected=ready.Rows.ToDictionary(r=>r.EmployeeId);
        foreach(var id in new[]{One,Two})
        {
            var native=await F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=id,PayPeriod="2026-10"},default);
            Assert.True(native.Complete,string.Join(";",native.Issues.Select(i=>i.Message)));Assert.False(native.AttendanceEvidence!.ActualPresenceKnown);
            Assert.Equal(expected[id].Gross,native.Gross);Assert.Equal(expected[id].TotalDeductions,native.Deductions);Assert.Equal(expected[id].EmployerCost,native.Benefits);Assert.Equal(expected[id].NetPay,native.Net);
            Assert.All(native.Lines.Where(l=>l.NamedOperands is not null),l=>Assert.Equal(id,l.NamedOperands!.ReportedPeriodProof!.NativeAttendance!.EmployeeId));
        }
        var one=receipt.ReportedInputs.Single(a=>a.Inputs.EmployeeId==One);var indexKey=ReportedPeriodHistoricalInputStore.MonthKey(One,"2026-10");
        await db.ExecuteAsync("UPDATE modulesettings SET IsEnabled=FALSE WHERE client_id=97961 AND ModuleCode=@Code",new{Code=indexKey});
        await Assert.ThrowsAnyAsync<Exception>(()=>F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=One,PayPeriod="2026-10"},default));
        await db.ExecuteAsync("UPDATE modulesettings SET IsEnabled=TRUE WHERE client_id=97961 AND ModuleCode=@Code",new{Code=indexKey});
        var history=(IDictionary<string,object>)await db.QuerySingleAsync("SELECT * FROM employee_it0008_basic_pay WHERE Id=@Id",new{Id=one.Inputs.HistoryId});
        await db.ExecuteAsync("DELETE FROM employee_it0008_basic_pay WHERE Id=@Id",new{Id=one.Inputs.HistoryId});
        await Assert.ThrowsAnyAsync<Exception>(()=>F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=One,PayPeriod="2026-10"},default));
        var fields=history.Keys.ToArray();var values=new DynamicParameters();for(var i=0;i<fields.Length;i++)values.Add("p"+i,history[fields[i]]);
        await db.ExecuteAsync("INSERT INTO employee_it0008_basic_pay("+string.Join(',',fields.Select(f=>"`"+f+"`"))+") VALUES("+string.Join(',',fields.Select((_,i)=>"@p"+i))+")",values);
        var sidecarKey=ReportedPeriodHistoricalInputStore.Key(one.Inputs.HistoryId);var envelope=await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97961 AND ModuleCode=@Code",new{Code=sidecarKey});
        await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=JSON_SET(SettingsJson,'$.payload','forged') WHERE client_id=97961 AND ModuleCode=@Code",new{Code=sidecarKey});
        await Assert.ThrowsAnyAsync<Exception>(()=>F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=One,PayPeriod="2026-10"},default));
        await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@Json AS JSON) WHERE client_id=97961 AND ModuleCode=@Code",new{Code=sidecarKey,Json=envelope});
        var futureAfter=await F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=One,PayPeriod="2026-12"},default);
        Assert.True(futureAfter.Complete);Assert.Equal(futureBefore.Gross,futureAfter.Gross);Assert.Equal(futureBefore.Net,futureAfter.Net);Assert.Equal(preserved,await PreservedAsync());
        var draft=await F.Runs.CreateAsync(new(){ClientId=Client,PayPeriod="2026-10",PayDate=new(2026,11,1),TotalWorkingDays=30,AttendanceGroupId=Client,IncludedEmployeeIds=[One,Two]},"Synthetic closed reported payroll");
        Assert.NotNull(draft);Assert.Equal("Draft",draft!.Status);Assert.Equal(2,draft.Employees.Count);Assert.All(draft.Employees,e=>Assert.Null(e.PresentDays));
        Assert.Equal(ready.Rows.Sum(r=>r.NetPay),draft.NetPay);Assert.Equal(preserved,await PreservedAsync());
        // The generic target-only action must consume the same authenticated v2
        // closed-history/source/native-attendance proof, never replace null with
        // payable units, and leave the other employee's frozen lines untouched.
        var otherBefore=JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM payrunemployeelines WHERE PayRunId=@Id AND EmployeeId=@EmployeeId ORDER BY Id",new{Id=draft.Id,EmployeeId=Two}));
        var targetBefore=Assert.Single(draft.Employees,row=>row.EmployeeId==One);
        var recalc=await F.Runs.PreviewEmployeeRecalculationAsync(draft.Id,One);
        Assert.NotNull(recalc);Assert.True(recalc!.CanApply,string.Join(" ",recalc.Blockers));Assert.Null(recalc.After!.PresentDays);
        var corrected=await F.Runs.RecalculateEmployeeAsync(draft.Id,One,new(recalc.ExpectedFingerprint,true,"Synthetic confirmed closed-source native recalculation"),"Synthetic closed reported payroll");
        var targetAfter=Assert.Single(corrected!.Employees,row=>row.EmployeeId==One);
        Assert.Equal(targetBefore.Id,targetAfter.Id);Assert.Null(targetAfter.PresentDays);Assert.Equal(targetBefore.NetPay,targetAfter.NetPay);
        Assert.Equal(otherBefore,JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM payrunemployeelines WHERE PayRunId=@Id AND EmployeeId=@EmployeeId ORDER BY Id",new{Id=draft.Id,EmployeeId=Two})));
        Assert.Equal(preserved,await PreservedAsync());
    }
}

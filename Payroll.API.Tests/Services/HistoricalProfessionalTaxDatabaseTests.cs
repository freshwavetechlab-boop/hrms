using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed partial class HistoricalProfessionalTaxDatabaseTests : IAsyncLifetime
{
    private const int Client = 97726, One = 9772601, Two = 9772602, Template = 9772600, OldTemplate = 9772609;
    private const string BatchId = "97726000000000000000000000000001", Trigger = "codex_historical_97726_late_failure";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture = Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string connection ? new(connection) : null;
    private ReviewedPayrollFixture F => fixture ?? throw new InvalidOperationException("Explicit guarded synthetic database required.");
    private ExcelPayrollSetupRepository Repository => new(F.Config, new EmployeeRepository(F.Config, null!, null!), F.Runs);
    private static AuthUser Actor => new() { Id = Client, Email = "synthetic-history-reviewer@example.test", IsActive = true, ClientAccessMode = "All",
        Roles = ["super_admin"], Permissions = ["payroll.run", "employees.manage", "settings.manage"] };
    private sealed class IsolatedFactAttribute : FactAttribute
    { public IsolatedFactAttribute() { if (Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null) Skip = "Explicit sentinel-guarded synthetic payroll database required."; } }
    private sealed class IsolatedTheoryAttribute : TheoryAttribute
    { public IsolatedTheoryAttribute() { if (Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null) Skip = "Explicit sentinel-guarded synthetic payroll database required."; } }
    private ExcelPayslipBatch batch = new(); private ExcelPayslipCalculationSource source = new();

    public async Task InitializeAsync()
    {
        if (fixture is null) return;
        await F.InitializeAsync(); await using var db = await F.OpenAsync();
        await db.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS attendance_groups(id INT PRIMARY KEY AUTO_INCREMENT,client_id INT NOT NULL,policy_batch_id VARCHAR(64) DEFAULT '',name VARCHAR(160) DEFAULT '',work_location_id INT NULL,department VARCHAR(100) DEFAULT '',designation VARCHAR(100) DEFAULT '',work_week VARCHAR(80) DEFAULT '',attendance_cycle_start_day INT DEFAULT 1,attendance_cycle_end_day INT DEFAULT 31,payroll_report_generation_day INT DEFAULT 3,is_active BOOLEAN DEFAULT TRUE,shift_id INT NULL,attendance_mode VARCHAR(30) DEFAULT 'Monthly',allowed_shift_ids_json JSON NULL);
CREATE TABLE IF NOT EXISTS attendance_group_employees(attendance_group_id INT NOT NULL,employee_id INT NOT NULL,PRIMARY KEY(attendance_group_id,employee_id));");
        await CleanupAsync();
        await db.ExecuteAsync(@"
INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97726,'SYNTH-HIST','Synthetic closed salary',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,City,State) VALUES(97726,97726,'Synthetic history office','Synthetic city','Historical synthetic state 97726');
INSERT INTO salarycomponents(Id,Code,Name,Category,ComponentRole,StatutoryType,CalculationType,ValueText,Formula,ProRata,Taxable,PayType,Priority) VALUES
(9772601,'H_WAGES_97726','Historical wages','Earning','Regular Earning','None','Fixed Amount','0','',TRUE,TRUE,'Fixed Pay',100),
(9772602,'H_DEDUCTION_97726','Historical deduction','Deduction','Regular Deduction','None','Formula','0','H_WAGES_97726_EARNED*12%',FALSE,FALSE,'Fixed Pay',200),
(9772603,'H_EMPLOYER_97726','Historical employer cost','Benefit','Employer Benefit','None','Formula','0','H_WAGES_97726_EARNED*13%',FALSE,FALSE,'Fixed Pay',300);
INSERT INTO salarystructures(Id,ClientId,Name,AnnualCtc,Active) VALUES(9772600,97726,'Exact existing historical source template',933324,TRUE),(9772609,97726,'Retained older historical template',120000,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,Formula,ProRataOverride,SortOrder) VALUES
(9772600,'9772601','0','Fixed Amount','','true',1),(9772600,'9772602','0','Formula','H_WAGES_97726_EARNED*12%','false',2),(9772600,'9772603','0','Formula','H_WAGES_97726_EARNED*13%','false',3),
(9772609,'9772601','0','Fixed Amount','','true',1),(9772609,'9772602','0','Formula','H_WAGES_97726_EARNED*12%','false',2),(9772609,'9772603','0','Formula','0','false',3);
INSERT INTO tax_client_settings(client_id,financial_year,enabled,project_monthly_tds,active) VALUES(97726,'2026-27',FALSE,FALSE,TRUE);
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,WorkLocationId) VALUES
(9772601,97726,'HIST-SYN-A','Synthetic','One','2026-01-01',933324,'9772600',JSON_OBJECT('9772601',77777),JSON_OBJECT(),JSON_OBJECT(),97726),
(9772602,97726,'HIST-SYN-B','Synthetic','Two','2026-01-01',933324,'9772600',JSON_OBJECT('9772601',77777),JSON_OBJECT(),JSON_OBJECT(),97726);
INSERT INTO employeepersonaldetails(EmployeeId,UanNumber) VALUES(9772601,'101234569771'),(9772602,'101234569772');
INSERT INTO employeesalarycomponents(EmployeeId,ComponentId,Amount) VALUES(9772601,'9772601',77777),(9772602,'9772601',77777);
INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,SalaryJson,ChangeReason,CreatedBy) VALUES
(9772601,97726,'Legacy salary','2026-01-01',NULL,'Historical','9772609',120000,JSON_OBJECT('9772601',10000),'Retained old basis','synthetic'),
(9772601,97726,'Future salary','2026-10-01',NULL,'Active','9772600',933324,JSON_OBJECT('9772601',77777),'Retained future basis','synthetic'),
(9772602,97726,'Legacy salary','2026-01-01',NULL,'Historical','9772609',120000,JSON_OBJECT('9772601',10000),'Retained old basis','synthetic'),
(9772602,97726,'Future salary','2026-10-01',NULL,'Active','9772600',933324,JSON_OBJECT('9772601',77777),'Retained future basis','synthetic');
INSERT INTO attendance_groups(id,client_id,policy_batch_id,name,work_week,attendance_cycle_start_day,attendance_cycle_end_day,payroll_report_generation_day,is_active,attendance_mode) VALUES(97726,97726,'97726000-0000-0000-0000-000000000001','Historical monthly policy','Sunday off',1,31,3,TRUE,'Monthly');
INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(97726,9772601),(97726,9772602);
INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,source_type,remarks) VALUES
(97726,9772601,'2026-09',30,26,30,'Monthly','Existing reviewed actual attendance'),(97726,9772602,'2026-09',30,26,30,'Monthly','Existing reviewed actual attendance'),
(97726,9772601,'2026-10',31,27,31,'Monthly','Existing future attendance');");
        var rows = new List<ExcelPayslipRow>(); var captured = new List<ExcelPayslipCalculationSourceRow>();
        for (var index = 0; index < 2; index++)
        {
            var number = index + 2; var code = index == 0 ? "HIST-SYN-A" : "HIST-SYN-B"; var name = index == 0 ? "Synthetic One" : "Synthetic Two"; var uan = index == 0 ? "101234569771" : "101234569772";
            rows.Add(new() { Id = "hist-row-" + number, SourceRow = number, EmployeeCode = code, EmployeeName = name,
                Information = [new() { Label = "UAN", Value = uan }, new() { Label = "Attendance", Value = "30" }],
                Earnings = [new() { Label = "Wages", Amount = 10000 }], Deductions = [new() { Label = "Deduction", Amount = 1200 }],
                EmployerContributions = [new() { Label = "Employer cost", Amount = 1300 }], DeclaredGross = 10000, DeclaredDeductions = 1200, NetPay = 8800 });
            captured.Add(new() { SourceRow = number, Cells = new[] { code, name, uan, "30", "30", "10000", "10000", "1200", "1300", "8800" }
                .Select((value, column) => (ExcelPayslipCalculationCell?)new ExcelPayslipCalculationCell { Value = value,
                    FormulaText = column switch { 6 => $"F{number}/D{number}*E{number}", 7 => $"G{number}*12%", 8 => $"G{number}*13%", 9 => $"G{number}-H{number}", _ => null } }).ToList() });
        }
        batch = new() { Id = BatchId, ClientId = Client, ClientName = "Synthetic closed salary", Month = "2026-09", SheetName = "Salary", SourceFileName = "Synthetic history.xlsx", HeaderRow = 1,
            Rows = rows, CreatedAtUtc = new(2026, 11, 1), CreatedBy = "isolated test" };
        var labels = new[] { "Employee code", "Employee name", "UAN", "Payroll days", "Attendance", "Monthly wage", "Wages", "Deduction", "Employer cost", "Net pay" };
        var kinds = new[] { "employeeCode", "employeeName", "info", "ignore", "info", "ignore", "earning", "deduction", "employer", "netPay" };
        source = new() { SheetName = "Salary", HeaderRow = 1, ColumnCount = 10, Rows = captured, DependencyReviewStatus = "Legacy",
            Columns = labels.Select((label, index) => new ExcelPayslipCalculationColumn { ColumnIndex = index, SourceHeader = label, Label = label, Kind = kinds[index] }).ToList() };
        await db.ExecuteAsync("INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(@Client,@Id,CAST(@Batch AS JSON),JSON_OBJECT('requests',JSON_OBJECT('old',JSON_OBJECT('status','Sent'))),UTC_TIMESTAMP());INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(@Client,@Id,CAST(@Source AS JSON),UTC_TIMESTAMP())",
            new { Client, Id = BatchId, Batch = JsonSerializer.Serialize(batch, Json), Source = JsonSerializer.Serialize(source, Json) });
    }
    public async Task DisposeAsync() { if (fixture is not null) await CleanupAsync(); }
    private async Task CleanupAsync()
    {
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync("DROP TRIGGER IF EXISTS " + Trigger);
        // Only this test's explicitly owned client/IDs are removed; no global resets.
        await db.ExecuteAsync(@"
DELETE FROM employee_audit_trail WHERE EmployeeId IN (9772601,9772602);DELETE FROM professionaltaxslabs WHERE State='Historical synthetic state 97726'; DELETE FROM modulesettings WHERE client_id=97726;DELETE FROM tax_client_settings WHERE client_id=97726;
DELETE p FROM payrunemployees p JOIN payruns r ON r.Id=p.PayRunId WHERE r.ClientId=97726;DELETE FROM payruns WHERE ClientId=97726;DELETE FROM payrolladjustments WHERE ClientId=97726;
DELETE FROM employee_monthly_attendance WHERE client_id=97726;DELETE FROM employee_daily_attendance WHERE client_id=97726;
DELETE FROM attendance_group_employees WHERE attendance_group_id=97726;DELETE FROM attendance_groups WHERE client_id=97726;
DELETE FROM excel_payslip_batches WHERE client_id=97726;DELETE FROM excel_payslip_calculation_sources WHERE client_id=97726;
DELETE FROM employee_it0008_basic_pay WHERE ClientId=97726;DELETE FROM employee_it0009_bank_details WHERE ClientId=97726;DELETE FROM employee_it0000_actions WHERE ClientId=97726;
DELETE FROM employeesalarycomponents WHERE EmployeeId IN (9772601,9772602);DELETE FROM employeepersonaldetails WHERE EmployeeId IN (9772601,9772602);DELETE FROM employeepaymentdetails WHERE EmployeeId IN (9772601,9772602);
DELETE FROM employees WHERE ClientId=97726;DELETE FROM worklocations WHERE ClientId=97726;DELETE FROM clients WHERE Id=97726;
DELETE FROM salarystructurelines WHERE StructureId IN (9772600,9772609);DELETE FROM salarystructures WHERE Id IN (9772600,9772609);DELETE FROM salarycomponents WHERE Id IN (9772601,9772602,9772603);");
    }
    private HistoricalPeriodSalaryRequest Request(bool both = false)
    {
        var ids = both ? new[] { One, Two } : [One];
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Batch = batch, Source = source }, Json)))).ToLowerInvariant();
        var request = new HistoricalPeriodSalaryRequest { Assignments = ids.Select(id => new HistoricalPeriodSalaryAssignment(id == One ? "hist-row-2" : "hist-row-3", id, Template.ToString(CultureInfo.InvariantCulture))).ToList(),
            Setup = new() { BatchId = BatchId, RowIds = ids.Select(id => id == One ? "hist-row-2" : "hist-row-3").ToList(), TemplateName = "Reviewed closed source period", EffectiveFrom = "2026-09-01",
                PayrollDaysColumnIndex = 3, PayableDaysColumnIndex = 4, WorkWeek = "Sunday off", AttendanceCycleStartDay = 1, AttendanceCycleEndDay = 31, PayrollReportGenerationDay = 3,
                ImportAttendance = false, TransferReviewVersion = 1, ReviewedFixedInputs = true, ReviewedPolicyScope = true,
                TemplateScopeReviewVersion = 1,
                TemplateScope = new() { Reviewed = true, EffectiveTo = "2026-09-30", LocationMode = "Listed", WorkLocationIds = [Client], CategoryKind = "AllClientCategories", DivisorMeaning = "CalendarDays", PayableMeaning = "AlreadyPayable", PolicyBasis = "SavedSourceRules" },
                TemplateDecisions = [new() { VariantKey = Template.ToString(CultureInfo.InvariantCulture), ExistingTemplateId = Template.ToString(CultureInfo.InvariantCulture), ScopeAction = "AttestLegacyScope", ScopeReason = "Reviewed exact synthetic monthly source and existing native policy for September only" }],
                SourceColumnRoles = source.Columns.ToDictionary(c => c.ColumnIndex, c => c.ColumnIndex is 3 or 5 ? "info" : c.Kind),
                AttendanceReview = new() { Mode = "VerifyExistingNative", Reviewed = true, SourceHash = sourceHash, SourceMonth = "2026-09", CycleStart = "2026-09-01", CycleEnd = "2026-09-30", PayableMeaning = "AlreadyPayable", PresentMeaning = "NotMapped" },
                Components = [Component("9772601", "H_WAGES_97726", "Earning", "Regular Earning", "Fixed Amount", "", true, 100, 5, 6, true),
                    Component("9772602", "H_DEDUCTION_97726", "Deduction", "Regular Deduction", "Formula", "H_WAGES_97726_EARNED*12%", false, 200, null, 7, false),
                    Component("9772603", "H_EMPLOYER_97726", "Benefit", "Employer Benefit", "Formula", "H_WAGES_97726_EARNED*13%", false, 300, null, 8, false)] } };
        request.Setup.AttendanceReview!.BoundSourceInputs = ExcelPayrollSetupRepository.AttendanceBinding(request.Setup);
        return request;
    }
    private static ExcelPayrollSetupComponent Component(string id, string code, string category, string role, string calculation, string formula,
        bool prorata, int priority, int? input, int output, bool taxable) => new() { Key = code, ExistingComponentId = id, Code = code, Name = code,
            Category = category, ComponentRole = role, StatutoryType = "None", CalculationType = calculation, Formula = formula, Value = "0", ProRata = prorata,
            Priority = priority, SourceColumnIndex = input, ExpectedColumnIndex = output, Taxable = taxable, PayType = "Fixed Pay" };
    private Task<HistoricalPeriodSalaryReceipt> ApplyAsync(HistoricalPeriodSalaryPreview preview, string? id = null) => Repository.ApplyHistoricalSalaryAsync(Client,
        new() { ReviewToken = preview.ReviewToken, RequestId = id ?? Guid.NewGuid().ToString("N"), Acknowledged = true }, Actor);
    private async Task<HistoricalPeriodSalaryPreview> PreviewAsync(bool both = false)
    {
        var result = await Repository.PreviewHistoricalSalaryAsync(Client, Request(both), Actor);
        Assert.True(result.CanApply, string.Join(";", result.Blockers)); return result;
    }
    private async Task<int> WrittenAsync()
    { await using var db = await F.OpenAsync(); return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay WHERE ClientId=97726 AND ActionType='Closed Excel Salary'"); }
    private async Task<string> WitnessAsync()
    {
        await using var db = await F.OpenAsync(); await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        var state = await EmployeeRepository.ReadClosedSalaryHistoryStateAsync(db, tx, Client, [One, Two]);
        var sourceHash = await db.ExecuteScalarAsync<string>("SELECT CONCAT(SHA2(CAST(b.batch_json AS CHAR),256),SHA2(CAST(b.send_state_json AS CHAR),256),SHA2(CAST(s.source_json AS CHAR),256)) FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id WHERE b.client_id=97726 AND b.id=@Id", new { Id = BatchId }, tx);
        var attendance = await db.QueryAsync("SELECT * FROM employee_monthly_attendance WHERE client_id=97726 ORDER BY employee_id,attendance_month", transaction: tx);
        await tx.RollbackAsync(); return EmployeeRepository.ClosedSalaryHash(new { state.CurrentMasterHash, Source = sourceHash, Attendance = attendance });
    }

    private async Task<HistoricalPeriodSalaryRequest> PtRequestAsync(bool both=false)
    {
        await using(var db=await F.OpenAsync())
            await db.ExecuteAsync("INSERT INTO professionaltaxslabs(State,SalaryFrom,DeductionAmount,Active) SELECT 'Historical synthetic state 97726',0,200,TRUE WHERE NOT EXISTS(SELECT 1 FROM professionaltaxslabs WHERE State='Historical synthetic state 97726')");
        var request=Request(both);request.PeriodProfessionalTaxReview=new(){Version=1,Reviewed=true,SourceHash=request.Setup.AttendanceReview!.SourceHash,
            Reason="Reviewed complete source net and all financial outputs: source-authority PT zero for this closed month only.",
            Rows=request.Assignments.Select(a=>new HistoricalPeriodProfessionalTaxRow(a.EmployeeId,a.RowId,0)).ToList()};return request;
    }
    private async Task<HistoricalPeriodSalaryPreview> ReadyPtAsync(bool both=false)
    {
        var preview=await Repository.PreviewHistoricalSalaryAsync(Client,await PtRequestAsync(both),Actor);
        Assert.True(preview.CanApply,string.Join(";",preview.Blockers));return preview;
    }
    private async Task SaveSourceAsync()
    {
        await using var db=await F.OpenAsync();await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Batch AS JSON) WHERE client_id=@Client;UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=@Client",
            new{Client,Batch=JsonSerializer.Serialize(batch,Json),Source=JsonSerializer.Serialize(source,Json)});
    }
    private async Task<PayrollSimulationResult> NativeAsync(int employee=One,string period="2026-09")=>await F.Runs.SimulateEmployeeAsync(new(){Mode="Employee",ClientId=Client,EmployeeId=employee,PayPeriod=period},default);

    [IsolatedFact]
    public async Task Proposed_basis_preview_and_atomic_closed_control_have_full_native_parity_and_replay()
    {
        var request=await PtRequestAsync();var before=await WitnessAsync();
        var ordinary=await Repository.PreviewHistoricalSalaryAsync(Client,Request(),Actor);Assert.False(ordinary.CanApply);
        var preview=await Repository.PreviewHistoricalSalaryAsync(Client,request,Actor);
        Assert.True(preview.CanApply,string.Join(";",preview.Blockers));Assert.True(Assert.Single(preview.Rows).ProfessionalTax!.ProposedBasisWhatIf);
        Assert.Equal(before,await WitnessAsync());Assert.Equal(0,await WrittenAsync());
        var id=Guid.NewGuid().ToString("N");var receipt=await ApplyAsync(preview,id);var tax=Assert.Single(receipt.ProfessionalTax!);
        Assert.False(tax.ProposedBasisWhatIf);Assert.Equal(preview.Rows[0].ProfessionalTax!.NativeBasisHash,tax.NativeBasisHash);
        Assert.Equal(before,await WitnessAsync());Assert.Equal(1,await WrittenAsync());
        var actual=await NativeAsync();Assert.True(actual.Complete,string.Join(";",actual.Issues.Select(i=>i.Message)));Assert.Equal(8800,actual.Net);Assert.Equal(1200,actual.Deductions);
        Assert.Contains(actual.Lines,l=>l.Code=="PT"&&l.Earned==0);
        var other=await NativeAsync(Two);Assert.Equal(1400,other.Deductions);Assert.Equal(8600,other.Net);
        var future=await NativeAsync(period:"2026-10");Assert.True(future.Complete);Assert.Equal(200,future.Lines.Single(l=>l.Code=="PT").Earned);
        Assert.True((await ApplyAsync(preview,id)).Replayed);Assert.Equal(1,await WrittenAsync());
        await using var db=await F.OpenAsync();Assert.Equal(2,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=@Client",new{Client}));
        var old=await db.QueryAsync<string>("SELECT CONCAT(Id,':',CAST(AnnualCtc AS CHAR),':',SalaryJson) FROM employee_it0008_basic_pay WHERE ClientId=@Client AND ActionType<>'Closed Excel Salary' ORDER BY Id",new{Client});Assert.Equal(4,old.Count());
    }

    [IsolatedTheory]
    [InlineData("receiptMissing")][InlineData("receiptPurpose")][InlineData("receiptPayload")][InlineData("source")][InlineData("history")][InlineData("master")]
    public async Task Ordinary_native_loader_refuses_missing_corrupt_stale_or_foreign_historical_authority(string change)
    {
        var receipt=await ApplyAsync(await ReadyPtAsync());await using(var db=await F.OpenAsync())
            await db.ExecuteAsync(change switch{
                "receiptMissing"=>"DELETE FROM modulesettings WHERE client_id=@Client AND ModuleCode LIKE 'closed_salary_receipt:%'",
                "receiptPurpose"=>"UPDATE modulesettings SET SettingsJson=JSON_SET(SettingsJson,'$.purpose','Untrusted') WHERE client_id=@Client AND ModuleCode LIKE 'closed_salary_receipt:%'",
                "receiptPayload"=>"UPDATE modulesettings SET SettingsJson=JSON_SET(SettingsJson,'$.payload','forged') WHERE client_id=@Client AND ModuleCode LIKE 'closed_salary_receipt:%'",
                "source"=>"UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.unknownWitness','changed') WHERE client_id=@Client",
                "history"=>"UPDATE employee_it0008_basic_pay SET EffectiveTo='2026-09-29' WHERE ClientId=@Client AND ActionType='Closed Excel Salary'",
                _=>"UPDATE employees SET LastName='Changed' WHERE Id=@One"},new{Client,One});
        await Assert.ThrowsAnyAsync<Exception>(()=>NativeAsync());Assert.Equal(1,await WrittenAsync());
    }

    [IsolatedTheory]
    [InlineData("netLiteral")][InlineData("netConstant")][InlineData("hiddenPT")][InlineData("seededPT")][InlineData("positiveReview")][InlineData("staleHash")][InlineData("rounding")]
    public async Task Missing_symbolic_source_closure_conflicts_and_currency_near_miss_never_authorize_salary(string change)
    {
        var request=await PtRequestAsync();
        switch(change){
            case "netLiteral":source.Rows[0].Cells[9]!.FormulaText=null;break;
            case "netConstant":source.Rows[0].Cells[9]!.FormulaText="G2-H2-0";break;
            case "hiddenPT":source.Columns[5].SourceHeader="PT";break;
            case "seededPT":await using(var db=await F.OpenAsync())await db.ExecuteAsync("UPDATE employeepersonaldetails SET PtLwfWorkmenComp=1 WHERE EmployeeId=@One",new{One});break;
            case "positiveReview":request.PeriodProfessionalTaxReview!.Rows=[new(One,"hist-row-2",200)];break;
            case "staleHash":request.PeriodProfessionalTaxReview!.SourceHash=new string('a',64);break;
            case "rounding":source.Rows[0].Cells[7]!.Value="1200.006";batch.Rows[0].Deductions[0].Amount=1200.006m;batch.Rows[0].DeclaredDeductions=1200.006m;batch.Rows[0].NetPay=8799.994m;source.Rows[0].Cells[9]!.Value="8799.994";break;
        }
        await SaveSourceAsync();var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{Batch=batch,Source=source},Json)))).ToLowerInvariant();
        request.Setup.AttendanceReview!.SourceHash=hash;if(change!="staleHash")request.PeriodProfessionalTaxReview!.SourceHash=hash;
        if(change=="positiveReview")await Assert.ThrowsAsync<InvalidOperationException>(()=>Repository.PreviewHistoricalSalaryAsync(Client,request,Actor));
        else {var preview=await Repository.PreviewHistoricalSalaryAsync(Client,request,Actor);Assert.False(preview.CanApply);}
        Assert.Equal(0,await WrittenAsync());await using var check=await F.OpenAsync();Assert.Equal(0,await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=@Client",new{Client}));
    }

    [IsolatedFact]
    public async Task Initial_source_target_composes_without_claiming_a_hire_date_or_changing_current_future_basis()
    {
        await using(var db=await F.OpenAsync())await db.ExecuteAsync("DELETE FROM employee_it0008_basic_pay WHERE ClientId=@Client AND ActionType='Legacy salary';UPDATE employees SET AnnualCtc=120000,DateOfJoining='' WHERE ClientId=@Client",new{Client});
        source.ColumnCount=11;source.Columns.Add(new(){ColumnIndex=10,SourceHeader="Current CTC",Label="Current CTC",Kind="info"});
        foreach(var cells in source.Rows)cells.Cells.Add(new(){Value="10000",DataType="Number"});
        foreach(var row in batch.Rows)row.Information.Add(new(){Label="Current CTC",Value="10000"});await SaveSourceAsync();
        var request=await PtRequestAsync();request.SalaryTargetMode="ReviewedInitialSourceTarget";request.Setup.MonthlyCtcColumnIndex=10;
        request.InitialSourceTargetReview=new(){Version=1,Reviewed=true,MonthlyCtcColumnIndex=10,SourceHash=request.PeriodProfessionalTaxReview!.SourceHash,Meaning="MonthlyPayrollEarningsBudget",ActualJoiningDateNotEstablished=true};
        var before=await WitnessAsync();var preview=await Repository.PreviewHistoricalSalaryAsync(Client,request,Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
        var receipt=await ApplyAsync(preview);Assert.True(Assert.Single(receipt.InitialSourceTargets!).ActualJoiningDateNotEstablished);Assert.False(Assert.Single(receipt.ProfessionalTax!).ProposedBasisWhatIf);
        Assert.Equal(before,await WitnessAsync());Assert.Equal(8800,(await NativeAsync()).Net);await using var check=await F.OpenAsync();Assert.Equal("",await check.ExecuteScalarAsync<string>("SELECT DateOfJoining FROM employees WHERE Id=@One",new{One}));
    }

    [IsolatedTheory]
    [InlineData(false)][InlineData(true)]
    public async Task Historical_PT_uses_the_same_explicit_unique_bank_or_reviewed_name_identity_as_its_authenticated_salary_receipt(bool rename)
    {
        source.ColumnCount=11;source.Columns.Add(new(){ColumnIndex=10,SourceHeader="Bank Account No",Label="Bank account no",Kind="info"});
        for(var index=0;index<2;index++){var bank="0012345679972"+index;source.Rows[index].Cells.Add(new(){Value=bank,DataType="Text"});batch.Rows[index].Information.Add(new(){Label="Bank account no",Value=bank});if(!rename){source.Rows[index].Cells[2]!.Value="";batch.Rows[index].Information.Single(v=>v.Label=="UAN").Value="";}}
        await using(var db=await F.OpenAsync()){
            await db.ExecuteAsync("INSERT INTO employeepaymentdetails(EmployeeId,BankAccountNo) VALUES(@One,'00123456799720'),(@Two,'00123456799721')",new{One,Two});
            if(!rename)await db.ExecuteAsync("UPDATE employeepersonaldetails SET UanNumber='' WHERE EmployeeId IN (@One,@Two)",new{One,Two});
            if(rename)await db.ExecuteAsync("UPDATE employees SET LastName='Corrected' WHERE Id=@One",new{One});
        }
        await SaveSourceAsync();var request=await PtRequestAsync();request.SourceIdentityReview=new(){Version=1,Reviewed=true,SourceHash=request.PeriodProfessionalTaxReview!.SourceHash,Reason="Verified exact source bank ownership and reviewed name for this closed month only",Bindings=[new("hist-row-2",One,10,"Synthetic One",rename?"Synthetic Corrected":"Synthetic One",rename)]};
        var preview=await Repository.PreviewHistoricalSalaryAsync(Client,request,Actor);Assert.True(preview.CanApply,string.Join(";",preview.Blockers));
        var before=await WitnessAsync();var receipt=await ApplyAsync(preview);Assert.Single(receipt.SourceIdentities!);Assert.Equal(before,await WitnessAsync());Assert.Equal(8800,(await NativeAsync()).Net);
        await using var changed=await F.OpenAsync();await changed.ExecuteAsync("UPDATE employeepaymentdetails SET BankAccountNo='00123456799720' WHERE EmployeeId=@Two",new{Two});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>NativeAsync());
    }

    [IsolatedTheory]
    [InlineData("actor")][InlineData("client")][InlineData("period")][InlineData("basis")][InlineData("source")][InlineData("employee")]
    public async Task Even_authenticated_receipt_requires_exact_actor_client_period_source_employee_and_basis(string change)
    {
        var receipt=await ApplyAsync(await ReadyPtAsync());await using var db=await F.OpenAsync();var code="closed_salary_receipt:"+Client+":"+receipt.RequestId;
        var protector=new PortableIntegrationCredentialProtector(F.Config,new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),Microsoft.Extensions.Logging.Abstractions.NullLogger<PortableIntegrationCredentialProtector>.Instance);
        var envelope=System.Text.Json.Nodes.JsonNode.Parse((await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=@Client AND ModuleCode=@code",new{Client,code}))!)!;
        var saved=System.Text.Json.Nodes.JsonNode.Parse(protector.UnprotectHistoricalSalary(envelope["payload"]!.ToString())!)!;
        switch(change){case "actor":saved["actorId"]=Client+1;break;case "client":saved["receipt"]!["clientId"]=Client+1;break;case "period":saved["receipt"]!["payPeriod"]="2026-08";break;case "basis":saved["receipt"]!["professionalTax"]![0]!["nativeBasisHash"]=new string('a',64);break;case "source":saved["sourceHash"]=new string('a',64);break;default:saved["receipt"]!["rows"]![0]!["employeeId"]=Two;break;}
        envelope["payload"]=protector.ProtectHistoricalSalary(saved.ToJsonString());await db.ExecuteAsync("UPDATE modulesettings SET SettingsJson=CAST(@json AS JSON) WHERE client_id=@Client AND ModuleCode=@code",new{Client,code,json=envelope.ToJsonString()});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>NativeAsync());
    }

    [IsolatedFact]
    public async Task Late_ordinary_simulation_failure_rolls_back_history_controls_provisional_receipt_and_audit()
    {
        var preview=await ReadyPtAsync(true);var before=await WitnessAsync();await using var db=await F.OpenAsync();
        await db.ExecuteAsync("CREATE TRIGGER codex_historical_pt_97726_late AFTER INSERT ON modulesettings FOR EACH ROW BEGIN IF NEW.client_id=97726 AND NEW.ModuleCode LIKE 'closed_salary_receipt:%' THEN UPDATE employee_monthly_attendance SET payable_days=29 WHERE client_id=97726 AND employee_id=9772602 AND attendance_month='2026-09'; END IF; END");
        try{await Assert.ThrowsAsync<InvalidOperationException>(()=>ApplyAsync(preview));Assert.Equal(0,await WrittenAsync());Assert.Equal(before,await WitnessAsync());Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=@Client",new{Client}));}
        finally{await db.ExecuteAsync("DROP TRIGGER IF EXISTS codex_historical_pt_97726_late");}
    }
}

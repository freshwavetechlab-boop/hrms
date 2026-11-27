using System.Reflection;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Repositories;

public sealed class EmployeeBulkSalaryInputDatabaseTests
{
    private const string TestConnection = "FREVO_BULK_SALARY_INPUT_TEST_DB";
    private sealed class SyntheticMySqlFactAttribute : FactAttribute
    {
        public SyntheticMySqlFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(TestConnection)))
                Skip = "Requires a disposable localhost MySQL container named by its salary-bulk-test hostname.";
        }
    }

    [SyntheticMySqlFact]
    public Task Reviewed_workbook_updates_only_selected_inputs_and_effective_pay_period() => WithDatabaseAsync(CheckReviewedWorkbookAsync);

    [SyntheticMySqlFact]
    public Task Monthly_ctc_is_reviewed_and_persisted_as_one_annual_amount() => WithDatabaseAsync(async connection =>
    {
        await using var db = new MySqlConnection(connection);
        await db.OpenAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
        var repository = await InitializeRepositoryAsync(db, config);
        await db.ExecuteAsync("""
            CREATE TABLE attendance_groups(id INT PRIMARY KEY, client_id INT NOT NULL, is_active BOOLEAN);
            CREATE TABLE attendance_group_employees(attendance_group_id INT NOT NULL, employee_id INT NOT NULL);
            """);
        Task<decimal> Master(string code) => db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE ClientId=42 AND EmployeeCode=@code", new { code });
        async Task Import(IFormFile file, string mode, bool keepCtc = false)
        {
            var preview = await repository.PreflightImportCsvAsync(42, file, mode);
            Assert.True(preview.CanImport, JsonSerializer.Serialize(preview.Rows));
            var decisions = JsonSerializer.Serialize(preview.Rows.Select(row => new {
                row.RowNumber, row.Sheet, action = row.MatchedEmployeeId.HasValue ? "update" : "insert", employeeId = row.MatchedEmployeeId,
                fieldChoices = row.Changes.ToDictionary(c => c.Field, c => keepCtc && c.Field == "annualCtc" ? "keepExisting" : "useImported")
            }));
            var result = await repository.ImportCsvAsync(42, file, mode, preview.ReviewToken, decisions);
            Assert.True(result.Errors.Count == 0, string.Join(" | ", result.Errors));
        }
        var csvBytes = System.Text.Encoding.UTF8.GetBytes("Employee Code,Monthly CTC\nTEST-1,21100.25");
        await Import(new FormFile(new MemoryStream(csvBytes), 0, csvBytes.Length, "file", "update.csv"), "update");
        Assert.Equal(253203m, await Master("TEST-1"));
        var insert = Workbook(["Employee Code","First Name","Date Of Joining","Monthly CTC"], ["NEW-CTC","New","2026-09-01","12000"]);
        await Import(insert, "insert");
        Assert.Equal(144000m, await Master("NEW-CTC"));
        await Import(Workbook(["Employee Code","Monthly CTC"], ["NEW-CTC","0"]), "upsert");
        Assert.Equal(0m, await Master("NEW-CTC"));

        var conflict = Workbook(["Employee Code","Annual CTC","Monthly CTC"], ["TEST-1","100000","9000"]);
        var blocked = await repository.PreflightImportCsvAsync(42, conflict, "update");
        Assert.Contains(blocked.Rows.Single().BlockingReasons, reason => reason.Contains("do not match"));
        Assert.NotEmpty((await repository.ImportCsvAsync(42, conflict, "update")).Errors);
        Assert.Equal(253203m, await Master("TEST-1"));
        string[] headers = ["Employee Code","Salary Template Id","Salary Effective From","Annual CTC","Monthly CTC","Basic [SALARY_INPUT:10]"];
        var dated = Workbook(headers, ["TEST-2","101","2026-10-01","100000","8333.33",""]);
        await Import(dated, "update");
        var keepAnnual = Workbook(headers, ["TEST-2","101","2026-10-02","","25000","17000"]);
        await Import(keepAnnual, "update", keepCtc: true);
        var history = (await db.QueryAsync<(decimal Annual, decimal Basic)>("""
            SELECT AnnualCtc,SalaryJson->>'$."10"' FROM employee_it0008_basic_pay
            WHERE EmployeeId=202 AND EffectiveFrom IN ('2026-10-01','2026-10-02') ORDER BY EffectiveFrom
            """)).ToArray();
        Assert.Equal(new[] { (100000m, 11000m), (100000m, 17000m) }, history);
    });

    [SyntheticMySqlFact]
    public Task Bulk_assignment_changes_only_template_for_eligible_reviewed_rows() => WithDatabaseAsync(CheckTemplateAssignmentAsync);

    [SyntheticMySqlFact]
    public Task Reviewed_test_cleanup_removes_paid_and_unpaid_native_rows_even_with_zero_basic_and_no_replacement() => WithDatabaseAsync(async connection =>
    {
        await using var db = new MySqlConnection(connection);
        await db.OpenAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
        var repository = await InitializeRepositoryAsync(db, config);
        await new PayRunRepository(config, null!).InitializeAsync();
        await db.ExecuteAsync("""
            DELETE FROM employee_it0008_basic_pay WHERE EmployeeId=201;
            DELETE FROM employeesalarycomponents WHERE EmployeeId=201;
            UPDATE employees SET SalaryStructureId='',AnnualCtc=0,SalaryJson='{}' WHERE Id=201;
            CREATE TABLE tax_computation_snapshots(id INT PRIMARY KEY,client_id INT,pay_run_id INT,employee_id INT,monthly_tds DECIMAL(18,2),snapshot_json JSON);
            CREATE TABLE notification_queue(Id INT PRIMARY KEY,ClientId INT,EventCode VARCHAR(60),ResourceType VARCHAR(60),ResourceId VARCHAR(100),Status VARCHAR(40),SentAt DATETIME,ErrorMessage TEXT);
            INSERT INTO payruns(Id,ClientId,PayPeriod,PayDate,RunCode,TotalWorkingDays,Status) VALUES
            (801,42,'2026-10','2026-11-01','REGULAR',31,'Partially Paid'),(802,42,'2026-09','2026-10-01','REGULAR',30,'Approved'),
            (803,42,'2026-11','2026-12-01','REGULAR',30,'Paid'),(804,42,'2026-12','2027-01-01','REGULAR',31,'Draft');
            INSERT INTO payrunemployees(Id,PayRunId,EmployeeId,ClientId,EmployeeCode,EmployeeName,PayableDays,PaymentStatus,GrossPay,NetPay,DetailsJson) VALUES
            (1,801,201,42,'TEST-1','First One',31,'Paid',0,0,'[{"Id":"NET_PAY","salaryTemplateId":"102"}]'),
            (2,801,202,42,'TEST-2','Second Two',31,'Pending',200,200,'[]'),
            (3,802,201,42,'TEST-1','First One',30,'Pending',40,40,'[]'),
            (4,803,201,42,'TEST-1','First One',30,'Paid',55,55,'[{"Id":"NET_PAY","salaryTemplateId":"101"}]'),
            (5,804,201,42,'TEST-1','First One',31,'Pending',60,60,'[]');
            INSERT INTO payrunemployeelines(PayRunEmployeeId,PayRunId,EmployeeId,ComponentCode,Amount)
                SELECT Id,PayRunId,EmployeeId,'BASIC',NetPay FROM payrunemployees;
            INSERT INTO payroll_calculation_traces(PayRunId,EmployeeId,ComponentCode,ComponentName,TraceOrder) VALUES(801,201,'BASIC','Basic',1);
            INSERT INTO payroll_validation_issues(PayRunId,EmployeeId,Message) VALUES(801,201,'Synthetic issue');
            INSERT INTO tax_computation_snapshots VALUES(1,42,801,201,0,'{}');
            INSERT INTO payrolladjustments(ClientId,EmployeeId,ComponentName,AdjustmentType,Amount,PayPeriod,PayRunType,Status,PayRunId)
            VALUES(42,201,'Travel','Reimbursement',40,'2026-09','Regular','Applied',802),(42,201,'Travel','Reimbursement',5,'2026-10','Regular','Applied',801);
            INSERT INTO notification_queue VALUES(1,42,'PAYSLIP.SEND','PayRunEmployee','1','Pending',NULL,NULL),
                (2,42,'PAYSLIP.PDF_SEND','PayRunPayslip','801:2:other','PayslipPdfPending',NULL,NULL);
            """);
        const string actor = "Admin #17";
        string[] headers = ["Employee ID","Employee Code","Salary Template Id","Assign Salary Template Only","Assign Payroll Template Policy"];
        var ordinary = Workbook(headers, ["201","TEST-1","101","TRUE","TRUE"]);
        var cleanup = Workbook([..headers,"Remove Superseded Test Payroll"], ["201","TEST-1","101","TRUE","TRUE","TRUE"]);
        static string Accept(EmployeeImportPreflightResult review) => JsonSerializer.Serialize(review.Rows.Select(row => new {
            row.Sheet,row.RowNumber,action="update",employeeId=row.MatchedEmployeeId,
            fieldChoices=row.Changes.ToDictionary(change => change.Field,_ => "useImported") }));
        var plain = await repository.PreflightImportCsvAsync(42, ordinary, "update");
        var assigned = await repository.ImportCsvAsync(42, ordinary, "update", plain.ReviewToken, Accept(plain));
        Assert.Empty(assigned.Errors);
        Assert.Equal(5, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrunemployees"));
        Assert.False((await repository.PreflightImportCsvAsync(42, cleanup, "update")).CanImport);
        Assert.NotEmpty((await repository.ImportCsvAsync(42, cleanup, "update", null, null, cleanupActor:actor)).Errors);
        var review = await repository.PreflightImportCsvAsync(42, cleanup, "update", cleanupActor:actor);
        var row = Assert.Single(review.Rows);
        Assert.Equal("removeTestPayslips", Assert.Single(row.Changes).Field);
        Assert.Collection(row.TestPayslips!, paid => Assert.Equal((801,true,false), (paid.RunId,paid.Paid,paid.Legacy)),
            unpaid => Assert.Equal((802,false,true), (unpaid.RunId,unpaid.Paid,unpaid.Legacy)),
            sameTemplate => Assert.Equal((803,true,false), (sameTemplate.RunId,sameTemplate.Paid,sameTemplate.Legacy)));
        Assert.NotEmpty((await repository.ImportCsvAsync(42, cleanup, "update", review.ReviewToken, Accept(review), cleanupActor:"Admin #18")).Errors);
        await db.ExecuteAsync("UPDATE payrunemployees SET GrossPay=1,NetPay=1 WHERE Id=1");
        var stale = await repository.ImportCsvAsync(42, cleanup, "update", review.ReviewToken, Accept(review), cleanupActor:actor);
        Assert.Contains(stale.Errors, message => message.Contains("changed after", StringComparison.Ordinal));
        Assert.Equal(5, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrunemployees"));
        // A delivery claimed after preview must roll everything back, even after the first run's deletion.
        await db.ExecuteAsync("INSERT INTO notification_queue VALUES(3,42,'PAYSLIP.SEND','PayRunEmployee','3','Processing',NULL,NULL)");
        review = await repository.PreflightImportCsvAsync(42, cleanup, "update", cleanupActor:actor);
        var busy = await repository.ImportCsvAsync(42, cleanup, "update", review.ReviewToken, Accept(review), cleanupActor:actor);
        Assert.Contains(busy.Errors, message => message.Contains("being sent", StringComparison.Ordinal));
        Assert.Equal(5, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrunemployees"));
        Assert.Equal("Pending", await db.ExecuteScalarAsync<string>("SELECT Status FROM notification_queue WHERE Id=1"));
        await db.ExecuteAsync("UPDATE notification_queue SET Status='Sent', SentAt=UTC_TIMESTAMP() WHERE Id=3");
        review = await repository.PreflightImportCsvAsync(42, cleanup, "update", cleanupActor:actor);
        var result = await repository.ImportCsvAsync(42, cleanup, "update", review.ReviewToken, Accept(review), cleanupActor:actor);
        Assert.True(result.Errors.Count == 0, string.Join(" | ", result.Errors));
        Assert.Equal(1,result.Updated);
        Assert.Equal(new[] {2,5}, await db.QueryAsync<int>("SELECT Id FROM payrunemployees ORDER BY Id"));
        Assert.Equal(("Approved",200m),await db.QuerySingleAsync<(string,decimal)>("SELECT Status,NetPay FROM payruns WHERE Id=801"));
        Assert.Equal(("Superseded",0m,"CLEANED-802"),await db.QuerySingleAsync<(string,decimal,string)>("SELECT Status,NetPay,RunCode FROM payruns WHERE Id=802"));
        Assert.Equal(new[] {"Cancelled","PayslipPdfPending","Sent"}, await db.QueryAsync<string>("SELECT Status FROM notification_queue ORDER BY Id"));
        Assert.Equal(new[] {"Approved","Applied"},await db.QueryAsync<string>("SELECT Status FROM payrolladjustments ORDER BY Id"));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT (SELECT COUNT(*) FROM tax_computation_snapshots)+(SELECT COUNT(*) FROM payroll_calculation_traces)+(SELECT COUNT(*) FROM payroll_validation_issues)+(SELECT COUNT(*) FROM payrunemployeelines WHERE PayRunEmployeeId IN (1,3))"));
        Assert.Equal(3,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrun_step_logs WHERE StepName='Removed test payslip' AND PerformedBy=@actor", new { actor }));
        Assert.Equal(0m,await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=201"));
        var report = await new ReportingRepository(config).RunAsync("salary-register", new() { ClientId=42, Month="2026-10" });
        Assert.Equal("TEST-2",Assert.Single(report.Rows)["Employee Code"]);
        var repeat = await repository.PreflightImportCsvAsync(42, cleanup, "update", cleanupActor:actor);
        Assert.Empty(Assert.Single(repeat.Rows).Changes);
        Assert.Empty(repeat.Rows[0].TestPayslips!);

        await db.ExecuteAsync("""
            INSERT INTO payruns(Id,ClientId,PayPeriod,PayDate,RunCode,TotalWorkingDays,Status)
                VALUES(806,42,'2027-02','2027-03-01','REGULAR',28,'Partially Paid');
            INSERT INTO payrunemployees(Id,PayRunId,EmployeeId,ClientId,EmployeeCode,EmployeeName,PayableDays,PaymentStatus,GrossPay,NetPay,DetailsJson)
                VALUES(8,806,201,42,'TEST-1','First One',28,'Pending',10,10,'[]'),
                      (9,806,202,42,'TEST-2','Second Two',28,'Paid',20,20,'[]');
            """);
        review = await repository.PreflightImportCsvAsync(42, cleanup, "update", cleanupActor:actor);
        Assert.Empty((await repository.ImportCsvAsync(42, cleanup, "update", review.ReviewToken, Accept(review), cleanupActor:actor)).Errors);
        Assert.Equal(("Paid",20m), await db.QuerySingleAsync<(string,decimal)>("SELECT Status,NetPay FROM payruns WHERE Id=806"));
        Assert.Equal(9, await db.ExecuteScalarAsync<int>("SELECT Id FROM payrunemployees WHERE PayRunId=806"));

        await db.ExecuteAsync("""
            INSERT INTO payruns(Id,ClientId,PayPeriod,PayDate,RunCode,TotalWorkingDays,Status)
                VALUES(805,42,'2027-01','2027-02-01','REGULAR',31,'Approved');
            INSERT INTO payrunemployees(Id,PayRunId,EmployeeId,ClientId,EmployeeCode,EmployeeName,PayableDays,PaymentStatus,GrossPay,NetPay,DetailsJson)
                VALUES(6,805,201,42,'TEST-1','First One',31,'Pending',10,10,'[]'),
                      (7,805,202,42,'TEST-2','Second Two',31,'Pending',20,20,'[]');
            INSERT INTO modulesettings VALUES(42,'pcst.active:805','{}'),(42,'prfi:run:805','{}'),(42,'payslip_design:42','{}');
            """);
        var incomplete = await repository.PreflightImportCsvAsync(42, cleanup, "update", cleanupActor:actor);
        Assert.Contains(Assert.Single(incomplete.Rows).BlockingReasons, message => message.Contains("whole roster", StringComparison.Ordinal));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrunemployees WHERE PayRunId=805"));
        var entireRoster = Workbook([..headers,"Remove Superseded Test Payroll"],
            ["201","TEST-1","101","TRUE","TRUE","TRUE"], ["202","TEST-2","101","TRUE","TRUE","TRUE"]);
        var entireReview = await repository.PreflightImportCsvAsync(42, entireRoster, "update", cleanupActor:actor);
        Assert.All(entireReview.Rows, reviewed => Assert.Empty(reviewed.BlockingReasons));
        var entireResult = await repository.ImportCsvAsync(42, entireRoster, "update", entireReview.ReviewToken, Accept(entireReview), cleanupActor:actor);
        Assert.True(entireResult.Errors.Count == 0, string.Join(" | ", entireResult.Errors));
        Assert.Equal(2, entireResult.Updated);
        Assert.Equal(new[] {5}, await db.QueryAsync<int>("SELECT Id FROM payrunemployees ORDER BY Id"));
        Assert.Equal("Superseded", await db.ExecuteScalarAsync<string>("SELECT Status FROM payruns WHERE Id=805"));
        Assert.Equal("payslip_design:42", await db.ExecuteScalarAsync<string>("SELECT ModuleCode FROM modulesettings"));
    });

    [SyntheticMySqlFact]
    public Task Assigned_template_uses_current_saved_formulas_without_reassignment_or_a_positive_ctc() => WithDatabaseAsync(async connection =>
    {
        await using var db = new MySqlConnection(connection);
        await db.OpenAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
        await InitializeRepositoryAsync(db, config);
        await db.ExecuteAsync("""
            ALTER TABLE modulesettings ADD IsEnabled BOOLEAN NOT NULL DEFAULT TRUE;
            INSERT INTO employee_salary_template_policies(EmployeeId,ClientId,SalaryStructureId,AssignedBy,AssignedAtUtc)
                VALUES(201,42,'101','Synthetic admin',UTC_TIMESTAMP());
            UPDATE employee_it0008_basic_pay SET AnnualCtc=0,SalaryJson='{"10":0,"11":0,"12":99999,"13":0}'
                WHERE EmployeeId=201 AND EffectiveFrom='2026-09-01';
            UPDATE salarystructurelines SET CalculationType='Formula',Formula='BASIC * 0.4 + 50',ValueText=''
                WHERE StructureId=101 AND ComponentId='12';
            """);
        var payroll = new PayRunRepository(config, null!);
        var sourceType = typeof(PayRunRepository).GetNestedType("PayRunSourceEmployee", BindingFlags.NonPublic)!;
        async Task<PayRunEmployee> CalculateSavedAsync()
        {
            var employee = Activator.CreateInstance(sourceType)!;
            sourceType.GetProperty("Id")!.SetValue(employee, 201);
            sourceType.GetProperty("ClientId")!.SetValue(employee, 42);
            sourceType.GetProperty("EmployeeCode")!.SetValue(employee, "TEST-1");
            var employees = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sourceType))!;
            employees.Add(employee);
            await using var tx = await db.BeginTransactionAsync();
            await (Task)typeof(PayRunRepository).GetMethod("LoadEmployeeTablesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(payroll, [db, tx, employees, "2026-10", true, true])!;
            var setup = await PayrollDataTableStore.GetSetupJsonAsync(db, tx);
            var row = (PayRunEmployee)typeof(PayRunRepository).GetMethod("BuildEmployee", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [0, employee, setup, "2026-10", 31, (decimal?)31m, 31m, new List<PayrollAdjustment>(), 0m, 0m, 0m, false, "TDS"])!;
            await tx.RollbackAsync();
            return row;
        }
        Assert.Equal(50m, (await CalculateSavedAsync()).NetPay);
        await db.ExecuteAsync("UPDATE salarystructurelines SET Formula='BASIC * 0.4 + 75' WHERE StructureId=101 AND ComponentId='12'");
        Assert.Equal(75m, (await CalculateSavedAsync()).NetPay);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_salary_template_policies WHERE EmployeeId=201 AND SalaryStructureId='101'"));
        Assert.Equal(0m, await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employee_it0008_basic_pay WHERE EmployeeId=201 AND EffectiveFrom='2026-09-01'"));
    });

    [SyntheticMySqlFact]
    public Task Directory_reports_todays_template_and_the_period_of_the_last_bulk_assignment() => WithDatabaseAsync(async connection =>
    {
        await using var db = new MySqlConnection(connection);
        await db.OpenAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
        var repository = await InitializeRepositoryAsync(db, config);
        var today = Payroll.API.Services.EmployeeEmploymentRules.Today;
        var from = today.AddDays(-28);
        var later = today.AddDays(-2);
        await db.ExecuteAsync("""
            INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,DateOfJoining,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson)
            VALUES(203,42,'TEST-3','Third','1990-01-01','102',360000,'{}','{}','{}'),
                  (204,42,'TEST-4','Fourth','1990-01-01','104',360000,'{}','{}','{}');
            INSERT INTO salarystructures(Id,ClientId,ClientRef,Name) VALUES(103,42,'42','Revised'),(104,43,'43','Other client');
            INSERT INTO salarystructurelines(StructureId,ComponentId,SortOrder) VALUES(103,'10',1);
            INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,SalaryJson)
            VALUES(203,42,'Hire',@from,@until,'Historical','102',120000,'{"10":10000}'),
                  (203,42,'Salary Change',@later,NULL,'Active','103',360000,'{"10":30000}');
            """, new { from, until = later.AddDays(-1), later });
        var file = Workbook(["Employee ID","Employee Code","Salary Template Id","Salary Effective From","Assign Salary Template Only"],
            ["203","TEST-3","101",from.ToString("yyyy-MM-dd"),"TRUE"]);
        var preview = await repository.PreflightImportCsvAsync(42, file, "update");
        var confirmation = JsonSerializer.Serialize(preview.Rows.Select(row => new { row.RowNumber, row.Sheet, action = "update",
            employeeId = row.MatchedEmployeeId, fieldChoices = row.Changes.ToDictionary(change => change.Field, _ => "useImported") }));
        var result = await repository.ImportCsvAsync(42, file, "update", preview.ReviewToken, confirmation);
        Assert.True(result.Errors.Count == 0, string.Join(" | ", result.Errors));
        Assert.Equal(1, result.Updated);

        var directory = (await repository.GetAsync(42)).ToDictionary(employee => employee.Id);
        Assert.Equal("103", directory[203].SalaryStructureId);
        Assert.Equal("Revised", directory[203].SalaryTemplateName);
        Assert.Equal(new EmployeeBulkTemplateAssignment("101", "Monthly", from, later.AddDays(-1)), directory[203].LatestBulkSalaryAssignment);
        Assert.Null(directory[204].SalaryTemplateName);
        Assert.Null(directory[204].LatestBulkSalaryAssignment);
        Assert.Equal("102", await db.QuerySingleAsync<string>("SELECT SalaryStructureId FROM employees WHERE Id=203"));

        // The selection date already has Monthly. The current salary and the next scheduled raise still use Revised.
        var raise = today.AddMonths(2);
        await db.ExecuteAsync("""
            UPDATE employee_it0008_basic_pay SET EffectiveTo=@end,SalaryStructureId='101',ActionType='Bulk Salary Input' WHERE EmployeeId=203 AND EffectiveFrom=@later;
            INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,Status,SalaryStructureId,AnnualCtc,SalaryJson)
            VALUES(203,42,'Raise',@raise,'Active','103',480000,'{"10":40000}');
            CREATE TABLE payruns(Id INT PRIMARY KEY,ClientId INT,PayPeriod VARCHAR(7),Status VARCHAR(30));
            CREATE TABLE payrunemployees(PayRunId INT,EmployeeId INT,ClientId INT,IsSkipped BOOLEAN);
            INSERT INTO payruns VALUES(1,42,@period,'Approved');
            INSERT INTO payrunemployees VALUES(1,203,42,FALSE);
            """, new { later, raise, end = raise.AddDays(-1), period = raise.ToString("yyyy-MM") });
        var backdatedForward = Workbook(["Employee ID","Employee Code","Salary Template Id","Salary Effective From","Assign Salary Template Only","Apply Template Forward"],
            ["203","TEST-3","101",from.ToString("yyyy-MM-dd"),"TRUE","TRUE"]);
        Assert.False((await repository.PreflightImportCsvAsync(42, backdatedForward, "update")).CanImport);
        var forward = Workbook(["Employee ID","Employee Code","Salary Template Id","Assign Salary Template Only","Apply Template Forward"],
            ["203","TEST-3","101","TRUE","TRUE"]);
        async Task<string> SalaryHistory() => JsonSerializer.Serialize(await db.QueryAsync(
            "SELECT * FROM employee_it0008_basic_pay WHERE EmployeeId IN(203,205) ORDER BY Id"));
        var originalHistory = await SalaryHistory();
        var forwardReview = await repository.PreflightImportCsvAsync(42, forward, "update");
        var change = Assert.Single(forwardReview.Rows.Single().Changes);
        Assert.Equal(("salaryTemplateId", "", "101"), (change.Field, change.OldValue, change.NewValue));
        var forwardResult = await repository.ImportCsvAsync(42, forward, "update", forwardReview.ReviewToken, confirmation);
        Assert.True(forwardResult.Errors.Count == 0, string.Join(" | ", forwardResult.Errors));
        Assert.Equal(1, forwardResult.Updated);
        var now = (await repository.GetAsync(42)).Single(employee => employee.Id == 203);
        Assert.Equal(("101","Monthly",360000m,30000m), (now.SalaryStructureId,now.SalaryTemplateName,now.AnnualCtc,now.SalaryComponents["10"]));
        var raised = await db.QuerySingleAsync<(string Template, decimal Ctc, decimal Basic)>("""
            SELECT SalaryStructureId,AnnualCtc,SalaryJson->>'$."10"' FROM employee_it0008_basic_pay WHERE EmployeeId=203 AND EffectiveFrom=@raise
            """, new { raise });
        Assert.Equal(("103",480000m,40000m), raised);
        Assert.Equal(originalHistory, await SalaryHistory());
        Assert.Equal("Approved", await db.ExecuteScalarAsync<string>("SELECT Status FROM payruns WHERE Id=1"));
        Assert.Empty((await repository.PreflightImportCsvAsync(42, forward, "update")).Rows.Single().Changes);

        // A later selection replaces the employee-level mapping without writing salary history.
        await db.ExecuteAsync("""
            INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,DateOfJoining,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson)
            VALUES(205,42,'TEST-5','Fifth','1990-01-01','',0,'{}','{}','{}');
            UPDATE payruns SET PayPeriod=@previousMonth,Status='Approved';
            """, new { previousMonth = today.AddMonths(-1).ToString("yyyy-MM") });
        var todayFile = Workbook(["Employee ID","Employee Code","Salary Template Id","Assign Salary Template Only","Assign Payroll Template Policy"],
            ["203","TEST-3","103","TRUE","TRUE"], ["205","TEST-5","103","TRUE","TRUE"]);
        var todayReview = await repository.PreflightImportCsvAsync(42, todayFile, "update");
        Assert.All(todayReview.Rows, row => { Assert.Empty(row.BlockingReasons); Assert.Single(row.Changes); });
        var todayDecisions = JsonSerializer.Serialize(todayReview.Rows.Select(row => new { row.RowNumber, row.Sheet, action = "update",
            employeeId = row.MatchedEmployeeId, fieldChoices = row.Changes.ToDictionary(item => item.Field, _ => "useImported") }));
        var todayResult = await repository.ImportCsvAsync(42, todayFile, "update", todayReview.ReviewToken, todayDecisions, requestedByUserId: 17);
        Assert.True(todayResult.Errors.Count == 0, string.Join(" | ", todayResult.Errors));
        Assert.Equal(2, todayResult.Updated);
        var roster = (await repository.GetAsync(42)).ToDictionary(row => row.Id);
        Assert.True(roster[203].UsesDatedSalaryInputs);
        Assert.False(roster[205].UsesDatedSalaryInputs);
        Assert.Equal("103", roster[203].SalaryTemplatePolicyId);
        Assert.Equal("103", roster[203].SalaryStructureId);
        Assert.Equal("Revised", roster[205].SalaryTemplateName);
        Assert.Equal(originalHistory, await SalaryHistory());
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_salary_template_policies WHERE ClientId=42 AND SalaryStructureId='103' AND AssignedBy='User #17'"));
        Assert.Empty(await EmployeeRepository.ReadTemplatePoliciesAsync(db, 999, [203,205]));
        var previouslyRead = roster[203];
        var fresh = (await repository.GetAsync(42)).Single(e => e.Id == 203);
        fresh.SalaryStructureId = "101";
        await repository.SaveAsync(fresh, "Test admin", "0008");
        Assert.Equal("101", (await repository.GetAsync(42)).Single(e => e.Id == 203).SalaryTemplatePolicyId);
        Assert.Equal(originalHistory, await SalaryHistory());
        previouslyRead.SalaryStructureId = "102";
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(previouslyRead, "Test admin", "0008"));
        Assert.Contains("changed", stale.Message);
    });

    [SyntheticMySqlFact]
    public Task Forced_assignment_fills_only_an_unpaid_salary_gap_from_the_reviewed_date() => WithDatabaseAsync(async connection =>
    {
        await using var db = new MySqlConnection(connection);
        await db.OpenAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
        var repository = await InitializeRepositoryAsync(db, config);
        await db.ExecuteAsync("""
            UPDATE employee_it0008_basic_pay SET EffectiveTo='2026-09-30' WHERE EmployeeId=201 AND ActionType='Hire';
            UPDATE employee_it0008_basic_pay SET ActionType='Bulk Salary Input' WHERE EmployeeId=201 AND EffectiveFrom='2026-11-01';
            INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,DateOfJoining,IsActive,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson)
            VALUES (203,42,'TEST-3','Third','2026-09-01',TRUE,'102',123000,'{"10":10250}','{}','{}'),
                   (204,42,'TEST-4','Fourth','2026-10-20',TRUE,'102',123000,'{"10":10250}','{}','{}');
            INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,Status,SalaryStructureId,AnnualCtc,SalaryJson)
            VALUES(203,42,'Hire','2026-12-01','Active','102',123000,'{"10":10250}'),
                  (204,42,'Hire','2026-12-01','Active','102',123000,'{"10":10250}');
            CREATE TABLE payruns(Id INT PRIMARY KEY,ClientId INT,PayPeriod VARCHAR(7),Status VARCHAR(30));
            CREATE TABLE payrunemployees(PayRunId INT,EmployeeId INT,ClientId INT,IsSkipped BOOLEAN);
            INSERT INTO payruns VALUES(1,42,'2026-10','Approved'),(2,42,'2026-11','Paid');
            INSERT INTO payrunemployees VALUES(1,201,42,TRUE),(2,201,42,FALSE),(2,203,42,FALSE);
            """);
        var file = Workbook(["Employee ID","Employee Code","Salary Template Id","Salary Effective From","Assign Salary Template Only"],
            ["201","TEST-1","101","2026-10-03","TRUE"],["202","TEST-2","101","2026-10-03","TRUE"],
            ["203","TEST-3","101","2026-10-03","TRUE"],["204","TEST-4","101","2026-10-03","TRUE"]);
        async Task<string> ProtectedRowsAsync() => JsonSerializer.Serialize(await db.QueryAsync(
            "SELECT * FROM employee_it0008_basic_pay WHERE EmployeeId<>201 OR EffectiveFrom<>'2026-10-03' ORDER BY Id"))
            + JsonSerializer.Serialize(await db.QueryAsync("SELECT Id,SalaryStructureId,AnnualCtc,CAST(SalaryJson AS CHAR) Inputs FROM employees ORDER BY Id"))
            + JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM employeesalarycomponents ORDER BY EmployeeId,ComponentId"))
            + JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM payruns ORDER BY Id"))
            + JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM payrunemployees ORDER BY PayRunId,EmployeeId"));
        static string Confirm(EmployeeImportPreflightResult preview) => JsonSerializer.Serialize(preview.Rows.Select(row => new
        {
            row.RowNumber, row.Sheet, action = row.BlockingReasons.Count == 0 ? "update" : "skip", employeeId = row.MatchedEmployeeId,
            fieldChoices = row.Changes.ToDictionary(change => change.Field, _ => "useImported")
        }));
        var original = await ProtectedRowsAsync();
        var normal = await repository.PreflightImportCsvAsync(42, file, "update");
        Assert.True(normal.Rows[0].CanForceSalaryAssignment);
        Assert.NotEmpty(normal.Rows[0].BlockingReasons);
        Assert.Empty(normal.Rows[1].BlockingReasons);
        Assert.False(normal.Rows[2].CanForceSalaryAssignment);
        Assert.False(normal.Rows[3].CanForceSalaryAssignment);

        const string actor = "synthetic-admin";
        Assert.NotEmpty((await repository.ImportCsvAsync(42, file, "update", null, "[]", actor)).Errors);
        var unsolicitedForce = await repository.ImportCsvAsync(42, file, "update", normal.ReviewToken, Confirm(normal), actor);
        Assert.NotEmpty(unsolicitedForce.Errors);
        var forced = await repository.PreflightImportCsvAsync(42, file, "update", actor);
        Assert.Empty(forced.Rows[0].BlockingReasons);
        Assert.Contains(forced.Rows[0].Changes, change => change.Field == "salaryTemplateId");
        Assert.Empty(forced.Rows[1].Changes);
        Assert.NotEmpty(forced.Rows[2].BlockingReasons);
        Assert.Contains(forced.Rows[3].BlockingReasons, msg => msg.Contains("joining date", StringComparison.OrdinalIgnoreCase));
        var decisions = Confirm(forced);
        var unauthorized = await repository.ImportCsvAsync(42, file, "update", forced.ReviewToken, decisions);
        Assert.NotEmpty(unauthorized.Errors);
        var wrongActor = await repository.ImportCsvAsync(42, file, "update", forced.ReviewToken, decisions, "unrelated-admin");
        Assert.NotEmpty(wrongActor.Errors);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay WHERE EmployeeId=201 AND EffectiveFrom='2026-10-03'"));

        var result = await repository.ImportCsvAsync(42, file, "update", forced.ReviewToken, decisions, actor);
        Assert.True(result.Errors.Count == 0, string.Join(" | ", result.Errors));
        Assert.Equal(1, result.Updated);
        var inserted = await db.QuerySingleAsync<(string Template, decimal Ctc, string Inputs, DateTime End, string Status, string Author, string Action)>("""
            SELECT SalaryStructureId,AnnualCtc,CAST(SalaryJson AS CHAR),EffectiveTo,Status,CreatedBy,ActionType
            FROM employee_it0008_basic_pay WHERE EmployeeId=201 AND EffectiveFrom='2026-10-03'
            """);
        Assert.Equal(("101", 0m, "{}", new DateTime(2026,10,31), "Historical", actor, "Bulk Salary Input"), inserted);
        Assert.True(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail WHERE EmployeeId=201 AND ChangedBy=@actor AND EffectiveFrom='2026-10-03'", new { actor }) > 0);
        Assert.Equal(original, await ProtectedRowsAsync());
        var repeat = await repository.PreflightImportCsvAsync(42, file, "update", actor);
        Assert.Empty(repeat.Rows[0].Changes);
        var repeated = await repository.ImportCsvAsync(42, file, "update", repeat.ReviewToken, Confirm(repeat), actor);
        Assert.Empty(repeated.Errors);
        Assert.Equal(0, repeated.Updated);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay WHERE EmployeeId=201 AND EffectiveFrom='2026-10-03'"));

        var amounts = Workbook(["Employee Code","Salary Template Id","Salary Effective From","Basic [SALARY_INPUT:10]","Telephone [SALARY_INPUT:11]"],
            ["TEST-1","101","2026-10-03","12000","0"]);
        var forcedWorkbook = await repository.PreflightImportCsvAsync(42, amounts, "update", actor);
        Assert.False(forcedWorkbook.CanImport);
        Assert.Equal(Guid.Empty, forcedWorkbook.ReviewToken);
        var inputReview = await repository.PreflightImportCsvAsync(42, amounts, "update");
        Assert.Empty(inputReview.Rows.Single().BlockingReasons);
        var appliedInputs = await repository.ImportCsvAsync(42, amounts, "update", inputReview.ReviewToken, Confirm(inputReview));
        Assert.True(appliedInputs.Errors.Count == 0, string.Join(" | ", appliedInputs.Errors));
        var values = await db.QuerySingleAsync<(decimal Basic, decimal Phone, DateTime Until)>("""
            SELECT SalaryJson->>'$."10"',SalaryJson->>'$."11"',EffectiveTo FROM employee_it0008_basic_pay
            WHERE EmployeeId=201 AND EffectiveFrom='2026-10-03'
            """);
        Assert.Equal((12000m, 0m, new DateTime(2026,10,31)), values);
        Assert.Equal(original, await ProtectedRowsAsync());

        // A payroll can be approved after the operator reviewed the gap, before they confirm the assignment.
        await db.ExecuteAsync("UPDATE payruns SET Status='Draft' WHERE Id=2");
        var employeeWithPendingPayroll = Workbook(["Employee ID","Employee Code","Salary Template Id","Salary Effective From","Assign Salary Template Only"],
            ["203","TEST-3","101","2026-10-03","TRUE"]);
        var stale = await repository.PreflightImportCsvAsync(42, employeeWithPendingPayroll, "update", actor);
        Assert.Empty(stale.Rows.Single().BlockingReasons);
        await db.ExecuteAsync("UPDATE payruns SET Status='Approved' WHERE Id=2");
        var staleCommit = await repository.ImportCsvAsync(42, employeeWithPendingPayroll, "update", stale.ReviewToken, Confirm(stale), actor);
        Assert.NotEmpty(staleCommit.Errors);
        Assert.Equal(0, staleCommit.Updated);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay WHERE EmployeeId=203 AND EffectiveFrom='2026-10-03'"));
    });

    [SyntheticMySqlFact]
    public Task New_hire_salary_import_distinguishes_defaults_from_zero_and_recalculates_formulas() => WithDatabaseAsync(async connection =>
    {
        await using var db = new MySqlConnection(connection);
        await db.OpenAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
        var repository = await InitializeRepositoryAsync(db, config);
        await db.ExecuteAsync("""
            CREATE TABLE attendance_groups(id INT PRIMARY KEY, client_id INT NOT NULL, is_active BOOLEAN);
            CREATE TABLE attendance_group_employees(attendance_group_id INT NOT NULL, employee_id INT NOT NULL);
            """);
        await db.ExecuteAsync("UPDATE salarystructurelines SET ValueText='75' WHERE StructureId=101 AND ComponentId='11'");
        string[] headers = ["Employee Code","First Name","Date Of Joining","Salary Template Id","Salary Effective From",
            "Basic [SALARY_INPUT:10]","Telephone [SALARY_INPUT:11]"];
        var file = Workbook(headers,
            ["NEW-Z","Abc","2026-10-01","101","2026-10-01","20000","0"],
            ["NEW-D","Xyz","2026-10-01","101","2026-10-01","",""]);
        var pre = await repository.PreflightImportCsvAsync(42, file, "insert");
        Assert.True(pre.CanImport, JsonSerializer.Serialize(pre.Rows));
        var saved = await repository.ImportCsvAsync(42, file, "insert", pre.ReviewToken, "[]");
        Assert.True(saved.Errors.Count == 0, string.Join(" | ", saved.Errors));
        Assert.Equal(2, saved.Inserted);
        var employees = (await repository.GetAsync(42)).ToDictionary(e => e.EmployeeCode);
        var payroll = new PayRunRepository(config, null!);
        PayrollReviewComponent Component(string id, string code, string method, string value, string formula = "") =>
            new(id, code, "Earning", "Regular Earning", "", method, value, formula, "", false, true, true, 0, "Fixed Pay");
        var snapshot = new PayrollReviewSnapshot("Template", 42, "", 0,
            [Component("10","BASIC","Fixed Amount","0"),Component("11","TEL_ALLOW","Fixed Amount","75"),
             Component("12","HRA","Formula","","BASIC * 0.4")], [new("10"),new("11"),new("12")]);
        foreach (var (code, basic, phone) in new[] { ("NEW-Z",20000m,0m), ("NEW-D",0m,75m) })
        {
            var id = employees[code].Id;
            var stored = await db.QuerySingleAsync<(string Template, string Inputs)>(
                "SELECT SalaryStructureId,CAST(SalaryJson AS CHAR) FROM employee_it0008_basic_pay WHERE EmployeeId=@id", new { id });
            Assert.Equal("101", stored.Template);
            var inputs = JsonSerializer.Deserialize<Dictionary<string,decimal>>(stored.Inputs)!;
            if (code == "NEW-D") Assert.Empty(inputs);
            else Assert.Equal(phone, inputs["11"]);
            await db.ExecuteAsync("INSERT INTO employee_monthly_attendance VALUES(42,@id,'2026-10',31,31,31,'Manual','')", new { id });
            var simulated = await payroll.SimulateEmployeeTemplateAsync(new PayrollSimulationRequest { Mode = "EmployeeTemplate",
                ClientId = 42, EmployeeId = id, PayPeriod = "2026-10", TemplateId = "101", Snapshot = snapshot }, CancellationToken.None);
            Assert.True(simulated.Complete, JsonSerializer.Serialize(simulated.Issues));
            Assert.Equal(basic, simulated.Lines.Single(l => l.Code == "BASIC").Earned);
            Assert.Equal(phone, simulated.Lines.Single(l => l.Code == "TEL_ALLOW").Earned);
            Assert.Equal(basic * 0.4m, simulated.Lines.Single(l => l.Code == "HRA").Earned);
        }
        // Upsert also accepts defaults for a new hire while an existing employee with a blank salary row keeps their assignment.
        var mixed = Workbook(headers, ["NEW-M","Def","2026-10-01","101","2026-10-01","",""],
            ["TEST-1","First","2026-09-01","102","2026-10-01","",""]);
        var review = await repository.PreflightImportCsvAsync(42, mixed, "upsert");
        Assert.True(review.CanImport, JsonSerializer.Serialize(review.Rows));
        var result = await repository.ImportCsvAsync(42, mixed, "upsert", review.ReviewToken, "[]");
        Assert.True(result.Errors.Count == 0, string.Join(" | ", result.Errors));
        Assert.Equal(1, result.Inserted);
        Assert.Equal("101", await db.QuerySingleAsync<string>("SELECT SalaryStructureId FROM employee_it0008_basic_pay WHERE EmployeeId=201 AND EffectiveFrom='2026-09-01'"));
        Assert.Equal(0, await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay WHERE EmployeeId=201 AND EffectiveFrom='2026-10-01'"));
        var equalAmount = Workbook(["Employee Code","Salary Template Id","Salary Effective From","Basic [SALARY_INPUT:10]"],
            ["TEST-1","101","2026-10-01","10000"]);
        var equalReview = await repository.PreflightImportCsvAsync(42, equalAmount, "update");
        var equalResult = await repository.ImportCsvAsync(42, equalAmount, "update", equalReview.ReviewToken, "[]");
        Assert.True(equalResult.Errors.Count == 0, string.Join(" | ", equalResult.Errors));
        Assert.Equal("Bulk Salary Input", await db.QuerySingleAsync<string>(
            "SELECT ActionType FROM employee_it0008_basic_pay WHERE EmployeeId=201 AND EffectiveFrom='2026-10-01'"));
        var retry = await repository.PreflightImportCsvAsync(42, equalAmount, "update");
        var retryResult = await repository.ImportCsvAsync(42, equalAmount, "update", retry.ReviewToken, "[]");
        Assert.Empty(retryResult.Errors);
        Assert.Equal(0, retryResult.Updated);
    });

    private static async Task WithDatabaseAsync(Func<string, Task> check)
    {
        var settings = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable(TestConnection)!);
        Assert.Equal("127.0.0.1", settings.Server);
        Assert.Equal("employee_bulk_salary_test", settings.Database);
        await using var root = new MySqlConnection(settings.ConnectionString);
        await root.OpenAsync();
        Assert.Contains(await root.ExecuteScalarAsync<string>("SELECT @@hostname"), new[] { "salary-bulk-test", "frevo-workflow-scope-test-20261114" });
        var database = "bulk_salary_" + Guid.NewGuid().ToString("N");
        await root.ExecuteAsync($"CREATE DATABASE `{database}`");
        try
        {
            settings.Database = database;
            await check(settings.ConnectionString);
        }
        finally
        {
            await root.ExecuteAsync($"DROP DATABASE `{database}`");
        }
    }

    private static async Task<EmployeeRepository> InitializeRepositoryAsync(MySqlConnection db, IConfiguration config)
    {
        var repository = new EmployeeRepository(config, null!, null!);
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EssProfile", "profile-schema.sql")));
        await db.ExecuteAsync("ALTER TABLE employees MODIFY Id INT NOT NULL AUTO_INCREMENT");
        await PayrollDataTableStore.EnsureAsync(db);
        await repository.InitializeAsync();
        await db.ExecuteAsync("""
            ALTER TABLE employees ADD Grade VARCHAR(100) DEFAULT '', ADD PortalAccess BOOL DEFAULT FALSE, ADD PersonalJson JSON, ADD PaymentJson JSON;
            ALTER TABLE worklocations ADD ClientId INT NOT NULL DEFAULT 0, ADD City VARCHAR(100), ADD State VARCHAR(100), ADD IsActive BOOL DEFAULT TRUE;
            ALTER TABLE authusers ADD Email VARCHAR(190);
            CREATE TABLE dropdownmasters (Type VARCHAR(100), Value VARCHAR(100), ClientId INT, IsActive BOOL);
            CREATE TABLE payrollsetups (Id INT PRIMARY KEY, SetupJson JSON);
            CREATE TABLE modulesettings (client_id INT, ModuleCode VARCHAR(100), SettingsJson JSON);
            CREATE TABLE employee_monthly_attendance (client_id INT, employee_id INT, attendance_month VARCHAR(7),
              working_days INT,present_days DECIMAL(10,2),payable_days DECIMAL(10,2),source_type VARCHAR(50),remarks TEXT);
            INSERT INTO employee_monthly_attendance VALUES(42,201,'2026-10',31,31,31,'Manual',''),(42,202,'2026-10',31,31,31,'Manual','');
            INSERT INTO clients(Id,Name) VALUES(42,'Synthetic client');
            INSERT INTO salarystructures(Id,ClientId,ClientRef,Name) VALUES(101,42,'42','Monthly'),(102,42,'42','Later');
            INSERT INTO salarycomponents(Id,Code,Name,CalculationType,Formula) VALUES
            (10,'BASIC','Basic','Fixed Amount',''),(11,'TEL_ALLOW','Telephone','Fixed Amount',''),
            (12,'HRA','Housing','Formula','BASIC * 0.4'),(13,'OTHER','Other','Fixed Amount','');
            INSERT INTO salarystructurelines(StructureId,ComponentId,SortOrder) VALUES(101,'10',1),(101,'11',2),(101,'12',3),(101,'13',4),(102,'10',1);
            INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson)
            VALUES(201,42,'TEST-1','First','One','2026-09-01','102',300000,'{"10":25000}','{}','{}'),
                  (202,42,'TEST-2','Second','Two','2026-09-01','101',240000,'{"10":11000,"11":160,"13":444}','{}','{}');
            INSERT INTO employeesalarycomponents(EmployeeId,ComponentId,Amount) VALUES(201,'10',25000),(202,'10',11000),(202,'11',160),(202,'13',444);
            INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,EffectiveTo,Status,SalaryStructureId,AnnualCtc,SalaryJson,ChangeReason,CreatedBy)
            VALUES(201,42,'Hire','2026-09-01','2026-10-31','Historical','101',180000,'{"10":10000,"11":120,"13":333}','Initial','Test'),
                  (201,42,'Salary Change','2026-11-01',NULL,'Active','102',300000,'{"10":25000}','Future','Test'),
                  (202,42,'Hire','2026-09-01',NULL,'Active','101',240000,'{"10":11000,"11":160,"13":444}','Initial','Test');
            """);
        return repository;
    }

    private static async Task CheckReviewedWorkbookAsync(string connection)
    {
        await using var db = new MySqlConnection(connection);
        await db.OpenAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
        var repository = await InitializeRepositoryAsync(db, config);
        var storedTemplates = await db.QueryAsync("SELECT * FROM salarystructurelines ORDER BY Id");
        var before = JsonSerializer.Serialize(storedTemplates);
        async Task<(string Template, decimal Ctc, decimal? Basic, decimal? Phone, decimal? Other)> PayAt(int employee, string date) =>
            await db.QuerySingleAsync<(string,decimal,decimal?,decimal?,decimal?)>("""
                SELECT SalaryStructureId,AnnualCtc, SalaryJson->>'$."10"', SalaryJson->>'$."11"', SalaryJson->>'$."13"'
                FROM employee_it0008_basic_pay WHERE EmployeeId=@employee AND @date>=EffectiveFrom AND (EffectiveTo IS NULL OR @date<=EffectiveTo)
                ORDER BY EffectiveFrom DESC,Id DESC LIMIT 1
                """, new { employee, date });
        var september = await PayAt(201, "2026-09-30");
        var november = await PayAt(201, "2026-11-01");
        string[] headers = ["Employee Code","Salary Template Id","Salary Effective From","Basic [SALARY_INPUT:10]",
            "Telephone [SALARY_INPUT:11]","Annual CTC","Other [SALARY_INPUT:13]"];
        var workbook = Workbook(headers, ["TEST-1","101","2026-10-01","20000","0","216000",""],
            ["TEST-2","101","2026-10-01","21000","","","500"]);
        var review = await repository.PreflightImportCsvAsync(42, workbook, "update");
        Assert.True(review.CanImport, JsonSerializer.Serialize(review.Rows));
        Assert.True(review.RequiresConfirmation);
        Assert.Equal(2, review.Rows.Count);
        Assert.All(review.Rows, row => Assert.Empty(row.BlockingReasons));
        var decisions = review.Rows.Select(row => new { row.RowNumber, row.Sheet, action = "update", employeeId = row.MatchedEmployeeId,
            fieldChoices = row.Changes.ToDictionary(change => change.Field,
                change => change.Field == "salaryInput:13" ? "keepExisting" : "useImported") });
        var result = await repository.ImportCsvAsync(42, workbook, "update", review.ReviewToken, JsonSerializer.Serialize(decisions));
        Assert.True(result.Errors.Count == 0, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(2, result.Updated);
        Assert.Equal(0, result.Inserted);
        Assert.Equal(september, await PayAt(201, "2026-09-30"));
        Assert.Equal(november, await PayAt(201, "2026-11-01"));
        Assert.Equal(("101",216000m,(decimal?)20000m,(decimal?)0m,(decimal?)333m), await PayAt(201, "2026-10-01"));
        Assert.Equal(("101",240000m,(decimal?)21000m,(decimal?)160m,(decimal?)444m), await PayAt(202, "2026-10-01"));
        Assert.Equal(25000m, await db.QuerySingleAsync<decimal>("SELECT Amount FROM employeesalarycomponents WHERE EmployeeId=201 AND ComponentId='10'"));
        Assert.Equal(21000m, await db.QuerySingleAsync<decimal>("SELECT Amount FROM employeesalarycomponents WHERE EmployeeId=202 AND ComponentId='10'"));
        Assert.Equal(before, JsonSerializer.Serialize(await db.QueryAsync("SELECT * FROM salarystructurelines ORDER BY Id")));
        var directory = (await repository.GetAsync(42)).ToDictionary(employee => employee.Id);
        Assert.Equal(25000m, directory[201].SalaryComponents["10"]);
        Assert.Equal(21000m, directory[202].SalaryComponents["10"]);

        var payroll = new PayRunRepository(config, null!);
        PayrollReviewComponent Component(string id, string code, string method, string formula = "") =>
            new(id, code, "Earning", "Regular Earning", "", method, "", formula, "", false, true, true, 0, "Fixed Pay");
        var snapshot = new PayrollReviewSnapshot("Template", 42, "", 0,
            [Component("10", "BASIC", "Fixed Amount"), Component("11", "TEL_ALLOW", "Fixed Amount"),
             Component("12", "HRA", "Formula", "BASIC * 0.4"), Component("13", "OTHER", "Fixed Amount")],
            [new("10"), new("11"), new("12"), new("13")]);
        foreach (var (employee, basic, phone, other) in new[] { (201, 20000m, 0m, 333m), (202, 21000m, 160m, 444m) })
        {
            var simulated = await payroll.SimulateEmployeeTemplateAsync(new PayrollSimulationRequest
            {
                Mode = "EmployeeTemplate", Snapshot = snapshot, ClientId = 42, EmployeeId = employee, TemplateId = "101", PayPeriod = "2026-10"
            }, CancellationToken.None);
            Assert.True(simulated.Complete, JsonSerializer.Serialize(simulated.Issues));
            Assert.Equal(basic, simulated.ComponentInputs!["10"]);
            Assert.Equal(phone, simulated.ComponentInputs["11"]);
            Assert.Equal(other, simulated.ComponentInputs["13"]);
            Assert.Equal(basic * 0.4m, simulated.Lines.Single(line => line.Code == "HRA").Earned);
            Assert.Equal(basic + phone + other + basic * 0.4m, simulated.Net);
        }

        var calculated = Workbook(["Employee Code","Salary Template Id","Salary Effective From","Housing [SALARY_INPUT:12]"],
            ["TEST-1","101","2026-10-01","5000"]);
        var invalid = await repository.PreflightImportCsvAsync(42, calculated, "update");
        Assert.False(invalid.CanImport);
        Assert.Contains(invalid.Rows.Single().BlockingReasons, reason => reason.Contains("Calculated", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task CheckTemplateAssignmentAsync(string connection)
    {
        await using var db = new MySqlConnection(connection);
        await db.OpenAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
        var repository = await InitializeRepositoryAsync(db, config);
        await db.ExecuteAsync("""
            UPDATE employee_it0008_basic_pay SET SalaryStructureId='102' WHERE EmployeeId=202;
            INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,DateOfJoining,IsActive,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson)
            VALUES (203,42,'TEST-3','Third','2026-09-01',TRUE,'',0,'{}','{}','{}'),
                   (204,42,'TEST-4','Fourth','2026-10-20',TRUE,'',0,'{}','{}','{}'),
                   (205,42,'TEST-5','Fifth','2026-09-01',FALSE,'',0,'{}','{}','{}'),
                   (206,42,'TEST-6','Sixth','2026-09-01',TRUE,'101',300,'{"10":25}','{}','{}');
            INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,Status,SalaryStructureId,AnnualCtc,SalaryJson)
            VALUES (206,42,'Hire','2026-09-01','Active','101',300,'{"10":25}');
            """);
        static IFormFile Assign(params int[] employees) => Workbook([["Employee ID","Employee Code","Salary Template Id","Salary Effective From","Assign Salary Template Only"],
            .. employees.Select(id => new[] { id.ToString(), "TEST-" + (id - 200), "102", "2026-10-01", "TRUE" })]);
        async Task<string> MasterAmountsAsync() => JsonSerializer.Serialize(await db.QueryAsync(
            "SELECT Id,AnnualCtc,CAST(SalaryJson AS CHAR) SalaryJson FROM employees ORDER BY Id"))
            + JsonSerializer.Serialize(await db.QueryAsync("SELECT EmployeeId,ComponentId,Amount FROM employeesalarycomponents ORDER BY EmployeeId,ComponentId"));
        async Task<(string Template, decimal Ctc, string Json)> At(int employee, string date) =>
            await db.QuerySingleAsync<(string,decimal,string)>("""
                SELECT SalaryStructureId,AnnualCtc,CAST(SalaryJson AS CHAR) FROM employee_it0008_basic_pay
                WHERE EmployeeId=@employee AND EffectiveFrom<=@date AND (EffectiveTo IS NULL OR EffectiveTo>=@date)
                ORDER BY EffectiveFrom DESC,Id DESC LIMIT 1
                """, new { employee, date });
        static string Confirm(EmployeeImportPreflightResult preview) => JsonSerializer.Serialize(preview.Rows.Select(row => new
        {
            row.RowNumber, row.Sheet, action = row.BlockingReasons.Count > 0 ? "skip" : "update", employeeId = row.MatchedEmployeeId,
            fieldChoices = row.Changes.ToDictionary(c => c.Field, c => row.MatchedEmployeeId == 206 ? "keepExisting" : "useImported")
        }));
        var source = Assign(201,202,203,204,205,206);
        var oldSeptember = await At(201, "2026-09-30");
        var oldNovember = await At(201, "2026-11-01");
        var oldMasterAmounts = await MasterAmountsAsync();
        var pre = await repository.PreflightImportCsvAsync(42, source, "update");
        var web = JsonSerializer.SerializeToElement(pre, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(web.GetProperty("supportsDatedSalaryInputs").GetBoolean());
        Assert.True(pre.CanImport);
        Assert.True(pre.RequiresConfirmation);
        Assert.Equal(new[] { 201, 203, 206 }, pre.Rows.Where(r => r.Changes.Any(c => c.Field == "salaryTemplateId") && r.BlockingReasons.Count == 0)
            .Select(r => r.MatchedEmployeeId.GetValueOrDefault()));
        Assert.Empty(pre.Rows[1].Changes);
        Assert.Contains(pre.Rows[3].BlockingReasons, reason => reason.Contains("joining date", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pre.Rows[4].BlockingReasons, reason => reason.Contains("active", StringComparison.OrdinalIgnoreCase));
        var applied = await repository.ImportCsvAsync(42, source, "update", pre.ReviewToken, Confirm(pre));
        Assert.True(applied.Errors.Count == 0, string.Join(" | ", applied.Errors));
        Assert.Equal(2, applied.Updated);
        Assert.Equal(0, applied.Inserted);
        Assert.Equal(oldMasterAmounts, await MasterAmountsAsync());
        Assert.Equal(oldSeptember, await At(201, "2026-09-30"));
        Assert.Equal(oldNovember, await At(201, "2026-11-01"));
        Assert.Equal(("102",oldSeptember.Ctc,oldSeptember.Json), await At(201, "2026-10-01"));
        Assert.Equal(("102",0m,"{}"), await At(203, "2026-10-01"));
        Assert.Equal("101", (await At(206, "2026-10-01")).Template);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay WHERE EmployeeId IN (204,205)"));
        var versionCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay");
        var again = Assign(201,202,203);
        var unchanged = await repository.PreflightImportCsvAsync(42, again, "update");
        Assert.All(unchanged.Rows, row => { Assert.Empty(row.Changes); Assert.Empty(row.BlockingReasons); });
        var retry = await repository.ImportCsvAsync(42, again, "update", unchanged.ReviewToken, Confirm(unchanged));
        Assert.Empty(retry.Errors);
        Assert.Equal(0, retry.Updated);
        Assert.Equal(versionCount, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay"));

        var staleFile = Assign(206);
        var stale = await repository.PreflightImportCsvAsync(42, staleFile, "update");
        await db.ExecuteAsync("UPDATE employees SET IsActive=FALSE WHERE Id=206");
        var useImported = JsonSerializer.Serialize(stale.Rows.Select(r => new { r.RowNumber, r.Sheet, action = "update", employeeId = r.MatchedEmployeeId,
            fieldChoices = new Dictionary<string,string> { ["salaryTemplateId"] = "useImported" } }));
        var rejected = await repository.ImportCsvAsync(42, staleFile, "update", stale.ReviewToken, useImported);
        Assert.Contains(rejected.Errors, reason => reason.Contains("eligible", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(versionCount, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_it0008_basic_pay"));

        // A legacy empty version used employee-level amounts. Assigning on its existing
        // date freezes those inputs, while a newly assigned empty employee stays empty.
        await db.ExecuteAsync("""
            INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,DateOfJoining,IsActive,SalaryStructureId,AnnualCtc,SalaryJson,PersonalJson,PaymentJson)
            VALUES(207,42,'TEST-7','Seventh','2026-09-01',TRUE,'101',924,'{"10":77}','{}','{}');
            INSERT INTO employee_it0008_basic_pay(EmployeeId,ClientId,ActionType,EffectiveFrom,Status,SalaryStructureId,AnnualCtc,SalaryJson)
            VALUES(207,42,'Hire','2026-10-01','Active','101',924,'{}');
            UPDATE employees SET SalaryJson='{"10":88}' WHERE Id=203;
            INSERT INTO employeesalarycomponents(EmployeeId,ComponentId,Amount) VALUES(203,'10',88),(207,'10',77);
            """);
        var withFallback = Assign(207);
        var reviewed = await repository.PreflightImportCsvAsync(42, withFallback, "update");
        Assert.Equal("MatchedByEmployeeId", reviewed.Rows.Single().MatchStatus);
        var completed = await repository.ImportCsvAsync(42, withFallback, "update", reviewed.ReviewToken, Confirm(reviewed));
        Assert.Empty(completed.Errors);
        Assert.Equal(1, completed.Updated);
        var previouslyEffectiveInputs = JsonSerializer.Deserialize<Dictionary<string, decimal>>((await At(207, "2026-10-01")).Json);
        Assert.Equal(77m, previouslyEffectiveInputs!["10"]);
        Assert.Equal("{}", (await At(203, "2026-10-01")).Json);
    }

    [SyntheticMySqlFact]
    public Task Bell_tracks_the_requesters_assignment_when_the_page_closes_and_shows_partial_failures() => WithDatabaseAsync(async connection =>
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection }).Build();
        await using var db = new MySqlConnection(connection);
        await db.OpenAsync();
        var repository = await InitializeRepositoryAsync(db, config);
        var reader = new EmployeeRepository(config, null!, null!);
        var owner = new AuthUser { Id = 61, IsActive = true, Permissions = ["employees.manage"] };
        var other = new AuthUser { Id = 62, IsActive = true, Permissions = ["employees.manage"] };
        var file = Workbook(["Employee ID","Employee Code","Salary Template Id","Salary Effective From","Assign Salary Template Only"],
            ["201","TEST-1","102","2026-10-03","TRUE"], ["202","TEST-2","102","2026-10-03","TRUE"]);
        var preview = await repository.PreflightImportCsvAsync(42, file, "update");
        var choices = JsonSerializer.Serialize(preview.Rows.Select(row => new
        {
            row.RowNumber, row.Sheet, action = "update", employeeId = row.MatchedEmployeeId,
            fieldChoices = row.Changes.ToDictionary(change => change.Field, _ => "useImported")
        }));
        // Second row becomes ineligible after review. Hold the client lock so even a fast test database
        // can observe the active notification from a fresh repository, independent of the starting page.
        await db.ExecuteAsync("UPDATE employees SET IsActive=FALSE WHERE Id=202");
        EmployeeImportJobStatus started;
        await using (var hold = await db.BeginTransactionAsync())
        {
            await db.ExecuteScalarAsync<int>("SELECT Id FROM clients WHERE Id=42 FOR UPDATE", transaction: hold);
            started = await repository.StartImportCsvJobAsync(42, file, "update", preview.ReviewToken, choices, requestedByUserId: owner.Id);
            Assert.Equal("Queued", started.State);
            var visible = Assert.Single(await reader.OwnEmployeeImportJobsAsync(owner));
            Assert.Equal(started.JobId, visible.JobId);
            Assert.Equal("salary-template-assignment", visible.Kind);
            Assert.Contains(visible.State, new[] { "Queued", "Processing" });
            Assert.False(await reader.DismissEmployeeImportJobAsync(owner, started.JobId));
            await hold.RollbackAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        EmployeeImportJobStatus job;
        do
        {
            await Task.Delay(50, timeout.Token);
            job = Assert.Single(await reader.OwnEmployeeImportJobsAsync(owner, started.JobId));
        } while (job.State is "Queued" or "Processing");
        Assert.Equal("Failed", job.State);
        Assert.Equal(1, job.Updated);
        Assert.Equal(2, job.CompletedRows);
        Assert.Contains(job.Errors, message => message.StartsWith("TEST-2"));
        Assert.Empty(Assert.Single(await reader.OwnEmployeeImportJobsAsync(owner)).Errors);
        Assert.Empty(await reader.OwnEmployeeImportJobsAsync(other, started.JobId));
        Assert.False(await reader.DismissEmployeeImportJobAsync(other, started.JobId));
        owner.ClientAccessMode = "Selected";
        owner.AssignedClientIds = [7];
        Assert.Empty(await reader.OwnEmployeeImportJobsAsync(owner));
        Assert.False(await reader.DismissEmployeeImportJobAsync(owner, started.JobId));
        owner.AssignedClientIds = [7,42];
        Assert.True(await reader.DismissEmployeeImportJobAsync(owner, started.JobId));
        Assert.Empty(await reader.OwnEmployeeImportJobsAsync(owner));
        Assert.Equal(started.JobId, Assert.Single(await reader.OwnEmployeeImportJobsAsync(owner, started.JobId)).JobId);
        // If a worker process stops while queued/processing, its named lock is released.
        // The retained checkpoint can still be opened and dismissed; no work is replayed.
        await db.ExecuteAsync("UPDATE employee_import_jobs SET State='Processing',DismissedAtUtc=NULL WHERE JobId=@id", new { id = started.JobId.ToString() });
        var interrupted = Assert.Single(await reader.OwnEmployeeImportJobsAsync(owner, started.JobId));
        Assert.Equal("Failed", interrupted.State);
        Assert.Equal(1, interrupted.Updated);
        Assert.Contains("interrupted", Assert.Single(interrupted.Errors));
        Assert.True(await reader.DismissEmployeeImportJobAsync(owner, started.JobId));

        var update = Workbook(["Employee Code","Salary Template Id","Salary Effective From","Basic [SALARY_INPUT:10]"],
            ["TEST-1","102","2026-10-03","23000"]);
        var reviewed = await repository.PreflightImportCsvAsync(42, update, "update");
        var approve = JsonSerializer.Serialize(reviewed.Rows.Select(row => new
        {
            row.RowNumber, row.Sheet, action = "update", employeeId = row.MatchedEmployeeId,
            fieldChoices = row.Changes.ToDictionary(change => change.Field, _ => "useImported")
        }));
        var ordinary = await repository.StartImportCsvJobAsync(42, update, "update", reviewed.ReviewToken, approve, requestedByUserId: owner.Id);
        EmployeeImportJobStatus notification;
        do
        {
            await Task.Delay(50, timeout.Token);
            notification = Assert.Single(await reader.OwnEmployeeImportJobsAsync(owner));
        } while (notification.State is "Queued" or "Processing");
        Assert.Equal(ordinary.JobId, notification.JobId);
        Assert.Equal("employee-import", notification.Kind);
        Assert.Equal("update", notification.Operation);
        Assert.Equal("Completed", notification.State);
        Assert.Equal(1, notification.Updated);
    });

    private static IFormFile Workbook(params string[][] rows)
    {
        var build = typeof(EmployeeRepository).GetMethod("BuildXlsx", BindingFlags.Static | BindingFlags.NonPublic)!;
        var sheets = new (string Name, IEnumerable<string[]> Rows)[] { ("Employees", rows) };
        var bytes = (byte[])build.Invoke(null, [sheets])!;
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "synthetic-employees.xlsx");
    }
}

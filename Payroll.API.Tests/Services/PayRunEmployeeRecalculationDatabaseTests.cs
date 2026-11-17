using System.Text.Json;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed class PayRunEmployeeRecalculationDatabaseTests : IAsyncLifetime
{
    private const int Client = 97972, One = 9797201, Two = 9797202, Template = 9797200;
    private readonly ReviewedPayrollFixture? fixture = Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is { } c ? new(c) : null;
    private ReviewedPayrollFixture F => fixture ?? throw new InvalidOperationException("Owned loopback fixture required.");
    private bool seeded;
    private sealed class IsolatedFactAttribute : FactAttribute { public IsolatedFactAttribute() { if (Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null) Skip = "Explicit owned sentinel fixture required."; } }

    public async Task InitializeAsync()
    {
        if (fixture is null) return;
        await using var db = await F.OpenAsync();
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT (SELECT COUNT(*) FROM clients WHERE Id=97972)+(SELECT COUNT(*) FROM employees WHERE ClientId=97972)+(SELECT COUNT(*) FROM salarycomponents WHERE Id=9797201)+(SELECT COUNT(*) FROM salarystructures WHERE Id=9797200)+(SELECT COUNT(*) FROM payruns WHERE ClientId=97972)"));
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='workflowinstances'") > 0)
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM workflowinstances WHERE WorkflowId=97972"));
        F.Config["Payroll:PayableOnlyAttendanceEnabled"] = "true";
        seeded = true;
        var evidence = SourcePayableAttendance.Encode(new(1, Client, One, "2026-10", new string('a', 32), "synthetic-recalculation-one", new string('b', 32), new string('c', 64), 30, 20));
        await db.ExecuteAsync(@"INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97972,'RECALC-ONLY','Synthetic single employee correction',TRUE);
INSERT INTO worklocations(Id,ClientId,Name,City,State) VALUES(97972,97972,'Synthetic office','Synthetic city','Synthetic state');
INSERT INTO employees(Id,ClientId,EmployeeCode,FirstName,LastName,DateOfJoining,AnnualCtc,SalaryStructureId,SalaryJson,PersonalJson,PaymentJson,WorkLocationId,PortalAccess,IsActive) VALUES
(9797201,97972,'RECALC-ONE','Synthetic','One','2026-01-01',240000,'9797200',JSON_OBJECT('9797201',10000),JSON_OBJECT(),JSON_OBJECT(),97972,FALSE,TRUE),
(9797202,97972,'RECALC-TWO','Synthetic','Two','2026-01-01',240000,'9797200',JSON_OBJECT('9797201',20000),JSON_OBJECT(),JSON_OBJECT(),97972,FALSE,TRUE);
INSERT INTO employeesalarycomponents(EmployeeId,ComponentId,Amount) VALUES(9797201,'9797201',10000),(9797202,'9797201',20000);
INSERT INTO salarycomponents(Id,Code,Name,Category,ComponentRole,StatutoryType,CalculationType,ValueText,Formula,BaseComponent,ProRata,Taxable,Priority,PayType,Active)
VALUES(9797201,'RECALC_BASIC','Basic','Earning','Other Earning','None','Manual / Variable','0','','',TRUE,TRUE,1,'Monthly',TRUE);
INSERT INTO salarystructures(Id,ClientId,ClientRef,Name,AnnualCtc,Active) VALUES(9797200,97972,'97972','Synthetic correction template',240000,TRUE);
INSERT INTO salarystructurelines(StructureId,ComponentId,ValueText,CalculationType,Formula,BaseComponent,ProRataOverride,RoundingMode,SortOrder) VALUES(9797200,9797201,'0','Manual / Variable','','','true','',1);
INSERT INTO attendance_groups(id,client_id,name,policy_batch_id,attendance_mode,work_week,attendance_cycle_start_day,attendance_cycle_end_day,payroll_report_generation_day,is_active)
VALUES(97972,97972,'Synthetic Monthly','synthetic-recalc-97972','Monthly','Sunday off',1,31,28,TRUE);
INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(97972,9797201),(97972,9797202);
INSERT INTO tax_client_settings(client_id,financial_year,enabled,project_monthly_tds,active) VALUES(97972,'2026-27',FALSE,FALSE,TRUE);
INSERT INTO employee_monthly_attendance(client_id,employee_id,attendance_month,working_days,present_days,payable_days,lop_days,source_type,remarks) VALUES
(97972,9797201,'2026-10',30,NULL,20,10,'ExcelPayableOnly',@Evidence),(97972,9797202,'2026-10',30,30,30,0,'Monthly','Independent synthetic attendance');", new { Evidence = evidence });
    }

    public async Task DisposeAsync()
    {
        if (!seeded) return;
        await using var db = await F.OpenAsync();
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='workflowinstances'") > 0)
            await db.ExecuteAsync("DELETE FROM workflowinstances WHERE WorkflowId=97972 AND ResourceType='PayRun'");
        await db.ExecuteAsync(@"DROP TRIGGER IF EXISTS codex_recalc_97972_fail;
DELETE l FROM payrunemployeelines l JOIN payruns r ON r.Id=l.PayRunId WHERE r.ClientId=97972;
DELETE l FROM payroll_calculation_traces l JOIN payruns r ON r.Id=l.PayRunId WHERE r.ClientId=97972;
DELETE l FROM payroll_reconciliation_results l JOIN payruns r ON r.Id=l.PayRunId WHERE r.ClientId=97972;
DELETE l FROM payroll_validation_issues l JOIN payruns r ON r.Id=l.PayRunId WHERE r.ClientId=97972;
DELETE l FROM payrun_step_logs l JOIN payruns r ON r.Id=l.PayRunId WHERE r.ClientId=97972;
DELETE FROM tax_computation_snapshots WHERE client_id=97972; DELETE FROM payrunemployees WHERE ClientId=97972; DELETE FROM payruns WHERE ClientId=97972;
DELETE FROM payrolladjustments WHERE ClientId=97972; DELETE FROM tax_client_settings WHERE client_id=97972;
DELETE FROM attendance_group_employees WHERE employee_id IN(9797201,9797202);DELETE FROM attendance_groups WHERE client_id=97972;
DELETE FROM employee_monthly_attendance WHERE client_id=97972; DELETE FROM employeesalarycomponents WHERE EmployeeId IN(9797201,9797202);
DELETE FROM employees WHERE ClientId=97972; DELETE FROM worklocations WHERE ClientId=97972; DELETE FROM clients WHERE Id=97972;
DELETE FROM salarystructurelines WHERE StructureId=9797200; DELETE FROM salarystructures WHERE Id=9797200; DELETE FROM salarycomponents WHERE Id=9797201;");
    }

    private async Task<string> SavedAsync(int runId, int? employeeId = null)
    {
        await using var db = await F.OpenAsync();
        var key = new { RunId = runId, EmployeeId = employeeId };
        return JsonSerializer.Serialize(new {
            Rows = await db.QueryAsync("SELECT * FROM payrunemployees WHERE PayRunId=@RunId AND (@EmployeeId IS NULL OR EmployeeId=@EmployeeId) ORDER BY Id", key),
            Lines = await db.QueryAsync("SELECT * FROM payrunemployeelines WHERE PayRunId=@RunId AND (@EmployeeId IS NULL OR EmployeeId=@EmployeeId) ORDER BY Id", key),
            Traces = await db.QueryAsync("SELECT * FROM payroll_calculation_traces WHERE PayRunId=@RunId AND (@EmployeeId IS NULL OR EmployeeId=@EmployeeId) ORDER BY Id", key),
            Tax = await db.QueryAsync("SELECT * FROM tax_computation_snapshots WHERE pay_run_id=@RunId AND (@EmployeeId IS NULL OR employee_id=@EmployeeId) ORDER BY id", key)
        });
    }

    [IsolatedFact]
    public async Task Native_target_only_recalculation_preserves_other_employee_unknown_presence_and_rejects_stale_paid_policy_and_ambiguous_tax()
    {
        var run = await F.Runs.CreateAsync(new() { ClientId = Client, PayPeriod = "2026-10", PayDate = new(2026, 11, 1), TotalWorkingDays = 30, AttendanceGroupId = Client, IncludedEmployeeIds = [One, Two] }, "Synthetic recalculation owner");
        Assert.NotNull(run); Assert.Equal("Draft", run!.Status); Assert.Equal(2, run.Employees.Count);
        var original = Assert.Single(run.Employees, row => row.EmployeeId == One); Assert.Null(original.PresentDays);
        var otherBefore = await SavedAsync(run.Id, Two); var allBefore = await SavedAsync(run.Id);
        var preview = await F.Runs.PreviewEmployeeRecalculationAsync(run.Id, One); Assert.NotNull(preview); Assert.True(preview!.CanApply, string.Join(" ", preview.Blockers));
        Assert.Equal(allBefore, await SavedAsync(run.Id));
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync("UPDATE employeesalarycomponents SET Amount=12000 WHERE EmployeeId=9797201 AND ComponentId='9797201'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => F.Runs.RecalculateEmployeeAsync(run.Id, One, new(preview.ExpectedFingerprint, true, "Old preview cannot apply"), "Synthetic owner"));
        Assert.Equal(allBefore, await SavedAsync(run.Id));
        preview = await F.Runs.PreviewEmployeeRecalculationAsync(run.Id, One); Assert.True(preview!.CanApply, string.Join(" ", preview.Blockers));
        Assert.Null(preview.After!.PresentDays); Assert.Equal(8000m, preview.After.Net); Assert.Single(preview.Changes);
        var changed = await F.Runs.RecalculateEmployeeAsync(run.Id, One, new(preview.ExpectedFingerprint, true, "Synthetic monthly salary input corrected"), "Synthetic owner");
        Assert.NotNull(changed); Assert.Equal(run.Id, changed!.Id); Assert.Equal("Draft", changed.Status); Assert.Equal(2, changed.Employees.Count);
        var saved = Assert.Single(changed.Employees, row => row.EmployeeId == One); Assert.Equal(original.Id, saved.Id); Assert.Null(saved.PresentDays); Assert.Equal(8000m, saved.NetPay);
        Assert.Equal(otherBefore, await SavedAsync(run.Id, Two)); Assert.Equal(changed.Employees.Sum(row => row.NetPay), changed.NetPay);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payrun_step_logs WHERE PayRunId=@Id AND EmployeeId=9797201 AND StepName='Reviewed single employee recalculation'", new { Id = run.Id }));
        var archived = await db.ExecuteScalarAsync<string>("SELECT CAST(OldValueJson AS CHAR) FROM payrun_step_logs WHERE PayRunId=@Id AND StepName='Reviewed single employee recalculation'", new { Id = run.Id });
        Assert.NotNull(archived);
        using (var receipt = JsonDocument.Parse(archived))
        {
            Assert.Equal(original.Id, receipt.RootElement.GetProperty("Before").GetProperty("Id").GetInt32());
            Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("Before").GetProperty("PresentDays").ValueKind);
            Assert.Contains(original.EmployeeCode, receipt.RootElement.GetProperty("OldTracesJson").GetString());
        }
        await db.ExecuteAsync("UPDATE payruns SET Status='Approved' WHERE Id=@Id", new { Id = run.Id });
        Assert.False((await F.Runs.PreviewEmployeeRecalculationAsync(run.Id, One))!.CanApply);
        await Assert.ThrowsAsync<InvalidOperationException>(() => F.Runs.RecalculateEmployeeAsync(run.Id, One, new(preview.ExpectedFingerprint, true, "Cannot bypass approval"), "Synthetic owner"));
        await db.ExecuteAsync("UPDATE payruns SET Status='Draft' WHERE Id=@Id;UPDATE payrunemployees SET PaymentStatus='Paid' WHERE PayRunId=@Id AND EmployeeId=9797201", new { Id = run.Id });
        Assert.False((await F.Runs.PreviewEmployeeRecalculationAsync(run.Id, One))!.CanApply);
        await db.ExecuteAsync("UPDATE payrunemployees SET PaymentStatus='Pending',ManualTds=100 WHERE PayRunId=@Id AND EmployeeId=9797201", new { Id = run.Id });
        Assert.Contains((await F.Runs.PreviewEmployeeRecalculationAsync(run.Id, One))!.Blockers, value => value.Contains("positive saved TDS"));
        await db.ExecuteAsync("UPDATE payrunemployees SET ManualTds=0 WHERE PayRunId=@Id AND EmployeeId=9797201", new { Id = run.Id });
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name='workflowinstances'") > 0)
        {
            var pendingId = await db.ExecuteScalarAsync<long>("INSERT INTO workflowinstances(WorkflowId,ResourceType,ResourceId,RequestorUserId,PayloadJson,Status) VALUES(97972,'PayRun',@ResourceId,97972,JSON_OBJECT(),'Pending');SELECT LAST_INSERT_ID();", new { ResourceId = run.Id.ToString() });
            Assert.Contains((await F.Runs.PreviewEmployeeRecalculationAsync(run.Id, One))!.Blockers, value => value.Contains("open approval workflow"));
            await db.ExecuteAsync("DELETE FROM workflowinstances WHERE Id=@Id AND WorkflowId=97972", new { Id = pendingId });
        }
        var detailsBefore = await db.ExecuteScalarAsync<string>("SELECT CAST(DetailsJson AS CHAR) FROM payrunemployees WHERE PayRunId=@Id AND EmployeeId=9797201", new { Id = run.Id });
        await db.ExecuteAsync("UPDATE payrunemployees SET DetailsJson=JSON_OBJECT() WHERE PayRunId=@Id AND EmployeeId=9797201", new { Id = run.Id });
        Assert.Contains((await F.Runs.PreviewEmployeeRecalculationAsync(run.Id, One))!.Blockers, value => value.Contains("details are invalid"));
        await db.ExecuteAsync("UPDATE payrunemployees SET DetailsJson=CAST(@Details AS JSON) WHERE PayRunId=@Id AND EmployeeId=9797201", new { Id = run.Id, Details = detailsBefore });
        await db.ExecuteAsync("DELETE FROM attendance_group_employees WHERE employee_id=9797201");
        Assert.Contains((await F.Runs.PreviewEmployeeRecalculationAsync(run.Id, One))!.Blockers, value => value.Contains("policy"));
        await db.ExecuteAsync("INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(97972,9797201)");
        // Fail after the row/lines/traces/totals replacement and prove transaction rollback.
        await db.ExecuteAsync("UPDATE employeesalarycomponents SET Amount=14000 WHERE EmployeeId=9797201 AND ComponentId='9797201'");
        preview = await F.Runs.PreviewEmployeeRecalculationAsync(run.Id, One); Assert.True(preview!.CanApply, string.Join(" ", preview.Blockers));
        var beforeFailure = await SavedAsync(run.Id); var net = await db.ExecuteScalarAsync<decimal>("SELECT NetPay FROM payruns WHERE Id=@Id", new { Id = run.Id });
        await db.ExecuteAsync("CREATE TRIGGER codex_recalc_97972_fail BEFORE INSERT ON payrun_step_logs FOR EACH ROW BEGIN IF NEW.StepName='Reviewed single employee recalculation' AND NEW.PerformedBy='Synthetic injected rollback' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Synthetic final receipt failure'; END IF; END");
        await Assert.ThrowsAnyAsync<Exception>(() => F.Runs.RecalculateEmployeeAsync(run.Id, One, new(preview.ExpectedFingerprint, true, "Synthetic rollback verification"), "Synthetic injected rollback"));
        Assert.Equal(beforeFailure, await SavedAsync(run.Id)); Assert.Equal(net, await db.ExecuteScalarAsync<decimal>("SELECT NetPay FROM payruns WHERE Id=@Id", new { Id = run.Id }));
        await db.ExecuteAsync("DROP TRIGGER codex_recalc_97972_fail");
    }
}

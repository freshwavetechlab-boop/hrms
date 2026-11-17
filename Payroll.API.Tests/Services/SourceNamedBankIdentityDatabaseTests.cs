using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed partial class SourceNamedInputDatabaseTests
{
    private const string FirstBank = "00123456789097746", SecondBank = "00123456789097747";

    private async Task<ExcelPayrollSetupRequest> BankOnlyRequestAsync()
    {
        var request = await ConfigureAsync();
        await using var db = await F.OpenAsync();
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97746"), Json)!;
        var source = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97746"), Json)!;
        batch.Rows[0].Information.Single(i => i.Label == "Aadhaar").Value = "";
        source.Rows.Single(r => r.SourceRow == 2).Cells[15]!.Value = "";
        source.Rows.Single(r => r.SourceRow == 2).Cells[6]!.Value = "12";
        source.Rows.Single(r => r.SourceRow == 2).Cells[9]!.Value = "3000";
        source.Rows.Single(r => r.SourceRow == 2).Cells[12]!.Value = "39000";
        source.Rows.Single(r => r.SourceRow == 2).Cells[14]!.Value = "38900";
        batch.Rows[0].Information.Single(i => i.Label == "Overtime hours").Value = "12";
        batch.Rows[0].Earnings.Single(i => i.Label == "Overtime").Amount = 3000;
        batch.Rows[0].DeclaredGross = 39000; batch.Rows[0].NetPay = 38900;
        source.Columns.Add(new() { ColumnIndex = 16, SourceHeader = "Account No", Label = "Account No", Kind = "info" }); source.ColumnCount++;
        source.Rows.Single(r => r.SourceRow == 1).Cells.Add(new() { Value = "Account No", DataType = "Text" });
        foreach (var row in batch.Rows)
        {
            var account = row.SourceRow == 2 ? FirstBank : SecondBank;
            row.Information.Add(new() { Label = "Account No", Value = account });
            source.Rows.Single(r => r.SourceRow == row.SourceRow).Cells.Add(new() { Value = account, DataType = "Text" });
        }
        await db.ExecuteAsync(@"UPDATE employeepersonaldetails SET AadhaarNumber='' WHERE EmployeeId=9774601;
INSERT INTO employeepaymentdetails(EmployeeId,BankAccountNo) VALUES(9774601,@First),(9774602,@Second);
UPDATE excel_payslip_batches SET batch_json=CAST(@Batch AS JSON) WHERE client_id=97746;
UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=97746",
            new { First = FirstBank, Second = SecondBank, Batch = JsonSerializer.Serialize(batch, Json), Source = JsonSerializer.Serialize(source, Json) });
        request.SourceColumnRoles[16] = "info";
        var sourceHash = await RefreshBankSourceHashAsync(request);
        request.SourceIdentityReview = new() { Version = 1, Reviewed = true, SourceHash = sourceHash,
            Reason = "Explicitly reviewed the unchanged exact code/name and independent globally unique supplied account for this existing employee.",
            Bindings = [new("first-2", One, 16, "First Person", "First Person", false)] };
        return request;
    }

    private async Task<string> RefreshBankSourceHashAsync(ExcelPayrollSetupRequest request)
    {
        await using var db = await F.OpenAsync();
        var batch = JsonSerializer.Deserialize<ExcelPayslipBatch>(await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97746"), Json)!;
        var source = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=97746"), Json)!;
        var hash = ExcelPayslipRepository.EmploymentSourceHash(batch, source);
        request.AttendanceReview!.SourceHash = hash;
        if (request.SourceIdentityReview is not null) request.SourceIdentityReview.SourceHash = hash;
        return hash;
    }

    private async Task<ExcelPayrollSetupApplyRequest> ApplyBankOnlyAsync()
    {
        var request = await BankOnlyRequestAsync();
        var preview = await Repo().PreviewAsync(Client, request);
        Assert.True(preview.CanApply, string.Join(";", preview.Issues.Select(i => i.Message)));
        var command = new ExcelPayrollSetupApplyRequest { Request = request, RequestId = Guid.NewGuid().ToString("N"), PlanHash = preview.PlanHash };
        await Repo().ApplyAsync(Client, command, "Synthetic bank-only reviewer", Actor.Id);
        return command;
    }

    [IsolatedFact]
    public async Task Explicit_reviewed_bank_only_source_can_create_first_named_assignment_and_native_draft_without_government_or_profile_links()
    {
        var command = await ApplyBankOnlyAsync();
        Assert.True((await Repo(false).ApplyAsync(Client, command, "Synthetic bank-only reviewer", Actor.Id)).Replayed);
        var config = Config(false); var runs = new PayRunRepository(config, new(config));
        var salary = await runs.SimulateEmployeeAsync(new() { Mode = "Employee", ClientId = Client, EmployeeId = One, PayPeriod = "2026-10" }, default);
        Assert.True(salary.Complete, string.Join(";", salary.Issues.Select(i => i.Message))); Assert.Equal(38900m, salary.Net);
        Assert.Equal(446400m, Assert.Single(salary.LocationAllocations!.Parts).AnnualCtc);
        Assert.Equal(12m, Assert.Single(salary.LocationAllocations.Parts).NamedInputs!.Values["OT_HOURS"]);
        await using var db = await F.OpenAsync();
        Assert.Equal("", await db.ExecuteScalarAsync<string>("SELECT AadhaarNumber FROM employeepersonaldetails WHERE EmployeeId=9774601"));
        Assert.Equal(FirstBank, await db.ExecuteScalarAsync<string>("SELECT BankAccountNo FROM employeepaymentdetails WHERE EmployeeId=9774601"));
        Assert.Null(JsonNode.Parse(await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=97746"))!["profileLinks"]);
        var policy = await db.ExecuteScalarAsync<int>("SELECT id FROM attendance_groups WHERE client_id=97746 LIMIT 1");
        var draft = await runs.CreateAsync(new() { ClientId = Client, PayPeriod = "2026-10", PayDate = new(2026, 11, 1),
            TotalWorkingDays = 31, IncludedEmployeeIds = [One, Two], AttendanceGroupId = policy }, "Synthetic first reviewed bank payroll");
        Assert.Equal(38900m, await db.ExecuteScalarAsync<decimal>("SELECT NetPay FROM payrunemployees WHERE PayRunId=@Id AND EmployeeId=9774601", new { draft.Id }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM salarystructures WHERE ClientId=97746"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97746 AND ModuleCode LIKE 'excel_location_correction:%'"));
    }

    [IsolatedFact]
    public async Task Bank_only_apply_rechecks_global_ownership_after_preview_before_persisting_assignments()
    {
        var request = await BankOnlyRequestAsync(); var preview = await Repo().PreviewAsync(Client, request);
        Assert.True(preview.CanApply, string.Join(";", preview.Issues.Select(i => i.Message)));
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync("INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97747,'BANK-FOREIGN','Synthetic foreign bank',TRUE);UPDATE employees SET ClientId=97747 WHERE Id=9774602;UPDATE employeepaymentdetails SET BankAccountNo=@FirstBank WHERE EmployeeId=9774602", new { FirstBank });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Repo().ApplyAsync(Client,
            new() { Request = request, PlanHash = preview.PlanHash, RequestId = Guid.NewGuid().ToString("N") }, "Synthetic bank-only reviewer", Actor.Id));
        Assert.Contains("not globally unique", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=97746"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97746 AND ModuleCode LIKE 'named-input-a:%'"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_monthly_attendance WHERE client_id=97746"));
    }

    [IsolatedTheory]
    [InlineData("no-review", "source-month employment proof")]
    [InlineData("wrong-bank", "shared, unmatched or owned by another employee")]
    [InlineData("shared-bank", "shared, unmatched or owned by another employee")]
    [InlineData("foreign-bank", "not globally unique")]
    [InlineData("stale-review", "one exact existing client employee code")]
    [InlineData("bank-formula", "exact independent literal")]
    [InlineData("bank-exponent", "exact independent literal")]
    [InlineData("government-conflict", "conflict")]
    [InlineData("name-change", "names")]
    public async Task Bank_only_preview_cannot_waive_identity_source_or_global_bank_conflicts(string mutation, string reason)
    {
        var request = await BankOnlyRequestAsync(); await using var db = await F.OpenAsync();
        if (mutation == "no-review") request.SourceIdentityReview = null;
        if (mutation == "wrong-bank") await db.ExecuteAsync("UPDATE employeepaymentdetails SET BankAccountNo='00123456789099999' WHERE EmployeeId=9774601");
        if (mutation == "shared-bank") await db.ExecuteAsync("UPDATE employeepaymentdetails SET BankAccountNo=@FirstBank WHERE EmployeeId=9774602", new { FirstBank });
        if (mutation == "foreign-bank") await db.ExecuteAsync("INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97747,'BANK-FOREIGN','Synthetic foreign bank',TRUE);UPDATE employees SET ClientId=97747 WHERE Id=9774602;UPDATE employeepaymentdetails SET BankAccountNo=@FirstBank WHERE EmployeeId=9774602", new { FirstBank });
        if (mutation == "stale-review") request.SourceIdentityReview!.SourceHash = new string('f', 64);
        if (mutation == "bank-formula") await db.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.rows[1].cells[16].formulaText','1') WHERE client_id=97746");
        if (mutation == "bank-exponent") await db.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.rows[1].cells[16].dataType','Number','$.rows[1].cells[16].value','1.2345678901234E15') WHERE client_id=97746");
        if (mutation == "government-conflict") await db.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.rows[1].cells[15].value','345678901234') WHERE client_id=97746");
        if (mutation == "name-change") request.SourceIdentityReview!.Bindings = [new("first-2", One, 16, "First Person", "Different Person", true)];
        if (mutation is "bank-formula" or "bank-exponent" or "government-conflict") await RefreshBankSourceHashAsync(request);
        try
        {
            var preview = await Repo().PreviewAsync(Client, request); Assert.False(preview.CanApply);
            Assert.Contains(preview.Issues, issue => issue.Severity == "Error" && issue.Message.Contains(reason, StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidOperationException error) { Assert.Contains(reason, error.Message, StringComparison.OrdinalIgnoreCase); }
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_payroll_location_assignments WHERE client_id=97746"));
    }

    [IsolatedTheory]
    [InlineData("current-bank", "shared, unmatched or owned by another employee")]
    [InlineData("foreign-bank", "not globally unique")]
    [InlineData("receipt-review", "exact Applied receipt")]
    public async Task Signed_bank_identity_is_revalidated_by_the_ordinary_reader(string mutation, string reason)
    {
        await ApplyBankOnlyAsync(); await using var db = await F.OpenAsync();
        if (mutation == "current-bank") await db.ExecuteAsync("UPDATE employeepaymentdetails SET BankAccountNo='00123456789099999' WHERE EmployeeId=9774601");
        if (mutation == "foreign-bank") await db.ExecuteAsync("INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97747,'BANK-FOREIGN','Synthetic foreign bank',TRUE);UPDATE employees SET ClientId=97747 WHERE Id=9774602;UPDATE employeepaymentdetails SET BankAccountNo=@FirstBank WHERE EmployeeId=9774602", new { FirstBank });
        if (mutation == "receipt-review") await db.ExecuteAsync("UPDATE excel_payroll_setup_imports SET request_json=JSON_SET(request_json,'$.sourceIdentityReview.bindings[0].employeeId',9774602) WHERE client_id=97746");
        var config = Config(false); var runs = new PayRunRepository(config, new(config));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runs.SimulateEmployeeAsync(new() { Mode = "Employee", ClientId = Client, EmployeeId = One, PayPeriod = "2026-10" }, default));
        Assert.Contains(reason, failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("EssPayslipFlow")]
public sealed class ExcelPayslipBatchUpdateDatabaseTests
{
    private sealed class IsolatedFactAttribute : FactAttribute
    {
        public IsolatedFactAttribute() { if (Environment.GetEnvironmentVariable("FREVO_EXCEL_BATCH_UPDATE_TEST_DB") is null) Skip = "Explicit isolated batch-update database required."; }
    }
    private sealed class IsolatedTheoryAttribute : TheoryAttribute
    {
        public IsolatedTheoryAttribute() { if (Environment.GetEnvironmentVariable("FREVO_EXCEL_BATCH_UPDATE_TEST_DB") is null) Skip = "Explicit isolated batch-update database required."; }
    }
    private static AuthUser Actor(int id = 9907) => new() { Id = id, IsActive = true, Email = "update-test@example.invalid", DisplayName = "Synthetic update reviewer", ClientAccessMode = "All", Roles = ["admin"], Permissions = ["employees.manage", "reports.payroll.view", "payroll.run"] };
    private static async Task<ExcelPayslipBatchUpdateFixture> FixtureAsync(bool second = false)
    {
        var fixture = new ExcelPayslipBatchUpdateFixture(Environment.GetEnvironmentVariable("FREVO_EXCEL_BATCH_UPDATE_TEST_DB")!);
        await fixture.ResetAsync(); if (second) await fixture.AddSecondEmployeeAsync(); return fixture;
    }
    private static ExcelPayslipUpdatePreviewRequest Input(params (string Code, Dictionary<string, string> Values)[] rows) => new() { Month = "2026-10", SourceFileName = "Synthetic reviewed updates.xlsx", Rows = rows.Select(row => new ExcelPayslipUpdateRowInput { EmployeeCode = row.Code, Values = row.Values }).ToList() };
    private static ExcelPayslipUpdateApplyRequest Apply(ExcelPayslipUpdatePreview review, params string[] fields) => new() { Token = review.Token, RequestId = Guid.NewGuid().ToString("N"), Acknowledged = true, SelectedChangeIds = review.Changes.Where(c => fields.Length == 0 || fields.Contains(c.FieldId)).Where(c => c.CanApply && !c.Computed).Select(c => c.Id).ToList() };
    private static ExcelPayslipBatch Batch(JsonNode node) => node.Deserialize<ExcelPayslipBatch>(ExcelPayslipRepository.JsonOptions)!;
    private static string Financial(ExcelPayslipRow row) => JsonSerializer.Serialize(new { row.Earnings, row.Deductions, row.EmployerContributions, row.NetPay, row.DeclaredGross, row.DeclaredDeductions }, ExcelPayslipRepository.JsonOptions);
    private static void SameDocuments((JsonNode Batch, JsonNode Source, string State) before, (JsonNode Batch, JsonNode Source, string State) after)
    {
        Assert.True(JsonNode.DeepEquals(before.Batch, after.Batch), "Saved batch changed unexpectedly.");
        Assert.True(JsonNode.DeepEquals(before.Source, after.Source), "Saved formula source changed unexpectedly.");
        Assert.Equal(before.State, after.State);
    }
    private static async Task<ExcelPayslipUpdatePreview> ReviewUanAsync(ExcelPayslipBatchUpdateFixture f, string value = "001234567891") => await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Input(("PROFILE-107", new() { ["source:3"] = value })), Actor());
    private static async Task<(JsonNode Document, string Stored)> ReceiptAsync(ExcelPayslipBatchUpdateFixture f, string requestId)
    {
        await using var db = await f.OpenAsync();
        var stored = await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=907 AND ModuleCode=@Code", new { Code = "xp_update_receipt:907:" + requestId }) ?? throw new InvalidOperationException("Applied review receipt missing.");
        var envelope = JsonNode.Parse(stored)!; Assert.Null(envelope["expiresAtUtc"]);
        Assert.DoesNotContain("oldValue", stored); Assert.DoesNotContain("newValue", stored);
        var protector = new PortableIntegrationCredentialProtector(f.Config, new EphemeralDataProtectionProvider(), NullLogger<PortableIntegrationCredentialProtector>.Instance);
        var plaintext = protector.UnprotectExcelPayslipUpdate(envelope["payload"]!.GetValue<string>());
        Assert.NotNull(plaintext); return (JsonNode.Parse(plaintext!)!, stored);
    }
    private static async Task AssertNoNewEntitiesOrMailAsync(ExcelPayslipBatchUpdateFixture f, int employees = 1)
    {
        await using var db = await f.OpenAsync();
        Assert.Equal(employees, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=907"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM excel_payslip_batches WHERE client_id=907"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM excel_payslip_calculation_sources WHERE client_id=907"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM authusers WHERE ClientId=907"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_queue"));
        Assert.Equal(f.OriginalSendStateJson, (await f.DocumentsAsync()).State);
    }

    [IsolatedFact]
    public async Task Reviewed_subset_updates_existing_master_and_same_batch_without_changing_salary_formulas_or_mail_history()
    {
        var f = await FixtureAsync(); var before = await f.DocumentsAsync(); var oldFinancial = Financial(Batch(before.Batch).Rows.Single());
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(before.Source.Deserialize<ExcelPayslipCalculationSource>(ExcelPayslipRepository.JsonOptions)!, Batch(before.Batch)));
        var catalog = await f.Repository.GetBulkUpdateCatalogAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Actor());
        Assert.Empty(catalog.Blockers); Assert.Single(catalog.Rows); Assert.Contains(catalog.Fields, x => x.Id == "source:3" && x.Editable);
        Assert.Contains(catalog.Fields, x => x.Id == "source:4" && !x.Editable); Assert.Contains(catalog.Fields, x => x.Id == "source:5" && !x.Editable);
        var emailField = Assert.Single(catalog.Fields, x => x.MasterField == "email" && x.Editable);
        var initialReview = await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Input(("PROFILE-107", new() { ["source:1"] = "Reviewed Employee", ["source:3"] = "001234567891", [emailField.Id] = "unselected@example.invalid" })), Actor());
        Assert.Empty(initialReview.Blockers); Assert.All(initialReview.Changes.Where(x => !x.Computed), change => Assert.True(change.CanApply, change.Reason));
        // The UI re-previews the chosen input subset so its formula effects are
        // also exactly reviewed. The excluded email must remain untouched.
        var review = await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Input(("PROFILE-107", new() { ["source:1"] = "Reviewed Employee", ["source:3"] = "001234567891" })), Actor());
        var request = Apply(review, "source:1", "source:3"); Assert.Equal(2, request.SelectedChangeIds.Count);
        var result = await f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, request, Actor());
        Assert.Equal(2, result.AppliedChanges); Assert.Equal(1, result.UpdatedEmployees); Assert.False(result.Replayed); Assert.Equal(ExcelPayslipBatchUpdateFixture.BatchId, result.Batch.Id);
        var after = await f.DocumentsAsync(); var row = Batch(after.Batch).Rows.Single();
        var receipt = (await ReceiptAsync(f, request.RequestId)).Document;
        Assert.Equal("Synthetic reviewed updates.xlsx", receipt["sourceFileName"]!.GetValue<string>());
        Assert.True(DateTime.TryParse(receipt["appliedAtUtc"]!.GetValue<string>(), out _));
        var evidence = receipt["changes"]!.AsArray(); Assert.Equal(2, evidence.Count);
        var uanEvidence = Assert.Single(evidence, change => change!["fieldId"]!.GetValue<string>() == "source:3")!;
        Assert.Equal("", uanEvidence["oldValue"]!.GetValue<string>()); Assert.Equal("001234567891", uanEvidence["newValue"]!.GetValue<string>());
        Assert.Equal("PROFILE-107", uanEvidence["employeeCode"]!.GetValue<string>()); Assert.Equal("Batch + Master", uanEvidence["destination"]!.GetValue<string>());
        var nameEvidence = Assert.Single(evidence, change => change!["fieldId"]!.GetValue<string>() == "source:1")!;
        Assert.Equal("Synthetic Employee", nameEvidence["oldValue"]!.GetValue<string>()); Assert.Equal("Reviewed Employee", nameEvidence["newValue"]!.GetValue<string>());
        Assert.DoesNotContain("unselected@example.invalid", receipt.ToJsonString());
        Assert.Equal("Reviewed Employee", row.EmployeeName); Assert.Equal("source@example.test", row.Email); Assert.Equal(oldFinancial, Financial(row));
        Assert.Equal("001234567891", row.Information.Single(x => x.Label == "UAN").Value);
        Assert.Equal("unrecognized-but-preserved", after.Batch["futureMetadata"]!["keep"]!.GetValue<string>());
        Assert.Equal("unrecognized-but-preserved", after.Source["futureMetadata"]!["keep"]!.GetValue<string>());
        Assert.Equal("001234567891", after.Source["rows"]![0]!["cells"]![3]!["value"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(before.Source["rows"]![0]!["cells"]![4], after.Source["rows"]![0]!["cells"]![4]));
        Assert.True(JsonNode.DeepEquals(before.Source["rows"]![0]!["cells"]![5], after.Source["rows"]![0]!["cells"]![5]));
        await using var db = await f.OpenAsync();
        Assert.Equal("Reviewed Employee", await db.ExecuteScalarAsync<string>("SELECT TRIM(CONCAT(FirstName,' ',LastName)) FROM employees WHERE Id=107 AND ClientId=907"));
        Assert.Equal("001234567891", await db.ExecuteScalarAsync<string>("SELECT UanNumber FROM employeepersonaldetails WHERE EmployeeId=107"));
        Assert.Equal("source@example.test", await db.ExecuteScalarAsync<string>("SELECT WorkEmail FROM employees WHERE Id=107"));
        Assert.Equal(480000m, await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=107"));
        Assert.Equal("unchanged", await db.ExecuteScalarAsync<string>("SELECT JSON_UNQUOTE(JSON_EXTRACT(PersonalJson,'$.keepCustomPersonal')) FROM employees WHERE Id=107"));
        Assert.True(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail WHERE EmployeeId=107 AND (LOWER(FieldName) LIKE '%uan%' OR LOWER(FieldName) LIKE '%name%')") >= 2);
        Assert.Equal("001234567891", await db.ExecuteScalarAsync<string>("SELECT UanNumber FROM employee_it0002_personal_data WHERE EmployeeId=107 AND Status='Active' ORDER BY Id DESC LIMIT 1"));
        var refreshed = (await f.Repository.GetAsync(907, ExcelPayslipBatchUpdateFixture.BatchId))!;
        Assert.True(ExcelPayslipPresentation.Linked(refreshed.Rows.Single()));
        Assert.Equal("001234567891", ExcelPayslipPresentation.Current(refreshed.Rows.Single()).Information.Single(x => x.Label == "UAN").Value);
        await AssertNoNewEntitiesOrMailAsync(f);
    }

    [IsolatedFact]
    public async Task Reviewed_master_contact_bank_and_ctc_changes_preserve_saved_period_salary_and_leading_zero_identifiers()
    {
        var f = await FixtureAsync(); var before = await f.DocumentsAsync(); var originalSalary = Financial(Batch(before.Batch).Rows.Single());
        var catalog = await f.Repository.GetBulkUpdateCatalogAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Actor());
        var values = new Dictionary<string, string>();
        foreach (var (key, value) in new[] { ("email", "reviewed@example.invalid"), ("mobile", "9000000117"), ("bankAccount", "0012345678999"), ("ifsc", "ABCD0000456"), ("annualCtc", "600000") })
        {
            var field = Assert.Single(catalog.Fields, x => x.MasterField == key && x.Editable); values.Add(field.Id, value);
        }
        var review = await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Input(("PROFILE-107", values)), Actor());
        Assert.Empty(review.Blockers); Assert.Equal(5, Apply(review).SelectedChangeIds.Count);
        var result = await f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Apply(review), Actor());
        Assert.Equal(5, result.AppliedChanges); Assert.Equal(1, result.UpdatedEmployees);
        var after = await f.DocumentsAsync(); Assert.Equal(originalSalary, Financial(Batch(after.Batch).Rows.Single()));
        Assert.True(JsonNode.DeepEquals(before.Source["rows"]![0]!["cells"]![4], after.Source["rows"]![0]!["cells"]![4]));
        Assert.True(JsonNode.DeepEquals(before.Source["rows"]![0]!["cells"]![5], after.Source["rows"]![0]!["cells"]![5]));
        await using var db = await f.OpenAsync();
        Assert.Equal("reviewed@example.invalid", await db.ExecuteScalarAsync<string>("SELECT WorkEmail FROM employees WHERE Id=107"));
        Assert.Equal("9000000117", await db.ExecuteScalarAsync<string>("SELECT Mobile FROM employeepersonaldetails WHERE EmployeeId=107"));
        Assert.Equal("0012345678999", await db.ExecuteScalarAsync<string>("SELECT BankAccountNo FROM employeepaymentdetails WHERE EmployeeId=107"));
        Assert.Equal("ABCD0000456", await db.ExecuteScalarAsync<string>("SELECT IfscCode FROM employeepaymentdetails WHERE EmployeeId=107"));
        Assert.Equal(600000m, await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=107"));
        Assert.Equal("unchanged", await db.ExecuteScalarAsync<string>("SELECT JSON_UNQUOTE(JSON_EXTRACT(PaymentJson,'$.keepCustomPayment')) FROM employees WHERE Id=107"));
        Assert.Equal("0012345678999", await db.ExecuteScalarAsync<string>("SELECT BankAccountNo FROM employee_it0009_bank_details WHERE EmployeeId=107 AND Status='Active' ORDER BY Id DESC LIMIT 1"));
        Assert.Equal(600000m, await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employee_it0008_basic_pay WHERE EmployeeId=107 AND Status='Active' ORDER BY Id DESC LIMIT 1"));
        await AssertNoNewEntitiesOrMailAsync(f);
    }

    [IsolatedFact]
    public async Task Explicit_organization_updates_use_existing_client_masters_and_refresh_policy_membership_without_salary_changes()
    {
        var f = await FixtureAsync(); var before = await f.DocumentsAsync(); await using var db = await f.OpenAsync();
        await db.ExecuteAsync("INSERT INTO worklocations(Id,ClientId,Name,City,State,IsActive) VALUES(908,907,'Synthetic reviewed office','Synthetic city','Synthetic state',TRUE) ON DUPLICATE KEY UPDATE ClientId=907,Name='Synthetic reviewed office',IsActive=TRUE;INSERT INTO dropdownmasters(ClientId,Type,Value,IsActive) VALUES(907,'Designation','Senior Analyst',TRUE);INSERT INTO attendance_groups(id,client_id,name,work_location_id,department,designation,is_active) VALUES(9071,907,'Original rule',907,'Operations','Analyst',TRUE),(9072,907,'Reviewed rule',908,'Operations','Senior Analyst',TRUE);INSERT INTO attendance_group_employees(attendance_group_id,employee_id) VALUES(9071,107)");
        var catalog = await f.Repository.GetBulkUpdateCatalogAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Actor());
        var location = Assert.Single(catalog.Fields, x => x.MasterField == "workLocation" && x.Editable);
        var designation = Assert.Single(catalog.Fields, x => x.MasterField == "designation" && x.Editable);
        var review = await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Input(("PROFILE-107", new() { [location.Id] = "Synthetic reviewed office", [designation.Id] = "Senior Analyst" })), Actor());
        Assert.Empty(review.Blockers);
        await f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Apply(review), Actor());
        Assert.Equal(908, await db.ExecuteScalarAsync<int>("SELECT WorkLocationId FROM employees WHERE Id=107"));
        Assert.Equal("Senior Analyst", await db.ExecuteScalarAsync<string>("SELECT Designation FROM employees WHERE Id=107"));
        Assert.Equal(new[] { 9072 }, (await db.QueryAsync<int>("SELECT attendance_group_id FROM attendance_group_employees WHERE employee_id=107 ORDER BY attendance_group_id")).ToArray());
        Assert.Equal(Financial(Batch(before.Batch).Rows.Single()), Financial(Batch((await f.DocumentsAsync()).Batch).Rows.Single()));
        await AssertNoNewEntitiesOrMailAsync(f);
    }

    [IsolatedFact]
    public async Task Reviewed_plain_attendance_input_recalculates_derived_cells_in_place_using_original_formulas()
    {
        var f = await FixtureAsync(); await f.AddAttendanceInputAsync();
        await using (var setup = await f.OpenAsync())
            await setup.ExecuteAsync("UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.rows[0].cells[14].dataType','Text','$.rows[0].cells[4].formulaText','1244.5678*SUM(O2)/30') WHERE client_id=907");
        var before = await f.DocumentsAsync();
        var review = await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Input(("PROFILE-107", new() { ["source:14"] = "15" })), Actor());
        Assert.Empty(review.Blockers); Assert.Contains(review.Changes, c => c.FieldId == "source:14" && c.CanApply && !c.Computed);
        Assert.Contains(review.Changes, c => c.FieldId == "source:4" && c.Computed);
        Assert.Contains(review.Changes, c => c.FieldId == "source:5" && c.Computed);
        var request = Apply(review, "source:14");
        var result = await f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, request, Actor());
        Assert.Equal(ExcelPayslipBatchUpdateFixture.BatchId, result.Batch.Id);
        var after = await f.DocumentsAsync(); var row = Batch(after.Batch).Rows.Single();
        Assert.Equal(622.2839m, row.Earnings.Single().Amount); Assert.Equal(612.2839m, row.NetPay); Assert.Equal(622.2839m, row.DeclaredGross);
        Assert.Equal(10m, row.Deductions.Single().Amount); Assert.Equal(12.3456m, row.EmployerContributions.Single().Amount);
        Assert.Equal("15", row.Information.Single(x => x.Label == "Paid days").Value);
        Assert.Equal("Number", after.Source["rows"]![0]!["cells"]![14]!["dataType"]!.GetValue<string>());
        var evidence = (await ReceiptAsync(f, request.RequestId)).Document["changes"]!.AsArray();
        var netEvidence = Assert.Single(evidence, change => change!["fieldId"]!.GetValue<string>() == "source:5")!;
        Assert.True(netEvidence["computed"]!.GetValue<bool>()); Assert.Equal("1234.5678", netEvidence["oldValue"]!.GetValue<string>()); Assert.Equal("612.2839", netEvidence["newValue"]!.GetValue<string>());
        for (var index = 0; index < 16; index++)
            Assert.Equal(before.Source["rows"]![0]!["cells"]![index]?["formulaText"]?.GetValue<string>(), after.Source["rows"]![0]!["cells"]![index]?["formulaText"]?.GetValue<string>());
        var savedSource = after.Source.Deserialize<ExcelPayslipCalculationSource>(ExcelPayslipRepository.JsonOptions)!;
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(savedSource, Batch(after.Batch)));
        // A fresh evaluator has none of the edit-time fixed inputs. SUM must
        // still see the saved day count as a number rather than ignore text.
        Assert.Equal(622.2839m, ExcelPayslipCalculationService.EvaluateCapturedCell(savedSource, "E2").Value);
        Assert.Equal(612.2839m, ExcelPayslipCalculationService.EvaluateCapturedCell(savedSource, "F2").Value);
        await using var db = await f.OpenAsync(); Assert.Equal(480000m, await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=107"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail WHERE EmployeeId=107"));
        await AssertNoNewEntitiesOrMailAsync(f);
    }

    [IsolatedFact]
    public async Task Reduced_numeric_selection_cannot_apply_formula_outcomes_that_were_not_shown_in_its_preview()
    {
        var f = await FixtureAsync(); await f.AddAttendanceInputAsync(); var before = await f.DocumentsAsync();
        var review = await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Input(("PROFILE-107", new() { ["source:14"] = "15", ["source:10"] = "20" })), Actor());
        Assert.Empty(review.Blockers); Assert.Contains(review.Changes, c => c.FieldId == "source:5" && c.Computed && decimal.Parse(c.NewValue, System.Globalization.CultureInfo.InvariantCulture) == 602.2839m);
        // Omitting the PF edit would make net 612.2839, which was not shown in
        // that review. It requires the UI's fresh subset preview first.
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Apply(review, "source:14"), Actor()));
        SameDocuments(before, await f.DocumentsAsync());
        var subset = await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Input(("PROFILE-107", new() { ["source:14"] = "15" })), Actor());
        await f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Apply(subset), Actor());
        Assert.Equal(612.2839m, Batch((await f.DocumentsAsync()).Batch).Rows.Single().NetPay);
        await AssertNoNewEntitiesOrMailAsync(f);
    }

    [IsolatedFact]
    public async Task Existing_processed_month_blocks_financial_updates_but_keeps_metadata_updates_available()
    {
        var f = await FixtureAsync(); await f.AddAttendanceInputAsync(); await using var db = await f.OpenAsync();
        await db.ExecuteAsync("INSERT INTO payruns(ClientId,PayPeriod,Status) VALUES(907,'2026-10','Approved')");
        var catalog = await f.Repository.GetBulkUpdateCatalogAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Actor());
        Assert.Contains(catalog.Fields, c => c.Id == "source:14" && !c.Editable);
        Assert.Contains(catalog.Fields, c => c.Id == "source:15" && c.MasterField == "annualCtc" && !c.Editable);
        Assert.Contains(catalog.Fields, c => c.MasterField == "annualCtc" && !c.Editable);
        Assert.Contains(catalog.Fields, c => c.Id == "source:3" && c.Editable);
        var before = await f.DocumentsAsync();
        try { var invalid = await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Input(("PROFILE-107", new() { ["source:14"] = "15" })), Actor()); Assert.True(invalid.Blockers.Count > 0 || !invalid.Changes.Any(c => c.CanApply && !c.Computed)); } catch (InvalidOperationException) { }
        SameDocuments(before, await f.DocumentsAsync());
        var review = await ReviewUanAsync(f); Assert.Empty(review.Blockers);
        await f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Apply(review), Actor());
        var after = await f.DocumentsAsync(); Assert.Equal(Financial(Batch(before.Batch).Rows.Single()), Financial(Batch(after.Batch).Rows.Single()));
        Assert.Equal("001234567891", await db.ExecuteScalarAsync<string>("SELECT UanNumber FROM employeepersonaldetails WHERE EmployeeId=107"));
        await AssertNoNewEntitiesOrMailAsync(f);
    }

    [IsolatedFact]
    public async Task Numeric_zero_requires_separate_acknowledgement_and_never_means_a_blank_clear()
    {
        var f = await FixtureAsync(); var before = await f.DocumentsAsync();
        var catalog = await f.Repository.GetBulkUpdateCatalogAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Actor());
        var ctc = Assert.Single(catalog.Fields, c => c.MasterField == "annualCtc" && c.Editable);
        var blanks = await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Input(("PROFILE-107", new() { [ctc.Id] = "  " })), Actor());
        Assert.Empty(blanks.Changes); SameDocuments(before, await f.DocumentsAsync());
        var review = await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Input(("PROFILE-107", new() { [ctc.Id] = "0" })), Actor());
        Assert.Empty(review.Blockers); var request = Apply(review);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, request, Actor()));
        SameDocuments(before, await f.DocumentsAsync()); request.AcknowledgeZeroChanges = true;
        await f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, request, Actor());
        await using var db = await f.OpenAsync(); Assert.Equal(0m, await db.ExecuteScalarAsync<decimal>("SELECT AnnualCtc FROM employees WHERE Id=107"));
        Assert.Equal(Financial(Batch(before.Batch).Rows.Single()), Financial(Batch((await f.DocumentsAsync()).Batch).Rows.Single()));
        await AssertNoNewEntitiesOrMailAsync(f);
    }

    [IsolatedFact]
    public async Task Applied_request_replays_without_second_audit_or_history_and_cannot_change_selection()
    {
        var f = await FixtureAsync(); var review = await ReviewUanAsync(f); var request = Apply(review);
        var first = await f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, request, Actor());
        var before = await f.DocumentsAsync(); var receiptBefore = await ReceiptAsync(f, request.RequestId); await using var db = await f.OpenAsync(); var audit = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail WHERE EmployeeId=107");
        var replay = await f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, request, Actor());
        Assert.True(replay.Replayed); Assert.Equal(first.AppliedChanges, replay.AppliedChanges); SameDocuments(before, await f.DocumentsAsync());
        Assert.Equal(audit, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail WHERE EmployeeId=107"));
        Assert.Equal(receiptBefore.Stored, (await ReceiptAsync(f, request.RequestId)).Stored);
        request.SelectedChangeIds.Add("forged-change");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, request, Actor()));
        await AssertNoNewEntitiesOrMailAsync(f);
    }

    [IsolatedTheory]
    [InlineData("batch")]
    [InlineData("source")]
    [InlineData("master")]
    public async Task Stale_review_rejects_and_preserves_the_newer_saved_state(string changed)
    {
        var f = await FixtureAsync(); var review = await ReviewUanAsync(f); await using var db = await f.OpenAsync();
        await db.ExecuteAsync(changed switch {
            "batch" => "UPDATE excel_payslip_batches SET batch_json=JSON_SET(batch_json,'$.rows[0].employeeName','Later source name') WHERE client_id=907",
            "source" => "UPDATE excel_payslip_calculation_sources SET source_json=JSON_SET(source_json,'$.rows[0].cells[4].formulaText','1000+244.567800') WHERE client_id=907",
            _ => "UPDATE employeepersonaldetails SET UanNumber='009876543210' WHERE EmployeeId=107" });
        var before = await f.DocumentsAsync(); var audits = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail WHERE EmployeeId=107");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Apply(review), Actor()));
        SameDocuments(before, await f.DocumentsAsync()); Assert.Equal(audits, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail WHERE EmployeeId=107"));
        await AssertNoNewEntitiesOrMailAsync(f);
    }

    [IsolatedTheory]
    [InlineData("unknown-code")]
    [InlineData("duplicate-code")]
    [InlineData("wrong-month")]
    [InlineData("formula-output")]
    [InlineData("unknown-field")]
    public async Task Unreviewable_input_is_rejected_without_mutations(string scenario)
    {
        var f = await FixtureAsync(); var before = await f.DocumentsAsync();
        var input = scenario switch {
            "unknown-code" => Input(("PROFILE-999", new() { ["source:3"] = "001234567891" })),
            "duplicate-code" => Input(("PROFILE-107", new() { ["source:3"] = "001234567891" }), ("profile-107", new() { ["source:3"] = "001234567892" })),
            "formula-output" => Input(("PROFILE-107", new() { ["source:4"] = "999999" })),
            "unknown-field" => Input(("PROFILE-107", new() { ["master:employeeId"] = "999" })),
            _ => Input(("PROFILE-107", new() { ["source:3"] = "001234567891" })) };
        if (scenario == "wrong-month") input.Month = "2026-11";
        try { var preview = await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, input, Actor()); Assert.True(preview.Blockers.Count > 0 || !preview.Changes.Any(x => x.CanApply && !x.Computed), "Unsafe input must not produce selectable changes."); }
        catch (InvalidOperationException) { }
        SameDocuments(before, await f.DocumentsAsync()); await AssertNoNewEntitiesOrMailAsync(f);
    }

    [IsolatedTheory]
    [InlineData("ack")]
    [InlineData("forged-selection")]
    [InlineData("other-actor")]
    [InlineData("wrong-client")]
    [InlineData("token-tamper")]
    public async Task Applying_requires_acknowledged_authentic_actor_bound_review(string scenario)
    {
        var f = await FixtureAsync(); var review = await ReviewUanAsync(f); var before = await f.DocumentsAsync(); var request = Apply(review); var actor = Actor(); var client = 907;
        switch (scenario) {
            case "ack": request.Acknowledged = false; break;
            case "forged-selection": request.SelectedChangeIds.Add("injected"); break;
            case "other-actor": actor.Id = 9908; break;
            case "wrong-client": client = 908; break;
            case "token-tamper": request.Token = request.Token[..^4] + "AAAA"; break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => f.Repository.ApplyBulkUpdateAsync(client, ExcelPayslipBatchUpdateFixture.BatchId, request, actor));
        SameDocuments(before, await f.DocumentsAsync()); await AssertNoNewEntitiesOrMailAsync(f);
    }

    [IsolatedTheory]
    [InlineData("Pending")]
    [InlineData("Sending")]
    [InlineData("Unknown")]
    public async Task Active_or_uncertain_existing_mail_blocks_changes_to_its_batch(string status)
    {
        var f = await FixtureAsync(); var review = await ReviewUanAsync(f); await using var db = await f.OpenAsync();
        await db.ExecuteAsync("UPDATE notification_queue SET Status=@Status,SentAt=NULL", new { Status = status });
        var before = await f.DocumentsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Apply(review), Actor()));
        SameDocuments(before, await f.DocumentsAsync()); await AssertNoNewEntitiesOrMailAsync(f);
    }

    [IsolatedFact]
    public async Task Multiple_saved_source_parts_for_one_code_require_individual_review_instead_of_updating_an_arbitrary_part()
    {
        var f = await FixtureAsync(); var docs = await f.DocumentsAsync();
        var second = docs.Batch["rows"]![0]!.DeepClone(); second["id"] = "duplicate-source-part"; second["sourceRow"] = 3;
        docs.Batch["rows"]!.AsArray().Add(second);
        var sourceRow = docs.Source["rows"]![0]!.DeepClone(); sourceRow["sourceRow"] = 3;
        sourceRow["cells"]![5]!["formulaText"] = "E3-10"; sourceRow["cells"]![12]!["formulaText"] = "E3"; sourceRow["cells"]![13]!["formulaText"] = "K3";
        docs.Source["rows"]!.AsArray().Add(sourceRow);
        var typed = Batch(docs.Batch); var typedSource = docs.Source.Deserialize<ExcelPayslipCalculationSource>(ExcelPayslipRepository.JsonOptions)!;
        typed.ProfileLinks!.Add(new("duplicate-source-part", 3, 107, ExcelPayslipMasterSynchronization.ProfileIdentityHash(typed, typed.Rows[1], typedSource)));
        docs.Batch["profileLinks"] = JsonSerializer.SerializeToNode(typed.ProfileLinks, ExcelPayslipRepository.JsonOptions);
        await using var db = await f.OpenAsync();
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Batch AS JSON) WHERE client_id=907;UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=907", new { Batch = docs.Batch.ToJsonString(), Source = docs.Source.ToJsonString() });
        var before = await f.DocumentsAsync(); var review = await ReviewUanAsync(f);
        Assert.NotEmpty(review.Blockers); Assert.DoesNotContain(review.Changes, c => c.CanApply && !c.Computed);
        SameDocuments(before, await f.DocumentsAsync()); await AssertNoNewEntitiesOrMailAsync(f);
    }

    [IsolatedFact]
    public async Task Duplicate_owned_identity_cannot_be_assigned_to_another_employee()
    {
        var f = await FixtureAsync(second: true); await using var db = await f.OpenAsync();
        await db.ExecuteAsync("UPDATE employeepersonaldetails SET UanNumber='009876543210' WHERE EmployeeId=108");
        var before = await f.DocumentsAsync();
        try {
            var review = await ReviewUanAsync(f, "009876543210");
            if (review.Blockers.Count == 0 && review.Changes.Any(x => x.CanApply && !x.Computed))
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Apply(review), Actor()));
        } catch (InvalidOperationException) { }
        SameDocuments(before, await f.DocumentsAsync()); Assert.Equal("", await db.ExecuteScalarAsync<string>("SELECT COALESCE(UanNumber,'') FROM employeepersonaldetails WHERE EmployeeId=107"));
        await AssertNoNewEntitiesOrMailAsync(f, 2);
    }

    [IsolatedFact]
    public async Task Same_new_uan_proposed_for_two_employees_is_flagged_in_preview_without_blocking_independent_fields()
    {
        var f = await FixtureAsync(second: true); var before = await f.DocumentsAsync();
        var catalog = await f.Repository.GetBulkUpdateCatalogAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Actor());
        var email = Assert.Single(catalog.Fields, field => field.MasterField == "email" && field.Editable);
        var review = await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId,
            Input(("PROFILE-107", new() { ["source:3"] = "001234567891", [email.Id] = "independent@example.invalid" }),
                ("PROFILE-108", new() { ["source:3"] = "0012-3456-7891" })), Actor());
        var uans = review.Changes.Where(change => change.FieldId == "source:3").ToArray();
        Assert.Equal(2, uans.Length); Assert.All(uans, change => { Assert.False(change.CanApply); Assert.Contains("more than one employee", change.Reason, StringComparison.OrdinalIgnoreCase); });
        Assert.Contains(review.Changes, change => change.FieldId == email.Id && change.EmployeeCode == "PROFILE-107" && change.CanApply);
        SameDocuments(before, await f.DocumentsAsync());
        await using var db = await f.OpenAsync(); Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employeepersonaldetails WHERE EmployeeId IN(107,108) AND COALESCE(UanNumber,'')<>''"));
        await AssertNoNewEntitiesOrMailAsync(f, 2);
    }

    [IsolatedFact]
    public async Task Late_second_employee_write_failure_rolls_back_master_batch_source_history_and_audit_together()
    {
        var f = await FixtureAsync(second: true); var before = await f.DocumentsAsync();
        var review = await f.Repository.PreviewBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Input(("PROFILE-107", new() { ["source:3"] = "001234567891" }), ("PROFILE-108", new() { ["source:3"] = "001234567892" })), Actor());
        Assert.Empty(review.Blockers); Assert.Equal(2, Apply(review).SelectedChangeIds.Count);
        await using var db = await f.OpenAsync();
        // Owned synthetic DB only. The server exception occurs after the first
        // reviewed employee has been written, testing the shared transaction.
        await db.ExecuteAsync("CREATE TRIGGER synthetic_batch_update_late_failure BEFORE UPDATE ON employeepersonaldetails FOR EACH ROW BEGIN IF NEW.EmployeeId=108 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Synthetic late employee failure'; END IF; END");
        try { await Assert.ThrowsAnyAsync<Exception>(() => f.Repository.ApplyBulkUpdateAsync(907, ExcelPayslipBatchUpdateFixture.BatchId, Apply(review), Actor())); }
        finally { await db.ExecuteAsync("DROP TRIGGER synthetic_batch_update_late_failure"); }
        SameDocuments(before, await f.DocumentsAsync()); Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employee_audit_trail WHERE EmployeeId IN(107,108)"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employeepersonaldetails WHERE EmployeeId IN(107,108) AND COALESCE(UanNumber,'')<>''"));
        await AssertNoNewEntitiesOrMailAsync(f, 2);
    }
}

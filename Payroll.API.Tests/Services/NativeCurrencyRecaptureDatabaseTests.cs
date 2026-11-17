using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Configuration;
using Payroll.API.Models;
using Payroll.API.Repositories;
using PdfSharp.Pdf.IO;
using System.Text;

namespace Payroll.API.Tests.Services;

public sealed partial class NativeCurrencyStageDatabaseTests
{
    private static AuthUser RecaptureActor => new() { Id = Client, IsActive = true, ClientAccessMode = "All",
        Roles = ["super_admin"], Permissions = ["settings.manage", "employees.manage", "payroll.run"] };
    private PayRunRepository RecaptureRuns(bool enabled = true, bool safe = true)
    {
        var cfg = new ConfigurationBuilder().AddConfiguration(Config(true)).AddInMemoryCollection(new Dictionary<string, string?>
        { [PayRunRepository.NativeRecaptureFlag] = enabled.ToString(), ["BackgroundWorkers:Enabled"] = (!safe).ToString() }).Build();
        return new(cfg, new TaxEngineRepository(cfg), null, Protector);
    }
    private static NativeCurrencyRecaptureRequest Command(NativeCurrencyRecapturePreview preview, string? requestId = null) =>
        new(Client, "2026-09", preview.ReviewToken, requestId ?? Guid.NewGuid().ToString("N"), true);
    private async Task<PayRun> LegacyDraftAsync()
    {
        var repo = Runs(false); var queued = await repo.QueueAsync(Request(), "Synthetic legacy Draft"); Assert.NotNull(queued);
        var preview = await repo.GetSelectedProcessingPreviewAsync(queued.Id, Client, "2026-09"); Assert.NotNull(preview);
        Assert.True(preview.CanProcess, string.Join("; ", preview.Blockers));
        var draft = await repo.ProcessSelectedRunAsync(queued.Id, new() { ClientId = Client, PayPeriod = "2026-09",
            Confirmed = true, ExpectedFingerprint = preview.Fingerprint }, "Synthetic legacy Draft");
        Assert.NotNull(draft); Assert.Equal("Draft", draft.Status);
        await using var db = await F.OpenAsync();
        var attachment = await db.ExecuteScalarAsync<long>("""
INSERT INTO entity_attachments(public_id,client_id,attachment_attribute_id,field_configuration_id,storage_server_id,
entity_type,entity_id,original_file_name,stored_file_name,storage_key,file_extension,detected_mime_type,file_size_bytes,sha256_hash,uploaded_by_user_id)
VALUES(UUID(),97728,97728,97728,97728,'SyntheticPayroll',9772801,'retained.pdf','retained.pdf','synthetic/retained.pdf','.pdf','application/pdf',10,REPEAT('a',64),97728);
SELECT LAST_INSERT_ID();
""");
        var queue = await db.ExecuteScalarAsync<long>("""
INSERT INTO notification_queue(EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status,SentAt)
VALUES('EXCEL_PAYSLIP.SEND','ExcelPayslipBatch',@Resource,97728,'["synthetic@example.invalid"]','[]','[]','Retained synthetic payslip','<p>Retained synthetic history</p>','Sent','2026-10-01');
SELECT LAST_INSERT_ID();
""", new { Resource = BatchId + ":" + batch.Rows[0].Id });
        await db.ExecuteAsync("INSERT INTO notification_queue_attachments(QueueId,EntityAttachmentId) VALUES(@Queue,@Attachment)", new { Queue = queue, Attachment = attachment });
        await db.ExecuteAsync("INSERT INTO notification_logs(QueueId,EventCode,ResourceType,ResourceId,Recipient,Status,ProviderMessageId) VALUES(@Queue,'EXCEL_PAYSLIP.SEND','ExcelPayslipBatch',@Resource,'synthetic@example.invalid','Sent','retained-synthetic-message')",
            new { Queue = queue, Resource = BatchId + ":" + batch.Rows[0].Id });
        return draft;
    }
    private async Task<string> CompleteRecaptureWitness(int runId, bool includeInputs = true)
    {
        await using var db = await F.OpenAsync(); var records = new List<object>();
        foreach (var table in new[] { "payruns", "payrunemployees", "payrunemployeelines", "payrun_step_logs", "payroll_validation_issues", "payroll_reconciliation_results" })
            records.Add((await db.QueryAsync($"SELECT * FROM `{table}` WHERE {(table == "payruns" ? "Id" : "PayRunId")}=@Id ORDER BY Id", new { Id = runId })).ToArray());
        records.Add((await db.QueryAsync("SELECT * FROM tax_computation_snapshots WHERE pay_run_id=@Id ORDER BY id", new { Id = runId })).ToArray());
        records.Add((await db.QueryAsync("SELECT * FROM payrolladjustments WHERE PayRunId=@Id ORDER BY Id", new { Id = runId })).ToArray());
        records.Add((await db.QueryAsync(includeInputs ? "SELECT * FROM payroll_calculation_traces WHERE PayRunId=@Id ORDER BY Id"
            : "SELECT Id,PayRunId,EmployeeId,EmployeeCode,ComponentCode,ComponentName,ParentComponentCode,TraceOrder,RuleUsed,FormulaUsed,BaseAmount,Factor,CalculatedAmount,CAST(OutputJson AS CHAR) OutputJson,CreatedAt FROM payroll_calculation_traces WHERE PayRunId=@Id ORDER BY Id", new { Id = runId })).ToArray());
        records.Add(await SourceWitnessAsync());
        records.Add((await db.QueryAsync("SELECT * FROM notification_queue WHERE ClientId=97728 ORDER BY Id")).ToArray());
        records.Add((await db.QueryAsync("SELECT l.* FROM notification_logs l JOIN notification_queue q ON q.Id=l.QueueId WHERE q.ClientId=97728 ORDER BY l.Id")).ToArray());
        records.Add((await db.QueryAsync("SELECT a.* FROM notification_queue_attachments a JOIN notification_queue q ON q.Id=a.QueueId WHERE q.ClientId=97728 ORDER BY a.Id")).ToArray());
        records.Add((await db.QueryAsync("SELECT * FROM entity_attachments WHERE client_id=97728 ORDER BY id")).ToArray());
        return JsonSerializer.Serialize(records, Json);
    }
    private async Task<int> RecaptureSettingCount()
    {
        await using var db = await F.OpenAsync();
        return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM modulesettings WHERE client_id=97728 AND (ModuleCode LIKE 'pcr%' OR ModuleCode LIKE 'pcst%')");
    }

    [LocalFact]
    public async Task Draft_recapture_preview_is_read_only_and_compact_then_preserves_all_salary_and_original_outputs()
    {
        var run = await LegacyDraftAsync();
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync("INSERT INTO tax_computation_snapshots(pay_run_id,employee_id,monthly_tds) VALUES(@Id,9772801,123.45)", new { run.Id });
        var before = await CompleteRecaptureWitness(run.Id); var preserved = await CompleteRecaptureWitness(run.Id, false);
        var originals = (await db.QueryAsync<PayrollCalculationTrace>("SELECT *,CAST(InputJson AS CHAR) InputJson,CAST(OutputJson AS CHAR) OutputJson FROM payroll_calculation_traces WHERE PayRunId=@Id ORDER BY Id", new { run.Id })).ToArray();
        var repo = RecaptureRuns(); var preview = await repo.PreviewNativeCurrencyRecaptureAsync(run.Id, Client, "2026-09", RecaptureActor);
        Assert.Equal(before, await CompleteRecaptureWitness(run.Id)); Assert.Equal(0, await RecaptureSettingCount());
        Assert.Equal(2, preview.EmployeeCount); Assert.Equal(originals.Length, preview.TraceCount); Assert.True(preview.ReviewToken.Length < 3000);
        var command = Command(preview); var result = await repo.ApplyNativeCurrencyRecaptureAsync(run.Id, command, RecaptureActor);
        Assert.False(result.Replayed); Assert.Equal(new(true, true, true, true, true), result.Preservation);
        Assert.Equal(preserved, await CompleteRecaptureWitness(run.Id, false)); Assert.NotEqual(before, await CompleteRecaptureWitness(run.Id));
        var row = await ReadAsync(repo, run.Id); Assert.NotNull(row); Assert.Equal(986.496m, Assert.Single(row.Lines, l => l.Code == Pf).UnroundedEarned);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payroll_calculation_traces WHERE PayRunId=@Id AND JSON_EXTRACT(OutputJson,'$.nativeCurrencyStage') IS NOT NULL", new { run.Id }));
        Assert.Equal(6, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payroll_calculation_traces WHERE PayRunId=@Id AND JSON_EXTRACT(InputJson,'$.nativeCurrencyStage') IS NOT NULL", new { run.Id }));
        var archiveJson = await db.ExecuteScalarAsync<string>("SELECT CAST(SettingsJson AS CHAR) FROM modulesettings WHERE client_id=97728 AND ModuleCode LIKE 'pcra:%'");
        var envelope = JsonNode.Parse(archiveJson)!; var plain = Protector.UnprotectNativeCurrencyRecapture(envelope["payload"]!.GetValue<string>());
        var archived = JsonNode.Parse(plain!)!["traces"]!.Deserialize<PayrollCalculationTrace[]>(Json)!;
        Assert.Equal(JsonSerializer.Serialize(originals, Json), JsonSerializer.Serialize(archived, Json));
        var after = await CompleteRecaptureWitness(run.Id); var count = await RecaptureSettingCount();
        var replay = await repo.ApplyNativeCurrencyRecaptureAsync(run.Id, command, RecaptureActor);
        Assert.True(replay.Replayed); Assert.Equal(result.ProcessingEpochHash, replay.ProcessingEpochHash);
        Assert.Equal(after, await CompleteRecaptureWitness(run.Id)); Assert.Equal(count, await RecaptureSettingCount());
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, isReadOnly: true);
        var complete = await repo.ReadNativeCurrencyRunStageAsync(db, tx, Client, run.Id); Assert.NotNull(complete); Assert.Equal(2, complete.Rows.Length); await tx.RollbackAsync();
    }

    [LocalFact]
    public async Task Draft_recapture_second_employee_failure_rolls_back_annotations_archives_receipts_and_keeps_old_epoch()
    {
        var run = await LegacyDraftAsync(); var repo = RecaptureRuns();
        var preview = await repo.PreviewNativeCurrencyRecaptureAsync(run.Id, Client, "2026-09", RecaptureActor);
        var before = await CompleteRecaptureWitness(run.Id); await using var db = await F.OpenAsync();
        await db.ExecuteAsync("""
CREATE TRIGGER currency_97728_fail_recapture BEFORE UPDATE ON payroll_calculation_traces FOR EACH ROW
BEGIN IF NEW.EmployeeId=9772802 AND JSON_EXTRACT(NEW.InputJson,'$.nativeCurrencyStage') IS NOT NULL
THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Synthetic recapture rollback'; END IF; END
""");
        await Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => repo.ApplyNativeCurrencyRecaptureAsync(run.Id, Command(preview), RecaptureActor));
        Assert.Equal(before, await CompleteRecaptureWitness(run.Id)); Assert.Equal(0, await RecaptureSettingCount());
    }

    [LocalTheory]
    [InlineData("salary")][InlineData("trace")][InlineData("attendance")][InlineData("source")][InlineData("roster")][InlineData("status")][InlineData("payment")]
    [InlineData("queuedMail")][InlineData("mailLog")][InlineData("mailAttachment")]
    public async Task Draft_recapture_rejects_every_stale_review_without_any_partial_write(string changed)
    {
        var run = await LegacyDraftAsync(); var repo = RecaptureRuns();
        var preview = await repo.PreviewNativeCurrencyRecaptureAsync(run.Id, Client, "2026-09", RecaptureActor); await using var db = await F.OpenAsync();
        var sql = changed switch
        {
            "salary" => "UPDATE employeesalarycomponents SET Amount=Amount+1 WHERE EmployeeId=9772801",
            "trace" => "UPDATE payroll_calculation_traces SET InputJson=JSON_SET(InputJson,'$.changed',TRUE) WHERE PayRunId=@Id",
            "attendance" => "UPDATE employee_monthly_attendance SET payable_days=15 WHERE client_id=97728 AND employee_id=9772801",
            "source" => "UPDATE excel_payslip_batches SET send_state_json=JSON_OBJECT('changed','mail history') WHERE client_id=97728",
            "roster" => "UPDATE payrunemployees SET EmployeeId=9772899 WHERE PayRunId=@Id AND EmployeeId=9772801",
            "status" => "UPDATE payruns SET Status='Approved' WHERE Id=@Id",
            "queuedMail" => "UPDATE notification_queue SET ErrorMessage='Changed retained email' WHERE ClientId=97728",
            "mailLog" => "UPDATE notification_logs l JOIN notification_queue q ON q.Id=l.QueueId SET l.ProviderMessageId='changed' WHERE q.ClientId=97728",
            "mailAttachment" => "UPDATE entity_attachments SET sha256_hash=REPEAT('b',64) WHERE client_id=97728",
            _ => "UPDATE payrunemployees SET PaymentStatus='Paid',PaymentDate='2026-09-30' WHERE PayRunId=@Id AND EmployeeId=9772801"
        };
        await db.ExecuteAsync(sql, new { run.Id }); var before = await CompleteRecaptureWitness(run.Id);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => repo.ApplyNativeCurrencyRecaptureAsync(run.Id, Command(preview), RecaptureActor));
        Assert.Equal(before, await CompleteRecaptureWitness(run.Id)); Assert.Equal(0, await RecaptureSettingCount());
    }

    [LocalFact]
    public async Task Draft_recapture_enforces_runtime_actor_client_confirmation_token_and_workflow_guards()
    {
        var run = await LegacyDraftAsync(); var repo = RecaptureRuns(); var actor = RecaptureActor;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecaptureRuns(false).PreviewNativeCurrencyRecaptureAsync(run.Id, Client, "2026-09", actor));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecaptureRuns(true, false).PreviewNativeCurrencyRecaptureAsync(run.Id, Client, "2026-09", actor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => repo.PreviewNativeCurrencyRecaptureAsync(run.Id, Client, "2026-09", Actor));
        var preview = await repo.PreviewNativeCurrencyRecaptureAsync(run.Id, Client, "2026-09", actor); var command = Command(preview);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ApplyNativeCurrencyRecaptureAsync(run.Id, command with { Confirmed = false }, actor));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ApplyNativeCurrencyRecaptureAsync(run.Id, command with { ClientId = Client + 1 }, actor));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ApplyNativeCurrencyRecaptureAsync(run.Id, command with { ReviewToken = Protector.ProtectNativeCurrencyStage("{}") }, actor));
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync("INSERT INTO workflow_action_rules(ActivityCode,HttpMethod,PathPattern,ResourceType,IsActive) VALUES('CURRENCY_RECAPTURE_97728','POST',@Path,'PayRun',TRUE)", new { Path = $"/api/pay-runs/{run.Id}/currency-recapture" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ApplyNativeCurrencyRecaptureAsync(run.Id, command, actor));
        Assert.Equal(0, await RecaptureSettingCount());
    }

    [LocalFact]
    public async Task Draft_recapture_missing_original_request_is_held_instead_of_reconstructed()
    {
        var run = await Runs(false).CreateAsync(Request()); Assert.NotNull(run); var before = await CompleteRecaptureWitness(run.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RecaptureRuns().PreviewNativeCurrencyRecaptureAsync(run.Id, Client, "2026-09", RecaptureActor));
        Assert.Equal(before, await CompleteRecaptureWitness(run.Id)); Assert.Equal(0, await RecaptureSettingCount());
    }

    [LocalFact]
    public async Task Draft_recapture_checks_expiry_actor_stable_request_and_the_same_run_lock()
    {
        var run = await LegacyDraftAsync(); var repo = RecaptureRuns(); var actor = RecaptureActor;
        var preview = await repo.PreviewNativeCurrencyRecaptureAsync(run.Id, Client, "2026-09", actor); var command = Command(preview);
        var decoded = JsonNode.Parse(Protector.UnprotectNativeCurrencyRecapture(preview.ReviewToken)!)!;
        decoded["expiresAtUtc"] = DateTime.UtcNow.AddMinutes(-1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ApplyNativeCurrencyRecaptureAsync(run.Id,
            command with { ReviewToken = Protector.ProtectNativeCurrencyRecapture(decoded.ToJsonString()) }, actor));
        var other = RecaptureActor; other.Id++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ApplyNativeCurrencyRecaptureAsync(run.Id, command, other));
        await using var db = await F.OpenAsync();
        var name = await db.ExecuteScalarAsync<string>("SELECT CONCAT('payroll:',MD5(DATABASE()),':',@Id)", new { run.Id });
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT GET_LOCK(@Name,0)", new { Name = name }));
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ApplyNativeCurrencyRecaptureAsync(run.Id, command, actor)); }
        finally { await db.ExecuteAsync("SELECT RELEASE_LOCK(@Name)", new { Name = name }); }
        Assert.Equal(0, await RecaptureSettingCount());
        await repo.ApplyNativeCurrencyRecaptureAsync(run.Id, command, actor);
        // One request ID cannot be replayed as another signed review/actor.
        var forgedScope = JsonNode.Parse(Protector.UnprotectNativeCurrencyRecapture(preview.ReviewToken)!)!;
        forgedScope["planHash"] = new string('a', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ApplyNativeCurrencyRecaptureAsync(run.Id,
            command with { ReviewToken = Protector.ProtectNativeCurrencyRecapture(forgedScope.ToJsonString()) }, actor));
    }

    [LocalFact]
    public async Task Draft_recapture_final_receipt_failure_rolls_back_the_whole_new_epoch_and_preserves_original_history()
    {
        var run = await LegacyDraftAsync(); var repo = RecaptureRuns();
        var preview = await repo.PreviewNativeCurrencyRecaptureAsync(run.Id, Client, "2026-09", RecaptureActor);
        var before = await CompleteRecaptureWitness(run.Id); await using var db = await F.OpenAsync();
        await db.ExecuteAsync("""
CREATE TRIGGER currency_97728_fail_receipt BEFORE INSERT ON modulesettings FOR EACH ROW
BEGIN IF NEW.client_id=97728 AND NEW.ModuleCode LIKE 'pcrr:%'
THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Synthetic final receipt rollback'; END IF; END
""");
        await Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => repo.ApplyNativeCurrencyRecaptureAsync(run.Id, Command(preview), RecaptureActor));
        Assert.Equal(before, await CompleteRecaptureWitness(run.Id)); Assert.Equal(0, await RecaptureSettingCount());
    }

    [LocalFact]
    public async Task Draft_recapture_keeps_every_zero_salary_employee_and_zero_component_in_the_complete_receipt()
    {
        await using var db = await F.OpenAsync();
        foreach (var row in batch.Rows)
        {
            foreach (var field in row.Information.Where(f => f.Label is "Payable days" or "Actual present")) field.Value = "0";
            foreach (var line in row.Earnings.Concat(row.Deductions)) line.Amount = 0;
            row.DeclaredGross = 0; row.DeclaredDeductions = 0; row.NetPay = 0;
        }
        foreach (var row in source.Rows) foreach (var index in new[] { 1, 3, 4, 5, 6, 9 }) row.Cells[index]!.Value = "0";
        var hash = ExcelPayslipRepository.EmploymentSourceHash(batch, source);
        var request = JsonSerializer.Deserialize<ExcelPayrollSetupRequest>((await db.ExecuteScalarAsync<string>("SELECT CAST(request_json AS CHAR) FROM excel_payroll_setup_imports WHERE id=@ImportId", new { ImportId }))!, Json)!;
        var receipt = JsonSerializer.Deserialize<ExcelPayrollSetupResult>((await db.ExecuteScalarAsync<string>("SELECT CAST(result_json AS CHAR) FROM excel_payroll_setup_imports WHERE id=@ImportId", new { ImportId }))!, Json)!;
        request.AttendanceReview!.SourceHash = hash; receipt.Transfer!.SourceHash = hash;
        receipt.TransferRows = receipt.TransferRows.Select(r => r with { PresentDays = 0, PayableDays = 0, SourceNet = 0 }).ToList();
        await db.ExecuteAsync("""
UPDATE excel_payslip_batches SET batch_json=CAST(@BatchJson AS JSON) WHERE client_id=97728 AND id=@BatchId;
UPDATE excel_payslip_calculation_sources SET source_json=CAST(@SourceJson AS JSON) WHERE client_id=97728 AND batch_id=@BatchId;
UPDATE excel_payroll_setup_imports SET request_json=CAST(@Request AS JSON),result_json=CAST(@Receipt AS JSON) WHERE id=@ImportId;
UPDATE employee_monthly_attendance SET present_days=0,payable_days=0 WHERE client_id=97728;
UPDATE employee_daily_attendance SET status='Absent',payable_value=0 WHERE client_id=97728;
""", new { BatchId, ImportId, BatchJson = JsonSerializer.Serialize(batch, Json), SourceJson = JsonSerializer.Serialize(source, Json),
            Request = JsonSerializer.Serialize(request, Json), Receipt = JsonSerializer.Serialize(receipt, Json) });
        await SaveDesignAsync();
        var run = await LegacyDraftAsync(); var repo = RecaptureRuns(); var before = await CompleteRecaptureWitness(run.Id, false);
        var preview = await repo.PreviewNativeCurrencyRecaptureAsync(run.Id, Client, "2026-09", RecaptureActor);
        var result = await repo.ApplyNativeCurrencyRecaptureAsync(run.Id, Command(preview), RecaptureActor);
        Assert.Equal(2, result.EmployeeCount); Assert.Equal(before, await CompleteRecaptureWitness(run.Id, false));
        foreach (var employee in new[] { One, Two })
        {
            var frozen = await ReadAsync(repo, run.Id, employee); Assert.NotNull(frozen); Assert.Equal(3, frozen.Lines.Length);
            Assert.All(frozen.Lines, line => { Assert.Equal(0, line.UnroundedEarned); Assert.Equal(0, line.AccountedEarned); });
        }
    }

    [LocalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task Actual_approved_PDF_uses_protected_regular_or_recaptured_stage_and_rejects_missing_receipt(bool recapture)
    {
        var repo = RecaptureRuns(); PayRun run;
        if (recapture)
        {
            run = await LegacyDraftAsync();
            var preview = await repo.PreviewNativeCurrencyRecaptureAsync(run.Id, Client, "2026-09", RecaptureActor);
            await repo.ApplyNativeCurrencyRecaptureAsync(run.Id, Command(preview), RecaptureActor);
        }
        else { run = (await Runs(true).CreateAsync(Request()))!; Assert.NotNull(run); }
        Assert.NotNull(await repo.SubmitForApprovalAsync(run.Id)); Assert.NotNull(await repo.ApproveAsync(run.Id));
        var before = await CompleteRecaptureWitness(run.Id);
        var designs = new PayslipDesignRepository(Config(true), Protector, new());
        var pdf = await designs.CreatePdfAsync(Client, run.Id, One); Assert.NotNull(pdf);
        using var document = PdfReader.Open(new MemoryStream(pdf.Bytes), PdfDocumentOpenMode.Import);
        var content = Encoding.Latin1.GetString(document.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue);
        Assert.Contains("(986)", content); Assert.DoesNotContain("(987)", content); Assert.Contains("7,173", content);
        Assert.Equal(before, await CompleteRecaptureWitness(run.Id));
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync("UPDATE modulesettings SET IsEnabled=FALSE WHERE client_id=97728 AND ModuleCode=@Code", new { Code = "pcst.active:" + run.Id });
        await Assert.ThrowsAsync<InvalidOperationException>(() => designs.CreatePdfAsync(Client, run.Id, One));
        Assert.Equal(before, await CompleteRecaptureWitness(run.Id));
        await db.ExecuteAsync("UPDATE modulesettings SET IsEnabled=TRUE,SettingsJson=JSON_OBJECT('value','tampered') WHERE client_id=97728 AND ModuleCode=@Code", new { Code = "pcst.active:" + run.Id });
        await Assert.ThrowsAsync<InvalidOperationException>(() => designs.CreatePdfAsync(Client, run.Id, One));
    }

    private sealed class LocalTheoryAttribute : TheoryAttribute { public LocalTheoryAttribute() { if (Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null) Skip = "Explicit sentinel-guarded fixture required."; } }
}

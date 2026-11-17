using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("ReviewedPayroll")]
public sealed class ExcelPayslipSaveWarningPreservationDatabaseTests : IAsyncLifetime
{
    private const int Client = 97791;
    private const string BatchId = "97791000000000000000000000000001", OriginalId = "97791000000000000000000000000002";
    private static readonly DateTime ReviewedAt = new(2026, 10, 7, 12, 34, 56, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ReviewedPayrollFixture? fixture = Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is string c ? new(c) : null;
    private ReviewedPayrollFixture F => fixture ?? throw new InvalidOperationException("Sentinel-guarded loopback fixture required.");
    private ExcelPayslipRepository Repo => new(F.Config, new(F.Config, null!, null!, NullLogger<NotificationRepository>.Instance), new());
    private sealed class IsolatedFactAttribute : FactAttribute
    {
        public IsolatedFactAttribute() { if (Environment.GetEnvironmentVariable("FREVO_REVIEWED_PAYROLL_TEST_DB") is null) Skip = "Explicit sentinel-guarded loopback fixture required."; }
    }
    public async Task InitializeAsync() { if (fixture is not null) await CleanAsync(); }
    public async Task DisposeAsync() { if (fixture is not null) await CleanAsync(); }
    private async Task CleanAsync()
    {
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync(@"
DELETE l FROM notification_logs l JOIN notification_queue q ON q.Id=l.QueueId WHERE q.ClientId=97791;
DELETE FROM notification_queue WHERE ClientId=97791;
DELETE FROM excel_payslip_batch_originals WHERE client_id=97791;
DELETE FROM excel_payslip_originals WHERE client_id=97791;
DELETE FROM entity_attachments WHERE client_id=97791;
DELETE FROM excel_payslip_calculation_sources WHERE client_id=97791;
DELETE FROM excel_payslip_batches WHERE client_id=97791;
DELETE FROM clients WHERE Id=97791;");
    }
    private static string Money(ExcelPayslipRow row) => JsonSerializer.Serialize(new
        { row.Earnings, row.Deductions, row.EmployerContributions, row.NetPay, row.DeclaredGross, row.DeclaredDeductions }, Json);
    private async Task<(ExcelPayslipBatch Batch, ExcelPayslipCalculationSource Source, string State, string Mail)> ReadAsync()
    {
        await using var db = await F.OpenAsync();
        var saved = await db.QuerySingleAsync<(string Batch, string Source, string State)>(@"
SELECT CAST(b.batch_json AS CHAR),CAST(s.source_json AS CHAR),CAST(b.send_state_json AS CHAR)
FROM excel_payslip_batches b JOIN excel_payslip_calculation_sources s ON s.client_id=b.client_id AND s.batch_id=b.id
WHERE b.client_id=97791 AND b.id=@BatchId", new { BatchId });
        var queue = (await db.QueryAsync("SELECT * FROM notification_queue WHERE ClientId=97791 ORDER BY Id")).ToArray();
        var logs = (await db.QueryAsync("SELECT l.* FROM notification_logs l JOIN notification_queue q ON q.Id=l.QueueId WHERE q.ClientId=97791 ORDER BY l.Id")).ToArray();
        return (JsonSerializer.Deserialize<ExcelPayslipBatch>(saved.Batch, Json)!,
            JsonSerializer.Deserialize<ExcelPayslipCalculationSource>(saved.Source, Json)!, saved.State,
            JsonSerializer.Serialize(new { Queue = queue, Logs = logs }, Json));
    }
    private async Task SeedAsync()
    {
        var batch = new ExcelPayslipBatch
        {
            Id = BatchId, ClientId = Client, Month = "2026-10", SheetName = "Synthetic salary", HeaderRow = 1,
            SourceFileName = "Synthetic legacy salary.xlsx", SimpleLayout = true, CreatedBy = "stored-owner@example.invalid",
            CreatedAtUtc = new(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc), OriginalWorkbooks = [new(OriginalId, "Synthetic legacy salary.xlsx")]
        };
        var source = new ExcelPayslipCalculationSource { SheetName = batch.SheetName, HeaderRow = 1, ColumnCount = 11, DateSystem = "1900" };
        foreach (var (label, kind) in new[] { ("Employee Code", "employeeCode"), ("Employee Name", "employeeName"),
            ("Posting State", "info"), ("Account No", "info"), ("IFSC Code", "info"), ("Basic", "earning"),
            ("Allowance", "earning"), ("PF", "deduction"), ("Gross", "grossTotal"), ("Net", "netPay"), ("Deductions", "deductionTotal") })
            source.Columns.Add(new() { ColumnIndex = source.Columns.Count, SourceHeader = label, Label = label, Kind = kind });
        for (var i = 0; i < 62; i++)
        {
            var basic = 1000.123456789m + i;
            var row = new ExcelPayslipRow { Id = "stable-" + i, SourceRow = i + 2, EmployeeCode = "WARN-" + i.ToString("D3"),
                EmployeeName = "Synthetic person " + i, Information = [new() { Label = "Posting State" }, new() { Label = "Account No" }, new() { Label = "IFSC Code" }],
                Earnings = [new() { Label = "Basic", Amount = basic }, new() { Label = "Allowance", Amount = 20 }],
                Deductions = [new() { Label = "PF", Amount = 10 }], DeclaredGross = basic, DeclaredDeductions = 10, NetPay = basic + 10 };
            if (i < 6) row.WarningReview = new(ExcelPayslipRepository.NegativeAmountHash(row), "stored-reviewer@example.invalid", ReviewedAt,
                ExcelPayslipRepository.SourceRowWarnings(row));
            row.Warnings = ExcelPayslipRepository.RowWarnings(row); batch.Rows.Add(row);
            var values = new[] { row.EmployeeCode, row.EmployeeName, "", "", "", basic.ToString(CultureInfo.InvariantCulture), "20", "10",
                basic.ToString(CultureInfo.InvariantCulture), row.NetPay.ToString(CultureInfo.InvariantCulture), "10" };
            source.Rows.Add(new() { SourceRow = row.SourceRow, Cells = values.Select((value, column) => (ExcelPayslipCalculationCell?)new()
                { DataType = column < 5 ? "Text" : "Number", Value = value }).ToList() });
        }
        Assert.Null(ExcelPayslipCalculationService.ValidateSource(source, batch));
        await using var db = await F.OpenAsync();
        await db.ExecuteAsync("INSERT INTO clients(Id,Code,Name,IsActive) VALUES(97791,'WARN-SAVE','Synthetic warning retention',TRUE)");
        var attachment = await db.ExecuteScalarAsync<long>(@"
INSERT INTO entity_attachments(public_id,client_id,attachment_attribute_id,field_configuration_id,storage_server_id,entity_type,entity_id,
original_file_name,stored_file_name,storage_key,file_extension,detected_mime_type,file_size_bytes,sha256_hash,uploaded_by_user_id)
VALUES(UUID(),97791,97791,97791,97791,'SyntheticPayroll',97791,'Synthetic legacy salary.xlsx','synthetic.xlsx','synthetic/warnings.xlsx','.xlsx',
'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',10,REPEAT('a',64),97791);SELECT LAST_INSERT_ID();");
        await db.ExecuteAsync(@"
INSERT INTO excel_payslip_originals(client_id,id,attachment_id,sha256_hash) VALUES(97791,@OriginalId,@Attachment,REPEAT('a',64));
INSERT INTO excel_payslip_batch_originals(client_id,batch_id,original_id,original_file_name) VALUES(97791,@BatchId,@OriginalId,'Synthetic legacy salary.xlsx');
INSERT INTO excel_payslip_batches(client_id,id,batch_json,send_state_json,created_at) VALUES(97791,@BatchId,CAST(@Batch AS JSON),
JSON_OBJECT('requests',JSON_OBJECT('historical',JSON_OBJECT('retain','exact-old-history')),'deliveries',JSON_OBJECT(),'jobs',JSON_OBJECT()),'2026-10-31');
INSERT INTO excel_payslip_calculation_sources(client_id,batch_id,source_json,created_at) VALUES(97791,@BatchId,CAST(@Source AS JSON),'2026-10-31');",
            new { OriginalId, Attachment = attachment, BatchId, Batch = JsonSerializer.Serialize(batch, Json), Source = JsonSerializer.Serialize(source, Json) });
        var queue = await db.ExecuteScalarAsync<long>(@"
INSERT INTO notification_queue(EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status,SentAt)
VALUES('EXCEL_PAYSLIP.SEND','ExcelPayslipBatch',@Resource,97791,'[""synthetic@example.invalid""]','[]','[]','Stored synthetic mail','Retained historical payload','Sent','2026-10-31');SELECT LAST_INSERT_ID();",
            new { Resource = BatchId + ":stable-0" });
        await db.ExecuteAsync(@"INSERT INTO notification_logs(QueueId,EventCode,ResourceType,ResourceId,Recipient,Status,ProviderMessageId)
VALUES(@Queue,'EXCEL_PAYSLIP.SEND','ExcelPayslipBatch',@Resource,'synthetic@example.invalid','Sent','old-synthetic-message')",
            new { Queue = queue, Resource = BatchId + ":stable-0" });
    }

    [IsolatedFact]
    public async Task Normal_Save_retains_six_trusted_legacy_acknowledgements_all62_money_and_row_ids_and_exact_old_mail()
    {
        await SeedAsync(); var before = await ReadAsync();
        var oldMoney = before.Batch.Rows.ToDictionary(r => r.Id, Money);
        var oldReviews = before.Batch.Rows.Take(6).ToDictionary(r => r.Id, r => JsonSerializer.Serialize(r.WarningReview, Json));
        var request = before.Batch; request.CalculationSource = before.Source;
        foreach (var row in request.Rows.Skip(59))
        {
            row.Information[0].Value = "Reviewed synthetic site";
            row.Information[1].Value = "001234567890" + row.SourceRow;
            row.Information[2].Value = "TEST0000123";
            var cells = request.CalculationSource.Rows.Single(r => r.SourceRow == row.SourceRow).Cells;
            for (var index = 0; index < 3; index++) cells[index + 2]!.Value = row.Information[index].Value;
        }
        foreach (var row in request.Rows)
        {
            row.WarningReview = new(ExcelPayslipRepository.NegativeAmountHash(row), "incoming-forged@example.invalid", ReviewedAt.AddDays(1), ["Caller cannot approve"]);
            row.WarningReviewToken = "caller-token";
        }
        var saved = await Repo.SaveAsync(Client, request, "synthetic-operator@example.invalid");
        Assert.Null(saved.Error); Assert.NotNull(saved.Item); Assert.Equal(BatchId, saved.Item.Id);
        var after = await ReadAsync(); Assert.Equal(before.State, after.State); Assert.Equal(before.Mail, after.Mail);
        Assert.Equal(oldMoney.Keys.Order(), after.Batch.Rows.Select(r => r.Id).Order());
        Assert.All(after.Batch.Rows, row => { Assert.Equal(oldMoney[row.Id], Money(row)); Assert.Null(row.WarningReviewToken); });
        Assert.All(after.Batch.Rows.Take(6), row => { Assert.Equal(oldReviews[row.Id], JsonSerializer.Serialize(row.WarningReview, Json)); Assert.Empty(row.Warnings); });
        Assert.All(after.Batch.Rows.Skip(6), row => { Assert.Null(row.WarningReview); Assert.NotEmpty(row.Warnings); });
        var changed = after.Batch.Rows[0]; changed.Earnings[0].Amount += 1; changed.NetPay += 1;
        var sourceRow = after.Source.Rows.Single(r => r.SourceRow == changed.SourceRow);
        sourceRow.Cells[5]!.Value = changed.Earnings[0].Amount.ToString(CultureInfo.InvariantCulture);
        sourceRow.Cells[9]!.Value = changed.NetPay.ToString(CultureInfo.InvariantCulture);
        changed.WarningReview = new(ExcelPayslipRepository.NegativeAmountHash(changed), "incoming-forged@example.invalid", ReviewedAt.AddDays(1), ExcelPayslipRepository.SourceRowWarnings(changed));
        after.Batch.CalculationSource = after.Source;
        var changedSave = await Repo.SaveAsync(Client, after.Batch, "synthetic-operator@example.invalid");
        Assert.Null(changedSave.Error); var final = await ReadAsync();
        Assert.Null(final.Batch.Rows[0].WarningReview); Assert.NotEqual("stable-0", final.Batch.Rows[0].Id); Assert.NotEmpty(final.Batch.Rows[0].Warnings);
        Assert.All(final.Batch.Rows.Skip(1).Take(5), row => Assert.Equal(oldReviews[row.Id], JsonSerializer.Serialize(row.WarningReview, Json)));
        Assert.Equal(before.State, final.State); Assert.Equal(before.Mail, final.Mail);
        await using var db = await F.OpenAsync();
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM excel_payslip_batches WHERE client_id=97791"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE ClientId=97791"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_queue WHERE ClientId=97791"));
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using MySqlConnector;
using PdfSharp.Pdf.IO;
using Payroll.API.Models;
using Payroll.API.Repositories;
using Payroll.API.Services;
using Payroll.API.Tests.Fixtures;

namespace Payroll.API.Tests.Services;

[Collection("EssPayslipFlow")]
public sealed class ExcelPayslipCorrectedResendDatabaseTests
{
    private sealed class IsolatedFactAttribute : FactAttribute
    {
        public IsolatedFactAttribute() { if (Environment.GetEnvironmentVariable("FREVO_EXCEL_CORRECTED_RESEND_TEST_DB") is null) Skip = "Explicit isolated corrected-payslip test database required."; }
    }
    private sealed class IsolatedTheoryAttribute : TheoryAttribute
    {
        public IsolatedTheoryAttribute() { if (Environment.GetEnvironmentVariable("FREVO_EXCEL_CORRECTED_RESEND_TEST_DB") is null) Skip = "Explicit isolated corrected-payslip test database required."; }
    }

    [IsolatedFact]
    public async Task Explicit_correction_appends_idempotent_delivery_and_worker_sends_frozen_metadata_with_saved_salary_to_loopback_only()
    {
        var connection = Environment.GetEnvironmentVariable("FREVO_EXCEL_CORRECTED_RESEND_TEST_DB")!;
        var target = new MySqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1", target.Server); Assert.Equal((uint)33330, target.Port); Assert.Equal("attendance_location_test", target.Database);
        var f = new EssPayslipFlowFixture(connection);
        await f.ResetAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EssProfile"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connection, ["OutboundDelivery:Suppressed"] = "false" }).Build();
        var notifications = new NotificationRepository(config, null!, null!, NullLogger<NotificationRepository>.Instance);
        var slips = new ExcelPayslipRepository(config, notifications, new ExcelPayslipPdfService());
        await using var db = new MySqlConnection(connection); await db.OpenAsync();
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS entity_attachments(id BIGINT PRIMARY KEY)");
        await notifications.InitializeAsync();
        await SeedMailTemplateAsync(notifications);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS auditlogs(Id BIGINT PRIMARY KEY AUTO_INCREMENT,UserId INT NULL,UserEmail VARCHAR(190),Action VARCHAR(120) NOT NULL,Resource VARCHAR(190),Method VARCHAR(20),Path VARCHAR(500),StatusCode INT NOT NULL DEFAULT 0,DetailsJson JSON NOT NULL,CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP)");
        await db.ExecuteAsync("DELETE FROM notification_queue_attachments;DELETE FROM notification_logs;DELETE FROM notification_queue");
        await using var smtp = new LoopbackSmtp();
        await notifications.SaveSmtpAsync(new() { Id = 1, IsEnabled = true, DeliveryPaused = false, Host = "127.0.0.1", Port = smtp.Port, EnableSsl = false, FromEmail = "sender@example.invalid", FromName = "Synthetic sender", ImapEnabled = false });
        async Task<string> StoredBatchHash() => (await db.ExecuteScalarAsync<string>("SELECT SHA2(CAST(batch_json AS CHAR),256) FROM excel_payslip_batches WHERE client_id=907 AND id=@Id", new { Id = EssPayslipFlowFixture.BatchId }))!;
        async Task<string> StoredSourceHash() => (await db.ExecuteScalarAsync<string>("SELECT SHA2(CAST(source_json AS CHAR),256) FROM excel_payslip_calculation_sources WHERE client_id=907 AND batch_id=@Id", new { Id = EssPayslipFlowFixture.BatchId }))!;
        async Task<ExcelPayslipSendState> State() => JsonSerializer.Deserialize<ExcelPayslipSendState>((await db.ExecuteScalarAsync<string>("SELECT CAST(send_state_json AS CHAR) FROM excel_payslip_batches WHERE client_id=907 AND id=@Id", new { Id = EssPayslipFlowFixture.BatchId }))!, ExcelPayslipRepository.JsonOptions)!;
        static SendExcelPayslipsRequest Request(bool corrected = false) => new() { RequestId = Guid.NewGuid().ToString("N"), RowIds = ["e2e-row"], Mode = "Individual", AmountDecimalPlaces = 2, ResendCorrected = corrected, AcknowledgeResend = corrected };
        var batchHash = await StoredBatchHash(); var sourceHash = await StoredSourceHash();
        // Seed historical SMTP acceptance through the real ordinary submit path;
        // it is not sent to SMTP, so the sole network delivery is the corrected one.
        var firstRequest = Request(); var first = await slips.SubmitMailJobAsync(907, EssPayslipFlowFixture.BatchId, firstRequest, 9907, "test@example.invalid");
        Assert.Null(first.Error); Assert.Equal("Queued", Assert.Single(first.Item!.Items).Status);
        var historical = Assert.Single((await State()).Deliveries.Values);
        await db.ExecuteAsync("UPDATE notification_queue SET Status='Sent',SentAt=UTC_TIMESTAMP() WHERE Id=@Id", new { Id = historical.QueueId });
        await db.ExecuteAsync("UPDATE employees SET FirstName='Corrected',LastName='Synthetic Employee',WorkEmail='corrected@example.invalid',AnnualCtc=9999999,SalaryStructureId='Different salary setup',WorkLocationId=908 WHERE Id=107 AND ClientId=907;UPDATE employeepersonaldetails SET UanNumber='001234567890',Mobile='9000000999' WHERE EmployeeId=107;INSERT INTO worklocations(Id,ClientId,Name,City) VALUES(908,907,'Later current office','Synthetic city') ON DUPLICATE KEY UPDATE Name=VALUES(Name)");
        var normalRetry = await slips.SubmitMailJobAsync(907, EssPayslipFlowFixture.BatchId, Request(), 9907, "test@example.invalid");
        Assert.Null(normalRetry.Error); Assert.Equal("Skipped", Assert.Single(normalRetry.Item!.Items).Status);
        var beforeCorrection = await State(); var oldDeliveryJson = JsonSerializer.Serialize(beforeCorrection.Deliveries, ExcelPayslipRepository.JsonOptions);
        var oldRequests = beforeCorrection.Requests.ToDictionary(kv => kv.Key, kv => kv.Value.GetRawText());
        var oldJobs = beforeCorrection.Jobs.ToDictionary(kv => kv.Key, kv => JsonSerializer.Serialize(kv.Value, ExcelPayslipRepository.JsonOptions));
        var noAck = Request(true); noAck.AcknowledgeResend = false;
        Assert.NotNull((await slips.SubmitMailJobAsync(907, EssPayslipFlowFixture.BatchId, noAck, 9907, "test@example.invalid")).Error);
        Assert.NotNull((await slips.SubmitMailJobAsync(908, EssPayslipFlowFixture.BatchId, Request(true), 9907, "test@example.invalid")).Error);
        var wrongRow = Request(true); wrongRow.RowIds = ["another-batch-row"];
        Assert.NotNull((await slips.SubmitMailJobAsync(907, EssPayslipFlowFixture.BatchId, wrongRow, 9907, "test@example.invalid")).Error);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_queue"));

        var correctedRequest = Request(true);
        // Client-supplied snapshot-shaped JSON is unknown input and cannot override
        // the server-owned corrected document, recipient or captured money.
        var forgedInput = JsonSerializer.SerializeToNode(correctedRequest, ExcelPayslipRepository.JsonOptions)!.AsObject();
        forgedInput["correctionSnapshot"] = JsonSerializer.SerializeToNode(new { clientId = 999, employeeName = "Injected Identity", email = "attacker@example.invalid", netPay = 999999 });
        correctedRequest = forgedInput.Deserialize<SendExcelPayslipsRequest>(ExcelPayslipRepository.JsonOptions)!;
        var correction = await slips.SubmitMailJobAsync(907, EssPayslipFlowFixture.BatchId, correctedRequest, 9907, "test@example.invalid");
        Assert.Null(correction.Error); Assert.Equal("Queued", Assert.Single(correction.Item!.Items).Status);
        Assert.True(correction.Item.Job.ResendCorrected);
        Assert.Equal(1, correction.Item.Job.Queued);
        var overlapping = await slips.SubmitMailJobAsync(907, EssPayslipFlowFixture.BatchId, Request(true), 9907, "test@example.invalid");
        Assert.Null(overlapping.Error);
        Assert.Equal("Skipped", Assert.Single(overlapping.Item!.Items).Status);
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_queue"));
        var queuedState = await State();
        Assert.Equal(2, queuedState.Deliveries.Count);
        var correctedDocument = Assert.Single(queuedState.Deliveries.Values, d => d.RequestId == correctedRequest.RequestId);
        var correctionEvidence = queuedState.Requests[ExcelPayslipRepository.CorrectionSnapshotPrefix + correctedRequest.RequestId];
        Assert.False(correctionEvidence.TryGetProperty("calculationSource", out _));
        Assert.DoesNotContain("1000+244.5678", correctionEvidence.GetRawText());
        Assert.Equal(64, correctionEvidence.GetProperty("sourceCalculationSha256").GetString()!.Length);
        Assert.NotEqual(historical.Id, correctedDocument.Id); Assert.NotEqual(historical.QueueId, correctedDocument.QueueId);
        Assert.Equal("corrected@example.invalid", correctedDocument.Email);
        Assert.Equal(oldDeliveryJson, JsonSerializer.Serialize(queuedState.Deliveries.Where(kv => beforeCorrection.Deliveries.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value), ExcelPayslipRepository.JsonOptions));
        foreach (var (key, value) in oldRequests) Assert.Equal(value, queuedState.Requests[key].GetRawText());
        foreach (var (key, value) in oldJobs) Assert.Equal(value, JsonSerializer.Serialize(queuedState.Jobs[key], ExcelPayslipRepository.JsonOptions));
        var immutableQueuedDocument = JsonSerializer.Serialize(correctedDocument, ExcelPayslipRepository.JsonOptions);
        // A later Master change must not silently change an already queued correction.
        await db.ExecuteAsync("UPDATE employees SET FirstName='Later unrelated master',LastName='Name',WorkEmail='later@example.invalid',AnnualCtc=8888888 WHERE Id=107;UPDATE employeepersonaldetails SET UanNumber='009876543210' WHERE EmployeeId=107");
        var lostResponseRetry = await slips.SubmitMailJobAsync(907, EssPayslipFlowFixture.BatchId, correctedRequest, 9907, "test@example.invalid");
        Assert.Null(lostResponseRetry.Error); Assert.Equal(correction.Item.Job.Id, lostResponseRetry.Item!.Job.Id);
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_queue"));
        Assert.Equal(immutableQueuedDocument, JsonSerializer.Serialize((await State()).Deliveries[correctedDocument.Id], ExcelPayslipRepository.JsonOptions));
        Assert.Equal(batchHash, await StoredBatchHash()); Assert.Equal(sourceHash, await StoredSourceHash());

        Assert.Equal(1, await notifications.ProcessPendingAsync(CancellationToken.None, correctedDocument.QueueId));
        using var message = await smtp.Message;
        Assert.Equal("corrected@example.invalid", Assert.Single(message.To.Mailboxes).Address);
        var attachment = Assert.IsType<MimePart>(Assert.Single(message.Attachments)); using var bytes = new MemoryStream(); attachment.Content!.DecodeTo(bytes);
        using var pdf = PdfReader.Open(new MemoryStream(bytes.ToArray()), PdfDocumentOpenMode.Import);
        var text = string.Join('\n', pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(page => Encoding.Latin1.GetString(page.Contents.CreateSingleContent().Stream.UnfilteredValue)));
        Assert.Contains("Corrected Synthetic Employee", text); Assert.Contains("001234567890", text); Assert.Contains("1,234.57", text); Assert.Contains("Synthetic period office", text);
        Assert.DoesNotContain("Later unrelated master", text); Assert.DoesNotContain("009876543210", text); Assert.DoesNotContain("Injected Identity", text);
        Assert.DoesNotContain("9,999,999", text); Assert.Equal(1, pdf.PageCount);
        Assert.Equal("Sent", await db.ExecuteScalarAsync<string>("SELECT Status FROM notification_queue WHERE Id=@Id", new { Id = correctedDocument.QueueId }));
        Assert.Equal("Sent", await db.ExecuteScalarAsync<string>("SELECT Status FROM notification_queue WHERE Id=@Id", new { Id = historical.QueueId }));
        // A generic notification retry must not reset an SMTP-accepted correction.
        await notifications.RetryAsync(correctedDocument.QueueId);
        Assert.Equal("Sent", await db.ExecuteScalarAsync<string>("SELECT Status FROM notification_queue WHERE Id=@Id", new { Id = correctedDocument.QueueId }));
        Assert.Equal(0, await notifications.ProcessPendingAsync(CancellationToken.None, correctedDocument.QueueId));
        var completed = await State();
        foreach (var (key, value) in oldRequests) Assert.Equal(value, completed.Requests[key].GetRawText());
        Assert.Equal(batchHash, await StoredBatchHash()); Assert.Equal(sourceHash, await StoredSourceHash());
    }

    [IsolatedFact]
    public async Task Code_only_employee_match_cannot_promote_unverified_Master_bank_or_contact_into_a_correction()
    {
        var connection = Environment.GetEnvironmentVariable("FREVO_EXCEL_CORRECTED_RESEND_TEST_DB")!;
        var f = new EssPayslipFlowFixture(connection); // Strict localhost/port/database guard runs before all writes.
        await f.ResetAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EssProfile"));
        var notifications = new NotificationRepository(f.Config, null!, null!, NullLogger<NotificationRepository>.Instance);
        var slips = new ExcelPayslipRepository(f.Config, notifications, new ExcelPayslipPdfService());
        await using var db = new MySqlConnection(connection); await db.OpenAsync();
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS entity_attachments(id BIGINT PRIMARY KEY)");
        await notifications.InitializeAsync();
        await SeedMailTemplateAsync(notifications);
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS auditlogs(Id BIGINT PRIMARY KEY AUTO_INCREMENT,UserId INT NULL,UserEmail VARCHAR(190),Action VARCHAR(120) NOT NULL,Resource VARCHAR(190),Method VARCHAR(20),Path VARCHAR(500),StatusCode INT NOT NULL DEFAULT 0,DetailsJson JSON NOT NULL,CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP)");
        await notifications.SaveSmtpAsync(new() { IsEnabled = true, Host = "127.0.0.1", Port = 25252, EnableSsl = false, FromEmail = "sender@example.invalid", ImapEnabled = false });
        var raw = JsonSerializer.Deserialize<ExcelPayslipBatch>((await db.ExecuteScalarAsync<string>("SELECT CAST(batch_json AS CHAR) FROM excel_payslip_batches WHERE client_id=907 AND id=@Id", new { Id = EssPayslipFlowFixture.BatchId }))!, ExcelPayslipRepository.JsonOptions)!;
        var source = JsonSerializer.Deserialize<ExcelPayslipCalculationSource>((await db.ExecuteScalarAsync<string>("SELECT CAST(source_json AS CHAR) FROM excel_payslip_calculation_sources WHERE client_id=907 AND batch_id=@Id", new { Id = EssPayslipFlowFixture.BatchId }))!, ExcelPayslipRepository.JsonOptions)!;
        raw.ProfileLinks = null; raw.Rows[0].Information.RemoveAll(f => f.Label == "PAN");
        source.Columns[2].Label = source.Columns[2].SourceHeader = "Unreviewed source field"; source.Columns[2].Kind = "ignore"; source.Rows[0].Cells[2]!.Value = "";
        var savedSalary = JsonSerializer.Serialize(new { raw.Rows[0].Earnings, raw.Rows[0].Deductions, raw.Rows[0].EmployerContributions, raw.Rows[0].NetPay });
        await db.ExecuteAsync("UPDATE excel_payslip_batches SET batch_json=CAST(@Batch AS JSON) WHERE client_id=907 AND id=@Id;UPDATE excel_payslip_calculation_sources SET source_json=CAST(@Source AS JSON) WHERE client_id=907 AND batch_id=@Id;UPDATE employees SET WorkEmail='unverified@example.invalid',AnnualCtc=9999999 WHERE Id=107;UPDATE employeepersonaldetails SET UanNumber='001234567890',Mobile='9000000222' WHERE EmployeeId=107;UPDATE employeepaymentdetails SET BankAccountNo='999999999999' WHERE EmployeeId=107", new { Id = EssPayslipFlowFixture.BatchId, Batch = JsonSerializer.Serialize(raw, ExcelPayslipRepository.JsonOptions), Source = JsonSerializer.Serialize(source, ExcelPayslipRepository.JsonOptions) });
        var request = new SendExcelPayslipsRequest { RequestId = Guid.NewGuid().ToString("N"), RowIds = ["e2e-row"], Mode = "Individual", ResendCorrected = true, AcknowledgeResend = true };
        var result = await slips.SubmitMailJobAsync(907, EssPayslipFlowFixture.BatchId, request, 9907, "test@example.invalid");
        Assert.Null(result.Error); Assert.Equal("Queued", Assert.Single(result.Item!.Items).Status);
        var state = JsonSerializer.Deserialize<ExcelPayslipSendState>((await db.ExecuteScalarAsync<string>("SELECT CAST(send_state_json AS CHAR) FROM excel_payslip_batches WHERE client_id=907 AND id=@Id", new { Id = EssPayslipFlowFixture.BatchId }))!, ExcelPayslipRepository.JsonOptions)!;
        var snapshot = state.Requests[ExcelPayslipRepository.CorrectionSnapshotPrefix + request.RequestId].Deserialize<ExcelPayslipCorrectionSnapshot>(ExcelPayslipRepository.JsonOptions)!;
        var frozen = Assert.Single(snapshot.Batch.Rows);
        Assert.Equal("Synthetic Employee", frozen.EmployeeName); Assert.Equal("source@example.test", frozen.Email);
        Assert.Equal("9000000107", frozen.Information.Single(f => f.Label == "Mobile No").Value);
        Assert.Equal("0012345678901", frozen.Information.Single(f => f.Label == "Account No").Value);
        Assert.Empty(frozen.Information.Single(f => f.Label == "UAN").Value); Assert.Null(frozen.MasterDetails);
        Assert.Equal(savedSalary, JsonSerializer.Serialize(new { frozen.Earnings, frozen.Deductions, frozen.EmployerContributions, frozen.NetPay }));
        var delivery = Assert.Single(state.Deliveries.Values, d => d.RequestId == request.RequestId);
        Assert.Equal(NotificationRepository.ExcelPayslipCorrectedPendingStatus, await db.ExecuteScalarAsync<string>("SELECT Status FROM notification_queue WHERE Id=@Id", new { Id = delivery.QueueId }));
        // No worker/network call is made by this identity-projection regression.
    }

    [IsolatedTheory]
    [InlineData("ESS.PROFILE_UPDATED", "Pending")]
    [InlineData("EXCEL_PAYSLIP.SEND", "ExcelPending")]
    public async Task Existing_notification_retry_does_not_require_optional_Excel_storage(string eventCode, string expectedStatus)
    {
        var connection = Environment.GetEnvironmentVariable("FREVO_EXCEL_CORRECTED_RESEND_TEST_DB")!;
        var f = new EssPayslipFlowFixture(connection); // Hard guard precedes the synthetic-only table removal.
        await f.ResetAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EssProfile"));
        await using var db = new MySqlConnection(connection); await db.OpenAsync();
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS entity_attachments(id BIGINT PRIMARY KEY)");
        var notifications = new NotificationRepository(f.Config, null!, null!, NullLogger<NotificationRepository>.Instance);
        await notifications.InitializeAsync();
        var queueId = await db.ExecuteScalarAsync<long>("INSERT INTO notification_queue(EventCode,ResourceType,ResourceId,ClientId,ToJson,CcJson,BccJson,Subject,BodyHtml,Status,RetryCount) VALUES(@Event,@Type,@Resource,907,'[\"recipient@example.invalid\"]','[]','[]','Synthetic retry','No send is performed','Failed',5);SELECT LAST_INSERT_ID()", new { Event = eventCode, Type = eventCode == NotificationRepository.ExcelPayslipEvent ? "ExcelPayslipBatch" : "Employee", Resource = EssPayslipFlowFixture.BatchId + ":" + Guid.NewGuid().ToString("N") });
        await db.ExecuteAsync("DROP TABLE excel_payslip_batches");
        await notifications.RetryAsync(queueId);
        var queue = await db.QuerySingleAsync<NotificationQueueItem>("SELECT * FROM notification_queue WHERE Id=@Id", new { Id = queueId });
        Assert.Equal(expectedStatus, queue.Status); Assert.Equal(0, queue.RetryCount); Assert.Null(queue.SentAt);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM notification_logs WHERE QueueId=@Id AND Status='Sent'", new { Id = queueId }));
        // The test intentionally calls neither ProcessPending nor SMTP.
    }

    private static Task<NotificationTemplate> SeedMailTemplateAsync(NotificationRepository notifications) => notifications.SaveTemplateAsync(new()
    {
        Code = NotificationRepository.ExcelPayslipTemplateCode, Name = "Synthetic corrected payslip test",
        SubjectTemplate = "{{clientName}} payslip - {{month}}", BodyTemplate = "<p>Payslip for {{employeeName}} for {{month}} is attached as a PDF.</p>", IsHtml = true, IsActive = true
    });

    private sealed class LoopbackSmtp : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(60));
        public int Port { get; }
        public Task<MimeMessage> Message { get; }
        public LoopbackSmtp() { listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; Message = CaptureAsync(); }
        private async Task<MimeMessage> CaptureAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token); await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8); await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 localhost synthetic SMTP"); MimeMessage? message = null;
            while (await reader.ReadLineAsync(stop.Token) is { } line)
            {
                if (line.StartsWith("EHLO", StringComparison.Ordinal)) { await writer.WriteLineAsync("250-localhost"); await writer.WriteLineAsync("250 PIPELINING"); }
                else if (line == "DATA")
                {
                    await writer.WriteLineAsync("354 Send message"); var content = new StringBuilder();
                    while (await reader.ReadLineAsync(stop.Token) is { } part && part != ".") content.AppendLine(part.StartsWith("..", StringComparison.Ordinal) ? part[1..] : part);
                    using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(content.ToString())); message = await MimeMessage.LoadAsync(bytes, stop.Token); await writer.WriteLineAsync("250 Accepted");
                }
                else if (line == "QUIT") { await writer.WriteLineAsync("221 Bye"); break; }
                else await writer.WriteLineAsync("250 OK");
            }
            return message ?? throw new InvalidOperationException("No synthetic message received.");
        }
        public ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); stop.Dispose(); return ValueTask.CompletedTask; }
    }
}

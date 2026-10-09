using System.Text;
using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipDeliveryTests
{
    private static ExcelPayslipBatch Batch() => new()
    {
        Id = "0123456789abcdef0123456789abcdef", ClientId = 11, Month = "2026-09", SheetName = "Salary", HeaderRow = 1,
        Rows = Enumerable.Range(0, 3).Select(index => new ExcelPayslipRow
        {
            Id = $"row-{index + 2}", SourceRow = index + 2, EmployeeName = $"Example {index}",
            Email = index == 0 ? "person@example.test" : "", NetPay = 100,
            Earnings = [new() { Label = "Wages", Amount = 100 }]
        }).ToList()
    };

    private static (NotificationRepository.ExcelPayslipMailDocument Document, ExcelPayslipRepository.ExcelPayslipQueueStatus Queue)
        Delivery(ExcelPayslipBatch batch, ExcelPayslipSendState state, string status = "Pending", params ExcelPayslipRow[] rows)
    {
        var document = NotificationRepository.PrepareExcelPayslipMail(batch, rows.Length == 0 ? [batch.Rows[0]] : rows,
            "recipient@example.test", Encoding.ASCII.GetBytes("%PDF-1.4\nfixture"), Guid.NewGuid().ToString("N"), "tester", false, 0, ExcelPayslipTests.MailTemplate);
        document.QueueId = state.Deliveries.Count + 1;
        state.Deliveries.Add(document.Id, document);
        return (document, new()
        {
            Id = document.QueueId, ClientId = batch.ClientId, ResourceType = "ExcelPayslipBatch", ResourceId = batch.Id + ":" + document.Id,
            EventCode = NotificationRepository.ExcelPayslipEvent, Status = status,
            CreatedAt = document.CreatedAtUtc, SentAt = status == "Sent" ? document.CreatedAtUtc.AddMinutes(1) : null
        });
    }

    private static SendExcelPayslipsRequest Request(ExcelPayslipBatch batch, string mode = "Individual") => new()
    {
        RequestId = Guid.NewGuid().ToString("N"), Mode = mode, Email = "hr@example.test", RowIds = batch.Rows.Select(row => row.Id).ToList()
    };

    [Theory]
    [InlineData("Pending", "Queued")]
    [InlineData("Retry", "Queued")]
    [InlineData("ExcelPending", "Queued")]
    [InlineData("ExcelRetry", "Queued")]
    [InlineData("Processing", "Queued")]
    [InlineData("Sending", "Queued")]
    [InlineData("Sent", "Sent")]
    [InlineData("Failed", "Failed")]
    [InlineData("Cancelled", "Unknown")]
    public void RecordedDeliveriesAreNeverAutomaticallyQueuedAgain(string queueStatus, string expected)
    {
        var batch = Batch(); var state = new ExcelPayslipSendState(); var (document, queue) = Delivery(batch, state, queueStatus);
        var item = ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, [queue]).Items[0];
        Assert.Equal(expected, item.Status); Assert.False(item.CanSend);
        Assert.Equal(document.Email, item.Email); Assert.Equal(document.CreatedAtUtc, item.QueuedAtUtc);
        Assert.Equal(queue.SentAt, item.SentAtUtc);
        Assert.True(ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, [queue]).Items[1].CanSend);
    }

    [Fact]
    public void ReopenedBatchRestoresStatusesFromPersistentDeliveryMetadataAndLiveQueue()
    {
        var batch = Batch(); var state = new ExcelPayslipSendState(); var (_, queue) = Delivery(batch, state);
        var reopened = JsonSerializer.Deserialize<ExcelPayslipSendState>(JsonSerializer.Serialize(state, ExcelPayslipRepository.JsonOptions), ExcelPayslipRepository.JsonOptions)!;
        Assert.Equal("Queued", ExcelPayslipRepository.BuildDeliveryStatuses(batch, reopened, [queue]).Items[0].Status);
        queue.Status = "Sent"; queue.SentAt = DateTime.UtcNow;
        var row = ExcelPayslipRepository.BuildDeliveryStatuses(batch, reopened, [queue]).Items[0];
        Assert.Equal("Sent", row.Status); Assert.Equal(queue.SentAt, row.SentAtUtc); Assert.False(row.CanSend);
    }

    [Fact]
    public void EarlierSuccessfulDeliveryIsRetainedWhenTheGlobalQueueWasResetForRetry()
    {
        var batch = Batch(); var state = new ExcelPayslipSendState(); var (_, queue) = Delivery(batch, state, "Retry");
        queue.LastSentAt = DateTime.UtcNow.AddHours(-1);
        var row = ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, [queue]).Items[0];
        Assert.Equal("Sent", row.Status); Assert.Equal(queue.LastSentAt, row.SentAtUtc); Assert.False(row.CanSend);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("client")]
    [InlineData("event")]
    [InlineData("resource")]
    [InlineData("type")]
    [InlineData("descriptor")]
    public void MissingOrMismatchedQueueEvidenceIsUnknownAndCannotClaimSentOrPermitResend(string change)
    {
        var batch = Batch(); var state = new ExcelPayslipSendState(); var (document, queue) = Delivery(batch, state, "Sent");
        switch (change)
        {
            case "client": queue.ClientId++; break;
            case "event": queue.EventCode = "OTHER"; break;
            case "resource": queue.ResourceId = "other-batch:" + document.Id; break;
            case "type": queue.ResourceType = "Other"; break;
            case "descriptor": document.ClientId++; break;
        }
        var row = ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, change == "missing" ? [] : [queue]).Items[0];
        Assert.Equal("Unknown", row.Status); Assert.Null(row.SentAtUtc); Assert.False(row.CanSend);
        if (change == "descriptor") Assert.Empty(row.Email);
    }

    [Theory]
    [InlineData("Queued", false)]
    [InlineData("Already queued", false)]
    [InlineData("Skipped", false)]
    [InlineData("Error", true)]
    [InlineData("Ready", true)]
    public void LegacyReceiptsOnlyBlockRowsForWhichAPreviousSendWasRecorded(string savedStatus, bool canSend)
    {
        var batch = Batch(); var state = new ExcelPayslipSendState();
        state.Requests["old-request"] = JsonSerializer.SerializeToElement(new
        {
            result = new { items = new[] { new { rowId = batch.Rows[0].Id, status = savedStatus } } }
        });
        var row = ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, []).Items[0];
        Assert.Equal(canSend, row.CanSend); Assert.Equal(canSend ? "Pending" : "Unknown", row.Status); Assert.Null(row.SentAtUtc);
    }

    [Fact]
    public async Task SubsequentRequestOnlyQueuesEmployeesWhoseMissingRecipientsWereFilledLater()
    {
        var batch = Batch(); var sourceBefore = JsonSerializer.Serialize(batch); var state = new ExcelPayslipSendState();
        var queues = new List<ExcelPayslipRepository.ExcelPayslipQueueStatus>();
        var requestedRows = new List<string>(); var readinessChecks = 0;
        Task<string?> Ready() { readinessChecks++; return Task.FromResult<string?>(null); }
        Task<string?> Queue(IReadOnlyList<ExcelPayslipRow> rows, string email)
        {
            requestedRows.AddRange(rows.Select(row => row.Id));
            var (document, queue) = Delivery(batch, state, "Pending", rows.ToArray()); document.Email = email; queues.Add(queue);
            return Task.FromResult<string?>(null);
        }
        async Task<ExcelPayslipDeliveryResult> Send(SendExcelPayslipsRequest request)
        {
            var status = ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, queues);
            var reviewed = ExcelPayslipRepository.ReviewDeliverySelection(batch.Rows, request, status);
            return (await ExcelPayslipRepository.DispatchDeliverySelectionAsync(batch.Rows, request, reviewed, Ready, Queue)).Item!;
        }
        var firstRequest = Request(batch); var first = await Send(firstRequest);
        Assert.Equal(new[] { "Queued", "Error", "Error" }, first.Items.Select(item => item.Status));
        Assert.Equal(new[] { batch.Rows[0].Id }, requestedRows);
        var secondRequest = Request(batch);
        secondRequest.EmailOverrides = new() { [batch.Rows[1].Id] = "second@example.test", [batch.Rows[2].Id] = "third@example.test" };
        Assert.NotEqual(firstRequest.RequestId, secondRequest.RequestId);
        var second = await Send(secondRequest);
        Assert.Equal(new[] { "Skipped", "Queued", "Queued" }, second.Items.Select(item => item.Status));
        Assert.Equal(batch.Rows.Select(row => row.Id), requestedRows); // Each row was queued exactly once across request IDs.
        Assert.Equal(2, readinessChecks);
        var third = await Send(Request(batch));
        Assert.All(third.Items, item => Assert.Equal("Skipped", item.Status));
        Assert.Equal(2, readinessChecks); Assert.Equal(3, requestedRows.Count);
        Assert.Equal(sourceBefore, JsonSerializer.Serialize(batch)); // Original emails and integrity hash input are untouched.
    }

    [Fact]
    public async Task CombinedPdfContainsOnlyFreshRowsAndLaterIndividualOverrideCannotResendAnIncludedRow()
    {
        var batch = Batch(); var state = new ExcelPayslipSendState(); var (_, queue) = Delivery(batch, state, "Sent");
        var request = Request(batch, "Combined");
        var reviewed = ExcelPayslipRepository.ReviewDeliverySelection(batch.Rows, request, ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, [queue]));
        var emitted = new List<string[]>();
        var result = await ExcelPayslipRepository.DispatchDeliverySelectionAsync(batch.Rows, request, reviewed,
            () => Task.FromResult<string?>(null), (rows, email) =>
            {
                Assert.Equal("hr@example.test", email); emitted.Add(rows.Select(row => row.Id).ToArray());
                return Task.FromResult<string?>(null);
            });
        Assert.Equal(batch.Rows.Skip(1).Select(row => row.Id), Assert.Single(emitted));
        Assert.Equal(new[] { "Skipped", "Queued", "Queued" }, result.Item!.Items.Select(item => item.Status));
        var individual = Request(batch); individual.EmailOverrides[batch.Rows[0].Id] = "different@example.test";
        var repeated = ExcelPayslipRepository.ReviewDeliverySelection([batch.Rows[0]], individual, ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, [queue]));
        Assert.Equal("Skipped", Assert.Single(repeated.Items).Status);
    }

    [Fact]
    public async Task AllSkippedRowsNeverRunSmtpReadinessOrPdfQueueCallbacks()
    {
        var batch = Batch(); var state = new ExcelPayslipSendState(); var (_, queue) = Delivery(batch, state, "Sent", batch.Rows.ToArray());
        var request = Request(batch, "Combined");
        var reviewed = ExcelPayslipRepository.ReviewDeliverySelection(batch.Rows, request, ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, [queue]));
        var result = await ExcelPayslipRepository.DispatchDeliverySelectionAsync(batch.Rows, request, reviewed,
            () => throw new Exception("Must not access SMTP, even if disabled."), (_, _) => throw new Exception("Must not render a PDF or enqueue."));
        Assert.Null(result.Error); Assert.All(result.Item!.Items, item => Assert.Equal("Skipped", item.Status));
    }

    [Fact]
    public void SentEvidenceWinsOverLaterFailedAttemptsAndEmptyHistoryRemainsPending()
    {
        var batch = Batch(); var state = new ExcelPayslipSendState(); var (_, sent) = Delivery(batch, state, "Sent");
        var (_, failed) = Delivery(batch, state, "Failed");
        var result = ExcelPayslipRepository.BuildDeliveryStatuses(batch, state, [failed, sent]);
        Assert.Equal("Sent", result.Items[0].Status);
        Assert.All(result.Items.Skip(1), item => { Assert.Equal("Pending", item.Status); Assert.True(item.CanSend); });
    }
}

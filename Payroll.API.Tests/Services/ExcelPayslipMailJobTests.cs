using System.Text.Json;
using Payroll.API.Models;
using Payroll.API.Repositories;

namespace Payroll.API.Tests.Services;

public sealed class ExcelPayslipMailJobTests
{
    private static ExcelPayslipBatch Batch(int count = 3) => new()
    {
        Id = "0123456789abcdef0123456789abcdef", ClientId = 11, ClientName = "Example", Month = "2026-09", SourceFileName = "salary.xlsx",
        Rows = Enumerable.Range(1, count).Select(index => new ExcelPayslipRow { Id = $"row-{index}", SourceRow = index + 1,
            EmployeeName = $"Person {index}", Email = $"person{index}@example.test", NetPay = 100,
            Earnings = [new() { Label = "Salary", Amount = 100 }] }).ToList()
    };

    private static SendExcelPayslipsRequest Request(ExcelPayslipBatch batch, string mode = "Individual") => new()
    {
        RequestId = Guid.NewGuid().ToString("N"), RowIds = batch.Rows.Select(row => row.Id).ToList(), Mode = mode,
        Email = "hr@example.test", IncludeSeal = false, AmountDecimalPlaces = 0
    };

    private static (ExcelPayslipMailJobRecord Job, List<ExcelPayslipRepository.ExcelPayslipQueueStatus> Queues,
        List<NotificationRepository.ExcelPayslipMailDocument> Documents, ExcelPayslipDeliveryResult Review)
        Prepare(ExcelPayslipBatch batch, SendExcelPayslipsRequest request, ExcelPayslipSendState? state = null)
    {
        var review = ExcelPayslipRepository.ReviewDeliverySelection(batch.Rows, request,
            ExcelPayslipRepository.BuildDeliveryStatuses(batch, state ?? new(), []));
        var documents = ExcelPayslipRepository.PrepareMailJobDocuments(batch, batch.Rows, request, review, request.RequestId, "operator");
        for (var index = 0; index < documents.Count; index++) documents[index].QueueId = index + 1;
        var job = ExcelPayslipRepository.CreateMailJobRecord(batch, request.RequestId, 7, "operator", batch.Rows, review, documents);
        var queues = documents.Select(document => new ExcelPayslipRepository.ExcelPayslipQueueStatus
        {
            Id = document.QueueId, ClientId = batch.ClientId, EventCode = NotificationRepository.ExcelPayslipEvent,
            ResourceType = "ExcelPayslipBatch", ResourceId = batch.Id + ":" + document.Id, Status = "Pending", CreatedAt = job.CreatedAtUtc
        }).ToList();
        return (job, queues, documents, review);
    }

    [Fact]
    public void WholeBatchAccepts1000IndividualRowsWithoutPdfBytesAndKeepsOriginalSnapshot()
    {
        var batch = Batch(1000); var original = JsonSerializer.Serialize(batch); var request = Request(batch);
        Assert.Null(ExcelPayslipRepository.ValidateSendRequest(batch.Id, request, 1000));
        Assert.NotNull(ExcelPayslipRepository.ValidateSendRequest(batch.Id, request, 25)); // Legacy limit remains intact.
        var (job, queues, documents, _) = Prepare(batch, request);
        Assert.Equal(1000, documents.Count); Assert.Equal(1000, documents.Select(document => document.Id).Distinct().Count());
        Assert.Single(documents.Select(document => document.BatchSha256).Distinct());
        Assert.All(documents, document => { Assert.Single(document.RowIds); Assert.False(document.IncludeSeal); });
        var summary = ExcelPayslipRepository.BuildMailJobSummary(job, queues);
        Assert.Equal(1000, summary.Total); Assert.Equal(1000, summary.Queued); Assert.Equal(0, summary.Sent);
        Assert.Equal(0, summary.Completed); Assert.Equal(0, summary.PercentComplete); Assert.False(summary.IsComplete);
        Assert.Equal(original, JsonSerializer.Serialize(batch));
        Assert.DoesNotContain("%PDF", JsonSerializer.Serialize(documents));
        request.RowIds.Add("extra"); Assert.NotNull(ExcelPayslipRepository.ValidateSendRequest(batch.Id, request, 1000));
    }

    [Fact]
    public void CombinedJobCountsPayslipsNotQueueItemsAndOnlyContainsFreshSubset()
    {
        var batch = Batch(); var state = new ExcelPayslipSendState();
        var old = NotificationRepository.PrepareExcelPayslipMailMetadata(batch, [batch.Rows[0]], "old@example.test", "old", "operator", false, 0);
        old.QueueId = 50; state.Deliveries[old.Id] = old;
        var (job, queues, documents, _) = Prepare(batch, Request(batch, "Combined"), state);
        Assert.Equal(batch.Rows.Skip(1).Select(row => row.Id), Assert.Single(documents).RowIds);
        var pending = ExcelPayslipRepository.BuildMailJobSummary(job, queues);
        Assert.Equal(3, pending.Total); Assert.Equal(2, pending.Queued); Assert.Equal(1, pending.Skipped); Assert.Equal(33, pending.PercentComplete);
        queues[0].Status = "Sent";
        var complete = ExcelPayslipRepository.BuildMailJobSummary(job, queues);
        Assert.Equal(2, complete.Sent); Assert.Equal(1, complete.Skipped); Assert.Equal(3, complete.Completed);
        Assert.Equal(100, complete.PercentComplete); Assert.True(complete.IsComplete); Assert.Equal("Completed", complete.Status);
    }

    [Theory]
    [InlineData("Pending", "Queued", 1, 0, 0)]
    [InlineData("Retry", "Queued", 1, 0, 0)]
    [InlineData("Processing", "Processing", 1, 0, 0)]
    [InlineData("Sent", "Completed", 0, 0, 0)]
    [InlineData("Failed", "Needs attention", 0, 1, 0)]
    [InlineData("Cancelled", "Needs attention", 0, 0, 1)]
    public void ProgressUsesActualQueueStateRatherThanStoredQueuedReceipt(string state, string expected, int queued, int failed, int unknown)
    {
        var batch = Batch(1); var prepared = Prepare(batch, Request(batch)); prepared.Queues[0].Status = state;
        var summary = ExcelPayslipRepository.BuildMailJobSummary(prepared.Job, prepared.Queues);
        Assert.Equal(expected, summary.Status); Assert.Equal(queued, summary.Queued); Assert.Equal(failed, summary.Failed);
        Assert.Equal(unknown, summary.Unknown); Assert.Equal(state == "Sent" ? 1 : 0, summary.Sent); Assert.Equal(queued == 0, summary.IsComplete);
    }

    [Fact]
    public void MissingEmailsRemainUnqueuedAndTheirEarlierJobOutcomeDoesNotChangeAfterLaterSend()
    {
        var batch = Batch(); batch.Rows[1].Email = ""; batch.Rows[2].Email = "invalid";
        var first = Prepare(batch, Request(batch)); Assert.Single(first.Documents); Assert.Equal(2, first.Job.MissingEmail);
        first.Queues[0].Status = "Sent";
        var state = new ExcelPayslipSendState { Deliveries = first.Documents.ToDictionary(document => document.Id) };
        var nextRequest = Request(batch); nextRequest.EmailOverrides = new() { [batch.Rows[1].Id] = "two@example.test", [batch.Rows[2].Id] = "three@example.test" };
        var next = Prepare(batch, nextRequest, state);
        Assert.Equal(2, next.Documents.Count); Assert.Equal(1, next.Job.Skipped); Assert.Equal(0, next.Job.MissingEmail);
        var original = ExcelPayslipRepository.BuildMailJobSummary(first.Job, first.Queues);
        Assert.Equal(2, original.MissingEmail); Assert.Equal(1, original.Sent); Assert.Equal("Needs attention", original.Status);
        Assert.True(original.IsComplete); Assert.Equal(100, original.PercentComplete);
    }

    [Fact]
    public void NoReadyRowsProducesNoDocumentsButRetainsFinishedAttemptMetadata()
    {
        var batch = Batch(); foreach (var row in batch.Rows) row.Email = "";
        var prepared = Prepare(batch, Request(batch)); Assert.Empty(prepared.Documents);
        var summary = ExcelPayslipRepository.BuildMailJobSummary(prepared.Job, []);
        Assert.Equal(3, summary.MissingEmail); Assert.Equal(0, summary.Sent); Assert.True(summary.IsComplete);
        Assert.Equal("Needs attention", summary.Status);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("client")]
    [InlineData("event")]
    [InlineData("resource")]
    [InlineData("type")]
    public void MissingOrWrongScopeQueueCannotBeCountedAsSent(string change)
    {
        var batch = Batch(1); var prepared = Prepare(batch, Request(batch)); var queue = prepared.Queues[0]; queue.Status = "Sent";
        switch (change)
        {
            case "client": queue.ClientId++; break;
            case "event": queue.EventCode = "OTHER"; break;
            case "resource": queue.ResourceId = "other"; break;
            case "type": queue.ResourceType = "Other"; break;
            case "missing": prepared.Queues.Clear(); break;
        }
        var summary = ExcelPayslipRepository.BuildMailJobSummary(prepared.Job, prepared.Queues);
        Assert.Equal(0, summary.Sent); Assert.Equal(1, summary.Unknown); Assert.Equal("Needs attention", summary.Status);
    }

    [Fact]
    public void LongRunningProcessingIsFlaggedWithoutDeclaringDeliveryOrAllowingDismissal()
    {
        var batch = Batch(1); var prepared = Prepare(batch, Request(batch)); prepared.Queues[0].Status = "Processing";
        var summary = ExcelPayslipRepository.BuildMailJobSummary(prepared.Job, prepared.Queues, prepared.Job.CreatedAtUtc.AddHours(2));
        Assert.Equal("Needs attention", summary.Status); Assert.False(summary.IsComplete); Assert.Equal(1, summary.Queued);
        Assert.Equal(0, summary.Completed); Assert.Equal(0, summary.Sent); Assert.Equal("Processing", prepared.Queues[0].Status);
    }

    [Fact]
    public void SuccessfulLogEvidenceSurvivesQueueResetAndJobsSurviveSerialization()
    {
        var batch = Batch(1); var prepared = Prepare(batch, Request(batch)); prepared.Queues[0].Status = "Retry";
        prepared.Queues[0].LastSentAt = DateTime.UtcNow;
        var state = new ExcelPayslipSendState { Jobs = new() { [prepared.Job.Id] = prepared.Job }, Deliveries = prepared.Documents.ToDictionary(document => document.Id) };
        var restored = JsonSerializer.Deserialize<ExcelPayslipSendState>(JsonSerializer.Serialize(state, ExcelPayslipRepository.JsonOptions), ExcelPayslipRepository.JsonOptions)!;
        var summary = ExcelPayslipRepository.BuildMailJobSummary(restored.Jobs[prepared.Job.Id], prepared.Queues);
        Assert.Equal(1, summary.Sent); Assert.Equal(100, summary.PercentComplete);
        Assert.Empty(JsonSerializer.Deserialize<ExcelPayslipSendState>("{}", ExcelPayslipRepository.JsonOptions)!.Jobs);
    }

    [Theory]
    [InlineData(7, 11, true)]
    [InlineData(8, 11, false)]
    [InlineData(7, 12, false)]
    [InlineData(0, 11, false)]
    public void OwnershipUsesImmutableUserIdAndClientRatherThanEmail(int actor, int client, bool expected)
    {
        var batch = Batch(); var prepared = Prepare(batch, Request(batch));
        Assert.Equal(expected, ExcelPayslipRepository.OwnsMailJob(prepared.Job, actor, client, batch.Id));
        Assert.False(ExcelPayslipRepository.OwnsMailJob(prepared.Job, 7, 11, "another-batch"));
    }

    [Fact]
    public void ResponseLostReceiptReturnsSameOutcomeAndRejectsChangedRecipientsOrBatch()
    {
        var batch = Batch(52); var request = Request(batch); var prepared = Prepare(batch, request);
        foreach (var item in prepared.Review.Items) item.Status = "Queued";
        var fingerprint = ExcelPayslipRepository.SendFingerprint(batch.Id, request);
        var receipt = JsonSerializer.Serialize(new { fingerprint, result = prepared.Review }, ExcelPayslipRepository.JsonOptions);
        var restored = ExcelPayslipRepository.RestoreSendRequest(receipt, ExcelPayslipRepository.SendFingerprint(batch.Id, request));
        Assert.Equal(52, restored.Item!.Items.Count); Assert.All(restored.Item.Items, item => Assert.Equal("Already queued", item.Status));
        request.EmailOverrides[batch.Rows[0].Id] = "changed@example.test";
        Assert.NotNull(ExcelPayslipRepository.RestoreSendRequest(receipt, ExcelPayslipRepository.SendFingerprint(batch.Id, request)).Error);
        Assert.NotNull(ExcelPayslipRepository.RestoreSendRequest(receipt, ExcelPayslipRepository.SendFingerprint(Guid.NewGuid().ToString("N"), request)).Error);
    }
}
